using System.IO;
using System.IO.Pipes;
using System.Windows.Threading;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

/// <summary>
/// The only place a relayed command may be executed. Implemented by <see cref="MainWindow"/> so that every command
/// still goes through the existing gate, Guardian lease and safety checks; the pipe server itself never touches
/// hardware, never reads a profile and never decides whether a write is allowed.
/// </summary>
internal interface IAppCommandEndpoint
{
    /// <summary>Runs on the UI dispatcher. Returns a bounded, log-safe response for the requesting CLI process.</summary>
    Task<ToolkitCommandIpcResponse> HandleAppCommandAsync(ToolkitCommand command, string requestId);
}

/// <summary>Per-user pipe naming. Both ends live in this process image, so the discriminator only splits users.</summary>
internal static class AppCommandPipeIdentity
{
    public static string CurrentUserDiscriminator() =>
        (Environment.UserDomainName ?? string.Empty) + "\\" + (Environment.UserName ?? string.Empty);

    /// <summary>Fixed, path-free name derived from the current user; the server additionally restricts the ACL to it.</summary>
    public static string PipeName => ToolkitCommandIpcProtocol.PipeNameFor(CurrentUserDiscriminator());
}

/// <summary>Bounded, single-request frames: 4-byte little-endian length + UTF-8 JSON body.</summary>
internal static class AppCommandPipeFrames
{
    /// <summary>Reads one frame; returns null on a closed connection or an out-of-bounds length.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[ToolkitCommandIpcProtocol.FrameHeaderBytes];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
            return null;
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > maxBytes)
            return null;
        var payload = new byte[length];
        return await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false) ? payload : null;
    }

    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                return false;
            offset += read;
        }
        return true;
    }
}

/// <summary>
/// Serves the command pipe of the running main instance. Design constraints, in order of importance:
/// <list type="bullet">
/// <item>Only this process may control the EC; the server never executes anything itself, it dispatches to the UI.</item>
/// <item>Access is limited to the current user (the pipe DACL comes from <see cref="PipeOptions.CurrentUserOnly"/>).</item>
/// <item>One request per connection, one connection at a time, bounded frame size and bounded timeouts.</item>
/// <item>A write command is executed at most once per request id (bounded in-memory cache); a duplicate retry is
/// answered, never re-run.</item>
/// <item>Dispose stops accepting immediately; in-flight answers then fail with "unavailable".</item>
/// </list>
/// </summary>
internal sealed class AppCommandPipeServer : IDisposable
{
    private const int MaxServerInstances = 1;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandlerStartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadOnlyExecutionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandExecutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(10);
    private static readonly int LogEveryNthFailure = 20;

    private readonly string _pipeName;
    private readonly Dispatcher _dispatcher;
    private readonly IAppCommandEndpoint _endpoint;

    /// <summary>
    /// In-memory request-id deduplication of write commands. A client that timed out may retry the very same frame;
    /// the server then answers from this cache and never calls the endpoint again. Nothing is persisted, so a restart
    /// clears it, and only the bounded answer (status/message/report) is kept - never a path or a command line.
    /// </summary>
    private readonly ToolkitCommandRequestCache _requestCache = new();

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private int _disposed;
    private int _consecutiveFailures;

