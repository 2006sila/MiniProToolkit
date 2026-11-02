namespace MiniProToolkit.Core;

/// <summary>
/// Ordered phases of the exit sequence. The ordering is the safety property: everything before
/// <see cref="HardwareRelease"/> is reversible (control state, Guardian lease, optional readers),
/// everything from <see cref="HardwareRelease"/> on is not, because the hardware and telemetry
/// handles are final and have no re-open API.
/// </summary>
public enum ShutdownPhase
{
    /// <summary>
    /// Stop timers/power notifications, wait for the sampling gate and hand the fans back to the
    /// EC (RestoreAutomatic + lease cleanup). A failure here leaves a fully usable session behind.
    /// </summary>
    ControlRecovery = 0,

    /// <summary>
    /// Telemetry recorder and <c>PowerSwitchHardware</c> are released. From this point a running
    /// session can no longer sample or control anything, so a later failure must not resume.
    /// </summary>
    HardwareRelease = 1,

    /// <summary>Tray, taskbar windows, OSD, hotkeys and the shutdown signal are torn down.</summary>
    UiTeardown = 2,

    /// <summary>Exit finished; the process may close.</summary>
    Completed = 3
}

/// <summary>What a failure during the exit sequence is allowed to do.</summary>
public enum ShutdownFailureAction
{
    /// <summary>Nothing irreversible happened yet: restore the session and keep running.</summary>
    ResumeSession,

    /// <summary>
    /// Hardware/telemetry are already released: never resume (sampling with a disposed hardware
    /// object and a cleared lease would only fail repeatedly), finish the exit instead.
    /// </summary>
    FinishExitWithoutResume
}

/// <summary>
/// Tracks the exit phase and answers the single safety question of a failed exit: may the app
/// resume sampling, or must it keep closing? Pure state (no App, hardware or WPF types) so the
/// "hardware disposed + UI teardown failure =&gt; no resume" rule is unit-testable.
/// </summary>
public sealed class ShutdownSequence
{
    private ShutdownPhase _phase = ShutdownPhase.ControlRecovery;

    public ShutdownPhase Phase => _phase;

    /// <summary>True once the hardware/telemetry handles have been (or are being) released.</summary>
    public bool HardwareReleased => _phase >= ShutdownPhase.HardwareRelease;

    /// <summary>True only while nothing irreversible has happened yet.</summary>
    public bool CanResumeAfterFailure => _phase < ShutdownPhase.HardwareRelease;

    /// <summary>
    /// Advances to the next phase. The sequence is monotonic: a late or repeated call can never
    /// move the exit back to a resumable phase, so a race cannot resurrect a released session.
    /// </summary>
    public void Enter(ShutdownPhase phase)
    {
        if (phase > _phase)
            _phase = phase;
    }

    /// <summary>
    /// Decision for a failure that happened while <see cref="Phase"/> was the given phase.
    /// Passing the phase explicitly lets a caller make the decision for the phase it failed in
    /// instead of relying on the caller having advanced the sequence first.
    /// </summary>
    public static ShutdownFailureAction DecideFailureAction(ShutdownPhase failurePhase) =>
        failurePhase < ShutdownPhase.HardwareRelease
            ? ShutdownFailureAction.ResumeSession
            : ShutdownFailureAction.FinishExitWithoutResume;

    /// <summary>Decision for the current phase.</summary>
    public ShutdownFailureAction DecideFailureAction() => DecideFailureAction(_phase);
}
