namespace MiniProToolkit.Core;

/// <summary>
/// A window notification that has to be turned into taskbar overlay work. The App layer maps
/// native messages onto these kinds, so the decision "re-mount the taskbar windows" versus
/// "only reposition them" stays testable without a desktop session or a Win32 hook.
/// </summary>
public enum TaskbarRefreshTrigger
{
    /// <summary>
    /// Explorer broadcast <c>RegisterWindowMessage("TaskbarCreated")</c>: the shell (and every
    /// <c>Shell_TrayWnd</c>/<c>Shell_SecondaryTrayWnd</c>) was recreated, so the old overlay
    /// windows point at destroyed taskbars and have to be re-enumerated and re-mounted.
    /// </summary>
    ShellRecreated,

    /// <summary><c>WM_DISPLAYCHANGE</c>: resolution/refresh/monitor layout changed.</summary>
    DisplayChanged,

    /// <summary><c>WM_DPICHANGED</c>: the window was moved onto a monitor with another DPI.</summary>
    DpiChanged,

    /// <summary>
    /// <c>WM_SETTINGCHANGE</c>: the taskbar was moved, resized or switched to auto-hide. The
    /// existing taskbar handles stay valid, so the windows only need to be repositioned.
    /// </summary>
    TaskbarSettingChanged
}

/// <summary>
/// Pure decision table for taskbar overlay refresh notifications. Nothing here calls Win32,
/// touches WPF or reads hardware; it only answers three questions:
/// <list type="number">
/// <item>Is an incoming window message one of the supported notifications?</item>
/// <item>Does that notification require re-enumerating the taskbars (shell restart) or is
/// repositioning the existing windows enough?</item>
/// <item>Does an event-driven notification bypass the periodic refresh interval?</item>
/// </list>
/// </summary>
public static class TaskbarRefreshPolicy
{
    /// <summary>Name of the shell broadcast message; Explorer posts it to every top-level window.</summary>
    public const string TaskbarCreatedMessageName = "TaskbarCreated";

    /// <summary><c>WM_DISPLAYCHANGE</c>.</summary>
    public const int WmDisplayChange = 0x007E;

    /// <summary><c>WM_SETTINGCHANGE</c>.</summary>
    public const int WmSettingChange = 0x001A;

    /// <summary><c>WM_DPICHANGED</c>.</summary>
    public const int WmDpiChanged = 0x02E0;

    /// <summary>
    /// True when <paramref name="message"/> is the registered <c>TaskbarCreated</c> token.
    /// A token of <c>0</c> means the registration failed and is never treated as a match, so an
    /// unrelated message with <c>0</c> or <c>WM_NULL</c> cannot fake a shell restart.
    /// </summary>
    public static bool IsTaskbarCreatedMessage(int message, int taskbarCreatedToken) =>
        taskbarCreatedToken != 0 && message == taskbarCreatedToken;

    /// <summary>
    /// Maps a window message onto a taskbar refresh trigger, or <c>null</c> when the message is
    /// not a supported notification. The registered token is checked first because its value is
    /// allocated dynamically and could otherwise collide with a constant message.
    /// </summary>
    public static TaskbarRefreshTrigger? ClassifyWindowMessage(int message, int taskbarCreatedToken)
    {
        if (IsTaskbarCreatedMessage(message, taskbarCreatedToken))
            return TaskbarRefreshTrigger.ShellRecreated;
        return message switch
        {
            WmDisplayChange => TaskbarRefreshTrigger.DisplayChanged,
            WmDpiChanged => TaskbarRefreshTrigger.DpiChanged,
            WmSettingChange => TaskbarRefreshTrigger.TaskbarSettingChanged,
            _ => null
        };
    }

    /// <summary>
    /// True only for a shell restart: the taskbar HWNDs from the previous Explorer session are
    /// gone, so the windows must be re-enumerated instead of merely moved.
    /// </summary>
    public static bool RemountsTaskbarWindows(TaskbarRefreshTrigger trigger) =>
        trigger == TaskbarRefreshTrigger.ShellRecreated;

