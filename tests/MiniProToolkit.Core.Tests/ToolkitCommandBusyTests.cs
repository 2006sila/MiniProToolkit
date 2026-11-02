using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Pure tests for the "pre-dispatch" side of the CLI relay: what a connect failure means when the caller already knows
/// that another process owns the instance lock. Nothing here opens a pipe, starts the app, reads the EC or opens a
/// Guardian lease - only the classification helper, its wording and the exit-code mapping are exercised.
/// </summary>
public sealed class ToolkitCommandBusyTests
{
    private static readonly string BusyPhrase = "主实例忙或暂时无法连接，命令未发送";

    /// <summary>
    /// Regression guard for the reported bug: with the lock held by the running instance, a connect timeout/IO failure
    /// used to say "MiniProToolkit 未启动" (exit code 4). The instance exists, so it has to be reported as busy and
    /// unreachable, with an exit code that is not "no instance".
    /// </summary>
    [Theory]
    [InlineData(ToolkitCommandAction.Status)]
    [InlineData(ToolkitCommandAction.ApplyProfile)]
    [InlineData(ToolkitCommandAction.SetMode)]
    [InlineData(ToolkitCommandAction.RestoreAutomatic)]
    public void KnownLockPreConnectFailureIsBusyAndNotNoInstance(ToolkitCommandAction action)
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var response = ToolkitCommandIpcProtocol.ClassifyPreDispatchFailure(id, action,
            instanceKnownByLock: true, ToolkitCommandConnectFailure.Unreachable);

        Assert.Equal(ToolkitCommandIpcStatus.TimedOut, response.Status);
        Assert.NotEqual(ToolkitCommandIpcStatus.Unavailable, response.Status);
        Assert.NotEqual(ToolkitCommandIpcStatus.Ok, response.Status);
        Assert.False(response.Executed);
        Assert.Equal(id, response.RequestId);
        Assert.Equal(ToolkitCommandIpcProtocol.ActionToken(action), response.Action);
        Assert.Equal(ToolkitCommandIpcProtocol.Version, response.Protocol);

        // Wording: busy/unreachable and explicitly "not sent" - never "not started" and never "may already have run".
        Assert.Contains(BusyPhrase, response.Message);
        Assert.DoesNotContain("未启动", response.Message);
        Assert.DoesNotContain("没有找到正在运行的主实例", response.Message);
        Assert.DoesNotContain("可能已经执行", response.Message);
        Assert.True(response.Message.Length <= ToolkitCommandIpcProtocol.MaxMessageChars);

