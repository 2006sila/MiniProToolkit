namespace MiniProToolkit.Core;

/// <summary>Which path asked the running app to exit.</summary>
public enum ExitRequestSource
{
    /// <summary>
    /// Tray menu, window/taskbar button or command channel: a human is in front of the app and can
    /// answer a prompt.
    /// </summary>
    User = 0,

    /// <summary>
    /// Installer/uninstaller/service stop: <c>Set</c> on the <c>Local\MiniProToolkit.Shutdown</c>
    /// event. Nobody can dismiss a dialog on this path, so a failure may only be logged.
    /// </summary>
    ShutdownSignal = 1
}

/// <summary>What a failed exit attempt is allowed to show the user.</summary>
public enum ExitFailureNotice
{
    /// <summary>Interactive exit: the failure dialog is shown and the session resumes.</summary>
    ShowDialog = 0,

    /// <summary>
    /// Signal-driven exit: log/diagnostic only. A modal box would block the installer, which sets
    /// the shutdown event exactly once and gives up after its own timeout.
    /// </summary>
    LogOnly = 1
}

/// <summary>
/// Serializes exit requests and remembers where the current one came from. Pure state (no WPF,
/// hardware, wait handle or dispatcher types) so the three rules that broke shutdown/reinstall are
/// unit-testable:
/// <list type="bullet">
///   <item>only one exit sequence may run at a time (no concurrent <c>Close</c>/<c>OnClosing</c>);</item>
///   <item>the wait for the shutdown signal must be re-armed after a failed exit, otherwise a second
///     stop request is never accepted again;</item>
///   <item>a signal-driven failure must not open a modal dialog.</item>
/// </list>
/// Thread-safe: the shutdown wait callback arrives on the thread pool, the user request on the UI
/// thread.
/// </summary>
public sealed class ExitRequestGate
{
    private readonly object _sync = new();
    private ExitRequestSource? _source;
    private bool _exitInFlight;
    private bool _teardownStarted;
    private bool _pendingSignal;
    private bool _shutdownSignalSeen;

    /// <summary>Source of the request that owns (or last owned) the gate; null after rearm/teardown.</summary>
    public ExitRequestSource? Source
    {
        get { lock (_sync) return _source; }
    }

    /// <summary>True while an exit sequence owns the gate and a second one must be refused.</summary>
    public bool IsExitInFlight
    {
        get { lock (_sync) return _exitInFlight; }
    }

    /// <summary>True once the hardware/UI teardown started; nothing may be re-armed after that.</summary>
    public bool IsTeardownStarted
    {
        get { lock (_sync) return _teardownStarted; }
    }

    /// <summary>True when a shutdown signal was seen during the current/last exit attempt.</summary>
    public bool ShutdownSignalSeen
    {
        get { lock (_sync) return _shutdownSignalSeen; }
    }

    /// <summary>
    /// Claims the gate for one exit sequence. Returns false when an exit already runs (the request
    /// is refused instead of opening a nested one) or when teardown started (nothing may be queued
    /// after the dispatcher is gone). A shutdown signal that races a running exit is remembered so
    /// the retry after a failed exit is not lost.
    /// </summary>
    public bool TryBeginExit(ExitRequestSource source)
    {
        lock (_sync)
        {
            if (_teardownStarted)
                return false;
            if (source == ExitRequestSource.ShutdownSignal)
                _shutdownSignalSeen = true;
            if (_exitInFlight)
            {
                if (source == ExitRequestSource.ShutdownSignal)
                    _pendingSignal = true;
                return false;
            }
            _exitInFlight = true;
            _source = source;
            return true;
        }
    }

    /// <summary>
    /// Releases the gate after a failed exit that resumed the session (the caller is back to a fully
    /// usable state), instead of leaving an exit-only latch behind. Returns true when a shutdown
    /// signal arrived while that exit ran: the caller should retry once with
    /// <see cref="ExitRequestSource.ShutdownSignal"/> so an installer waiting on the event is not
    /// left hanging. Nothing is re-armed once teardown started.
    /// </summary>
    public bool Rearm()
    {
        lock (_sync)
        {
            if (_teardownStarted)
                return false;
            _exitInFlight = false;
            _source = null;
            _shutdownSignalSeen = false;
            if (!_pendingSignal)
                return false;
            _pendingSignal = false;
            return true;
        }
    }

    /// <summary>
    /// Final phase: no further request is accepted and no pending retry survives. Called before the
    /// wait registration is unregistered and the event handle disposed.
    /// </summary>
    public void BeginTeardown()
    {
        lock (_sync)
        {
            _teardownStarted = true;
            _pendingSignal = false;
        }
    }

    /// <summary>Notice rule for the request currently owning the gate.</summary>
    public ExitFailureNotice DecideFailureNotice()
    {
        lock (_sync)
            return DecideFailureNotice(_source ?? ExitRequestSource.User, _shutdownSignalSeen);
    }

    /// <summary>
    /// A manual exit keeps its dialog (the user has to see that the fans were not restored). A
    /// signal-driven exit only logs, and an exit that saw a shutdown signal during its attempt is
    /// treated the same way so a waiting installer is never blocked by a modal box.
    /// </summary>
    public static ExitFailureNotice DecideFailureNotice(ExitRequestSource source, bool shutdownSignalSeen) =>
        source == ExitRequestSource.ShutdownSignal || shutdownSignalSeen
            ? ExitFailureNotice.LogOnly
            : ExitFailureNotice.ShowDialog;
}
