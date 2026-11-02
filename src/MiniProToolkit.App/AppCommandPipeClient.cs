using System.IO;
using System.IO.Pipes;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

/// <summary>
/// CLI side of the command pipe. It only sends one already-parsed command to the running main instance and returns
/// that instance's answer: it never reads the EC, never opens a Guardian lease, never applies a profile and never
/// starts a second hardware-controlling window. Every failure is reported as a response the host can turn into a
/// dedicated exit code, so nothing here can look like success by accident. The failure side is split by connection
/// state, because the cases are not interchangeable: before the connect succeeds nothing was sent, after it the
/// request is on the wire and a lost answer means "may already have executed" ("timed out", exit code 6, confirm with
/// <c>status</c>).
/// <para>
/// The pre-connect case is split once more, by what the instance lock proves. When the caller knows another process
/// holds the lock (<c>instanceKnownByLock</c>), a connect timeout or an I/O failure must not claim "MiniProToolkit is
/// not started": the instance exists but is busy, so the answer is the "主实例忙或暂时无法连接，命令未发送" timeout
/// (exit code 6, and explicitly "not sent" because no byte left this process). Without that evidence the answer stays
/// the "no instance" one (exit code 4). A peer that fails the server-identity check is always a refusal (exit code 4)
/// and is never re-labelled "busy".
/// </para>
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
    public static ToolkitCommandIpcResponse Send(ToolkitCommandIpcRequest request, TimeSpan? responseTimeout = null,
        bool instanceKnownByLock = false) =>
        SendAsync(request, responseTimeout, instanceKnownByLock).GetAwaiter().GetResult();

    /// <param name="instanceKnownByLock">
    /// True only when the caller already proved that another process owns the toolkit's single-instance lock. It
    /// changes nothing after a successful connect; it only replaces "no instance" with "busy or temporarily
    /// unreachable, nothing sent" (exit code 6) for a pre-connect timeout/IO failure. The default <c>false</c> keeps the
    /// existing behaviour for every other caller (for example the window activation of a second plain launch).
    /// </param>
    public static async Task<ToolkitCommandIpcResponse> SendAsync(ToolkitCommandIpcRequest request,
        TimeSpan? responseTimeout = null, bool instanceKnownByLock = false)
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
            // is pre-dispatch, so a rejection still means no action was sent - and a wrong server is a refusal, never
            // the "the real instance is busy" answer, whatever the instance lock says.
            if (!NativeMethods.IsExpectedCommandPipeServer(client.SafePipeHandle))
                return PreDispatchFailure(request, action, instanceKnownByLock,
                    ToolkitCommandConnectFailure.UntrustedServer);

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
            return connected ? Unconfirmed(request, action) :
                PreDispatchFailure(request, action, instanceKnownByLock);
        }
        catch (OperationCanceledException)
        {
            return connected ? Unconfirmed(request, action) :
                PreDispatchFailure(request, action, instanceKnownByLock);
        }
        catch (IOException)
        {
            return connected ? Unconfirmed(request, action) :
                PreDispatchFailure(request, action, instanceKnownByLock);
        }
        catch (UnauthorizedAccessException)
        {
            return connected ? Unconfirmed(request, action) :
                PreDispatchFailure(request, action, instanceKnownByLock);
        }
    }

    /// <summary>
    /// Post-connect failure: the answer is lost, so the CLI reports "timed out / may already have executed" (exit
    /// code 6) instead of "no instance" (exit code 4) and points the caller at <c>status</c> for confirmation.
    /// </summary>
    private static ToolkitCommandIpcResponse Unconfirmed(ToolkitCommandIpcRequest request,
        ToolkitCommandAction action) =>
        ToolkitCommandIpcProtocol.ConnectionLostAfterSend(request.RequestId ?? string.Empty, action);

    /// <summary>
    /// Pre-dispatch failure (nothing was sent), classified by the caller's lock evidence: known-lock and unreachable
    /// means the existing instance is busy/unready (exit code 6, "not sent"), otherwise "no instance" (exit code 4).
    /// </summary>
    private static ToolkitCommandIpcResponse PreDispatchFailure(ToolkitCommandIpcRequest request,
        ToolkitCommandAction action, bool instanceKnownByLock,
        ToolkitCommandConnectFailure failure = ToolkitCommandConnectFailure.Unreachable) =>
        ToolkitCommandIpcProtocol.ClassifyPreDispatchFailure(request.RequestId ?? string.Empty, action,
            instanceKnownByLock, failure);

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
