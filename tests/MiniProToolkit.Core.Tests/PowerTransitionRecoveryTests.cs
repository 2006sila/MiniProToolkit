using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Logic tests for the sleep/resume decision table and the resume refresh coalescer.
/// They exercise the pure rules only: no Win32 power notification, no WMI, no EC and
/// no UI thread is involved.
/// </summary>
public sealed class PowerTransitionRecoveryTests
{
    [Fact]
    public void ResumeIsTheOnlyTransitionThatTriggersARefresh()
    {
        Assert.Equal(PowerTransitionAction.RefreshAfterResume,
            PowerTransitionPolicy.Decide(PowerTransitionKind.Resume));

        Assert.NotEqual(PowerTransitionAction.RefreshAfterResume,
            PowerTransitionPolicy.Decide(PowerTransitionKind.Suspend));
        Assert.NotEqual(PowerTransitionAction.RefreshAfterResume,
            PowerTransitionPolicy.Decide(PowerTransitionKind.StatusChange));
        Assert.NotEqual(PowerTransitionAction.RefreshAfterResume,
            PowerTransitionPolicy.Decide(PowerTransitionKind.Unknown));
    }

    [Fact]
    public void SuspendIsRecordOnlyAndNeverRefreshes()
    {
        Assert.Equal(PowerTransitionAction.RecordSuspendOnly,
            PowerTransitionPolicy.Decide(PowerTransitionKind.Suspend));
    }

    [Fact]
    public void StatusChangeAndUnknownAreIgnored()
    {
        Assert.Equal(PowerTransitionAction.Ignore, PowerTransitionPolicy.Decide(PowerTransitionKind.StatusChange));
        Assert.Equal(PowerTransitionAction.Ignore, PowerTransitionPolicy.Decide(PowerTransitionKind.Unknown));
    }

    [Fact]
    public void CoalescerAllowsOnlyOneQueuedRefresh()
    {
        var coalescer = new ResumeRefreshCoalescer();

        Assert.False(coalescer.IsPending);
        Assert.True(coalescer.TrySchedule());
        Assert.True(coalescer.IsPending);
        Assert.False(coalescer.TrySchedule());
        Assert.False(coalescer.TrySchedule());
        Assert.True(coalescer.IsPending);
    }

    [Fact]
    public void CompletingTheRefreshAllowsTheNextResumeToSchedule()
    {
        var coalescer = new ResumeRefreshCoalescer();

        Assert.True(coalescer.TrySchedule());
        coalescer.Complete();

        Assert.False(coalescer.IsPending);
        Assert.True(coalescer.TrySchedule());
    }

    [Fact]
    public void CoalescerStaysConsistentUnderConcurrentResumeNotifications()
    {
        var coalescer = new ResumeRefreshCoalescer();
        var scheduled = 0;

        Parallel.For(0, 32, _ =>
        {
            if (coalescer.TrySchedule())
                Interlocked.Increment(ref scheduled);
        });

        Assert.Equal(1, scheduled);
    }
}
