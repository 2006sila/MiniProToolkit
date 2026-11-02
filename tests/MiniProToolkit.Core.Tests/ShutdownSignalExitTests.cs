using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Exit request sequencing: the reported defect was that the single-shot shutdown wait was never
/// re-armed after a failed exit, so a second stop request from the installer/uninstaller was
/// ignored, and that the signal path still opened a modal dialog.
/// </summary>
public class ShutdownSignalExitTests
{
    [Fact]
    public void SecondShutdownSignalIsAcceptedAfterFailedExitWasRearmed()
    {
        var gate = new ExitRequestGate();

        Assert.True(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));
        // Failed (reversible) exit: the session resumed, so the next stop request must be taken.
        // Nothing raced the failed attempt, hence no retry is pending.
        Assert.False(gate.Rearm());
        Assert.True(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));
        Assert.Equal(ExitRequestSource.ShutdownSignal, gate.Source);
        Assert.True(gate.IsExitInFlight);
    }

    [Fact]
    public void RearmResetsSourceSoAUserExitKeepsItsDialog()
    {
        var gate = new ExitRequestGate();
        Assert.True(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));
        gate.Rearm();

        Assert.Null(gate.Source);
        Assert.False(gate.IsExitInFlight);
        Assert.False(gate.ShutdownSignalSeen);

        Assert.True(gate.TryBeginExit(ExitRequestSource.User));
        Assert.Equal(ExitFailureNotice.ShowDialog, gate.DecideFailureNotice());
    }

    [Fact]
    public void ConcurrentRequestsEnterOnlyOnce()
    {
        var gate = new ExitRequestGate();
        var accepted = 0;
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 16).Select(index => new Thread(() =>
        {
            start.Wait();
            var source = index % 2 == 0 ? ExitRequestSource.User : ExitRequestSource.ShutdownSignal;
            if (gate.TryBeginExit(source))
                Interlocked.Increment(ref accepted);
        })).ToList();

        foreach (var thread in threads)
            thread.Start();
        start.Set();
        foreach (var thread in threads)
            thread.Join();

        // A single exit sequence: no nested Close/OnClosing, no second teardown.
        Assert.Equal(1, accepted);
        Assert.True(gate.IsExitInFlight);
    }

    [Fact]
    public void ShutdownSignalDuringRunningExitIsRememberedForOneRetry()
    {
        var gate = new ExitRequestGate();
        Assert.True(gate.TryBeginExit(ExitRequestSource.User));
        // Installer stop while the manual exit runs: refused, but not lost.
        Assert.False(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));
        Assert.True(gate.ShutdownSignalSeen);
        Assert.Equal(ExitFailureNotice.LogOnly, gate.DecideFailureNotice());

        Assert.True(gate.Rearm());
        Assert.True(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));
        // The retry is one-shot: a second failure does not loop.
        Assert.False(gate.Rearm());
    }

    [Fact]
    public void SignalDrivenFailureLogsInsteadOfShowingADialog()
    {
        var gate = new ExitRequestGate();
        Assert.True(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));

        Assert.Equal(ExitFailureNotice.LogOnly, gate.DecideFailureNotice());
        Assert.Equal(ExitFailureNotice.LogOnly,
            ExitRequestGate.DecideFailureNotice(ExitRequestSource.ShutdownSignal, shutdownSignalSeen: false));
        // A manual exit keeps its prompt.
        Assert.Equal(ExitFailureNotice.ShowDialog,
            ExitRequestGate.DecideFailureNotice(ExitRequestSource.User, shutdownSignalSeen: false));
        // ...unless an installer is waiting on the event.
        Assert.Equal(ExitFailureNotice.LogOnly,
            ExitRequestGate.DecideFailureNotice(ExitRequestSource.User, shutdownSignalSeen: true));
    }

    [Fact]
    public void TeardownRefusesFurtherRequestsAndNeverRearms()
    {
        var gate = new ExitRequestGate();
        Assert.True(gate.TryBeginExit(ExitRequestSource.User));
        gate.BeginTeardown();

        Assert.True(gate.IsTeardownStarted);
        Assert.False(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));
        Assert.False(gate.TryBeginExit(ExitRequestSource.User));
        // Hardware/UI are gone: no resume, no queued retry. The owning request keeps the gate, so
        // nothing can claim it again while the process finishes closing.
        Assert.False(gate.Rearm());
        Assert.True(gate.IsExitInFlight);
        Assert.Equal(ExitRequestSource.User, gate.Source);
    }

    [Fact]
    public void TeardownDropsAPendingSignalRetry()
    {
        var gate = new ExitRequestGate();
        Assert.True(gate.TryBeginExit(ExitRequestSource.User));
        Assert.False(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));
        gate.BeginTeardown();

        Assert.False(gate.Rearm());
        Assert.False(gate.TryBeginExit(ExitRequestSource.ShutdownSignal));
    }
}
