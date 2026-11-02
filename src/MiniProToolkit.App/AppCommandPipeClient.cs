using System.IO;
using System.IO.Pipes;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

/// <summary>
/// CLI side of the command pipe. It only sends one already-parsed command to the running main instance and returns
/// that instance's answer: it never reads the EC, never opens a Guardian lease, never applies a profile and never
/// starts a second hardware-controlling window. Every failure is reported as a response the host can turn into a
/// dedicated exit code, so nothing here can look like success by accident. The failure side is split by connection
/// state, because the two cases are not interchangeable: before the connect succeeds nothing was sent ("no instance",
/// exit code 4), after it the request is on the wire and a lost answer means "may already have executed" ("timed
/// out", exit code 6, confirm with <c>status</c>).
/// </summary>
internal static class AppCommandPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Client answer budget. It must outlive the server's worst case (10 s handler start + 30 s execution, see
    /// <see cref="ToolkitCommandIpcProtocol.ServerWorstCaseMilliseconds"/>) with a 5 s margin; a shorter budget would
    /// report a still-running write as "not executed" while the EC may already have been changed.
    /// </summary>
    private static readonly TimeSpan DefaultResponseTimeout =
        TimeSpan.FromMilliseconds(ToolkitCommandIpcProtocol.DefaultClientResponseTimeoutMilliseconds);

    /// <summary>Blocking wrapper for the WinExe startup path (no message loop is running yet).</summary>
    public static ToolkitCommandIpcResponse Send(ToolkitCommandIpcRequest request, TimeSpan? responseTimeout = null) =>
        SendAsync(request, responseTimeout).GetAwaiter().GetResult();

    public static async Task<ToolkitCommandIpcResponse> SendAsync(ToolkitCommandIpcRequest request,
        TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = ParseActionToken(request.Action);
        using var timeout = new CancellationTokenSource(responseTimeout ?? DefaultResponseTimeout);
        var connected = false;
        try
        {
            using var client = new NamedPipeClientStream(".", AppCommandPipeIdentity.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, timeout.Token).ConfigureAwait(false);
            // Do not trust a same-user process that merely created the expected pipe name. Verify the
            // connected server's PID and executable path before sending any command bytes. This check
            // is pre-dispatch, so a rejection still means no action was sent.
            if (!NativeMethods.IsExpectedCommandPipeServer(client.SafePipeHandle))
                return NoInstance(request, action);

            // From here on the request is on the wire, so no failure may claim that nothing happened: the running
            // instance can already have executed the write.
            connected = true;
            await AppCommandPipeFrames.WriteFrameAsync(client, ToolkitCommandIpcProtocol.SerializeRequest(request),
                timeout.Token).ConfigureAwait(false);
            var payload = await AppCommandPipeFrames.ReadFrameAsync(client, ToolkitCommandIpcProtocol.MaxResponseBytes,
                timeout.Token).ConfigureAwait(false);
            // EOF or an unusable answer after a sent request: the command may have run, so never report "not executed".
            if (payload is null)
                return Unconfirmed(request, action);
            return ToolkitCommandIpcProtocol.TryParseResponse(payload, request.RequestId)
                   ?? Unconfirmed(request, action);
        }
        catch (TimeoutException)
        {
            // The connect window elapsed before any instance accepted: nothing was sent, so no retry and no fallback.
            // (An already sent request is bounded by the cancellation token above and never lands here.)
            return connected ? Unconfirmed(request, action) : NoInstance(request, action);
        }
        catch (OperationCanceledException)
        {
            return connected ? Unconfirmed(request, action) : NoInstance(request, action);
        }
        catch (IOException)
        {
            return connected ? Unconfirmed(request, action) : NoInstance(request, action);
        }
        catch (UnauthorizedAccessException)
        {
            return connected ? Unconfirmed(request, action) : NoInstance(request, action);
        }
    }

    /// <summary>
    /// Post-connect failure: the answer is lost, so the CLI reports "timed out / may already have executed" (exit
    /// code 6) instead of "no instance" (exit code 4) and points the caller at <c>status</c> for confirmation.
    /// </summary>
    private static ToolkitCommandIpcResponse Unconfirmed(ToolkitCommandIpcRequest request,
        ToolkitCommandAction action) =>
        ToolkitCommandIpcProtocol.ConnectionLostAfterSend(request.RequestId ?? string.Empty, action);

    private static ToolkitCommandIpcResponse NoInstance(ToolkitCommandIpcRequest request,
        ToolkitCommandAction action) =>
        ToolkitCommandIpcProtocol.Unavailable(request.RequestId ?? string.Empty, action,
            "没有找到正在运行的主实例（MiniProToolkit 未启动，或命令通道未就绪），命令未执行。");

    private static ToolkitCommandAction ParseActionToken(string? token) => (token ?? string.Empty).ToLowerInvariant() switch
    {
        "status" => ToolkitCommandAction.Status,
        "apply-profile" => ToolkitCommandAction.ApplyProfile,
        "set-mode" => ToolkitCommandAction.SetMode,
        "restore-automatic" => ToolkitCommandAction.RestoreAutomatic,
        // UI-only: a second launch uses this to raise the window of the running instance.
        ToolkitCommandIpcProtocol.ActivateActionToken => ToolkitCommandAction.Activate,
        _ => ToolkitCommandAction.None
    };
}