    private AppCommandPipeServer(string pipeName, Dispatcher dispatcher, IAppCommandEndpoint endpoint)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _endpoint = endpoint;
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Starts the server. A failure (for example a pipe name already owned by another instance) is logged
    /// by the caller and leaves the app fully functional, only without the CLI channel.</summary>
    public static AppCommandPipeServer Start(IAppCommandEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint is not System.Windows.Threading.DispatcherObject dispatcherObject)
            throw new ArgumentException("The endpoint must be a dispatcher object.", nameof(endpoint));
        return new AppCommandPipeServer(AppCommandPipeIdentity.PipeName,
            dispatcherObject.Dispatcher, endpoint);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        // Never block the UI thread on shutdown: the loop observes cancellation and exits on its own.
        _ = _loop.ContinueWith(static (task, _) =>
            {
                if (task.IsFaulted)
                    AppLog.Write("command pipe loop stopped with an error", task.Exception);
            },
            null, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // A fresh instance per connection: no Disconnect() handshake to get wrong, and the number of
                // concurrent clients stays exactly one.
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                if (_lifetime.IsCancellationRequested)
                    break;
                _consecutiveFailures = 0;
                await ServeConnectionAsync(server).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                if (_lifetime.IsCancellationRequested)
                    break;
                await RestartAfterFailureAsync("command pipe connection failed", ex).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                if (_lifetime.IsCancellationRequested)
                    break;
                await RestartAfterFailureAsync("command pipe access denied", ex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_lifetime.IsCancellationRequested)
                    break;
                await RestartAfterFailureAsync("command pipe server error", ex).ConfigureAwait(false);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    /// <summary>
    /// Backoff instead of a hot retry loop. The common cause is benign - another instance of the same app already
    /// owns the pipe name - so the failure is logged sparsely and the channel recovers by itself once it is free.
    /// </summary>
    private async Task RestartAfterFailureAsync(string message, Exception exception)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures == 1 || failures % LogEveryNthFailure == 0)
            AppLog.Write($"{message} (attempt {failures})", exception);
        var delay = TimeSpan.FromMilliseconds(Math.Min(MaxRestartDelay.TotalMilliseconds,
            RestartDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 6))));
        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream server)
    {
        ToolkitCommandIpcResponse response;
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        requestCts.CancelAfter(RequestReadTimeout);
        byte[]? payload;
        try
        {
            payload = await AppCommandPipeFrames.ReadFrameAsync(server, ToolkitCommandIpcProtocol.MaxRequestBytes,
                requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A client that cannot finish a bounded request frame gets nothing back; it already times out locally.
            return;
        }
        catch (IOException)
        {
            return;
        }

        if (payload is null)
        {
            response = ToolkitCommandIpcProtocol.Rejected(string.Empty, ToolkitCommandAction.None,
                "请求格式不正确，未执行任何操作。");
        }
        else
        {
            var parsed = ToolkitCommandIpcProtocol.TryParseRequest(payload);
            response = parsed.Success
                ? await DispatchOnceAsync(parsed.Command!, parsed.RequestId).ConfigureAwait(false)
                : ToolkitCommandIpcProtocol.Rejected(parsed.RequestId, ToolkitCommandAction.None, parsed.Message);
        }

        try
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            writeCts.CancelAfter(ResponseWriteTimeout);
            await AppCommandPipeFrames.WriteFrameAsync(server,
                ToolkitCommandIpcProtocol.SerializeResponse(response), writeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The client gave up (its own timeout); the command result is already logged by the executor.
        }
    }

    /// <summary>
    /// Executes a write command at most once per request id. The id is recorded <em>before</em> the command reaches the
    /// UI thread, so a retry that arrives while the first attempt runs - or after it timed out without an answer - is
    /// answered instead of executed again. Read-only <c>status</c> and the UI-only <c>activate</c> are not
    /// deduplicated: they are cheap, side-effect free and must always return a fresh answer.
    /// </summary>
    private async Task<ToolkitCommandIpcResponse> DispatchOnceAsync(ToolkitCommand command, string requestId)
    {
        // The protocol already refuses a write without a well-formed request id; never build a shared cache key if a
        // caller constructs the command itself.
        if (!ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(command.Action) ||
            !ToolkitCommandIpcProtocol.IsValidRequestId(requestId))
            return await DispatchAsync(command, requestId).ConfigureAwait(false);

        var decision = _requestCache.Begin(requestId, command.Action);
        switch (decision.Kind)
        {
            case ToolkitCommandDispatchDecisionKind.Replay:
                AppLog.Write($"command pipe duplicate request answered from cache action={command.Action}");
                return ToolkitCommandIpcProtocol.Replayed(requestId, decision.CachedAnswer!);
            case ToolkitCommandDispatchDecisionKind.AlreadyInFlight:
                AppLog.Write($"command pipe duplicate request refused while in flight action={command.Action}");
                return ToolkitCommandIpcProtocol.DuplicateInFlight(requestId, command.Action);
        }

        var response = await DispatchAsync(command, requestId).ConfigureAwait(false);
        _requestCache.Complete(requestId, command.Action, response);
        return response;
    }

    /// <summary>
    /// Marshals one command to the UI dispatcher and waits, bounded, for the answer. A timeout never aborts the
    /// running command: the app keeps working and the client is told to re-check with <c>status</c>.
    /// </summary>
    private async Task<ToolkitCommandIpcResponse> DispatchAsync(ToolkitCommand command, string requestId)
    {
        try
        {
            var operation = _dispatcher.InvokeAsync(
                () => _endpoint.HandleAppCommandAsync(command, requestId),
                DispatcherPriority.Normal,
                CancellationToken.None);
            var pending = await operation.Task.WaitAsync(HandlerStartTimeout, _lifetime.Token).ConfigureAwait(false);
            var timeout = command.IsReadOnly ? ReadOnlyExecutionTimeout : CommandExecutionTimeout;
            return await pending.WaitAsync(timeout, _lifetime.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AppLog.Write($"command pipe handler timed out action={command.Action}");
            return ToolkitCommandIpcProtocol.TimedOut(requestId, command.Action);
        }
        catch (OperationCanceledException)
        {
            return ToolkitCommandIpcProtocol.Unavailable(requestId, command.Action,
                "程序正在退出，命令未执行。");
        }
        catch (Exception ex)
        {
            AppLog.Write("command pipe handler failed", ex);
            return ToolkitCommandIpcProtocol.Failed(requestId, command.Action,
                "主实例处理命令时出现内部错误，未完成操作。");
        }
    }
}