    /// <summary>
    /// Display/DPI/taskbar-setting notifications exist to move the overlay *now*, so they are
    /// allowed to bypass the user's periodic refresh interval. They still go through the
    /// coalescing/interval bookkeeping so the next periodic pass does not repeat the work.
    /// </summary>
    public static bool BypassesRefreshInterval(TaskbarRefreshTrigger trigger) =>
        RemountsTaskbarWindows(trigger) || trigger is TaskbarRefreshTrigger.DisplayChanged
            or TaskbarRefreshTrigger.DpiChanged or TaskbarRefreshTrigger.TaskbarSettingChanged;
}

/// <summary>
/// Shared bookkeeping for the taskbar overlay refresh. Two responsibilities, both pure state:
/// <list type="number">
/// <item><b>Coalescing</b> — Explorer's <c>TaskbarCreated</c> broadcast reaches every top-level
/// window and Windows can send several display notifications for one change. Only the caller
/// that got <c>true</c> from <see cref="TryQueue"/> queues a refresh and must call
/// <see cref="Complete"/> when it finished (including on failure); everything else is dropped,
/// so no second refresh is ever queued on top of a pending one.</item>
/// <item><b>Interval gate</b> — the periodic path uses <see cref="TryAcquireInterval"/> with a
/// caller-supplied monotonic timestamp, so the "one refresh per configured interval" rule is
/// testable without a clock or a WPF dispatcher. The event path calls
/// <see cref="MarkRefreshed"/> to keep the next periodic pass from repeating the same work.</item>
/// </list>
/// The timer thread and the UI thread touch this object, so every member is monitor-guarded.
/// </summary>
public sealed class TaskbarRefreshScheduler
{
    private readonly object _sync = new();
    private bool _queued;
    private bool _hasLastRefresh;
    private long _lastRefreshTimestamp;

    /// <summary>True while a refresh is queued but not completed yet.</summary>
    public bool IsQueued
    {
        get { lock (_sync) return _queued; }
    }

    /// <summary>Returns true only for the caller that must queue the refresh.</summary>
    public bool TryQueue()
    {
        lock (_sync)
        {
            if (_queued)
                return false;
            _queued = true;
            return true;
        }
    }

    /// <summary>Marks the queued refresh as finished so a later notification can queue again.</summary>
    public void Complete()
    {
        lock (_sync)
            _queued = false;
    }

    /// <summary>
    /// Periodic gate. Returns true (and records <paramref name="timestamp"/>) when at least
    /// <paramref name="interval"/> has elapsed since the last recorded refresh. A zero or
    /// negative interval disables the gate, and an unknown/negative timestamp delta is treated
    /// as elapsed so a clock that moved backwards cannot freeze the refresh forever.
    /// </summary>
    public bool TryAcquireInterval(long timestamp, long frequency, TimeSpan interval)
    {
        lock (_sync)
        {
            if (_hasLastRefresh && interval > TimeSpan.Zero && frequency > 0)
            {
                var elapsed = timestamp - _lastRefreshTimestamp;
                var required = interval.TotalSeconds * frequency;
                if (elapsed >= 0 && elapsed < required)
                    return false;
            }
            _hasLastRefresh = true;
            _lastRefreshTimestamp = timestamp;
            return true;
        }
    }

    /// <summary>
    /// Records an event-driven refresh without gating it. The next periodic pass waits a full
    /// interval instead of immediately repeating the same enumeration and repositioning.
    /// </summary>
    public void MarkRefreshed(long timestamp)
    {
        lock (_sync)
        {
            _hasLastRefresh = true;
            _lastRefreshTimestamp = timestamp;
        }
    }

    /// <summary>Forgets the last refresh so the next periodic pass runs regardless of the interval.</summary>
    public void ResetInterval()
    {
        lock (_sync)
        {
            _hasLastRefresh = false;
            _lastRefreshTimestamp = 0;
        }
    }
}