        // Exit code: 6 (busy/unreachable), never the "no instance" 4 and never 0.
        var exitCode = ToolkitCommandExitCodes.ForIpcStatus(response.Status);
        Assert.Equal(6, exitCode);
        Assert.Equal(ToolkitCommandExitCodes.CommandTimedOut, exitCode);
        Assert.NotEqual(ToolkitCommandExitCodes.NoRunningInstance, exitCode);
        Assert.NotEqual(ToolkitCommandExitCodes.Success, exitCode);
    }

    [Fact]
    public void KnownLockPreConnectFailureSurvivesTheWireWithStrictRequestIdMatching()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var payload = ToolkitCommandIpcProtocol.SerializeResponse(
            ToolkitCommandIpcProtocol.ClassifyPreDispatchFailure(id, ToolkitCommandAction.ApplyProfile, true));

        var parsed = ToolkitCommandIpcProtocol.TryParseResponse(payload, id);
        Assert.NotNull(parsed);
        Assert.Equal(ToolkitCommandIpcStatus.TimedOut, parsed!.Status);
        Assert.False(parsed.Executed);
        Assert.Contains(BusyPhrase, parsed.Message);
        Assert.Null(ToolkitCommandIpcProtocol.TryParseResponse(payload, ToolkitCommandIpcProtocol.NewRequestId()));
    }

    /// <summary>
    /// Without lock evidence the old contract stays exactly as it was: exit code 4 and the "no instance" text, because
    /// this process has no proof that anything is running.
    /// </summary>
    [Fact]
    public void UnknownLockPreConnectFailureStaysNoInstance()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var explicitUnknown = ToolkitCommandIpcProtocol.ClassifyPreDispatchFailure(id,
            ToolkitCommandAction.Status, instanceKnownByLock: false, ToolkitCommandConnectFailure.Unreachable);
        // The default is "unknown" so every caller that does not pass the lock evidence keeps the old behaviour.
        var defaulted = ToolkitCommandIpcProtocol.ClassifyPreDispatchFailure(id, ToolkitCommandAction.Status);

        foreach (var response in new[] { explicitUnknown, defaulted })
        {
            Assert.Equal(ToolkitCommandIpcStatus.Unavailable, response.Status);
            Assert.False(response.Executed);
            Assert.Contains("没有找到正在运行的主实例", response.Message);
            Assert.DoesNotContain(BusyPhrase, response.Message);
            Assert.Equal(ToolkitCommandExitCodes.NoRunningInstance,
                ToolkitCommandExitCodes.ForIpcStatus(response.Status));
            Assert.NotEqual(6, ToolkitCommandExitCodes.ForIpcStatus(response.Status));
        }
    }

    /// <summary>
    /// A pipe peer that fails the server-identity check is a refusal. Lock evidence must not turn a wrong server into
    /// "the real instance is busy": that would hide an impostor behind a load message.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UntrustedServerIsRefusedAndNeverCalledBusy(bool instanceKnownByLock)
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var response = ToolkitCommandIpcProtocol.ClassifyPreDispatchFailure(id,
            ToolkitCommandAction.ApplyProfile, instanceKnownByLock, ToolkitCommandConnectFailure.UntrustedServer);

        Assert.Equal(ToolkitCommandIpcStatus.Unavailable, response.Status);
        Assert.False(response.Executed);
        Assert.DoesNotContain(BusyPhrase, response.Message);
        Assert.Contains("没有找到正在运行的主实例", response.Message);
        Assert.Equal(ToolkitCommandExitCodes.NoRunningInstance,
            ToolkitCommandExitCodes.ForIpcStatus(response.Status));
    }

    /// <summary>
    /// After the connect succeeded the request is on the wire, so a lost answer must keep the existing "state unknown,
    /// do not resend blindly" answer - that case is not touched by the busy classification.
    /// </summary>
    [Fact]
    public void ConnectedTimeoutStillMeansStateUnknownAndIsNotTheBusyWording()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var lost = ToolkitCommandIpcProtocol.ConnectionLostAfterSend(id, ToolkitCommandAction.SetMode);

        Assert.Equal(ToolkitCommandIpcStatus.TimedOut, lost.Status);
        Assert.False(lost.Executed);
        Assert.Contains("可能已经执行", lost.Message);
        Assert.Contains("status", lost.Message);
        Assert.Equal(ToolkitCommandExitCodes.CommandTimedOut, ToolkitCommandExitCodes.ForIpcStatus(lost.Status));

        // Same exit code, different contract: "not sent" (retry safe) vs "may have run" (confirm with status first).
        var busy = ToolkitCommandIpcProtocol.ClassifyPreDispatchFailure(id, ToolkitCommandAction.SetMode, true);
        Assert.NotEqual(lost.Message, busy.Message);
        Assert.DoesNotContain("可能已经执行", busy.Message);
        Assert.DoesNotContain("命令未发送", lost.Message);
    }

    [Fact]
    public void ExitCodeMappingMatchTheDocumentedContract()
    {
        Assert.Equal(0, ToolkitCommandExitCodes.ForIpcStatus(ToolkitCommandIpcStatus.Ok));
        Assert.Equal(5, ToolkitCommandExitCodes.ForIpcStatus(ToolkitCommandIpcStatus.Rejected));
        Assert.Equal(5, ToolkitCommandExitCodes.ForIpcStatus(ToolkitCommandIpcStatus.Failed));
        Assert.Equal(4, ToolkitCommandExitCodes.ForIpcStatus(ToolkitCommandIpcStatus.Unavailable));
        Assert.Equal(6, ToolkitCommandExitCodes.ForIpcStatus(ToolkitCommandIpcStatus.TimedOut));
        Assert.Equal(6, ToolkitCommandExitCodes.CommandTimedOut);
        Assert.Equal(4, ToolkitCommandExitCodes.NoRunningInstance);
    }
}
