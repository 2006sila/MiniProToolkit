namespace MiniProToolkit.Core;

/// <summary>
/// Neutral mapping of a Windows power-mode notification. The App layer translates
/// <c>Microsoft.Win32.PowerModes</c> into these kinds so the decision table stays
/// testable without referencing the Win32 event plumbing.
/// </summary>
public enum PowerTransitionKind
{
    Unknown,
    StatusChange,
    Suspend,
    Resume
}

/// <summary>Work a single power transition is allowed to request.</summary>
public enum PowerTransitionAction
{
    /// <summary>Log only; no sampling, no control, no hardware access.</summary>
    Ignore,

    /// <summary>
    /// Record the suspend in the diagnostic journal. Deliberately no EC/fan write:
    /// writing "automatic" before sleep would silently change the user's control state.
    /// </summary>
    RecordSuspendOnly,

    /// <summary>
    /// Re-sample and refresh state through the existing serialized sampling/control path
    /// (diagnostic event, LHM reader reset, one snapshot refresh). No direct hardware write.
    /// </summary>
    RefreshAfterResume
}

/// <summary>
/// Decision table for sleep/resume handling. Only resume is allowed to trigger work, so a
/// stray status-change notification can never start a sampling pass or a hardware write.
/// </summary>
public static class PowerTransitionPolicy
{
    public static PowerTransitionAction Decide(PowerTransitionKind kind) => kind switch
    {
        PowerTransitionKind.Suspend => PowerTransitionAction.RecordSuspendOnly,
        PowerTransitionKind.Resume => PowerTransitionAction.RefreshAfterResume,
        _ => PowerTransitionAction.Ignore
    };
}

/// <summary>
/// Coalesces repeated resume notifications into at most one queued refresh. Windows can
/// raise several resume-related notifications for a single wake-up, and the notifications
/// arrive on the SystemEvents thread while the queued refresh runs on the UI thread, so the
/// gate is a monitor. A caller that received <c>true</c> must call <see cref="Complete"/>
/// when the queued refresh finishes (including on failure), otherwise later resumes in the
/// same session would be dropped.
/// </summary>
public sealed class ResumeRefreshCoalescer
{
    private readonly object _sync = new();
    private bool _pending;

    public bool IsPending
    {
        get { lock (_sync) return _pending; }
    }

    /// <summary>Returns true only for the caller that must queue the refresh.</summary>
    public bool TrySchedule()
    {
        lock (_sync)
        {
            if (_pending)
                return false;
            _pending = true;
            return true;
        }
    }

    /// <summary>Marks the queued refresh as finished so a later resume can schedule again.</summary>
    public void Complete()
    {
        lock (_sync)
            _pending = false;
    }
}
