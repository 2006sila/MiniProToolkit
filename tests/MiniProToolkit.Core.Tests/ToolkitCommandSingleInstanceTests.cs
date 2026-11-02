using System.Text;
using System.Text.Json;
using System.Threading;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Single-instance behaviour of the toolkit: the process lock that decides "first instance vs. second launch" and the
/// UI-only activation frame a second launch sends to the running instance. Nothing here starts the app, opens a window,
/// opens a pipe, touches the EC or opens a Guardian lease; the lock tests only use throw-away, per-test mutex names.
/// </summary>
public sealed class ToolkitCommandSingleInstanceTests
{
    private static string UniqueMutexName() =>
        @"Local\MiniProToolkit.Instance.Tests." + Guid.NewGuid().ToString("N");

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    private static T RunOnAnotherThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the helper thread did not finish");
        if (failure is not null)
            throw failure;
        return result;
    }

    [Fact]
    public void InstanceLockNameIsFixedMachineWideAndPathFree()
    {
        Assert.Equal(@"Global\MiniProToolkit.Instance.v1", ToolkitSingleInstanceLock.MutexName);
        // Global\ (not Local\) is the machine-wide boundary: another logon session or another user on the same
        // machine must see the lock as held instead of starting a second hardware-controlling instance.
        Assert.StartsWith(@"Global\", ToolkitSingleInstanceLock.MutexName);
        Assert.DoesNotContain(@"Local\", ToolkitSingleInstanceLock.MutexName);
        Assert.DoesNotContain("\\Users\\", ToolkitSingleInstanceLock.MutexName);
        Assert.DoesNotContain(":", ToolkitSingleInstanceLock.MutexName);
        // Fixed name: no user or session discriminator may sneak in (that would make it per user again).
        Assert.DoesNotContain(Environment.UserName, ToolkitSingleInstanceLock.MutexName);
    }

    /// <summary>
    /// The lock must work in the machine-wide namespace for the real app (its manifest requires administrator, so it
    /// holds SeCreateGlobalPrivilege). A non-elevated host cannot create it; that case has to fail closed as
    /// <see cref="ToolkitSingleInstanceState.Unavailable"/> (exit code 7) instead of pretending the lock was free.
    /// </summary>
    [Fact]
    public void GlobalNamespaceLockIsEitherAcquiredOrFailsClosed()
    {
        var name = @"Global\MiniProToolkit.Instance.Tests." + Guid.NewGuid().ToString("N");
        using var first = ToolkitSingleInstanceLock.TryAcquire(name);

        switch (first.State)
        {
            case ToolkitSingleInstanceState.Acquired:
                Assert.True(first.Acquired);
                var secondState = RunOnAnotherThread(() =>
                {
                    using var second = ToolkitSingleInstanceLock.TryAcquire(name);
                    return second.State;
                });
                Assert.Equal(ToolkitSingleInstanceState.AlreadyHeld, secondState);
                break;
            case ToolkitSingleInstanceState.Unavailable:
                // Fail closed: nothing is started on a guess, and the host turns this into exit code 7.
                Assert.False(first.Acquired);
                Assert.Equal(nameof(UnauthorizedAccessException), first.FailureCode);
                Assert.Equal(7, ToolkitCommandExitCodes.InstanceLockUnavailable);
                break;
            default:
                Assert.Fail("A fresh Global\\ lock name cannot be held by another process: " + first.State);
                break;
        }
    }

    /// <summary>
    /// A second user (or a second logon session) sees the machine-wide lock as held, but the command pipe is
    /// per-user, so it cannot reach the first user's server. That launch therefore ends with 8 (or 7 when the lock
    /// itself cannot be created) and never creates a second window or hardware owner.
    /// </summary>
    [Fact]
    public void AnotherUserCannotActivateTheRunningInstanceAndMustExitWithTheDedicatedCodes()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var answer = ToolkitCommandIpcProtocol.Unavailable(id, ToolkitCommandAction.Activate,
            "没有找到正在运行的主实例（MiniProToolkit 未启动，或命令通道未就绪），命令未执行。");
        Assert.NotEqual(ToolkitCommandIpcStatus.Ok, answer.Status);
        Assert.False(answer.Executed);

        // The activation path of the host maps everything except Ok onto 8; the lock failure stays 7.
        Assert.Equal(8, ToolkitCommandExitCodes.AlreadyRunning);
        Assert.Equal(7, ToolkitCommandExitCodes.InstanceLockUnavailable);
        Assert.NotEqual(ToolkitCommandExitCodes.Success, ToolkitCommandExitCodes.AlreadyRunning);

        // Different users get different pipe names, so the second user cannot address the first user's server.
        Assert.NotEqual(ToolkitCommandIpcProtocol.PipeNameFor(@"CONTOSO\alice"),
            ToolkitCommandIpcProtocol.PipeNameFor(@"CONTOSO\bob"));
    }

    [Fact]
    public void FirstProcessKeepsTheLockAndASecondProcessOnlySeeksItAsHeld()
    {
        var name = UniqueMutexName();
        using var first = ToolkitSingleInstanceLock.TryAcquire(name);
        Assert.Equal(ToolkitSingleInstanceState.Acquired, first.State);
        Assert.True(first.Acquired);
        Assert.False(first.RecoveredAbandoned);
        Assert.Null(first.FailureCode);

        // A mutex is re-entrant for its owning thread, so exclusivity can only be observed from another thread - which
        // is exactly the situation of a second process.
        var secondState = RunOnAnotherThread(() =>
        {
            using var second = ToolkitSingleInstanceLock.TryAcquire(name);
            // Nothing may be created from a "second instance" lock; releasing it must stay a no-op.
            return second.State;
        });
        Assert.Equal(ToolkitSingleInstanceState.AlreadyHeld, secondState);

        // The lock is only usable again once the owner really dropped it (i.e. on a real exit).
        first.Dispose();
        first.Dispose();
        var thirdState = RunOnAnotherThread(() =>
        {
            using var third = ToolkitSingleInstanceLock.TryAcquire(name);
            return third.State;
        });
        Assert.Equal(ToolkitSingleInstanceState.Acquired, thirdState);
    }

    [Fact]
    public void AbandonedLockIsTakenOverInsteadOfBeingTreatedAsARunningInstance()
    {
        var name = UniqueMutexName();
        using var ownerReady = new ManualResetEventSlim(false);
        using var ownerMayExit = new ManualResetEventSlim(false);
        var owner = new Thread(() =>
        {
            // Owns the mutex and then dies without releasing it: the OS marks it abandoned.
            var mutex = new Mutex(initiallyOwned: true, name);
            GC.KeepAlive(mutex);
            ownerReady.Set();
            ownerMayExit.Wait(TimeSpan.FromSeconds(10));
        })
        {
            IsBackground = true
        };
        owner.Start();
        Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(10)));
        ownerMayExit.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(10)));

        using var recovered = ToolkitSingleInstanceLock.TryAcquire(name);
        Assert.Equal(ToolkitSingleInstanceState.Acquired, recovered.State);
        Assert.True(recovered.Acquired);
        Assert.True(recovered.RecoveredAbandoned);
    }

    [Fact]
    public void ActivateRequestIsAcceptedWithoutAnyValueOrConfirmation()
    {
        var request = ToolkitCommandIpcProtocol.CreateActivateRequest();
        Assert.Equal(ToolkitCommandIpcProtocol.Version, request.Protocol);
        Assert.Equal(ToolkitCommandIpcProtocol.ActivateActionToken, request.Action);
        Assert.Null(request.ProfileName);
        Assert.Null(request.Mode);
        Assert.False(request.Confirmed);
        Assert.Equal(32, request.RequestId!.Length);

        var parsed = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(request));
        Assert.True(parsed.Success);
        Assert.Equal(ToolkitCommandAction.Activate, parsed.Command!.Action);
        Assert.Equal(request.RequestId, parsed.RequestId);
        Assert.Null(parsed.Command.ProfileName);
        Assert.Null(parsed.Command.Mode);
        Assert.False(parsed.Command.Confirmed);
        // UI only: it must never require a confirmation gate and never be routed as a hardware write.
        Assert.False(parsed.Command.RequiresConfirmation);
        Assert.Equal("activate", parsed.Command.SafeSummary);
        Assert.Equal("activate", ToolkitCommandIpcProtocol.ActionToken(ToolkitCommandAction.Activate));
    }

    [Theory]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"activate\",\"confirmed\":true}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"activate\",\"profileName\":\"Gaming\"}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"activate\",\"mode\":\"performance\"}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"activate\",\"confirmed\":false,\"mode\":\"balance\"}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"activate\",\"method\":\"SetFanDuty\"}")]
    public void ActivateRejectsAnyExtraPayload(string payload)
    {
        var parsed = ToolkitCommandIpcProtocol.TryParseRequest(Utf8(payload));
        Assert.False(parsed.Success);
        Assert.Equal(ToolkitCommandIpcStatus.Rejected, parsed.Status);
        Assert.Null(parsed.Command);
        // A rejection never echoes the submitted value.
        Assert.DoesNotContain("Gaming", parsed.Message);
        Assert.DoesNotContain("performance", parsed.Message);
        Assert.DoesNotContain("SetFanDuty", parsed.Message);
    }

    [Fact]
    public void ActivateIsNotAScriptableCommandLineAction()
    {
        // The CLI surface stays exactly the five documented actions: no script may request an activation.
        Assert.DoesNotContain(ToolkitCommandIpcProtocol.ActivateActionToken, ToolkitCommandLineParser.SupportedActions);
        var parsed = ToolkitCommandLineParser.Parse(["activate"]);
        Assert.False(parsed.Success);
        Assert.Equal(ToolkitCommandErrorCode.UnknownAction, parsed.Error!.Code);
        Assert.Null(parsed.Command);
    }

    [Fact]
    public void ActivateResponseIsAValidProtocolAnswer()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var payload = ToolkitCommandIpcProtocol.SerializeResponse(
            ToolkitCommandIpcProtocol.Ok(id, ToolkitCommandAction.Activate, "已切换到运行中的主窗口。"));
        var parsed = ToolkitCommandIpcProtocol.TryParseResponse(payload, id);

        Assert.NotNull(parsed);
        Assert.Equal(ToolkitCommandIpcStatus.Ok, parsed!.Status);
        Assert.Equal("activate", parsed.Action);
        Assert.True(parsed.Executed);
        Assert.Null(parsed.Report);

        // Log-safe summary of the second-instance path: action, status, executed. No argument text is ever included.
        var line = ToolkitCommandIpcProtocol.DescribeResponse(parsed);
        Assert.Contains("action=activate", line);
        Assert.Contains("status=Ok", line);
    }

    [Fact]
    public void SecondInstanceExitCodesAreDedicatedAndNonZero()
    {
        Assert.Equal(7, ToolkitCommandExitCodes.InstanceLockUnavailable);
        Assert.Equal(8, ToolkitCommandExitCodes.AlreadyRunning);
        Assert.NotEqual(ToolkitCommandExitCodes.Success, ToolkitCommandExitCodes.InstanceLockUnavailable);
        Assert.NotEqual(ToolkitCommandExitCodes.NoRunningInstance, ToolkitCommandExitCodes.InstanceLockUnavailable);
        Assert.NotEqual(ToolkitCommandExitCodes.NoRunningInstance, ToolkitCommandExitCodes.AlreadyRunning);
        Assert.NotEqual(ToolkitCommandExitCodes.CommandRejected, ToolkitCommandExitCodes.AlreadyRunning);
        // The usage text documents the new codes for scripts.
        Assert.Contains("7 ", ToolkitCommandLineParser.Usage);
        Assert.Contains("8 ", ToolkitCommandLineParser.Usage);
    }

    [Fact]
    public void PlainLaunchStillAsksTheHostToOpenTheWindowAndNotToRelay()
    {
        // The host decides first-instance vs. second launch through the lock, so the outcome itself must stay "open
        // the main window" for a plain launch - it must not be turned into a relay/activation command line.
        foreach (var arguments in new[] { Array.Empty<string>(), new[] { "--startup" }, new[] { "--demo" } })
        {
            var outcome = ToolkitCommandOutcome.From(arguments);
            Assert.True(outcome.OpenMainWindow);
            Assert.False(outcome.RelayCommand);
            Assert.Equal(ToolkitCommandExitCodes.Success, outcome.ExitCode);
        }
    }

    [Fact]
    public void ActivateJsonUsesCamelCaseMembersOnly()
    {
        var json = Encoding.UTF8.GetString(
            ToolkitCommandIpcProtocol.SerializeRequest(ToolkitCommandIpcProtocol.CreateActivateRequest()));
        using var document = JsonDocument.Parse(json);
        var members = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Contains("action", members);
        Assert.Contains("protocol", members);
        Assert.Contains("requestId", members);
        // No payload field is set: the value fields are present but explicitly null, never copied from an argument.
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("profileName").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("mode").ValueKind);
        Assert.Equal("activate", document.RootElement.GetProperty("action").GetString());
        Assert.False(document.RootElement.GetProperty("confirmed").GetBoolean());
    }
}
