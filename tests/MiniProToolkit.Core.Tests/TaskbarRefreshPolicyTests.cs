using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// The taskbar notification decisions and the refresh bookkeeping are pure state, so the
/// "shell restart re-mounts / display change only repositions" split, the unregistered-token
/// guard and the one-refresh-per-interval rule are pinned here without a desktop session.
/// </summary>
public sealed class TaskbarRefreshPolicyTests
{
    // A plausible dynamically allocated RegisterWindowMessage token (0xC000..0xFFFF range).
    private const int TaskbarCreatedToken = 0xC123;

    [Fact]
    public void RegisteredTaskbarCreatedTokenIsAReMount()
    {
        var trigger = TaskbarRefreshPolicy.ClassifyWindowMessage(
            TaskbarCreatedToken, TaskbarCreatedToken);
        Assert.Equal(TaskbarRefreshTrigger.ShellRecreated, trigger);
        Assert.True(TaskbarRefreshPolicy.RemountsTaskbarWindows(trigger!.Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0x0010)] // WM_CLOSE
    public void FailedRegistrationNeverMatchesEvenForZeroOrForeignMessages(int message)
    {
        Assert.False(TaskbarRefreshPolicy.IsTaskbarCreatedMessage(message, 0));
        Assert.Null(TaskbarRefreshPolicy.ClassifyWindowMessage(message, 0));
    }

    [Theory]
    [InlineData(TaskbarRefreshPolicy.WmDisplayChange, TaskbarRefreshTrigger.DisplayChanged)]
    [InlineData(TaskbarRefreshPolicy.WmDpiChanged, TaskbarRefreshTrigger.DpiChanged)]
    [InlineData(TaskbarRefreshPolicy.WmSettingChange, TaskbarRefreshTrigger.TaskbarSettingChanged)]
    public void DisplayAndSettingMessagesOnlyRepositionExistingWindows(
        int message, TaskbarRefreshTrigger expected)
    {
        var trigger = TaskbarRefreshPolicy.ClassifyWindowMessage(message, TaskbarCreatedToken);
        Assert.Equal(expected, trigger);
        Assert.False(TaskbarRefreshPolicy.RemountsTaskbarWindows(trigger!.Value));
        Assert.True(TaskbarRefreshPolicy.BypassesRefreshInterval(trigger.Value));
    }

    [Fact]
    public void UnrelatedMessagesAreIgnored()
    {
        Assert.Null(TaskbarRefreshPolicy.ClassifyWindowMessage(0x0312, TaskbarCreatedToken)); // WM_HOTKEY
        Assert.Null(TaskbarRefreshPolicy.ClassifyWindowMessage(0x0002, TaskbarCreatedToken)); // WM_DESTROY
    }

    [Fact]
    public void QueuedRefreshIsCoalescedUntilCompleted()
    {
        var scheduler = new TaskbarRefreshScheduler();
        Assert.True(scheduler.TryQueue());
        Assert.True(scheduler.IsQueued);
        Assert.False(scheduler.TryQueue());
        scheduler.Complete();
        Assert.False(scheduler.IsQueued);
        Assert.True(scheduler.TryQueue());
    }

    [Fact]
    public void PeriodicGateAllowsOnlyOneRefreshPerInterval()
    {
        var scheduler = new TaskbarRefreshScheduler();
        var interval = TimeSpan.FromSeconds(10);
        // 1 kHz monotonic clock, exactly what Stopwatch reports on Windows.
        Assert.True(scheduler.TryAcquireInterval(1_000, 1_000, interval));
        Assert.False(scheduler.TryAcquireInterval(5_000, 1_000, interval));
        Assert.False(scheduler.TryAcquireInterval(10_999, 1_000, interval));
        Assert.True(scheduler.TryAcquireInterval(11_000, 1_000, interval));
    }

    [Fact]
    public void EventDrivenRefreshHoldsOffTheNextPeriodicPass()
    {
        var scheduler = new TaskbarRefreshScheduler();
        var interval = TimeSpan.FromSeconds(10);
        Assert.True(scheduler.TryAcquireInterval(1_000, 1_000, interval));
        // A display change repositions immediately and records the slot.
        scheduler.MarkRefreshed(2_000);
        Assert.False(scheduler.TryAcquireInterval(6_000, 1_000, interval));
        Assert.True(scheduler.TryAcquireInterval(12_000, 1_000, interval));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ZeroOrNegativeIntervalDisablesTheGate(int seconds)
    {
        var scheduler = new TaskbarRefreshScheduler();
        var interval = TimeSpan.FromSeconds(seconds);
        Assert.True(scheduler.TryAcquireInterval(1_000, 1_000, interval));
        Assert.True(scheduler.TryAcquireInterval(1_000, 1_000, interval));
    }

    [Fact]
    public void ResetIntervalForcesTheNextPeriodicPassToRun()
    {
        var scheduler = new TaskbarRefreshScheduler();
        var interval = TimeSpan.FromSeconds(10);
        Assert.True(scheduler.TryAcquireInterval(1_000, 1_000, interval));
        Assert.False(scheduler.TryAcquireInterval(1_100, 1_000, interval));
        scheduler.ResetInterval();
        Assert.True(scheduler.TryAcquireInterval(1_100, 1_000, interval));
    }

    [Fact]
    public void BackwardsTimestampDoesNotFreezeTheGate()
    {
        var scheduler = new TaskbarRefreshScheduler();
        var interval = TimeSpan.FromSeconds(10);
        Assert.True(scheduler.TryAcquireInterval(9_000, 1_000, interval));
        // A clock that moved backwards is treated as elapsed instead of blocking forever.
        Assert.True(scheduler.TryAcquireInterval(500, 1_000, interval));
    }
}
