using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public class ShutdownSequenceTests
{
    [Fact]
    public void ControlRecoveryFailureMayResume()
    {
        var sequence = new ShutdownSequence();

        Assert.Equal(ShutdownPhase.ControlRecovery, sequence.Phase);
        Assert.False(sequence.HardwareReleased);
        Assert.True(sequence.CanResumeAfterFailure);
        Assert.Equal(ShutdownFailureAction.ResumeSession, sequence.DecideFailureAction());
    }

    [Fact]
    public void HardwareReleaseFailureNeverResumes()
    {
        var sequence = new ShutdownSequence();
        sequence.Enter(ShutdownPhase.HardwareRelease);

        Assert.True(sequence.HardwareReleased);
        Assert.False(sequence.CanResumeAfterFailure);
        Assert.Equal(ShutdownFailureAction.FinishExitWithoutResume, sequence.DecideFailureAction());
    }

    /// <summary>
    /// The reported defect: hardware was disposed and the following UI teardown threw; the old
    /// code called the resume path and kept a half-dead process sampling a disposed hardware
    /// object with a cleared lease.
    /// </summary>
    [Fact]
    public void HardwareDisposedPlusUiTeardownFailureDoesNotResume()
    {
        var sequence = new ShutdownSequence();
        sequence.Enter(ShutdownPhase.HardwareRelease);
        sequence.Enter(ShutdownPhase.UiTeardown);

        Assert.True(sequence.HardwareReleased);
        Assert.False(sequence.CanResumeAfterFailure);
        Assert.Equal(ShutdownFailureAction.FinishExitWithoutResume, sequence.DecideFailureAction());
        // The explicit phase overload must agree, so a caller that failed before advancing
        // the sequence still reaches the same verdict.
        Assert.Equal(ShutdownFailureAction.FinishExitWithoutResume,
            ShutdownSequence.DecideFailureAction(ShutdownPhase.HardwareRelease));
        Assert.Equal(ShutdownFailureAction.FinishExitWithoutResume,
            ShutdownSequence.DecideFailureAction(ShutdownPhase.UiTeardown));
    }

    [Fact]
    public void CompletedExitNeverResumes()
    {
        var sequence = new ShutdownSequence();
        sequence.Enter(ShutdownPhase.Completed);

        Assert.Equal(ShutdownPhase.Completed, sequence.Phase);
        Assert.False(sequence.CanResumeAfterFailure);
        Assert.Equal(ShutdownFailureAction.FinishExitWithoutResume, sequence.DecideFailureAction());
    }

    [Fact]
    public void PhasesAreMonotonic()
    {
        var sequence = new ShutdownSequence();
        sequence.Enter(ShutdownPhase.HardwareRelease);
        sequence.Enter(ShutdownPhase.UiTeardown);

        // A late/duplicate call (or a retried exit) must not move the sequence back to a
        // resumable phase.
        sequence.Enter(ShutdownPhase.ControlRecovery);
        sequence.Enter(ShutdownPhase.HardwareRelease);

        Assert.Equal(ShutdownPhase.UiTeardown, sequence.Phase);
        Assert.False(sequence.CanResumeAfterFailure);
    }

    [Fact]
    public void PhaseOrderKeepsHardwareReleaseAfterControlRecovery()
    {
        // The safety ordering (RestoreAutomatic/lease cleanup first, dispose hardware last) is
        // encoded in the enum values themselves.
        Assert.True(ShutdownPhase.ControlRecovery < ShutdownPhase.HardwareRelease);
        Assert.True(ShutdownPhase.HardwareRelease < ShutdownPhase.UiTeardown);
        Assert.True(ShutdownPhase.UiTeardown < ShutdownPhase.Completed);
    }
}
