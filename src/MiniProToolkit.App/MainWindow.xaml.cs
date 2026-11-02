using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MediaBrushes = System.Windows.Media.Brushes;
using WpfPoint = System.Windows.Point;
using MiniProToolkit.Core;
using MiniProToolkit.Hardware;

namespace MiniProToolkit.App;

public partial class MainWindow : Window, IAppCommandEndpoint
{
    private readonly bool _demo;
    private readonly PowerSwitchHardware? _hardware;
    // Optional read-only LHM cross-check; null in demo mode so nothing touches hardware.
    // Not readonly: the reader's Dispose() is final and it has no re-open API, so a system
    // resume replaces the instance with a fresh one to drop possibly stale driver handles.
    private LibreHardwareMonitorCpuSensor? _lhmCpuSensor;
    // Optional read-only LHM GPU reader (display/telemetry only, never a control input). Same
    // lifecycle as the CPU cross-check reader: null in demo mode so nothing touches hardware,
    // rebuilt by the background pass after a resume or a failed exit, released on exit.
    private LibreHardwareMonitorGpuSensor? _lhmGpuSensor;
    // Independent, optional read-only board/storage reader; never a CPU/control source.
    private LibreHardwareMonitorAuxTemperatureSensor? _lhmAuxTemperatureSensor;
    // Not readonly: Dispose() is final and the reader has no re-open API, so a failed exit
    // replaces the instance with a fresh one instead of leaving a permanently closed reader.
    private CpuFrequencyReader? _cpuFrequencyReader;
    // Optional Windows efficiency-class reader; display/JSON only, never an EC/control source.
    private CpuClassFrequencyReader? _cpuClassFrequencyReader;
    // Pure state machine owned by the background sampling pass (refresh gate held).
    private readonly GuardianLeaseStore _leaseStore = new();
    private readonly FanCurveConfigurationStore _curveConfigStore = new();
    private readonly FanCalibrationStore _calibrationStore = new();
    private readonly FanProfileStore _profileStore = new();
    private readonly ProfileAutomationStore _automationStore = new();
    private readonly ProfileAutomationDebouncer _automationDebouncer = new();
    private readonly ProfileAutomationProcessTracker _automationProcessTracker = new();
    private readonly ProfileAutomationProcessSampler _automationProcessSampler = new();
    // Runtime-only view of which rule matched or was skipped and why; fed with closed reason codes only, so it
    // never holds a process path, command line or pattern and never influences matching or applying.
    private readonly ProfileAutomationStatusBoard _automationStatusBoard = new();
    // Deduplicates log-safe "rule skipped" diagnostics so a denied process does not spam the log every sample.
    private readonly HashSet<string> _reportedProcessRuleSkips = new(StringComparer.Ordinal);
    // The 5 s automation timer must not start a second evaluation while the previous process inventory is
    // still running on the worker thread: the tracker/debouncer below are plain UI-thread state and have to
    // stay strictly ordered. Only read/written on the dispatcher thread.
    private bool _automationEvaluationInFlight;
    private ProfileAutomationSettings _automationSettings = ProfileAutomationSettings.Default;
    private readonly DispatcherTimer _automationTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private FanCalibration _calibration;
    private readonly TaskbarDisplaySettingsStore _taskbarSettingsStore = new();
    private TaskbarDisplaySettings _taskbarSettings;
    // Configurable global hotkeys (whitelisted gestures, rebindable in the taskbar settings dialog).
    private readonly HotkeySettingsStore _hotkeySettingsStore = new();
    private HotkeySettings _hotkeySettings = HotkeySettings.Default;
    // Display-only source selection; control paths keep reading the EC temperature.
    private readonly CpuDisplaySourceSettingsStore _displaySourceStore = new();
    private CpuDisplaySourceSettings _displaySourceSettings = CpuDisplaySourceSettings.Default;
    // Minimal OSD switch (display-only; see IsOsdEnabled). Loaded before the first OSD exists.
    private readonly OsdPreferencesStore _osdPreferencesStore = new();
    private OsdPreferences _osdPreferences = OsdPreferences.Default;
    private Forms.ToolStripMenuItem? _osdMenuItem;
    private Forms.ToolStripMenuItem? _osdThemeMenu;
    // Startup warning, if any (corrupt/unsupported settings). Persists across frames until
    // the next successful display-source save, so the first refresh cannot clear it.
    private string? _displaySourceWarning;
    private FanCurveSettings _curveSettings;
    private readonly GuardianLeasedFanTarget? _curveTarget;
    private FanCurveController? _curveController;
    // Curve write gate. Opened only by a pass that read a valid lease (or by the explicit curve
    // takeover), so a curve write can never publish or revive a lease Guardian already recovered.
    private readonly FanCurveLeaseGate _curveLeaseGate = new();
    private FanCurveEngine _curvePreview;
    private readonly ObservableCollection<FanProfile> _profiles = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _calibrationCancellationSync = new();
    private volatile CancellationTokenSource? _calibrationRunCts;
    private TaskCompletionSource<ControlAttempt>? _calibrationCompletion;
    private bool _calibrationRecoveryRequired;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    // Taskbar overlay refresh bookkeeping (interval gate for the periodic pass, coalescing for
    // the shell/display events). Pure state, so it is unit-tested in MiniProToolkit.Core.Tests.
    private readonly TaskbarRefreshScheduler _taskbarRefresh = new();
    private readonly TelemetryRecorder _telemetry = new();
    private readonly DiagnosticEventStore _diagnosticEvents = new();
    private readonly Queue<HardwareTelemetryRecord> _recentTelemetry = new();
    private readonly List<TaskbarStatusWindow> _taskbarWindows = [];
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly OsdWindow _osd;
    private EventWaitHandle? _shutdownSignal;
    private RegisteredWaitHandle? _shutdownRegistration;
    // Exit request bookkeeping: one exit sequence at a time, the source of the current request
    // (installer signal vs. user) and the re-arm after a failed exit. Pure state, so the
    // rearm/reentrancy/notice rules live in Core instead of the WPF event handlers.
    private readonly ExitRequestGate _exitRequests = new();
    private HwndSource? _hotkeySource;
    // Registration ids of the hotkeys that are actually live. Per-action degradation keeps the
    // successful ones registered, so this is a set instead of a single "all registered" flag.
    private readonly HashSet<int> _registeredHotkeys = [];
    // The main-window hook is attached at most once per HwndSource: a re-registration after a
    // failed exit must not stack a second hook (the callback would then run twice per message).
    // It carries the hotkeys *and* the shell/display notifications (TaskbarCreated, display
    // changes), so its lifetime is tracked independently of the hotkey registrations.
    private bool _windowHookAttached;
    // Token returned by RegisterWindowMessage("TaskbarCreated"). The shell broadcasts it after an
    // Explorer restart; 0 means the registration failed and no shell-restart refresh is possible.
    private int _taskbarCreatedMessage;
    // Exit phase tracker. Once the hardware/telemetry handles are released a failed exit must
    // finish closing instead of resuming sampling on a disposed hardware object.
    private readonly ShutdownSequence _shutdownSequence = new();
    private bool _closing;
    private bool _trayExitRequested;
    private bool _allowClose;
    private bool _restoreRequired;
    private bool _synchronizingCurveToggle;
    private bool _synchronizingProfile;
    private string? _lastFanFault;
    // Transition trackers for the pass-driven OSD events. They only remember what was already
    // reported, so a repeated failure (for example a recovery that keeps being retried once per
    // second) shows one popup instead of one per sampling pass.
    private FanRpmFeedbackState? _lastRpmFeedbackState;
    private bool _lastEmergencyFullSpeed;
    // Curve emergency dedup. Independent from _lastEmergencyFullSpeed on purpose: the curve's
    // 90 °C threshold is not the fixed-RPM emergency latch.
    private readonly FanCurveEmergencyTracker _curveEmergencyTracker = new();
    private bool _lastCurveEnabled;
    private string? _lastControlOsdKey;
    private HardwareSnapshot? _latestSnapshot;
    // Guardian state of the last completed sampling pass. Reused by the read-only CLI status answer so a status
    // request never blocks the UI thread on a service query.
    private bool _lastGuardianRunning;
    private bool _applyingProfile;
    private string? _appliedProfileName;
    // Sleep/resume plumbing. Power notifications arrive on the SystemEvents thread, so the
    // handler only classifies and marshals; all state stays owned by the UI thread.
    private readonly ResumeRefreshCoalescer _resumeCoalescer = new();
    private bool _powerEventsSubscribed;
    private volatile bool _lhmReinitializeRequested;
    // Same rule for the optional GPU reader: consumed by the next background pass.
    private volatile bool _lhmGpuReinitializeRequested;
    private volatile bool _lhmAuxTemperatureReinitializeRequested;
    // Same rule for the optional CPU-frequency reader: consumed by the next background pass.
    private volatile bool _cpuFrequencyReinitializeRequested;
    private volatile bool _cpuClassFrequencyReinitializeRequested;
    // A resume refresh waits for the shared gate instead of skipping a pass, but never forever.
    private static readonly TimeSpan ResumeRefreshGateTimeout = TimeSpan.FromSeconds(5);
    // Shutdown waits for an in-flight sampling pass, but must not hang the app indefinitely.
    private static readonly TimeSpan ExitGateTimeout = TimeSpan.FromSeconds(10);
    // Bounded wait for a shutdown wait callback that is still running before the event handle is
    // disposed (disposing it under a pending registered wait would throw on a pool thread).
    private static readonly TimeSpan ShutdownSignalTeardownTimeout = TimeSpan.FromSeconds(2);

    // Registration id of a hotkey action: stable, unique per action and independent of the gesture.
    private static int HotkeyId(HotkeyAction action) => 1 + (int)action;

    private static HotkeyAction? HotkeyActionFor(int id) => id >= 1 && id <= Enum.GetValues<HotkeyAction>().Length
        ? (HotkeyAction)(id - 1)
        : null;

    public int SnapshotCount { get; private set; }

    /// <summary>
    /// User switch for OSD popups, persisted at <c>%LOCALAPPDATA%\MiniProToolkit\osd.json</c>
    /// (default: on). Switching it off only silences the popup: every control path still runs,
    /// the fan/Guardian state is untouched and no hardware call is made or skipped.
    /// </summary>
    public bool IsOsdEnabled
    {
        get => _osdPreferences.IsEnabled;
        set
        {
            if (_osdPreferences.IsEnabled == value)
                return;
            var updated = _osdPreferences with { IsEnabled = value };
            _osdPreferences = updated;
            // Silence first, so a failing save still leaves the user's choice in effect.
            _osd.IsOsdEnabled = value;
            try
            {
                _osdPreferencesStore.Save(updated);
            }
            catch (Exception ex)
            {
                AppLog.Write("OSD preference could not be saved", ex);
            }
            if (_osdMenuItem is not null)
                _osdMenuItem.Checked = value;
            if (value)
                ShowOsd(OsdEvents.CommandSucceeded("OSD 提示已开启", DescribeOsdSample()));
        }
    }

    /// <summary>
    /// OSD theme preset, persisted alongside the on/off switch in the same
    /// <c>%LOCALAPPDATA%\MiniProToolkit\osd.json</c>. Presentation only: applying a theme touches
    /// the popup window and the settings file, never the EC, the free-form colors or the control loop.
    /// Only <see cref="OsdThemeCatalog.Presets"/> can be selected, so no color parsing of user input.
    /// User color/font-size/position overrides (<see cref="OsdPreferences.Appearance"/>) are kept as-is.
    /// </summary>
    public OsdThemePreset SelectedOsdTheme
    {
        get => _osdPreferences.Theme;
        set
        {
            var preset = OsdThemeCatalog.Normalize(value);
            if (_osdPreferences.Theme == preset)
            {
                UpdateOsdThemeChecks();
                return;
            }
            _osdPreferences = _osdPreferences with { Theme = preset };
            // Apply first so a failing save still leaves the chosen theme in effect.
            ApplyOsdAppearance();
            try
            {
                _osdPreferencesStore.Save(_osdPreferences);
            }
            catch (Exception ex)
            {
                AppLog.Write("OSD theme could not be saved", ex);
            }
            UpdateOsdThemeChecks();
            ShowOsd(OsdEvents.CommandSucceeded($"OSD 主题：{OsdThemeCatalog.DisplayName(preset)}", DescribeOsdSample()));
        }
    }

    /// <summary>
    /// Applies the persisted theme preset plus the user appearance overrides (colors, opacity, font
    /// size, placement) to the OSD window on the UI thread only. Any failure (invalid values, WPF
    /// object gone while closing) is logged and swallowed so the popup can never fault the app.
    /// </summary>
    private void ApplyOsdAppearance()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyOsdAppearance));
            return;
        }
        try
        {
            var appearance = _osdPreferences.ResolvedAppearance;
            var theme = OsdThemeCatalog.Resolve(_osdPreferences.Theme, appearance);
            theme.Validate();
            _osd.ApplyTheme(theme);
            _osd.ApplyPlacement(appearance.Placement);
        }
        catch (Exception ex)
        {
            AppLog.Write("OSD appearance could not be applied; keeping the previous appearance", ex);
        }
    }

    /// <summary>
    /// Opens the OSD appearance editor (colors / opacity / font size / position). Saving applies and
    /// persists the whole <see cref="OsdPreferences"/>; cancelling changes nothing. Presentation only:
    /// no hardware, network or Guardian call is made.
    /// </summary>
    private void OsdSettingsClick(object sender, RoutedEventArgs e)
    {
        var editor = new OsdSettingsWindow(_osdPreferences.ResolvedAppearance, _osdPreferences.EventTemplates) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } appearance)
            return;
        _osdPreferences = _osdPreferences with { Appearance = appearance, EventTemplates = editor.TemplateResult };
        // Apply first so a failing save still leaves the chosen appearance in effect.
        ApplyOsdAppearance();
        try
        {
            _osdPreferencesStore.Save(_osdPreferences);
        }
        catch (Exception ex)
        {
            AppLog.Write("OSD appearance could not be saved", ex);
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "保存 OSD 外观设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        ShowOsd(OsdEvents.CommandSucceeded($"OSD 外观：{appearance.Describe()}", DescribeOsdSample()));
    }

    private void UpdateOsdThemeChecks()
    {
        if (_osdThemeMenu is null)
            return;
        foreach (Forms.ToolStripItem item in _osdThemeMenu.DropDownItems)
            if (item is Forms.ToolStripMenuItem entry && entry.Tag is OsdThemePreset preset)
                entry.Checked = preset == _osdPreferences.Theme;
    }

    public MainWindow(bool demo = false)
    {
        _demo = demo;
        try
        {
            _shutdownSignal = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                "Local\\MiniProToolkit.Shutdown");
            // Deliberately not executeOnlyOnce: the installer/uninstaller sets the event once and
            // cannot re-signal, so a failed exit must keep the listener armed. The one-shot
            // semantics are replaced by the ExitRequestGate latch (one exit sequence at a time,
            // plus the remembered signal that raced a running exit).
            _shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
                _shutdownSignal,
                OnShutdownSignal,
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            AppLog.Write("shutdown signal registration failed", ex);
        }
        _curveSettings = _curveConfigStore.LoadOrDefault(out var configWarning);
        _calibration = _calibrationStore.LoadOrDefault(out var calibrationWarning);
        _curveSettings = FanCurveCalibrationCompatibility.Resolve(_curveSettings, _calibration, out var curveCalibrationWarning);
        if (curveCalibrationWarning is not null)
        {
            configWarning = configWarning is null ? curveCalibrationWarning : $"{configWarning}；{curveCalibrationWarning}";
            AppLog.Write(curveCalibrationWarning);
        }
        // The engine previews with the application's real calibration: the optional RPM feedback
        // loop turns the curve duty into its expected RPM from this table, never from the default.
        _curvePreview = new FanCurveEngine(_curveSettings, _calibration);
        var profiles = _profileStore.LoadOrDefault(out var profileWarning);
        foreach (var profile in profiles)
            _profiles.Add(profile);
        _automationSettings = _automationStore.LoadOrDefault(_profiles.Select(p => p.Name).ToArray(), out var automationWarning);
        _taskbarSettings = _taskbarSettingsStore.LoadOrDefault(out var taskbarWarning);
        _hotkeySettings = _hotkeySettingsStore.LoadOrDefault(out var hotkeyWarning);
        _displaySourceSettings = _displaySourceStore.LoadOrDefault(out var displaySourceWarning);
        _displaySourceWarning = displaySourceWarning;
        _osdPreferences = _osdPreferencesStore.LoadOrDefault(out var osdWarning);
        try
        {
            _curveConfigStore.Save(_curveSettings);
        }
        catch (Exception ex)
        {
            AppLog.Write("fan curve configuration save failed", ex);
        }
        _hardware = demo ? null : new PowerSwitchHardware(_calibration);
        _lhmCpuSensor = demo ? null : new LibreHardwareMonitorCpuSensor();
        // Separate reader with its own GPU-only LHM computer; constructor only allocates,
        // Computer.Open() happens on the background sampling pass.
        _lhmGpuSensor = demo ? null : new LibreHardwareMonitorGpuSensor();
        _lhmAuxTemperatureSensor = demo ? null : new LibreHardwareMonitorAuxTemperatureSensor();
        _cpuFrequencyReader = demo ? null : new CpuFrequencyReader();
        _cpuClassFrequencyReader = demo ? null : new CpuClassFrequencyReader();
        _curveTarget = demo ? null : new GuardianLeasedFanTarget(_hardware!, _leaseStore, _curveLeaseGate);
        _curveController = demo ? null : new FanCurveController(_curveTarget!, _curveSettings, _calibration);
        InitializeComponent();
        _osd = new OsdWindow();
        // A disabled OSD stays silent for every later ShowMessage call; control is unaffected.
        _osd.IsOsdEnabled = _osdPreferences.IsEnabled;
        // Theme and user appearance are UI/presentation only and loaded from the same file;
        // demo mode uses them too.
        ApplyOsdAppearance();
        _trayIcon = CreateTrayIcon();
        // Control and telemetry cadence is independent of taskbar settings.
        _timer.Interval = FixedRpmControlSession.ControlInterval;
        ProfileCombo.ItemsSource = _profiles;
        ProfileCombo.SelectedIndex = Math.Max(0, _profiles.Select((profile, index) => (profile, index))
            .FirstOrDefault(item => item.profile.Name == "自定义").index);
        StateChanged += OnStateChanged;
        SourceInitialized += (_, _) => RegisterHotkeys();
        CurveConfigText.Text = configWarning is null
            ? $"配置: {_curveConfigStore.Path}"
            : $"配置警告: {configWarning}";
        UpdateCalibrationRange(calibrationWarning);
        if (calibrationWarning is not null)
            AppLog.Write(calibrationWarning);
        if (profileWarning is not null)
            AppLog.Write(profileWarning);
        if (taskbarWarning is not null)
            AppLog.Write(taskbarWarning);
        if (hotkeyWarning is not null)
            AppLog.Write(hotkeyWarning);
        if (displaySourceWarning is not null)
            AppLog.Write(displaySourceWarning);
        if (automationWarning is not null)
            AppLog.Write(automationWarning);
        if (osdWarning is not null)
            AppLog.Write(osdWarning);
        UpdateAutomationPauseButton();
        UpdateDisplaySourceStatus();
        if (demo)
        {
            Title += " [演示：不访问硬件]";
            FanCurveToggle.IsEnabled = false;
            GuardianStatusText.Text = "Guardian: 演示模式，不访问服务";
            LhmText.Text = DemoLhmText;
            GpuText.Text = DemoGpuText;
            AuxTemperatureText.Text = DemoAuxTemperatureText;
        }
        else
        {
            UpdateGuardianStatus();
        }

        _timer.Tick += async (_, _) => await RefreshSnapshotAsync();
        _automationTimer.Tick += async (_, _) =>
        {
            try { await EvaluateAutomationAsync(); }
            catch (Exception ex)
            {
                _automationDebouncer.Reset();
                AppLog.Write("profile automation evaluation failed", ex);
                RecordDiagnosticEvent($"自动规则评估失败：{ex.GetBaseException().Message}");
            }
        };
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await RefreshSnapshotAsync();
            _automationTimer.Start();
        };
        Closing += OnClosing;
        // Subscribed once, from the constructor; removed again in OnClosing.
        SubscribePowerEvents();
    }

    private HardwareSnapshot ReadSnapshot() => _demo
        ? new HardwareSnapshot(DateTimeOffset.Now, 52, 2180, 0, PowerMode.Balance, 25, 40,
            FanControlMode.Unknown, null, null) { CpuPackagePowerWatts = 12.5 }
        : _hardware!.ReadSnapshot();

    private async Task<bool> RefreshSnapshotAsync(bool waitForGate = false)
    {
        // Periodic callers must not queue up behind a slow pass (zero wait). The resume path
        // waits briefly so a wake-up is never silently swallowed by an in-flight pass. The wait
        // is always bounded, so no caller (including the resume path) can block forever.
        if (_closing || !await _gate.WaitAsync(waitForGate ? ResumeRefreshGateTimeout : TimeSpan.Zero))
            return false;

        try
        {
            // Re-check the closing flag *under* the gate. OnClosing sets _closing, waits for this
            // same gate and then releases hardware and the lease; a pass that only got in while
            // shutdown was already running must not sample, touch the EC or renew the lease.
            if (_closing)
                return false;
            var state = await Task.Run(() =>
            {
                var snapshot = ReadSnapshot();
                var guardianRunning = _demo || GuardianAvailability.IsRunning();
                // Exactly one lease read per pass. The same verdict feeds the fixed-RPM feedback and
                // the curve branch, so the curve can never write on a lease this pass rejected.
                var leaseValid = !_demo && HasValidControlLease();
                if (!_demo)
                    snapshot = _hardware!.ApplyFixedRpmControl(snapshot, guardianRunning, leaseValid);
                // A resume may have invalidated the optional LHM driver handles, and a failed
                // exit disposes both optional readers; rebuild them on this background pass
                // (gate held) so nothing has to be rebuilt on the UI thread.
                if (!_demo)
                {
                    ReinitializeLhmReader();
                    ReinitializeGpuReader();
                    ReinitializeAuxTemperatureReader();
                    ReinitializeCpuFrequencyReader();
                    ReinitializeCpuClassFrequencyReader();
                }
                // Optional LibreHardwareMonitor cross-check. It runs on this background
                // pass, never on the UI thread, and its failures cannot mask WMI or
                // Energy Meter errors because it only fills separate snapshot fields.
                snapshot = AttachLhmCrossCheck(snapshot);
                // Optional read-only GPU temperature (display/telemetry only). Same background
                // pass, same rule: it only fills its own snapshot fields.
                snapshot = AttachGpuReading(snapshot);
                snapshot = AttachAuxTemperatures(snapshot);
                // Read-only CPU clock. Same background pass, same rule: additive
                // fields only, never a control input.
                snapshot = AttachCpuFrequency(snapshot);
                snapshot = AttachCpuClassFrequency(snapshot);
                // Display-only projection. CpuTemperatureC/CpuPackagePowerWatts (EC and
                // Energy Meter) stay untouched for control, protection and Guardian. Demo
                // mode has no LHM reader, so it always projects the EC demo values.
                snapshot = CpuDisplaySourceSelector.Apply(snapshot,
                    _demo ? CpuDisplaySource.EcEnergyMeter : _displaySourceSettings.Source);
                FanCurveDecision decision;
                FanCurveApplyResult? applied = null;
                // Same-pass lease verdict, kept so the status line can tell a safety-gate
                // refusal (Guardian/lease) apart from the curve engine's own sensor failure.
                FanCurveLeaseDecision? leaseDecision = null;
                if (_curveController is { } curve)
                {
                    var lease = FanCurveLeasePolicy.Evaluate(
                        curve.IsEnabled, curve.RecoveryRequired, guardianRunning, leaseValid);
                    leaseDecision = lease;
                    // Guardian stopped, lease invalid or recovery pending: stop the curve and hand
                    // control back. SetCurveDuty is not called on this path.
                    _curveLeaseGate.SetPass(lease.MayApply);
                    if (lease.RestoreRequired)
                    {
                        decision = FanCurveDecision.Restore(lease.Reason);
                        try
                        {
                            curve.Disable();
                            applied = new FanCurveApplyResult(true, decision);
                        }
                        catch (Exception ex)
                        {
                            applied = new FanCurveApplyResult(false, decision, ex.GetBaseException().Message);
                        }
                    }
                    else if (curve.IsEnabled)
                    {
                        applied = curve.Apply(snapshot);
                        decision = applied.Decision;
                    }
                    else
                    {
                        decision = _curvePreview.Evaluate(snapshot);
                    }
                }
                else
                {
                    _curveLeaseGate.Close();
                    decision = _curvePreview.Evaluate(snapshot);
                }
                // Renew only after a completed pass: a blocked WMI operation must not keep
                // ownership alive indefinitely. Failed recovery retains the lease without renewal.
                if (!_demo)
                {
                    if (!_calibrationRecoveryRequired && _curveController?.IsEnabled != true && _curveController?.RecoveryRequired != true)
                        _restoreRequired = _hardware!.RecoveryRequired;
                    RefreshLease(snapshot.RpmFeedbackState == FanRpmFeedbackState.Fault && !_hardware!.IsEmergencyFullSpeed);
                }
                return (snapshot, decision, applied, leaseDecision, guardianRunning);
            });

            if (_closing)
                return false;

            _lastGuardianRunning = state.guardianRunning;
            DisplaySnapshot(state.snapshot);
            var curveStatus = FanCurveStatusFormatter.Describe(
                state.decision,
                state.leaseDecision,
                curveEnabled: _curveController?.IsEnabled == true,
                applied: state.applied is { Success: true },
                currentDuty: _curveController?.CurrentDuty,
                applyError: state.applied is { Success: false } ? state.applied.Error : null,
                emergencyTemperatureC: _curveSettings.EmergencyTemperatureC);
            DisplayCurveState(curveStatus, state.decision, state.applied);
            var sample = DescribeOsdSample(state.snapshot);
            // 90 °C protection latches a fan fault in the fixed-RPM session; report it once as its
            // own event so the user sees the temperature limit instead of a generic fault line.
            var emergency = !_demo && _hardware?.IsEmergencyFullSpeed == true;
            if (emergency && !_lastEmergencyFullSpeed)
                ShowOsd(OsdEvents.EmergencyFullSpeed(state.snapshot.CpuTemperatureC, sample));
            // An emergency that was just cleared is not a "protection recovered" event on its own:
            // the explicit restore already produced its own message.
            ReportFeedbackTransition(state.snapshot, emergency || _lastEmergencyFullSpeed, sample);
            _lastEmergencyFullSpeed = emergency;
            ReportCurveOutcome(state.applied, sample);
            // Curve 90 °C emergency is its own event (one popup per entry). It reports the curve
            // path only; the fixed-RPM latch (_hardware.IsEmergencyFullSpeed) and its
            // osd-emergency key are untouched.
            var curveEmergencyEntry = _curveEmergencyTracker.Observe(curveStatus);
            if (curveEmergencyEntry)
            {
                ShowOsd(OsdEvents.CurveEmergencyFullSpeed(
                    state.snapshot.CpuTemperatureC, _curveController?.CurrentDuty, sample));
                RecordDiagnosticEvent(
                    $"曲线紧急满速：EC {state.snapshot.CpuTemperatureC:0.0}°C 达到 {_curveSettings.EmergencyTemperatureC:0}°C 曲线阈值（固定 RPM 紧急锁存未参与）");
            }
            var faultChanged = !string.Equals(_lastFanFault, state.snapshot.FanFault, StringComparison.Ordinal);
            if (faultChanged)
            {
                if (state.snapshot.FanFault is not null)
                {
                    if (!emergency)
                        ShowOsd(OsdEvents.FanProtection(state.snapshot.FanFault, sample));
                    RecordDiagnosticEvent($"风扇保护：{state.snapshot.FanFault}");
                }
                _lastFanFault = state.snapshot.FanFault;
            }
            var telemetryEvent = faultChanged && state.snapshot.FanFault is not null
                ? $"fan-fault: {state.snapshot.FanFault}"
                : curveEmergencyEntry
                    ? "curve-emergency"
                    : state.applied?.Decision.Action.ToString();
            // CurveStatus/CurveReason are JSON-only (no CSV schema change): the kind plus the
            // Core level/lease reason, never paths, command lines or adapter details.
            var telemetryRecord = HardwareTelemetryRecord.FromSnapshot(
                state.snapshot,
                telemetryEvent,
                curveStatus.Kind.ToString(),
                curveStatus.Reason,
                curveDecision: state.decision);
            _recentTelemetry.Enqueue(telemetryRecord);
            while (_recentTelemetry.Count > 120 ||
                   (_recentTelemetry.Count > 1 && state.snapshot.Timestamp - _recentTelemetry.Peek().Timestamp > TimeSpan.FromMinutes(2)))
                _recentTelemetry.Dequeue();
            UpdateTelemetryChart();
            try
            {
                _telemetry.Append(telemetryRecord);
            }
            catch (Exception ex)
            {
                AppLog.Write("telemetry append failed", ex);
            }
            if (!_demo)
                UpdateGuardianStatus();
            SnapshotCount++;
            UpdateTrayStatus(state.snapshot, state.guardianRunning);
            if (TaskbarToggle.IsChecked == true &&
                _taskbarRefresh.TryAcquireInterval(
                    System.Diagnostics.Stopwatch.GetTimestamp(),
                    System.Diagnostics.Stopwatch.Frequency,
                    TimeSpan.FromSeconds(_taskbarSettings.RefreshIntervalSeconds)))
            {
                EnsureTaskbars();
                foreach (var taskbar in _taskbarWindows)
                {
                    taskbar.TryAttachToTaskbar();
                    var safeManual = !_demo && state.snapshot.Error is null && state.snapshot.FanFault is null &&
                        state.snapshot.RpmFeedbackState != FanRpmFeedbackState.Fault && !_restoreRequired &&
                        _curveController?.RecoveryRequired != true && _hardware?.IsEmergencyFullSpeed != true &&
                        _curveController?.IsEnabled != true;
                    var safeRpmOrCurve = safeManual && state.snapshot.CpuTemperatureC is < 90 &&
                        state.snapshot.FanFault is null && state.snapshot.RpmFeedbackState != FanRpmFeedbackState.Fault;
                    taskbar.UpdateSnapshot(state.snapshot, state.guardianRunning, safeManual, safeRpmOrCurve,
                        !_demo && state.snapshot.Error is null);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("snapshot failed", ex);
            if (!_closing)
                DisplaySnapshot(HardwareSnapshot.ErrorSnapshot(ex.GetBaseException().Message));
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TelemetryChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
            UpdateTelemetryChart();
    }

    private void UpdateTelemetryChart()
    {
        if (TelemetryChart is null || TelemetryChart.ActualWidth <= 1 || TelemetryChart.ActualHeight <= 1)
            return;

        var width = TelemetryChart.ActualWidth;
        var height = TelemetryChart.ActualHeight;
        TelemetryChart.Children.Clear();
        AddChartGuide(0.0, height * 0.25, width);
        AddChartGuide(0.0, height * 0.5, width);
        AddChartGuide(0.0, height * 0.75, width);

        var temperature = new System.Windows.Shapes.Polyline
        {
            Stroke = MediaBrushes.OrangeRed,
            StrokeThickness = 2,
            Points = new System.Windows.Media.PointCollection()
        };
        var rpm = new System.Windows.Shapes.Polyline
        {
            Stroke = MediaBrushes.SteelBlue,
            StrokeThickness = 2,
            Points = new System.Windows.Media.PointCollection()
        };
        var targetRpm = new System.Windows.Shapes.Polyline
        {
            Stroke = MediaBrushes.MediumPurple,
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
            Points = new System.Windows.Media.PointCollection()
        };
        var duty = new System.Windows.Shapes.Polyline
        {
            Stroke = MediaBrushes.ForestGreen,
            StrokeThickness = 1,
            StrokeDashArray = [1, 2],
            Points = new System.Windows.Media.PointCollection()
        };
        var power = new System.Windows.Shapes.Polyline
        {
            Stroke = MediaBrushes.DarkViolet,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 2],
            Points = new System.Windows.Media.PointCollection()
        };
        var records = _recentTelemetry.ToArray();
        for (var index = 0; index < records.Length; index++)
        {
            var x = records.Length == 0 ? 0 : width * (1 - Math.Clamp(
                (records[^1].Timestamp - records[index].Timestamp).TotalSeconds / 120.0, 0, 1));
            if (records[index].EffectiveDisplayCpuTemperatureC is { } temp)
                temperature.Points.Add(new WpfPoint(x, height - Normalize(temp, 30, 100) * height));
            if (records[index].Fan1Rpm is { } fan)
                rpm.Points.Add(new WpfPoint(x, height - Normalize(fan, 0, 4500) * height));
            if (records[index].TargetRpm is { } target)
                targetRpm.Points.Add(new WpfPoint(x, height - Normalize(target, 0, 4500) * height));
            if (records[index].TargetDuty is { } targetDuty)
                duty.Points.Add(new WpfPoint(x, height - Normalize(targetDuty, 0, 100) * height));
            if (records[index].EffectiveDisplayCpuPackagePowerWatts is { } watts)
                power.Points.Add(new WpfPoint(x, height - Normalize(watts, 0, 80) * height));
        }
        TelemetryChart.Children.Add(temperature);
        TelemetryChart.Children.Add(rpm);
        TelemetryChart.Children.Add(targetRpm);
        TelemetryChart.Children.Add(duty);
        TelemetryChart.Children.Add(power);
    }

    private void AddChartGuide(double x, double y, double width)
    {
        TelemetryChart.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x,
            X2 = width,
            Y1 = y,
            Y2 = y,
            Stroke = MediaBrushes.LightGray,
            StrokeThickness = 1,
            StrokeDashArray = [2, 3]
        });
    }

    private static double Normalize(double value, double minimum, double maximum) =>
        Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);

    private void DisplaySnapshot(HardwareSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        var mode = FormatMode(snapshot.PowerMode);
        var displayTemperature = snapshot.EffectiveDisplayCpuTemperatureC;
        var temperatureFromLhm = snapshot.DisplayTemperatureFromLhm;
        var temperatureLabel = temperatureFromLhm ? "LHM" : "EC";
        var powerLabel = snapshot.DisplayPowerFromLhm ? "LHM" : "Energy Meter";
        var temp = FormatTemperature(displayTemperature, temperatureFromLhm);
        var fan = snapshot.Fan1Rpm is { } rpm ? $"{rpm} RPM" : "--";
        StatusText.Text = snapshot.Error is null
            ? $"{(_demo ? "演示数据（非实测）" : "读取正常")} · CPU {temp} · 风扇 {fan} · {mode}"
            : $"硬件读取失败：{snapshot.Error}";
        TemperatureText.Text = $"CPU 温度（{temperatureLabel}）: {temp}";
        PowerText.Text = $"CPU 封装功耗（{powerLabel}，非整机）: {FormatPower(snapshot.EffectiveDisplayCpuPackagePowerWatts)}";
        LhmText.Text = _demo ? DemoLhmText : FormatLhmCrossCheck(snapshot);
        GpuText.Text = _demo ? DemoGpuText : FormatGpuStatus(snapshot);
        AuxTemperatureText.Text = _demo ? DemoAuxTemperatureText : FormatAuxTemperatures(snapshot);
        var fanControl = _curveController?.IsEnabled == true
            ? $"安全曲线 {_curveController.CurrentDuty?.ToString() ?? "--"}%"
            : snapshot.FanControlMode switch
            {
                FanControlMode.FirmwareAutomatic => "已请求 EC 自动（不可回读模式）",
                FanControlMode.ManualDuty => $"本程序请求 {snapshot.TargetDuty}%",
                FanControlMode.FixedRpm => $"固定 RPM · 当前请求 duty {snapshot.TargetDuty}%",
                FanControlMode.Curve => $"曲线请求 duty {snapshot.TargetDuty}%",
                _ => "控制归属未知"
            };
        var targetRpm = snapshot.TargetRpm is { } target
            ? $" · 目标 {target} RPM{(snapshot.RequestedTargetRpm is { } requested && requested != target ? $"（请求 {requested}）" : string.Empty)}"
            : string.Empty;
        var feedback = snapshot.FanControlMode == FanControlMode.FixedRpm || snapshot.RpmFeedbackState == FanRpmFeedbackState.Fault
            ? $" · {_formatRpmFeedback(snapshot)}"
            : string.Empty;
        FanText.Text = $"风扇: {fan}{targetRpm} · {fanControl}{feedback}";
        if (snapshot.FanFault is not null)
        {
            StatusText.Text = $"风扇保护：{snapshot.FanFault}{(snapshot.Error is null ? "" : $" · 硬件读取失败：{snapshot.Error}")}";
            StatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
        }
        else
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Gray;
        }
        ModeText.Text = $"模式: {mode}";
        UsageText.Text = $"CPU 占用: {snapshot.CpuUsagePercent?.ToString() ?? "--"}%";
        MemoryText.Text = $"内存: {snapshot.MemoryUsagePercent?.ToString() ?? "--"}%";
        if (_demo)
        {
            CpuFrequencyText.Text = $"CPU 频率: {DemoMetricsText}";
        }
        else
        {
            CpuFrequencyText.Text = $"CPU 频率: {MetricDisplayFormat.Frequency(snapshot.CpuFrequencyMhz, snapshot.CpuFrequencyBaseMhz, snapshot.CpuFrequencyPercentOfBase)}";
        }
        CpuClassFrequencyText.Text = _demo
            ? $"CPU 类别频率（只读）: {DemoMetricsText}"
            : $"CPU 类别频率（只读）: {HardwareTelemetryRecord.FormatCpuClassFrequency(snapshot.CpuClassFrequency)}";
        UpdateDisplaySourceStatus();
    }

    private static string FormatTemperature(double? celsius, bool oneDecimal) =>
        celsius is { } value && double.IsFinite(value)
            ? oneDecimal ? $"{value:0.0}°C" : $"{value:0}°C"
            : "--°C";

    private void UpdateDisplaySourceStatus()
    {
        if (DisplaySourceStatusText is null)
            return;
        var fallback = _latestSnapshot?.DisplaySourceStatus;
        // Demo mode never samples LHM, so the effective source there is always EC/Energy Meter.
        var source = _demo ? CpuDisplaySource.EcEnergyMeter : _displaySourceSettings.Source;
        string text;
        if (_demo && _displaySourceSettings.UsesLhm)
            text = "演示模式：不采集 LHM，显示 EC/Energy Meter 演示值。";
        else if (source == CpuDisplaySource.LibreHardwareMonitor)
            text = fallback is null
                ? "主显示源：LHM（温度/功耗均来自 LHM）；风扇控制与 90°C 保护仍使用 EC 温度。"
                : $"主显示源：LHM · {fallback}";
        else
            text = "主显示源：EC 温度 + Energy Meter 功耗；风扇控制与 90°C 保护使用 EC 温度。";
        if (_displaySourceWarning is not null)
            text = $"{_displaySourceWarning} · {text}";
        DisplaySourceStatusText.Text = text;
        DisplaySourceStatusText.Foreground = fallback is not null || _displaySourceWarning is not null
            ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.Gray;
        if (TrendLegendText is not null)
            TrendLegendText.Text = "实时趋势（最近 2 分钟；温度/封装功耗曲线按主显示源：" +
                CpuDisplaySourceSelector.DescribeSource(source) +
                "）  温度 ━  实际 RPM ━  目标 RPM - -  duty · ·  CPU 封装功耗 - -";
    }

    private static string FormatPower(double? watts) =>
        watts is { } value && double.IsFinite(value) ? $"{value:0.0} W" : "-- W";

    // Called from the sampling pass only (background thread, refresh gate held). A resume can
    // leave the optional reader with invalidated driver handles; because Dispose() is final and
    // there is no re-open API, a fresh instance replaces the old one and re-opens lazily on the
    // next read. The same path rebuilds a reader that a failed exit already disposed. Never
    // throws: the reader is optional and not part of the control path.
    private void ReinitializeLhmReader()
    {
        if (!_lhmReinitializeRequested)
            return;
        // Demo mode never owns a reader; never construct one here.
        if (_demo)
        {
            _lhmReinitializeRequested = false;
            return;
        }
        _lhmReinitializeRequested = false;
        if (_lhmCpuSensor is { } stale)
        {
            try
            {
                stale.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Write("lhm dispose after resume failed", ex);
            }
            finally
            {
                // Never keep a reference to a disposed reader: a later sampling pass would
                // read from a closed driver handle (or a failed exit would reuse it).
                _lhmCpuSensor = null;
            }
        }
        try
        {
            _lhmCpuSensor = new LibreHardwareMonitorCpuSensor();
            AppLog.Write("power: LHM cross-check reader reinitialized after resume");
        }
        catch (Exception ex)
        {
            // Optional cross-check only: leave the reader null (the pass simply skips the
            // comparison, exactly like demo mode). The rebuild stays retryable - the next
            // resume or failed exit requests it again - but it is not retried on every tick
            // so a permanently broken rebuild cannot spam the log.
            _lhmCpuSensor = null;
            AppLog.Write("power: LHM cross-check reader reinitialization failed", ex);
        }
    }

    // Mirrors ReinitializeLhmReader for the optional CPU-frequency reader: a failed exit
    // disposed it and Dispose() is final, so a fresh instance replaces the stale one and
    // re-opens its counter lazily on the next read. Runs only on the background sampling
    // pass with the refresh gate held, never on the UI thread. Never throws: a failed
    // rebuild leaves the reader null and only the frequency line degrades.
    private void ReinitializeCpuFrequencyReader()
    {
        if (!_cpuFrequencyReinitializeRequested)
            return;
        // Demo mode never owns a reader; never construct one here.
        if (_demo)
        {
            _cpuFrequencyReinitializeRequested = false;
            return;
        }
        _cpuFrequencyReinitializeRequested = false;
        if (_cpuFrequencyReader is { } stale)
        {
            try
            {
                stale.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Write("cpu frequency reader dispose after failed exit failed", ex);
            }
            finally
            {
                // Never keep a reference to a disposed reader: a later pass would read from
                // a closed PerformanceCounter.
                _cpuFrequencyReader = null;
            }
        }
        try
        {
            _cpuFrequencyReader = new CpuFrequencyReader();
            AppLog.Write("metrics: CPU frequency reader reinitialized after failed exit");
        }
        catch (Exception ex)
        {
            // Optional metric only: leave it null (the label degrades, control is untouched).
            // The rebuild stays retryable through the next failed exit, but it is not retried
            // on every tick so a permanently broken rebuild cannot spam the log.
            _cpuFrequencyReader = null;
            AppLog.Write("metrics: CPU frequency reader reinitialization failed", ex);
        }
    }

    private HardwareSnapshot AttachLhmCrossCheck(HardwareSnapshot snapshot)
    {
        // Demo never owns a reader; a real session only has none while a rebuild failed or an
        // exit was aborted, so say so instead of showing unexplained placeholders.
        if (_lhmCpuSensor is null)
            return _demo ? snapshot : (snapshot with { LhmStatus = "LHM 对照不可用（读取器未初始化）" });
        try
        {
            var reading = _lhmCpuSensor.Read();
            return snapshot with
            {
                LhmCpuPackageTemperatureC = reading.CpuPackageTemperatureC,
                LhmCpuPackagePowerWatts = reading.CpuPackagePowerWatts,
                LhmStatus = reading.Status
            };
        }
        catch (Exception ex)
        {
            AppLog.Write("lhm cross-check failed", ex);
            return snapshot with { LhmStatus = "LHM 对照不可用" };
        }
    }

    // Optional read-only board/storage data; demo never constructs or opens an auxiliary reader.
    private const string DemoAuxTemperatureText = "主板/存储（LHM 只读）: 演示模式不采集（不访问硬件）";

    private void ReinitializeAuxTemperatureReader()
    {
        if (!_lhmAuxTemperatureReinitializeRequested) return;
        _lhmAuxTemperatureReinitializeRequested = false;
        try { _lhmAuxTemperatureSensor?.Dispose(); }
        catch { /* optional, display only */ }
        finally { _lhmAuxTemperatureSensor = null; }
        if (_demo) return;
        try { _lhmAuxTemperatureSensor = new LibreHardwareMonitorAuxTemperatureSensor(); }
        catch { /* Unknown until a later resume/failed-exit requests a rebuild */ }
    }

    // Background sampling pass only; adds no fan, Guardian, CPU or display-source inputs.
    private HardwareSnapshot AttachAuxTemperatures(HardwareSnapshot snapshot)
    {
        if (_demo) return snapshot;
        try
        {
            var reading = _lhmAuxTemperatureSensor?.Read()
                ?? LhmAuxTemperatureReading.Unknown("Unknown：辅助温度读取器未初始化");
            return snapshot with
            {
                MotherboardTemperatureC = reading.Motherboard.TemperatureC,
                MotherboardTemperatureSensorId = reading.Motherboard.SensorId,
                MotherboardTemperatureSensorName = reading.Motherboard.SensorName,
                StorageTemperatureC = reading.Storage.TemperatureC,
                StorageTemperatureSensorId = reading.Storage.SensorId,
                StorageTemperatureSensorName = reading.Storage.SensorName,
                AuxTemperatureStatus = reading.Status
            };
        }
        catch
        {
            return snapshot with
            {
                MotherboardTemperatureC = null, MotherboardTemperatureSensorId = null,
                MotherboardTemperatureSensorName = null, StorageTemperatureC = null,
                StorageTemperatureSensorId = null, StorageTemperatureSensorName = null,
                AuxTemperatureStatus = "Unknown：LHM 辅助温度不可用"
            };
        }
    }

    private static string FormatAuxTemperatures(HardwareSnapshot snapshot)
    {
        static string Temperature(double? value) => value is { } v && AuxTemperatureSelector.IsPlausible(v)
            ? $"{v:0.0}°C" : "Unknown";
        return $"主板（只读）: {Temperature(snapshot.MotherboardTemperatureC)} · 存储（NVMe/SATA 只读）: {Temperature(snapshot.StorageTemperatureC)} · {snapshot.AuxTemperatureStatus ?? "Unknown：无读数"}（不参与控制）";
    }

    // Optional read-only GPU temperature. Display/telemetry only: the value never replaces the EC
    // temperature, the Energy Meter power or any fan-control input, and it runs on the same
    // background pass as the CPU cross-check (never on the UI thread, no WMI/EC write).
    private HardwareSnapshot AttachGpuReading(HardwareSnapshot snapshot)
    {
        // Demo never owns a reader; a real session only has none while a rebuild failed or an
        // exit was aborted, so say so instead of showing unexplained placeholders.
        if (_lhmGpuSensor is null)
            return _demo ? snapshot : (snapshot with
            {
                GpuSensorStatus = "GPU（LHM 只读）不可用（读取器未初始化）"
            });
        try
        {
            var reading = _lhmGpuSensor.Read();
            return snapshot with
            {
                GpuTemperatureC = reading.GpuTemperatureC,
                GpuSensorName = reading.SensorName,
                GpuSensorStatus = reading.Status
            };
        }
        catch (Exception ex)
        {
            AppLog.Write("lhm gpu read failed", ex);
            return snapshot with { GpuSensorStatus = "GPU（LHM 只读）不可用" };
        }
    }

    // Mirrors ReinitializeLhmReader for the optional GPU reader: a resume can leave stale driver
    // handles and a failed exit disposed the instance, while Dispose() is final and there is no
    // re-open API, so a fresh instance replaces the stale one and re-opens lazily on the next
    // read. Runs only on the background sampling pass with the refresh gate held, never throws.
    private void ReinitializeGpuReader()
    {
        if (!_lhmGpuReinitializeRequested)
            return;
        // Demo mode never owns a reader; never construct one here.
        if (_demo)
        {
            _lhmGpuReinitializeRequested = false;
            return;
        }
        _lhmGpuReinitializeRequested = false;
        if (_lhmGpuSensor is { } stale)
        {
            try
            {
                stale.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Write("lhm gpu dispose after resume failed", ex);
            }
            finally
            {
                // Never keep a reference to a disposed reader.
                _lhmGpuSensor = null;
            }
        }
        try
        {
            _lhmGpuSensor = new LibreHardwareMonitorGpuSensor();
            AppLog.Write("power: LHM GPU reader reinitialized after resume");
        }
        catch (Exception ex)
        {
            // Optional display metric only: leave it null (the GPU line degrades) and stay
            // retryable through the next resume/failed exit without spamming the log.
            _lhmGpuSensor = null;
            AppLog.Write("power: LHM GPU reader reinitialization failed", ex);
        }
    }

    // Display-only line: the GPU value is labelled read-only and never presented as a control
    // input. An unusable reading shows "--" plus the redacted reader status.
    private static string FormatGpuStatus(HardwareSnapshot snapshot)
    {
        var temperature = snapshot.GpuTemperatureC is { } value && double.IsFinite(value)
            ? $"{value:0.0}°C"
            : "--";
        var sensor = string.IsNullOrWhiteSpace(snapshot.GpuSensorName) ? string.Empty : $" · {snapshot.GpuSensorName}";
        var status = string.IsNullOrWhiteSpace(snapshot.GpuSensorStatus) ? string.Empty : $" · {snapshot.GpuSensorStatus}";
        return $"GPU（LHM 只读，不参与控制）: {temperature}{sensor}{status}";
    }

    // The LHM values are labelled comparison data only: they never replace the EC
    // temperature or the Energy Meter power used by the main labels and control.
    private const string DemoLhmText = "LHM 对照：演示模式不采集（未初始化 LHM，不访问硬件）";

    // Demo mode owns no reader, so the GPU line says so instead of showing unexplained "--".
    private const string DemoGpuText = "GPU（LHM 只读）: 演示模式不采集（未初始化 LHM，不访问硬件）";

    // Demo mode owns no reader, so it says so instead of showing unexplained placeholders.
    private const string DemoMetricsText = "演示模式不采集（不访问性能计数器）";

    // Called only inside the existing background pass with the refresh gate held.
    private void ReinitializeCpuClassFrequencyReader()
    {
        if (!_cpuClassFrequencyReinitializeRequested) return;
        _cpuClassFrequencyReinitializeRequested = false;
        try { _cpuClassFrequencyReader?.Dispose(); }
        catch { /* Optional read-only metric; no control-path failure. */ }
        finally { _cpuClassFrequencyReader = null; }
        if (_demo) return;
        try { _cpuClassFrequencyReader = new CpuClassFrequencyReader(); }
        catch { /* Leave unavailable until a later resume/failed exit requests a rebuild. */ }
    }

    private HardwareSnapshot AttachCpuClassFrequency(HardwareSnapshot snapshot)
    {
        // Replace this pass's field even on failure; never borrow LastReading or an old snapshot.
        // Demo must not construct/open counters or native readers.
        if (_demo) return snapshot with { CpuClassFrequency = null };
        CpuClassFrequencyReading reading;
        try
        {
            reading = _cpuClassFrequencyReader?.Read()
                ?? CpuClassFrequencyReading.Unavailable("CPU 效率等级频率读取器未初始化（不影响温控）。");
        }
        catch
        {
            reading = CpuClassFrequencyReading.Unavailable("CPU 效率等级频率本次采样失败（不影响温控）。");
        }
        return snapshot with { CpuClassFrequency = reading };
    }

    private HardwareSnapshot AttachCpuFrequency(HardwareSnapshot snapshot)
    {
        // Demo never owns a reader; a real session only has none while a failed-exit rebuild
        // failed, so say so instead of reporting it as a sampling exception.
        if (_cpuFrequencyReader is null)
        {
            return _demo ? snapshot : (snapshot with
            {
                CpuFrequencySource = CpuFrequencySource.Unavailable,
                CpuFrequencyStatus = "CPU 频率读取器未初始化（不影响温控）"
            });
        }
        try
        {
            var reading = _cpuFrequencyReader.Read();
            return snapshot with
            {
                CpuFrequencyMhz = reading.CurrentMhz,
                CpuFrequencyPercentOfBase = reading.PercentOfBase,
                CpuFrequencyBaseMhz = reading.BaseMhz,
                CpuFrequencySource = reading.Source,
                CpuFrequencyStatus = reading.Status
            };
        }
        catch (Exception ex)
        {
            AppLog.Write("cpu frequency sampling failed", ex);
            return snapshot with
            {
                CpuFrequencySource = CpuFrequencySource.Unavailable,
                CpuFrequencyStatus = "CPU 频率采样失败（不影响温控）"
            };
        }
    }

    // The line shows whichever source is *not* the primary display source: when LHM is the
    // main display source it reports the original EC temperature / Energy Meter power that
    // fan control and the 90 °C protection actually use.
    private static string FormatLhmCrossCheck(HardwareSnapshot snapshot)
    {
        var status = string.IsNullOrWhiteSpace(snapshot.LhmStatus) ? string.Empty : $" · {snapshot.LhmStatus}";
        if (snapshot.DisplayCpuSource == CpuDisplaySource.LibreHardwareMonitor)
        {
            var ecTemperature = FormatTemperature(snapshot.CpuTemperatureC, false);
            var energyMeterPower = FormatPower(snapshot.CpuPackagePowerWatts);
            return $"EC/Energy Meter 对照（风扇控制/保护真值）: {ecTemperature} / {energyMeterPower}{status}";
        }
        var temperature = snapshot.LhmCpuPackageTemperatureC is { } value && double.IsFinite(value)
            ? $"{value:0.0}°C"
            : "--°C";
        var power = FormatPower(snapshot.LhmCpuPackagePowerWatts);
        return $"LHM 对照（EC 温度/Energy Meter 功耗不变）: {temperature} / {power}{status}";
    }

    /// <summary>
    /// Renders the curve status line from the pure <see cref="FanCurveStatusFormatter"/> result, so a
    /// Guardian/lease safety-gate refusal is never displayed as a sensor failure. Only the toggle
    /// side effects stay here: a failed pass and a completed restore always drop curve control.
    /// </summary>
    private void DisplayCurveState(FanCurveStatus status, FanCurveDecision decision, FanCurveApplyResult? applied)
    {
        CurvePreviewText.Text = status.Text;
        if (applied is { Success: false } ||
            (applied is { Success: true } && decision.Action == FanCurveAction.RestoreAutomatic))
            SetCurveToggle(false);
    }

    /// <summary>
    /// Reports fixed-RPM feedback transitions (fault / recovery) that an already completed
    /// sampling pass produced. Reads snapshot fields only: no hardware call, no extra sample.
    /// </summary>
    private void ReportFeedbackTransition(HardwareSnapshot snapshot, bool emergencyFullSpeed, OsdSample sample)
    {
        var previous = _lastRpmFeedbackState;
        var current = snapshot.RpmFeedbackState;
        _lastRpmFeedbackState = current;
        if (previous is null || previous == current)
            return;
        if (current == FanRpmFeedbackState.Fault)
        {
            // The 90 °C emergency message above already explained this fault.
            if (emergencyFullSpeed)
                return;
            ShowOsd(OsdEvents.FixedRpmFault(snapshot.FanFault, sample));
            return;
        }
        if (previous == FanRpmFeedbackState.Fault)
            ShowOsd(OsdEvents.FixedRpmRecovered(sample));
    }

    /// <summary>
    /// Reports control outcomes decided by the sampling pass (Guardian stop → EC automatic restored,
    /// recovery failure, curve apply failure). Deduplicated by outcome signature so a recovery that
    /// keeps being retried shows a single popup per distinct failure.
    /// </summary>
    private void ReportCurveOutcome(FanCurveApplyResult? applied, OsdSample sample)
    {
        var enabled = _curveController?.IsEnabled == true;
        var wasEnabled = _lastCurveEnabled;
        _lastCurveEnabled = enabled;
        if (applied is null)
            return;
        if (applied is { Success: false })
        {
            var key = "curve-fail:" + applied.Error;
            if (_lastControlOsdKey == key)
                return;
            _lastControlOsdKey = key;
            ShowOsd(OsdEvents.GuardianRecoveryFailed(applied.Error, sample));
            return;
        }
        if (!wasEnabled || enabled)
            return;
        // Curve was enabled on the previous pass and is now off: Guardian stopped or the
        // restore ran, which already wrote EC automatic. Only the popup is added here.
        _lastControlOsdKey = null;
        ShowOsd(OsdEvents.GuardianRecovered(sample));
        RecordDiagnosticEvent("Guardian 恢复：EC 自动已生效");
    }

    private void UpdateGuardianStatus()
    {
        var running = GuardianAvailability.IsRunning();
        GuardianStatusText.Text = running
            ? "Guardian: Running · 手动控制异常可恢复 EC 自动"
            : "Guardian: 未运行 · 无法启用曲线、固定 RPM 或满速";
        GuardianStatusText.Foreground = running
            ? System.Windows.Media.Brushes.DarkGreen
            : System.Windows.Media.Brushes.DarkOrange;
        if (!running && _curveController?.IsEnabled == true)
        {
            SetCurveToggle(false);
            CurvePreviewText.Text = "Guardian 已停止，正在恢复 EC 自动。";
        }
    }

    private void EnsureTaskbars()
    {
        var handles = NativeMethods.EnumerateTaskbars().ToArray();
        foreach (var stale in _taskbarWindows.Where(window =>
                     !window.IsNativeWindowAlive || !handles.Contains(window.TaskbarHandle)).ToArray())
        {
            _taskbarWindows.Remove(stale);
            stale.Close();
        }

        foreach (var handle in handles)
        {
            if (_taskbarWindows.Any(window => window.TaskbarHandle == handle && window.IsNativeWindowAlive))
                continue;
            var window = new TaskbarStatusWindow(_taskbarSettings, handle);
            _taskbarWindows.Add(window);
            window.OpenRequested += ShowMainWindow;
            window.RestoreRequested += async () => await RestoreAutomaticAsync();
            window.PowerModeRequested += mode => _ = ExecuteAsync($"任务栏：{FormatMode(mode)}模式", h => h.SetPowerMode(mode));
            window.FullSpeedRequested += () => _ = ExecuteAsync("任务栏：风扇满速", h => h.SetFanDuty(100), manual: true);
            window.FixedRpmRequested += rpm => _ = ExecuteAsync($"任务栏：固定 RPM {rpm}", h => h.SetFixedRpm(rpm), manual: true, requiresSafeTemperature: true);
            window.CurveRequested += EnableCurveFromMenu;
            // Same entry point as the main window's 任务栏设置 button: the taskbar menu must not
            // grow a second settings path. Runs on the UI thread (WPF menu click).
            window.TaskbarSettingsRequested += OpenTaskbarSettingsFromMenu;
            window.ExitRequested += RequestExit;
            window.Closed += (_, _) => _taskbarWindows.Remove(window);
            window.Show();
        }
    }

    private void OpenTaskbarSettingsFromMenu() => TaskbarSettingsClick(this, new RoutedEventArgs());

    /// <summary>
    /// Queues one event-driven taskbar refresh (shell re-created, display/DPI/taskbar setting
    /// changed). Called from the window hook, so it only classifies and marshals: the window
    /// work runs on the dispatcher, never inside the native callback, and repeated
    /// notifications collapse into the single queued refresh.
    /// </summary>
    private void ScheduleTaskbarRefresh(TaskbarRefreshTrigger trigger)
    {
        if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        if (!_taskbarRefresh.TryQueue())
        {
            AppLog.Write($"taskbar refresh: {trigger} coalesced (refresh already queued)");
            return;
        }
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => RunTaskbarRefresh(trigger)));
        }
        catch (Exception ex)
        {
            _taskbarRefresh.Complete();
            AppLog.Write($"taskbar refresh: scheduling {trigger} failed", ex);
        }
    }

    /// <summary>
    /// Runs the queued refresh on the UI thread. Only the taskbar windows are touched:
    /// a shell restart re-enumerates and re-mounts them (old taskbar handles are closed),
    /// everything else just repositions the existing windows. No snapshot is read and no
    /// EC/fan call is made, so this path cannot write hardware.
    /// </summary>
    private void RunTaskbarRefresh(TaskbarRefreshTrigger trigger)
    {
        try
        {
            if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            if (TaskbarToggle.IsChecked != true)
                return;
            // Claim the interval slot so the next periodic pass does not repeat the work; the
            // event itself is allowed to bypass the interval (that is the point of the event).
            _taskbarRefresh.MarkRefreshed(System.Diagnostics.Stopwatch.GetTimestamp());
            AppLog.Write($"taskbar refresh: {trigger}");
            if (TaskbarRefreshPolicy.RemountsTaskbarWindows(trigger))
                EnsureTaskbars();
            foreach (var taskbar in _taskbarWindows.ToArray())
                taskbar.TryAttachToTaskbar();
        }
        catch (Exception ex)
        {
            AppLog.Write($"taskbar refresh after {trigger} failed", ex);
        }
        finally
        {
            _taskbarRefresh.Complete();
        }
    }

    private void RecordDiagnosticEvent(string message)
    {
        try { _diagnosticEvents.Append(TelemetryHistory.Classify(message).ToString(), message); }
        catch (Exception ex) { AppLog.Write("diagnostic event append failed", ex); }
    }

    private void UpdateAutomationPauseButton()
    {
        if (AutomationPauseButton is not null)
        {
            AutomationPauseButton.Content = _automationSettings.Paused ? "恢复自动" : "暂停自动";
            AutomationPauseButton.IsEnabled = _automationSettings.Enabled;
        }
    }

    private void AutomationPauseClick(object sender, RoutedEventArgs e)
    {
        if (!_automationSettings.Enabled) return;
        var updated = _automationSettings with { Paused = !_automationSettings.Paused };
        try
        {
            _automationStore.Save(updated, _profiles.Select(p => p.Name).ToArray());
            _automationSettings = updated;
            _automationDebouncer.Reset();
            UpdateAutomationPauseButton();
            var message = updated.Paused ? "方案自动切换已暂停。" : "方案自动切换已恢复。";
            ProfileStatusText.Text = message;
            AppLog.Write(message);
            RecordDiagnosticEvent(message);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.GetBaseException().Message, "自动切换状态未保存", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AutomationClick(object sender, RoutedEventArgs e)
    {
        var editor = new ProfileAutomationWindow(_automationSettings, _profiles.ToArray(), _automationStatusBoard.Snapshot(), new ProfileAutomationHistory().Path) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } settings) return;
        try
        {
            var previousRules = _automationSettings.Rules;
            _automationStore.Save(settings, _profiles.Select(p => p.Name).ToArray());
            _automationSettings = settings;
            SyncAutomationRuleStatuses(previousRules, settings);
            _automationDebouncer.Reset();
            UpdateAutomationPauseButton();
            AppLog.Write($"profile automation settings saved: enabled={settings.Enabled}, paused={settings.Paused}, rules={settings.Rules.Count}");
            RecordDiagnosticEvent("自动方案配置已保存");
            ProfileStatusText.Text = settings.Enabled ? (settings.Paused ? "自动方案已暂停。" : "自动方案已启用。") : "自动方案已关闭。";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message, "保存自动方案失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task EvaluateAutomationAsync()
    {
        // Re-entry guard: a tick that lands while the background inventory of the previous tick is still in
        // flight is dropped instead of queuing a competing tracker update.
        if (_automationEvaluationInFlight) return;
        _automationEvaluationInFlight = true;
        try
        {
            await EvaluateAutomationCoreAsync();
        }
        finally
        {
            _automationEvaluationInFlight = false;
        }
    }

    private async Task EvaluateAutomationCoreAsync()
    {
        if (!_automationSettings.Enabled || _automationSettings.Paused)
        {
            _automationDebouncer.Reset();
            _automationProcessTracker.Reset();
            return;
        }
        if (_applyingProfile || _calibrationRunCts is not null)
        {
            _automationDebouncer.Reset();
            return;
        }
        if (_closing || _demo || !GuardianAvailability.IsRunning() || _latestSnapshot is not { Error: null } snapshot ||
            DateTimeOffset.Now - snapshot.Timestamp > TimeSpan.FromSeconds(5) ||
            OwnedWindows.OfType<Window>().Any(window => window.IsVisible))
        {
            _automationDebouncer.Reset();
            _automationProcessTracker.Reset();
            return;
        }
        var profileOwnedControl = _appliedProfileName is not null;
        if ((!profileOwnedControl && _restoreRequired) ||
            (!profileOwnedControl && _curveController?.IsEnabled == true) ||
            snapshot.FanFault is not null || snapshot.RpmFeedbackState == FanRpmFeedbackState.Fault ||
            _curveController is { IsEnabled: false, RecoveryRequired: true } || _hardware?.IsRecoveryPending == true || _hardware?.IsEmergencyFullSpeed == true)
        {
            _automationDebouncer.Reset();
            _automationProcessTracker.Reset();
            return;
        }
        var power = Forms.SystemInformation.PowerStatus.PowerLineStatus;
        bool? ac = power == Forms.PowerLineStatus.Online ? true : power == Forms.PowerLineStatus.Offline ? false : null;
        var now = DateTimeOffset.Now;
        var processRules = _automationSettings.Rules.Where(rule => rule.Enabled &&
            rule.Trigger is ProfileAutomationTrigger.ProcessStarted or ProfileAutomationTrigger.ProcessExited).ToArray();
        var watchedNames = processRules.Select(rule => ProfileAutomationProcessCondition.CleanOptional(rule.ProcessName))
            .Where(name => name is not null).Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IReadOnlyList<ProfileAutomationProcessInfo> runningProcesses = [];
        if (watchedNames.Length > 0)
        {
            var needsExecutablePath = processRules.Any(rule => ProfileAutomationProcessCondition.CleanOptional(rule.ProcessPath) is not null);
            var needsCommandLine = processRules.Any(rule =>
                ProfileAutomationProcessCondition.CleanOptional(rule.ProcessCommandLine) is not null ||
                ProfileAutomationProcessCondition.CleanOptional(rule.ProcessCommandLineRegex) is not null);
            try
            {
                // Only names, executable paths and (when a rule needs it) command lines of watched processes are
                // read. Details stay in memory for matching; the sampler never logs or persists them.
                // The inventory runs on a worker thread: Process.GetProcesses() plus per-process WMI command-line
                // lookups can stall for hundreds of milliseconds and must never block the dispatcher.
                runningProcesses = await Task.Run(() => _automationProcessSampler.Capture(
                    watchedNames,
                    needsExecutablePath,
                    needsCommandLine));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
            {
                _automationDebouncer.Reset();
                _automationProcessTracker.Reset();
                AppLog.Write("process automation inventory failed; process rules skipped", ex);
                RecordDiagnosticEvent($"进程自动规则采样失败：{ex.GetBaseException().Message}");
                return;
            }
            // Shutdown may have begun while the inventory ran on the worker thread. Everything below mutates
            // window state (tracker, debouncer, profile apply), so the late result is dropped instead.
            if (_closing) return;
        }
        var context = _automationProcessTracker.AddProcessInstances(
            new ProfileAutomationContext(ac, snapshot.CpuUsagePercent, now), watchedNames, runningProcesses);
        ReportSkippedProcessRules(processRules, context);
        RecordAutomationRuleStatuses(context, now);
        var rule = _automationDebouncer.Evaluate(_automationSettings, context, now, _appliedProfileName);
        if (rule is null)
        {
            _automationProcessTracker.ClearTransitions();
            return;
        }
        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Name, rule.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            _automationStatusBoard.RecordRejected(rule.Name, DateTimeOffset.Now);
            AppLog.Write("automation selected rule references a missing profile");
            RecordDiagnosticEvent("自动规则引用的方案不存在");
            _automationProcessTracker.ClearTransitions();
            await RecordAutomationExecutionAsync(rule, ProfileAutomationExecutionOutcome.Rejected,
                ProfileAutomationExecutionReason.MissingProfile, TimeSpan.Zero);
            return;
        }
        AppLog.Write("automation selected a rule for profile application");
        RecordDiagnosticEvent("自动规则触发方案应用");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ControlAttempt attempt;
        try
        {
            attempt = await ApplyProfileAsync(profile, automation: true);
        }
        catch
        {
            await RecordAutomationExecutionAsync(rule, ProfileAutomationExecutionOutcome.Rejected,
                ProfileAutomationExecutionReason.Exception, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            throw;
        }
        // Refine the tick that was just recorded: a rejected apply must not keep showing "condition met".
        if (attempt.Success) _automationStatusBoard.RecordApplied(rule.Name, DateTimeOffset.Now);
        else _automationStatusBoard.RecordRejected(rule.Name, DateTimeOffset.Now);
        await RecordAutomationExecutionAsync(rule,
            attempt.Success ? ProfileAutomationExecutionOutcome.Applied : ProfileAutomationExecutionOutcome.Rejected,
            attempt.Success ? ProfileAutomationExecutionReason.Applied : ProfileAutomationExecutionReason.Rejected,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    private static async Task RecordAutomationExecutionAsync(ProfileAutomationRule rule,
        ProfileAutomationExecutionOutcome outcome, ProfileAutomationExecutionReason reason, TimeSpan duration)
    {
        // Awaited under the existing evaluation re-entry guard, after Apply has released its gate.
        // Never queue unbounded fire-and-forget work or expose exception/name/condition text on failure.
        try
        {
            var recorded = await Task.Run(() => new ProfileAutomationHistory().Record(
                rule.Name, rule.ProfileName, outcome, reason, duration));
            if (!recorded) AppLog.Write("automation history unavailable");
        }
        catch { AppLog.Write("automation history unavailable"); }
    }

    /// <summary>
    /// Feeds this tick into the runtime status board and drops the rows of rules that are no longer active, so a
    /// disabled, renamed or deleted rule cannot keep showing a stale line. Only closed reason codes and counters are
    /// recorded; no process path, command line or pattern ever reaches the board, and nothing here changes matching.
    /// </summary>
    private void RecordAutomationRuleStatuses(ProfileAutomationContext context, DateTimeOffset now)
    {
        _automationStatusBoard.RecordEvaluations(_automationSettings.Rules, context, now);
        _automationStatusBoard.Prune(_automationSettings.Rules.Where(rule => rule.Enabled));
    }

    /// <summary>
    /// Aligns the runtime status board with a saved rule set: editing any rule restarts the counters (a status always
    /// describes the rule text the user sees), while an unchanged set only drops rows of renamed or deleted rules.
    /// </summary>
    private void SyncAutomationRuleStatuses(IReadOnlyList<ProfileAutomationRule> previousRules, ProfileAutomationSettings settings)
    {
        if (!settings.Enabled || !previousRules.SequenceEqual(settings.Rules)) _automationStatusBoard.Reset();
        _automationStatusBoard.Prune(settings.Rules);
    }

    /// <summary>
    /// Reports, once per rule and failure code, why a process rule had to be skipped (denied process data or an
    /// unusable command-line pattern). <see cref="ProfileAutomationMatchDiagnostic.Describe"/> is log-safe by design:
    /// it carries no executable paths, command lines or patterns.
    /// </summary>
    private void ReportSkippedProcessRules(IReadOnlyList<ProfileAutomationRule> processRules, ProfileAutomationContext context)
    {
        var skipped = ProfileAutomationEngine.ExplainSkippedProcessRules(processRules, context);
        if (skipped.Count == 0)
        {
            _reportedProcessRuleSkips.Clear();
            return;
        }
        foreach (var diagnostic in skipped)
        {
            var message = diagnostic.Describe();
            if (!_reportedProcessRuleSkips.Add(message)) continue;
            AppLog.Write(message);
            RecordDiagnosticEvent(message);
        }
    }

    private void TaskbarSettingsClick(object sender, RoutedEventArgs e)
    {
        var editor = new TaskbarSettingsWindow(_taskbarSettings, _hotkeySettings) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } settings || editor.HotkeyResult is not { } hotkeys)
            return;
        try
        {
            _taskbarSettingsStore.Save(settings);
            _taskbarSettings = settings;
            _taskbarRefresh.ResetInterval();
            foreach (var window in _taskbarWindows.ToArray())
                window.Close();
            _taskbarWindows.Clear();
            if (TaskbarToggle.IsChecked == true)
                EnsureTaskbars();
            AppLog.Write("taskbar settings saved");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "保存任务栏设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        // Hotkeys are a separate file and a separate apply step: a failed taskbar save must not
        // drop a valid rebinding, and the registrations only change after the file was written.
        try
        {
            _hotkeySettingsStore.Save(hotkeys);
            ApplyHotkeySettings(hotkeys);
            AppLog.Write($"global hotkeys updated: {hotkeys.Describe()}");
        }
        catch (Exception ex)
        {
            AppLog.Write("hotkey settings save failed", ex);
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "保存快捷键设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DisplaySourceSettingsClick(object sender, RoutedEventArgs e)
    {
        var editor = new DisplaySourceSettingsWindow(_displaySourceSettings, BuildLhmStatusSummary()) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } settings)
            return;
        try
        {
            // Persistence is best effort per the existing settings pattern: a failed
            // write is reported and the running display source stays unchanged.
            _displaySourceStore.Save(settings);
        }
        catch (Exception ex)
        {
            AppLog.Write("display source settings save failed", ex);
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "保存主显示源失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _displaySourceSettings = settings;
        // A successfully saved selection clears the startup warning for good.
        _displaySourceWarning = null;
        AppLog.Write($"cpu display source set to {settings.Source}");
        RecordDiagnosticEvent($"主显示源切换：{CpuDisplaySourceSelector.DescribeSource(settings.Source)}");
        ReprojectRecentTelemetry();
        RefreshDisplayFromCachedSnapshot();
    }

    // Re-projects the in-memory (max 2 minute) trend rows onto the new display source from
    // their original EC / Energy Meter / LHM values. Pure: no hardware read, no disk write,
    // no control pass. Already written history is left untouched.
    private void ReprojectRecentTelemetry()
    {
        if (_recentTelemetry.Count == 0)
            return;
        var source = _demo ? CpuDisplaySource.EcEnergyMeter : _displaySourceSettings.Source;
        var reprojected = new List<HardwareTelemetryRecord>(_recentTelemetry.Count);
        while (_recentTelemetry.Count > 0)
            reprojected.Add(CpuDisplaySourceSelector.Reproject(_recentTelemetry.Dequeue(), source));
        foreach (var record in reprojected)
            _recentTelemetry.Enqueue(record);
        UpdateTelemetryChart();
    }

    private string? BuildLhmStatusSummary() => _latestSnapshot?.LhmStatus ?? (_demo ? DemoLhmText : null);

    // Re-renders the last sample with the new display source. Display-only: no WMI
    // read, no fan/EC write and no control pass is triggered.
    private void RefreshDisplayFromCachedSnapshot()
    {
        if (_latestSnapshot is not { } cached)
        {
            UpdateDisplaySourceStatus();
            return;
        }
        var updated = CpuDisplaySourceSelector.Apply(cached,
            _demo ? CpuDisplaySource.EcEnergyMeter : _displaySourceSettings.Source);
        _latestSnapshot = updated;
        DisplaySnapshot(updated);
        UpdateTrayStatus(updated, _demo || GuardianAvailability.IsRunning());
        foreach (var taskbar in _taskbarWindows.ToArray())
            taskbar.UpdateDisplaySnapshot(updated);
    }

    private void ProfileSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_synchronizingProfile || ProfileStatusText is null || ProfileCombo.SelectedItem is not FanProfile profile) return;
        ProfileStatusText.Text = $"待应用：{profile.Name} · {profile.Summary}" +
            (_appliedProfileName is null ? "" : $"（上次成功应用：{_appliedProfileName}）");
    }

    private async void ApplyProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is FanProfile profile) await ApplyProfileAsync(profile);
    }

    private async void ManageProfilesClick(object sender, RoutedEventArgs e)
    {
        var editor = new FanProfileManagerWindow(_profiles, (ProfileCombo.SelectedItem as FanProfile)?.Name, _calibration, () => _latestSnapshot) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } profiles || _closing) return;
        try
        {
            _profileStore.Save(profiles);
            var profileNames = profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retainedRules = _automationSettings.Rules.Where(r => profileNames.Contains(r.ProfileName)).ToArray();
            var rulesRemoved = retainedRules.Length != _automationSettings.Rules.Count;
            var rulesCleanupSaved = true;
            if (rulesRemoved)
            {
                var updatedAutomation = _automationSettings with { Rules = retainedRules };
                try
                {
                    _automationStore.Save(updatedAutomation, profileNames.ToArray());
                    _automationSettings = updatedAutomation;
                    _automationDebouncer.Reset();
                    RecordDiagnosticEvent("已移除引用已删除/改名方案的自动规则");
                }
                catch (Exception saveError)
                {
                    rulesCleanupSaved = false;
                    AppLog.Write("automation rules cleanup after profile changes failed", saveError);
                }
            }
            _synchronizingProfile = true;
            _profiles.Clear();
            foreach (var p in profiles) _profiles.Add(p);
            ProfileCombo.SelectedItem = _profiles.FirstOrDefault(p => string.Equals(p.Name, editor.SelectedName, StringComparison.OrdinalIgnoreCase)) ?? _profiles.First();
            ProfileStatusText.Text = rulesRemoved && rulesCleanupSaved
                ? "方案已保存；已移除引用已删除/改名方案的自动规则。"
                : rulesRemoved
                    ? "方案已保存，但自动规则配置未能同步清理；已关闭自动切换，请检查配置。"
                    : "方案已保存；尚未切换硬件。";
            if (rulesRemoved && !rulesCleanupSaved)
            {
                _automationSettings = _automationSettings with { Enabled = false, Paused = true };
                _automationDebouncer.Reset();
            }
            AppLog.Write($"profiles saved: {profiles.Count}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "保存方案失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { _synchronizingProfile = false; }
        if (editor.ApplyRequested && ProfileCombo.SelectedItem is FanProfile selected) await ApplyProfileAsync(selected);
    }

    private async Task<ControlAttempt> ApplyProfileAsync(FanProfile profile, bool automation = false)
    {
        if (_calibrationRunCts is not null || _calibrationRecoveryRequired)
            return ControlAttempt.Fail("自动标定正在采样或恢复未完成，请先恢复 EC 自动。");
        if (_closing || _applyingProfile) return ControlAttempt.Fail("方案应用进行中或程序正在退出，未执行。");
        profile.Validate();
        if (_demo)
        {
            ProfileStatusText.Text = $"演示预览：{profile.Name} · {profile.Summary}；未访问硬件。";
            ShowOsd(OsdEvents.ProfilePreview(profile.Name, profile.Summary));
            return ControlAttempt.Fail("演示模式：未执行任何硬件写入。");
        }
        if (!await _gate.WaitAsync(0))
        {
            ProfileStatusText.Text = "硬件操作进行中，请稍后应用方案。";
            if (automation) _automationDebouncer.Reset();
            return ControlAttempt.Fail("硬件操作进行中，请稍后应用方案。");
        }
        var attempt = ControlAttempt.Fail("方案未能应用。");
        _applyingProfile = true;
        try
        {
            ProfileStatusText.Text = $"正在应用：{profile.Name}…";
            var result = await Task.Run(() =>
            {
                if (profile.FanMode != ProfileFanMode.Automatic && !GuardianAvailability.IsRunning())
                    throw new InvalidOperationException("该方案需要正在运行的 Guardian 服务。");
                if (_hardware!.IsEmergencyFullSpeed)
                    throw new InvalidOperationException("当前为紧急满速，请处理温度并显式恢复 EC 自动后再应用方案。");
                var before = _hardware.ReadSnapshot();
                if (before.Error is not null || before.PowerMode == PowerMode.Unknown)
                    throw new InvalidOperationException($"应用前读取硬件失败：{before.Error ?? "模式未知"}");
                if (profile.FanMode is ProfileFanMode.FixedRpm or ProfileFanMode.Curve &&
                    (before.CpuTemperatureC is null or >= 90))
                    throw new InvalidOperationException("温度达到 90°C，不能应用固定 RPM 或普通曲线方案，请使用满速保护。");
                if (profile.FanMode is ProfileFanMode.FixedRpm or ProfileFanMode.Curve &&
                    (before.FanFault is not null || before.RpmFeedbackState == FanRpmFeedbackState.Fault))
                    throw new InvalidOperationException("当前存在风扇保护故障，请先显式恢复 EC 自动后再应用手动方案。");
                return FanProfileApplication.Apply(profile, before.PowerMode, _curveSettings, new ProfileTarget(this));
            });
            SetCurveToggle(_curveController?.IsEnabled == true);
            ProfileStatusText.Text = result.Message;
            _appliedProfileName = result.Success ? profile.Name : null;
            if (result.Success)
            {
                CurveConfigText.Text = $"配置: {_curveConfigStore.Path}";
                _automationProcessTracker.ClearTransitions();
                if (!automation) _automationDebouncer.Reset();
            }
            else if (automation) _automationDebouncer.Reset();
            AppLog.Write(result.Message);
            RecordDiagnosticEvent($"方案应用{(result.Success ? "成功" : "失败")}：{profile.Name} · {result.Message}");
            ShowOsd(result.Success
                ? OsdEvents.ProfileApplied(profile.Name, profile.Summary, DescribeOsdSample())
                : OsdEvents.ProfileFailed(result.Message, DescribeOsdSample()));
            attempt = result.Success ? ControlAttempt.Ok(result.Message) : ControlAttempt.Fail(result.Message);
        }
        catch (Exception ex)
        {
            ProfileStatusText.Text = $"未应用：{ex.GetBaseException().Message}";
            AppLog.Write("profile apply rejected", ex);
            RecordDiagnosticEvent($"方案应用失败：{profile.Name} · {ex.GetBaseException().Message}");
            ShowOsd(OsdEvents.ProfileFailed(ex.GetBaseException().Message, DescribeOsdSample()));
            if (automation) _automationDebouncer.Reset();
            attempt = ControlAttempt.Fail(ex.GetBaseException().Message);
        }
        finally
        {
            _applyingProfile = false;
            _gate.Release();
        }
        await RefreshSnapshotAsync();
        return attempt;
    }

    private sealed class ProfileTarget(MainWindow owner) : IFanProfileTarget
    {
        public void RestoreAutomatic()
        {
            if (owner._curveController is { } curve && (curve.IsEnabled || curve.RecoveryRequired)) curve.Disable();
            else owner._hardware!.RestoreAutomatic();
            owner._restoreRequired = false;
            owner._calibrationRecoveryRequired = false;
            owner._leaseStore.ClearOwned(Environment.ProcessId);
        }
        public void SetPowerMode(PowerMode mode) => owner._hardware!.SetPowerMode(mode);
        public void SetCurve(FanCurveSettings settings) => owner.ApplyCurveSettings(settings);
        public void ApplyFanMode(FanProfile profile)
        {
            if (profile.FanMode == ProfileFanMode.Automatic) return;
            if (!GuardianAvailability.IsRunning()) throw new InvalidOperationException("Guardian 已停止。");
            if (profile.FanMode == ProfileFanMode.Curve)
            {
                // Same explicit takeover as the interactive toggle: the entry point publishes the
                // lease once, the curve write itself never does.
                owner.TakeOverCurveLease();
                owner._curveController!.Enable();
                var applied = owner._curveController.Apply(owner._hardware!.ReadSnapshot());
                if (!applied.Success || applied.Decision.Action == FanCurveAction.RestoreAutomatic)
                    throw new InvalidOperationException(applied.Error ?? "采样异常，曲线未启动。");
                return;
            }
            owner._leaseStore.WriteActive(Environment.ProcessId, "profile-" + profile.FanMode,
                DateTimeOffset.UtcNow, GuardianProcessIdentity.CurrentStartUtc);
            owner._restoreRequired = true;
            if (profile.FanMode == ProfileFanMode.FixedRpm) owner._hardware!.SetFixedRpm(profile.FixedRpm);
            else owner._hardware!.SetFanDuty(100);
        }
    }

    private void HistoryClick(object sender, RoutedEventArgs e)
    {
        new HistoryWindow(_telemetry.Directory) { Owner = this }.ShowDialog();
    }

    private void DiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var window = new DiagnosticsWindow(
            _curveConfigStore.Path,
            _telemetry.JsonlPath,
            _telemetry.CsvPath,
            _demo,
            _diagnosticEvents.Path)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void EditCurveClick(object sender, RoutedEventArgs e)
    {
        if (_curveController?.IsEnabled == true || _restoreRequired)
        {
            System.Windows.MessageBox.Show(this, "请先恢复 EC 自动，再编辑曲线。", "无法编辑曲线",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ProfileCombo.SelectedItem is not FanProfile selected) return;
        var editor = new FanCurveEditorWindow(selected.FanCurve, _calibration, () => _latestSnapshot) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } settings)
            return;

        try
        {
            var index = _profiles.IndexOf(selected);
            if (index < 0) return;
            var updated = selected with { FanCurve = settings };
            var profiles = _profiles.ToArray();
            profiles[index] = updated;
            _profileStore.Save(profiles);
            _synchronizingProfile = true;
            try
            {
                _profiles[index] = updated;
                ProfileCombo.SelectedItem = updated;
            }
            finally { _synchronizingProfile = false; }
            ProfileStatusText.Text = $"已保存 {updated.Name} 的曲线；点击应用后切换硬件。";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message, "保存曲线失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EditCalibrationClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _calibrationRunCts is not null || _curveController?.IsEnabled == true || _curveController?.RecoveryRequired == true || _restoreRequired)
        {
            System.Windows.MessageBox.Show(this, "请先恢复 EC 自动，再编辑标定。", "无法编辑标定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var editor = new FanCalibrationEditorWindow(_calibration, _demo ? null : RunFanCalibrationAsync) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } calibration || _closing)
            return;
        await _gate.WaitAsync();
        try
        {
            if (_closing) return;
            // Recheck after the modal dialog: hotkeys can change control ownership while it is open.
            if (_calibrationRunCts is not null || _calibrationRecoveryRequired || _curveController?.IsEnabled == true || _curveController?.RecoveryRequired == true || _restoreRequired)
                throw new InvalidOperationException("请先恢复 EC 自动，再保存标定。");
            // Validate the active curve against the candidate before any hardware or persistent change.
            // RPM-target curves may become unreachable with a new measured table.
            _curveSettings.ValidateCalibration(calibration);
            var previous = _calibration;
            _hardware?.UpdateCalibration(calibration);
            try
            {
                _calibrationStore.Save(calibration);
            }
            catch
            {
                _hardware?.UpdateCalibration(previous);
                throw;
            }
            _calibration = calibration;
            // The curve engine/controller derive the expected RPM from the calibration table, so a
            // new table also means new curve objects. Control is guaranteed to be off here (guard
            // above), and Dispose of a disabled controller touches no hardware.
            _curveController?.Dispose();
            _curvePreview = new FanCurveEngine(_curveSettings, calibration);
            _curveController = _demo ? null : new FanCurveController(_curveTarget!, _curveSettings, calibration);
            UpdateCalibrationRange();
            AppLog.Write("fan calibration saved; subsequent fixed RPM requests use the new calibration");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message, "保存标定失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    // No detached timeout task: even a slow synchronous hardware call is awaited under _gate.
    // A deadline prevents further writes, but never releases ownership while a call is in flight.
    private async Task<FanCalibration> RunFanCalibrationAsync(CancellationToken cancellationToken,
        IProgress<FanCalibrationSamplingProgress> progress)
    {
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("硬件操作进行中，不能开始自动标定。");
        CancellationTokenSource? runCts = null;
        TaskCompletionSource<ControlAttempt>? completion = null;
        var takeoverAttempted = false;
        var restored = false;
        try
        {
            if (_demo || _closing || _hardware is null || _curveTarget is null || _calibrationRunCts is not null ||
                _curveController?.IsEnabled == true || _curveController?.RecoveryRequired == true ||
                _restoreRequired || _calibrationRecoveryRequired || _hardware.RecoveryRequired ||
                _hardware.IsRecoveryPending || _hardware.IsEmergencyFullSpeed)
                throw new InvalidOperationException("当前不处于可标定的 EC 自动状态；请先恢复 EC 自动。");
            runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runCts.CancelAfter(FanCalibrationSampling.TotalTimeout);
            completion = new TaskCompletionSource<ControlAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_calibrationCancellationSync)
            {
                _calibrationCompletion = completion;
                _calibrationRunCts = runCts;
            }
            var token = runCts.Token;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            return await Task.Run(async () =>
            {
                void CheckDeadline()
                {
                    token.ThrowIfCancellationRequested();
                    if (_closing) throw new OperationCanceledException(token);
                    if (elapsed.Elapsed >= FanCalibrationSampling.TotalTimeout)
                        throw new TimeoutException("自动标定达到 4 分钟上限。");
                }
                void CheckLease()
                {
                    CheckDeadline();
                    var valid = GuardianAvailability.IsRunning() && HasValidControlLease();
                    _curveLeaseGate.SetPass(valid);
                    if (!valid) throw new InvalidOperationException("Guardian 已停止或标定租约失效，禁止继续写入或续租。");
                }
                void CheckSample(HardwareSnapshot snapshot, bool starting = false)
                {
                    if (FanCalibrationSampling.SampleError(snapshot, DateTimeOffset.UtcNow, starting) is { } error)
                        throw new InvalidOperationException(error);
                    if (_hardware.IsEmergencyFullSpeed || _hardware.IsRecoveryPending)
                        throw new InvalidOperationException("风扇紧急保护或恢复已占用控制。");
                }
                CheckDeadline();
                if (!GuardianAvailability.IsRunning()) throw new InvalidOperationException("Guardian 未运行。");
                var readClock = System.Diagnostics.Stopwatch.StartNew();
                var snapshot = _hardware.ReadSnapshot(); // Raw EC only; never runs fixed-RPM/curve loops.
                CheckDeadline();
                if (!GuardianAvailability.IsRunning()) throw new InvalidOperationException("Guardian 已停止。");
                CheckSample(snapshot, starting: true);
                if (readClock.Elapsed > FanCalibrationSampling.MaximumSampleAge)
                    throw new InvalidOperationException("标定前硬件读取超过 1 秒。");
                if (snapshot.FanControlMode != FanControlMode.FirmwareAutomatic || _hardware.RecoveryRequired)
                    throw new InvalidOperationException("硬件不处于 EC 自动状态。");
                _restoreRequired = true;
                takeoverAttempted = true; // Even a partially failed lease publication must restore.
                TakeOverCurveLease(); // Exactly one explicit takeover for this run.
                _curveLeaseGate.Close();
                var machine = new FanCalibrationSampling(DateTimeOffset.UtcNow);
                // Include the preflight timestamp in uniqueness/order checks; replaying it after
                // takeover must not count as a fresh heartbeat or a stable sample.
                CheckLease();
                machine.Observe(snapshot, DateTimeOffset.UtcNow, true, true);
                if (machine.IsTerminal) throw new InvalidOperationException(machine.Message);
                progress.Report(machine.Progress);
                while (!machine.IsTerminal)
                {
                    CheckLease();
                    machine.CheckDeadline(DateTimeOffset.UtcNow);
                    if (machine.IsTerminal) break;
                    if (machine.State == FanCalibrationSamplingState.AwaitingDuty)
                    {
                        CheckSample(snapshot); // Only a just-completed <=1 s raw sample permits a write.
                        CheckLease();
                        try
                        {
                            _curveTarget.SetCurveDuty(machine.DutyPercent);
                            CheckLease();
                            CheckSample(snapshot);
                            machine.DutyApplied(DateTimeOffset.UtcNow);
                        }
                        finally { _curveLeaseGate.Close(); }
                    }
                    if (machine.IsTerminal) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), token);
                    CheckLease();
                    readClock.Restart();
                    snapshot = _hardware.ReadSnapshot();
                    CheckLease();
                    CheckSample(snapshot);
                    if (readClock.Elapsed > FanCalibrationSampling.MaximumSampleAge)
                        throw new InvalidOperationException("硬件采样超过 1 秒，停止标定。");
                    machine.Observe(snapshot, DateTimeOffset.UtcNow, true, true);
                    progress.Report(machine.Progress);
                    if (machine.State is FanCalibrationSamplingState.Aborted or FanCalibrationSamplingState.Cancelled)
                        throw new InvalidOperationException(machine.Message);
                    // A duplicate timestamp is not a fresh completed sample and must never renew a lease.
                    if (!machine.LastObservationWasFresh)
                    {
                        _curveLeaseGate.Close();
                        continue;
                    }
                    CheckLease(); // Never WriteActive after lease expiry (including a slow read).
                    CheckSample(snapshot);
                    _leaseStore.WriteActive(Environment.ProcessId, "fan-calibration", DateTimeOffset.UtcNow,
                        GuardianProcessIdentity.CurrentStartUtc);
                    CheckLease();
                    _curveLeaseGate.Close();
                }
                if (machine.State != FanCalibrationSamplingState.Completed || machine.Result is null)
                    throw new InvalidOperationException(machine.Message);
                CheckDeadline();
                machine.Result.ValidateForFixedRpm();
                return machine.Result;
            });
        }
        finally
        {
            try
            {
                _curveLeaseGate.Close();
                if (takeoverAttempted)
                {
                    try
                    {
                        // Cancellation/deadline does not cancel restoration or abandon an in-flight call.
                        await Task.Run(() => _curveTarget!.RestoreAutomatic());
                        _restoreRequired = false;
                        _calibrationRecoveryRequired = false;
                        restored = true;
                    }
                    catch (Exception ex)
                    {
                        _restoreRequired = true;
                        _calibrationRecoveryRequired = true;
                        AppLog.Write("calibration automatic recovery failed; lease retained without renewal", ex);
                        throw new InvalidOperationException("EC 自动恢复失败；标定未应用，租约保留且不续租。请显式恢复 EC 自动。", ex);
                    }
                }
            }
            finally
            {
                _curveLeaseGate.Close();
                lock (_calibrationCancellationSync)
                {
                    if (runCts is not null)
                    {
                        _calibrationRunCts = null;
                        _calibrationCompletion = null;
                        runCts.Dispose();
                    }
                }
                completion?.TrySetResult(restored ? ControlAttempt.Ok("已取消/完成采样并恢复 EC 自动。") :
                    ControlAttempt.Fail("采样未接管或 EC 自动恢复未完成。"));
                _gate.Release();
            }
        }
    }

    private Task<ControlAttempt>? CancelCalibrationRun()
    {
        lock (_calibrationCancellationSync)
        {
            _calibrationRunCts?.Cancel();
            return _calibrationCompletion?.Task;
        }
    }

    private void UpdateCalibrationRange(string? warning = null)
    {
        CalibrationRangeText.Text = $"估计范围：{_calibration.RpmForDuty(40)}–{_calibration.RpmForDuty(100)} RPM（40%–100% duty）";
        CalibrationRangeText.ToolTip = warning is null
            ? $"标定：{_calibrationStore.Path}；实际可达转速受负载和温度影响。"
            : $"{warning}\n已使用默认标定：{_calibrationStore.Path}";
        CalibrationWarningText.Text = warning is null ? "" : "标定配置无效，已回退默认表；详情见提示和日志。";
        CalibrationWarningText.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyCurveSettings(FanCurveSettings settings)
    {
        // Reject incompatible targets before persisting or disposing the active controller.
        settings.ValidateCalibration(_calibration);
        _curveConfigStore.Save(settings);
        _curveController?.Dispose();
        _curveSettings = settings;
        _curvePreview = new FanCurveEngine(settings, _calibration);
        _curveController = _demo ? null : new FanCurveController(_curveTarget!, settings, _calibration);
    }

    private async void FanCurveToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _demo || _curveController is null || _synchronizingCurveToggle)
            return;

        if (_calibrationRunCts is not null)
        {
            SetCurveToggle(false);
            StatusText.Text = "自动标定采样中，拒绝切换曲线。";
            return;
        }
        if (FanCurveToggle.IsChecked == true)
        {
            if (_restoreRequired || _curveController.RecoveryRequired || _hardware!.RecoveryRequired)
            {
                SetCurveToggle(false);
                System.Windows.MessageBox.Show(this, "当前仍处于手动风扇控制或恢复未完成，请先恢复 EC 自动。", "无法启用安全曲线",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!GuardianAvailability.IsRunning())
            {
                SetCurveToggle(false);
                System.Windows.MessageBox.Show(this,
                    "未检测到正在运行的 MiniProToolkit Guardian 服务。请先安装并启动 Guardian，再启用曲线。",
                    "Guardian 未运行", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!await _gate.WaitAsync(0))
            {
                SetCurveToggle(false);
                StatusText.Text = "硬件操作进行中，请稍后启用曲线。";
                return;
            }
            try
            {
                var before = await Task.Run(() => _hardware!.ReadSnapshot());
                if (before.Error is not null || before.CpuTemperatureC is null)
                    throw new InvalidOperationException($"无法确认温度，已拒绝启用曲线：{before.Error ?? "温度不可用"}");
                if (before.CpuTemperatureC >= 90 || _hardware!.IsEmergencyFullSpeed)
                    throw new InvalidOperationException("温度达到 90°C 或处于紧急满速锁定，不能启用普通曲线；请处理温度并显式恢复 EC 自动。");
                if (before.FanFault is not null || before.RpmFeedbackState == FanRpmFeedbackState.Fault)
                    throw new InvalidOperationException("存在风扇保护故障，请先显式恢复 EC 自动后再启用曲线。");
                TakeOverCurveLease();
                _curveController.Enable();
            }
            catch (Exception ex)
            {
                SetCurveToggle(false);
                ShowOsd(OsdEvents.CurveRejected(ex.GetBaseException().Message, DescribeOsdSample()));
                System.Windows.MessageBox.Show(this, ex.GetBaseException().Message, "无法启用安全曲线",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { _gate.Release(); }
            AppLog.Write("fan curve enabled with Guardian");
            ShowOsd(OsdEvents.CurveEnabled(DescribeOsdSample()));
            await RefreshSnapshotAsync();
            return;
        }

        await DisableCurveAsync();
    }

    private async Task<ControlAttempt> DisableCurveAsync(bool interactive = true)
    {
        if (_curveController is null || (!_curveController.IsEnabled && !_curveController.RecoveryRequired))
            return ControlAttempt.Ok("安全曲线未运行。");
        if (!await _gate.WaitAsync(0))
        {
            SetCurveToggle(true);
            return ControlAttempt.Fail("硬件操作进行中，请稍后再停止安全曲线。");
        }

        var attempt = ControlAttempt.Fail("停止安全曲线失败。");
        try
        {
            await Task.Run(_curveController.Disable);
            _leaseStore.ClearOwned(Environment.ProcessId);
            AppLog.Write("fan curve disabled and EC automatic requested");
            RecordDiagnosticEvent("风扇曲线已停止并请求 EC 自动恢复");
            ShowOsd(OsdEvents.CurveStopped(DescribeOsdSample()));
            attempt = ControlAttempt.Ok("已关闭安全曲线并请求 EC 自动恢复。");
        }
        catch (Exception ex)
        {
            AppLog.Write("fan curve disable failed", ex);
            SetCurveToggle(true);
            ShowOsd(OsdEvents.CurveStopFailed(ex.GetBaseException().Message));
            attempt = ControlAttempt.Fail(ex.GetBaseException().Message);
            if (interactive)
                System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                    "停止安全曲线失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _gate.Release();
        }
        await RefreshSnapshotAsync();
        return attempt;
    }

    private async void TaskbarToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (TaskbarToggle.IsChecked == true)
            await RefreshSnapshotAsync();
        else
        {
            foreach (var window in _taskbarWindows.ToArray())
                window.Close();
            _taskbarWindows.Clear();
        }
        _taskbarRefresh.ResetInterval();
    }

    /// <summary>
    /// Single control entry point for every power-mode / manual fan write. Returns whether the write happened so a
    /// non-interactive caller (the CLI relay) can report an exact result; <paramref name="interactive"/> only controls
    /// the failure MessageBox, never the gate, the Guardian check or the restore-on-failure behaviour.
    /// </summary>
    private async Task<ControlAttempt> ExecuteAsync(string label, Action<PowerSwitchHardware> action, bool manual = false, bool restore = false, bool requiresSafeTemperature = false, bool interactive = true)
    {
        if (_calibrationRunCts is not null || (_calibrationRecoveryRequired && !restore))
            return ControlAttempt.Fail("自动标定正在采样或恢复未完成，拒绝新的硬件操作。");
        if (_closing)
            return ControlAttempt.Fail("程序正在退出，未执行。");
        if (_demo)
        {
            StatusText.Text = "演示模式：已拦截硬件写入。";
            AppLog.Write($"demo blocked: {label}");
            ShowOsd($"演示：{label}");
            return ControlAttempt.Fail("演示模式：未执行任何硬件写入。");
        }
        if (!await _gate.WaitAsync(0))
        {
            StatusText.Text = "硬件操作进行中，请稍后重试。";
            return ControlAttempt.Fail("硬件操作进行中，请稍后重试。");
        }

        var attempt = ControlAttempt.Fail("操作失败。");
        var manualWriteStarted = false;
        try
        {
            AppLog.Write($"request: {label}");
            RecordDiagnosticEvent($"操作请求：{label}");
            if (manual)
            {
                if (_curveController?.IsEnabled == true || _curveController?.RecoveryRequired == true ||
                    _hardware!.IsRecoveryPending || _hardware.IsEmergencyFullSpeed)
                    throw new InvalidOperationException("请先恢复 EC 自动，再切换风扇控制。");
                if (!GuardianAvailability.IsRunning())
                    throw new InvalidOperationException("Guardian 未运行，无法启用手动风扇控制。");
                if (requiresSafeTemperature)
                {
                    if (_latestSnapshot is { FanFault: not null } || _latestSnapshot?.RpmFeedbackState == FanRpmFeedbackState.Fault)
                        throw new InvalidOperationException("固定 RPM 出现过保护故障，请先显式恢复 EC 自动，再重新设置。");
                    var before = _hardware.ReadSnapshot();
                    if (before.Error is not null || before.CpuTemperatureC is null)
                        throw new InvalidOperationException($"无法确认温度，已拒绝固定 RPM：{before.Error ?? "温度不可用"}");
                    if (before.CpuTemperatureC >= 90)
                        throw new InvalidOperationException("温度达到 90°C，固定 RPM 已禁用；请使用满速保护并处理温度。");
                }
                _leaseStore.WriteActive(Environment.ProcessId, "manual-duty", DateTimeOffset.UtcNow,
                    GuardianProcessIdentity.CurrentStartUtc);
                _restoreRequired = true;
                manualWriteStarted = true;
            }
            await Task.Run(() => action(_hardware!));
            if (restore)
            {
                _restoreRequired = false;
                _calibrationRecoveryRequired = false;
                _leaseStore.ClearOwned(Environment.ProcessId);
            }
            AppLog.Write($"completed: {label}");
            RecordDiagnosticEvent($"操作完成：{label}");
            _appliedProfileName = null;
            ProfileStatusText.Text = $"手动操作：{label}；所选方案未重新应用。";
            // Same event point, structured body: no extra hardware read, values come from the last snapshot.
            ShowOsd(restore
                ? OsdEvents.GuardianRecovered(DescribeOsdSample())
                : OsdEvents.CommandSucceeded(label, DescribeOsdSample()));
            attempt = ControlAttempt.Ok($"已执行：{label}。");
        }
        catch (Exception ex)
        {
            AppLog.Write($"failed: {label}", ex);
            RecordDiagnosticEvent($"操作失败：{label} · {ex.GetBaseException().Message}");
            attempt = ControlAttempt.Fail(ex.GetBaseException().Message);
            if (manualWriteStarted)
            {
                try
                {
                    await Task.Run(() => _hardware!.RestoreAutomatic());
                    _restoreRequired = false;
                    _leaseStore.ClearOwned(Environment.ProcessId);
                }
                catch (Exception restoreException)
                {
                    AppLog.Write("manual control failed; automatic recovery pending, lease retained", restoreException);
                    RecordDiagnosticEvent($"手动操作恢复失败，租约保留：{restoreException.GetBaseException().Message}");
                }
            }
            if (!_closing)
                ShowOsd(restore
                    ? OsdEvents.GuardianRecoveryFailed(ex.GetBaseException().Message, DescribeOsdSample())
                    : OsdEvents.CommandFailed(label, ex.GetBaseException().Message));
            if (!_closing && interactive)
                System.Windows.MessageBox.Show(this, ex.GetBaseException().Message, label + "失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _gate.Release();
        }
        await RefreshSnapshotAsync();
        return attempt;
    }

    private async void SilenceClick(object sender, RoutedEventArgs e) =>
        await ExecuteAsync("静音模式", h => h.SetPowerMode(PowerMode.Silence));

    private async void BalanceClick(object sender, RoutedEventArgs e) =>
        await ExecuteAsync("均衡模式", h => h.SetPowerMode(PowerMode.Balance));

    private async void PerformanceClick(object sender, RoutedEventArgs e) =>
        await ExecuteAsync("性能模式", h => h.SetPowerMode(PowerMode.Performance));

    private async void FixedRpmClick(object sender, RoutedEventArgs e)
    {
        if (_curveController?.IsEnabled == true)
        {
            System.Windows.MessageBox.Show(this, "请先关闭安全曲线，或点击“恢复 EC 自动”。", "无法切换固定 RPM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(FixedRpmCombo.Text, out var requestedRpm) || requestedRpm < 1)
        {
            System.Windows.MessageBox.Show(this, "请输入有效的目标 RPM。", "固定 RPM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ExecuteAsync($"固定 RPM {requestedRpm}", h => h.SetFixedRpm(requestedRpm), manual: true, requiresSafeTemperature: true);
    }

    private async void FullSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_curveController?.IsEnabled == true)
        {
            System.Windows.MessageBox.Show(this, "请先关闭安全曲线，或点击“恢复 EC 自动”。", "无法切换满速",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ExecuteAsync("风扇满速", h => h.SetFanDuty(100), manual: true);
    }

    private async void RestoreAutomaticClick(object sender, RoutedEventArgs e) =>
        await RestoreAutomaticAsync();

    private async Task<ControlAttempt> RestoreAutomaticAsync(bool interactive = true)
    {
        if (CancelCalibrationRun() is { } calibrationCompletion)
            return await calibrationCompletion;
        if (_curveController?.IsEnabled == true || _curveController?.RecoveryRequired == true)
        {
            SetCurveToggle(false);
            return await DisableCurveAsync(interactive);
        }
        return await ExecuteAsync("恢复 EC 自动", h => h.RestoreAutomatic(), restore: true, interactive: interactive);
    }

    /// <summary>Result of one control entry point. Only used to answer a relayed command, never to skip a safety check.</summary>
    private sealed record ControlAttempt(bool Success, string Message)
    {
        public static ControlAttempt Ok(string message) => new(true, message);
        public static ControlAttempt Fail(string message) => new(false, message);
    }

    /// <summary>
    /// Runs one relayed CLI command on the UI thread. This is deliberately a thin mapper: every branch calls an
    /// existing control entry point, so the shared gate, the Guardian requirement, the demo check and the
    /// restore-on-failure path all apply exactly as they do for a click in the window.
    /// </summary>
    private async Task<ToolkitCommandIpcResponse> HandleAppCommandAsync(ToolkitCommand command, string requestId)
    {
        if (_closing)
            return ToolkitCommandIpcProtocol.Unavailable(requestId, command.Action, "程序正在退出，命令未执行。");

        switch (command.Action)
        {
            case ToolkitCommandAction.Status:
                return ToolkitCommandIpcProtocol.Ok(requestId, command.Action,
                    "已从运行中的主实例读取状态。", BuildCommandReport());

            case ToolkitCommandAction.ApplyProfile:
            {
                var profile = command.ProfileName is { } name
                    ? _profiles.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (profile is null)
                    return ToolkitCommandIpcProtocol.Rejected(requestId, command.Action,
                        "找不到该方案，未执行任何操作。");
                return RespondToCommand(requestId, command.Action, await ApplyProfileAsync(profile));
            }

            case ToolkitCommandAction.SetMode:
            {
                if (command.Mode is not { } mode || mode == PowerMode.Unknown)
                    return ToolkitCommandIpcProtocol.Rejected(requestId, command.Action, "缺少有效的电源模式，未执行任何操作。");
                return RespondToCommand(requestId, command.Action,
                    await ExecuteAsync($"{FormatMode(mode)}模式", handler => handler.SetPowerMode(mode), interactive: false));
            }

            case ToolkitCommandAction.RestoreAutomatic:
                return RespondToCommand(requestId, command.Action,
                    await RestoreAutomaticAsync(interactive: false));

            case ToolkitCommandAction.Activate:
                // The protocol already refuses any value on activate; check once more so this endpoint cannot be
                // driven with a payload even if a caller builds the record directly.
                if (command.ProfileName is not null || command.Mode is not null || command.Confirmed)
                    return ToolkitCommandIpcProtocol.Rejected(requestId, command.Action,
                        "activate 不接受参数，未执行任何操作。");
                return ActivateWindowForSecondInstance(requestId);

            default:
                return ToolkitCommandIpcProtocol.Rejected(requestId, command.Action, "动作不在白名单内，未执行任何操作。");
        }
    }

    Task<ToolkitCommandIpcResponse> IAppCommandEndpoint.HandleAppCommandAsync(ToolkitCommand command, string requestId) =>
        HandleAppCommandAsync(command, requestId);

    /// <summary>
    /// UI-only answer to a second launch: restore the window from the tray/minimized/hidden state and bring it to the
    /// front. It runs on the dispatcher thread and performs window operations only - no EC read/write, no WMI, no fan
    /// or Guardian call, no lease and no timer change - so it cannot affect fan control.
    /// </summary>
    private ToolkitCommandIpcResponse ActivateWindowForSecondInstance(string requestId)
    {
        if (_closing)
            return ToolkitCommandIpcProtocol.Unavailable(requestId, ToolkitCommandAction.Activate,
                "程序正在退出，窗口未激活。");
        // Same show/normal/activate sequence the tray menu uses, so no new window state handling is introduced.
        ShowMainWindow();
        AppLog.Write("activate: main window shown and activated for a second instance");
        return ToolkitCommandIpcProtocol.Ok(requestId, ToolkitCommandAction.Activate, "已切换到运行中的主窗口。");
    }

    private static ToolkitCommandIpcResponse RespondToCommand(string requestId, ToolkitCommandAction action,
        ControlAttempt attempt) =>
        attempt.Success
            ? ToolkitCommandIpcProtocol.Ok(requestId, action, attempt.Message)
            : ToolkitCommandIpcProtocol.Failed(requestId, action, attempt.Message);

    /// <summary>
    /// Read-only status answer built only from state the window already shows: no path, no command line, no process
    /// or adapter identity, and no hardware call (the EC is read by the sampling pass, not by this method).
    /// </summary>
    private ToolkitCommandIpcReport BuildCommandReport()
    {
        GuardianLease? lease = null;
        try { lease = _leaseStore.Read(); }
        catch (Exception ex) { AppLog.Write("guardian lease read failed for status", ex); }
        var snapshot = _latestSnapshot;
        return new ToolkitCommandIpcReport(
            PowerMode: snapshot is { PowerMode: not PowerMode.Unknown } ? snapshot.PowerMode.ToString().ToLowerInvariant() : null,
            // The EC reading stays the control value; the display-source projection is reported separately.
            CpuTemperatureC: snapshot?.CpuTemperatureC,
            Fan1Rpm: snapshot?.Fan1Rpm,
            Fan2Rpm: snapshot?.Fan2Rpm,
            FanControlMode: snapshot is null ? null : snapshot.FanControlMode.ToString().ToLowerInvariant(),
            TargetDuty: snapshot?.TargetDuty,
            TargetRpm: snapshot?.TargetRpm,
            RpmFeedbackState: snapshot is null ? null : snapshot.RpmFeedbackState.ToString().ToLowerInvariant(),
            FanFault: snapshot?.FanFault is not null || snapshot?.RpmFeedbackState == FanRpmFeedbackState.Fault,
            GuardianRunning: !_demo && _lastGuardianRunning,
            CurveEnabled: _curveController?.IsEnabled == true,
            ManualLeaseActive: lease?.ProcessId == Environment.ProcessId,
            AppliedProfile: _appliedProfileName,
            Demo: _demo,
            DisplaySource: _displaySourceSettings.Source.ToString().ToLowerInvariant(),
            SnapshotAgeSeconds: snapshot is null
                ? null
                : Math.Round((DateTimeOffset.Now - snapshot.Timestamp).TotalSeconds, 1));
    }

    private void ExitClick(object sender, RoutedEventArgs e) => RequestExit();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            _trayIcon.ShowBalloonTip(1500, "MiniProToolkit", "已最小化到系统托盘", Forms.ToolTipIcon.Info);
        }
    }

    private void RequestExit()
    {
        // Refused while an exit sequence already owns the gate: a second Close/OnClosing would run
        // the teardown twice (and, before this guard, could race the first one).
        if (!_exitRequests.TryBeginExit(ExitRequestSource.User))
            return;
        BeginExitSequence();
    }

    // Runs on a thread-pool thread (registered wait callback). It never touches WPF state: the gate
    // is a monitor, and the request is marshalled to the dispatcher only when a sequence may start.
    // The registration itself stays alive for the whole session (executeOnlyOnce: false), so a
    // failed exit is retried by the next Set() instead of leaving the app unclosable by script.
    private void OnShutdownSignal(object? state, bool timedOut)
    {
        if (timedOut)
            return;
        // Refuses teardown (nothing may be queued once the dispatcher is gone) and a concurrent
        // exit; a signal that races a running exit is remembered for the retry after a failure.
        if (!_exitRequests.TryBeginExit(ExitRequestSource.ShutdownSignal))
            return;
        AppLog.Write("exit: shutdown signal received");
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)RequestExitFromShutdownSignal);
        }
        catch (Exception ex)
        {
            // The dispatcher is already gone: nothing can be queued, but the gate must not stay
            // latched by a callback that will never run.
            AppLog.Write("exit: shutdown signal could not be marshalled to the UI thread", ex);
            _exitRequests.Rearm();
        }
    }

    // Signal-driven exit; the gate was claimed by OnShutdownSignal before the dispatcher was asked.
    private void RequestExitFromShutdownSignal()
    {
        if (_exitRequests.IsTeardownStarted)
            return;
        BeginExitSequence();
    }

    private void BeginExitSequence()
    {
        _trayExitRequested = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            // No sequence started, so the request must not stay latched (a latched gate would make
            // every later stop signal a no-op again).
            AppLog.Write("exit: closing the window failed", ex);
            _trayExitRequested = false;
            if (_exitRequests.Rearm())
                ScheduleShutdownSignalRetry();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        if (!_trayExitRequested)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(1500, "MiniProToolkit", "程序仍在后台运行，右键托盘图标可退出", Forms.ToolTipIcon.Info);
            return;
        }
        e.Cancel = true;
        if (_closing)
            return;

        _closing = true;
        _ = CancelCalibrationRun(); // The sampling finally owns restoration before this exit can acquire _gate.
        // Stop consuming power notifications before the recovery sequence: a wake-up during
        // shutdown must not queue another sampling pass. Re-subscribed if the exit fails.
        UnsubscribePowerEvents();
        // Drop any resume slot left queued by a wake-up that raced the flag above, so a later
        // session (or a failed exit) is not blocked by a stale pending slot.
        _resumeCoalescer.Complete();
        _timer.Stop();
        _automationTimer.Stop();
        IsEnabled = false;
        // The gate wait is bounded: shutdown must never hang behind a stuck sampling pass.
        // Without the gate the recovery sequence must not start (it would race the in-flight
        // pass for the EC/lease), so a timeout aborts this exit and the app keeps running.
        var gateAcquired = false;
        try
        {
            gateAcquired = await _gate.WaitAsync(ExitGateTimeout);
            if (!gateAcquired)
            {
                AppLog.Write("exit: timed out waiting for the sampling gate");
                // Decided before the resume: the resume re-arms the gate and clears the state this
                // rule depends on.
                var timeoutNotice = _exitRequests.DecideFailureNotice();
                RecordDiagnosticEvent($"退出等待采样完成超时：已取消本次退出{DescribeExitNotice(timeoutNotice)}");
                ResumeAfterFailedExit();
                ShowExitFailureNotice(timeoutNotice,
                    "采样任务超时未结束，暂不退出。请稍后重试退出。",
                    "暂时无法退出");
                return;
            }
            await Task.Run(() =>
            {
                // Phase 1, reversible: hand the fans back to the EC and drop our lease while the
                // hardware objects are still alive. A failure here leaves a fully usable session,
                // so the catch below may resume it.
                try
                {
                    if (_curveController is { } controller &&
                        (controller.IsEnabled || controller.RecoveryRequired))
                        controller.Dispose();
                    if (_restoreRequired)
                    {
                        _hardware!.RestoreAutomatic();
                        _restoreRequired = false;
                        AppLog.Write("exit: requested EC automatic and read RPM");
                        RecordDiagnosticEvent("退出时请求 EC 自动恢复成功");
                    }
                    if (!_restoreRequired && _curveController?.RecoveryRequired != true)
                        _leaseStore.ClearOwned(Environment.ProcessId);
                }
                finally
                {
                    // The optional LHM reader is always released, even when the EC
                    // recovery above throws, so a failed exit never leaks the driver handle.
                    // The fields are cleared so nothing (including a restarted sampling timer
                    // after a failed exit) reuses a disposed reader; the rebuild is requested
                    // in that case.
                    ReleaseOptionalReaders();
                }
            });

            // Phase 2, irreversible: telemetry and hardware handles are final and the lease is
            // already gone, so a resume could only restart a failing sampling loop. The phase is
            // entered *before* the first Dispose(), so even a partially completed release (for
            // example telemetry disposed, hardware dispose thrown) is treated as released.
            _shutdownSequence.Enter(ShutdownPhase.HardwareRelease);
            await Task.Run(() =>
            {
                _telemetry.Dispose();
                _hardware?.Dispose();
            });

            // Phase 3, irreversible UI teardown.
            _shutdownSequence.Enter(ShutdownPhase.UiTeardown);
            foreach (var window in _taskbarWindows.ToArray())
                window.Close();
            _taskbarWindows.Clear();
            UnregisterHotkeys();
            _hotkeySource = null;
            _osd.Close();
            TeardownShutdownSignal();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _shutdownSequence.Enter(ShutdownPhase.Completed);
            _allowClose = true;
        }
        catch (Exception ex) when (_shutdownSequence.DecideFailureAction() == ShutdownFailureAction.ResumeSession)
        {
            // Nothing irreversible happened yet: the hardware is still alive and the lease
            // handling failed above, so the session is resumed and the user is asked to fix the
            // restoration. Guardian/RestoreAutomatic failures are never swallowed here.
            AppLog.Write("exit restoration failed", ex);
            // Decided before the resume: the resume re-arms the gate and clears the state this
            // rule depends on. A signal-driven exit only logs here, a manual one still prompts.
            var notice = _exitRequests.DecideFailureNotice();
            RecordDiagnosticEvent($"退出恢复失败：{ex.GetBaseException().Message}{DescribeExitNotice(notice)}");
            ResumeAfterFailedExit();
            ShowExitFailureNotice(notice,
                "恢复自动失败，暂不退出。请重试“恢复 EC 自动”。\n" + ex.GetBaseException().Message,
                "需要处理风扇恢复");
        }
        catch (Exception ex)
        {
            // Hardware/telemetry are already released and the lease is cleared. Resuming would
            // restart sampling on a disposed hardware object, so the process must close instead.
            AppLog.Write("exit teardown failed after hardware release; finishing exit without resume", ex);
            RecordDiagnosticEvent(
                $"退出清理失败（硬件已释放，不再恢复采样，按安全方式退出）：{ex.GetBaseException().Message}");
            _allowClose = true;
        }
        finally
        {
            // Only release a gate this method actually acquired.
            if (gateAcquired)
                _gate.Release();
        }
        if (_allowClose)
            Close();
    }

    // Undoes the shutdown state after an aborted exit (gate timeout or a failure in the
    // reversible phase) so the window keeps working. Called only while
    // ShutdownSequence.CanResumeAfterFailure holds; after the hardware release there is nothing
    // safe to resume. The disposed LHM and CPU-frequency readers are rebuilt lazily by the next
    // sampling pass through the normal reinitialize path instead of being reused after Dispose().
    private void ResumeAfterFailedExit()
    {
        // Captured before the gate is re-armed: the notice rule looks at the request state that
        // the re-arm clears.
        var notice = _exitRequests.DecideFailureNotice();
        _closing = false;
        // A cancelled exit is no longer an exit request: without this reset the next plain window
        // close would run the full teardown instead of the tray semantics (hide, keep sampling),
        // and it must be re-established before any retry is scheduled.
        _trayExitRequested = false;
        _resumeCoalescer.Complete();
        if (!_demo)
        {
            _lhmReinitializeRequested = true;
            _lhmGpuReinitializeRequested = true;
            _lhmAuxTemperatureReinitializeRequested = true;
            _cpuFrequencyReinitializeRequested = true;
            _cpuClassFrequencyReinitializeRequested = true;
        }
        SubscribePowerEvents();
        if (!_powerEventsSubscribed)
            AppLog.Write("exit resume: power notifications not subscribed; sampling continues once per second");
        IsEnabled = true;
        // Checked instead of restarted blindly: a timer that still runs must not be restarted,
        // and a stopped one has to run again before sampling is claimed to work.
        if (!_timer.IsEnabled)
            _timer.Start();
        if (!_automationTimer.IsEnabled)
            _automationTimer.Start();
        // The exit sequence may already have unregistered the global hotkeys. Without them the
        // keyboard control paths are silently lost, so re-register and verify instead of
        // assuming the session is fully back (the hook itself is attached at most once).
        if (!EnsureHotkeysRegistered())
        {
            AppLog.Write("exit resume: global hotkeys could not be registered again");
            RecordDiagnosticEvent($"退出已取消，但全局热键无法恢复注册：请改用窗口或托盘继续控制风扇{DescribeExitNotice(notice)}");
            ShowExitFailureNotice(notice,
                "退出已取消，但全局热键无法重新注册。请使用窗口或托盘菜单继续控制风扇，或重启程序。",
                "热键未恢复");
        }
        // Re-arm: the wait registration itself stayed alive (executeOnlyOnce: false), only the
        // latch and the one-shot bookkeeping are reset here. Without this the next stop signal
        // after a failed exit would be refused forever (the reported defect).
        if (!_exitRequests.Rearm())
            return;
        // A stop request arrived while this exit ran: an installer/uninstaller is waiting on the
        // event. Retried exactly once, with the signal source so the retry never blocks it with a
        // dialog; a second failure drops the retry instead of looping.
        AppLog.Write("exit resume: retrying once for a shutdown signal that arrived during the failed exit");
        RecordDiagnosticEvent("退出已取消，期间收到安装/卸载停止信号：按停止信号重新尝试退出一次");
        ScheduleShutdownSignalRetry();
    }

    private void ScheduleShutdownSignalRetry()
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            // Background priority: the current OnClosing returns (and releases the sampling gate)
            // before the retry starts, so the retry does not wait on its own gate.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)RetryShutdownSignalExit);
        }
        catch (Exception ex)
        {
            AppLog.Write("exit resume: shutdown signal retry could not be scheduled", ex);
        }
    }

    private void RetryShutdownSignalExit()
    {
        if (_closing || !_exitRequests.TryBeginExit(ExitRequestSource.ShutdownSignal))
            return;
        BeginExitSequence();
    }

    // Pairs the RegisterWaitForSingleObject in the constructor: the gate is disarmed first (a late
    // signal becomes a no-op and nothing is queued on a shutting-down dispatcher), then the wait is
    // unregistered and a callback that is still running is waited for before the event handle is
    // disposed. Disposing the event under a live registered wait would throw ObjectDisposedException
    // on a thread-pool thread.
    private void TeardownShutdownSignal()
    {
        _exitRequests.BeginTeardown();
        var registration = _shutdownRegistration;
        _shutdownRegistration = null;
        var signal = _shutdownSignal;
        _shutdownSignal = null;
        if (registration is not null)
        {
            using var callbackFinished = new ManualResetEvent(false);
            try
            {
                if (!registration.Unregister(callbackFinished) ||
                    !callbackFinished.WaitOne(ShutdownSignalTeardownTimeout))
                    AppLog.Write("exit: shutdown wait callback was still running at teardown");
            }
            catch (Exception ex)
            {
                AppLog.Write("exit: shutdown wait could not be unregistered", ex);
            }
        }
        signal?.Dispose();
    }

    // Signal-driven exits must never open a modal dialog: the installer sets the shutdown event
    // once and gives up after 15 s, so an undismissable MessageBox turns a restore failure into a
    // failed install/uninstall. Same failure, log/diagnostic only; a manual exit keeps its prompt.
    private void ShowExitFailureNotice(ExitFailureNotice notice, string message, string title)
    {
        if (notice == ExitFailureNotice.LogOnly)
        {
            AppLog.Write($"exit: '{title}' dialog suppressed for a signal-driven exit: {message}");
            return;
        }
        System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string DescribeExitNotice(ExitFailureNotice notice) => notice == ExitFailureNotice.LogOnly
        ? "（安装/卸载停止信号：仅记录，不弹窗）"
        : string.Empty;

    // Optional readers only: releasing them must never mask the EC recovery, and a failed exit
    // rebuilds them lazily in the next sampling pass (fields are cleared so a disposed instance
    // can never be reused).
    private void ReleaseOptionalReaders()
    {
        try
        {
            _lhmCpuSensor?.Dispose();
        }
        finally
        {
            _lhmCpuSensor = null;
            try { _lhmGpuSensor?.Dispose(); }
            catch { /* best effort: the reader is optional and non-control */ }
            finally { _lhmGpuSensor = null; }
            try { _lhmAuxTemperatureSensor?.Dispose(); }
            catch { /* best effort: optional and non-control */ }
            finally { _lhmAuxTemperatureSensor = null; }
            try { _cpuFrequencyReader?.Dispose(); }
            catch { /* best effort: the reader is optional and non-control */ }
            finally { _cpuFrequencyReader = null; }
            try { _cpuClassFrequencyReader?.Dispose(); }
            catch { /* best effort: optional and non-control */ }
            finally { _cpuClassFrequencyReader = null; }
        }
    }

    // Re-registers the global hotkeys only when they are missing, and reports the real state
    // afterwards instead of assuming success.
    private bool EnsureHotkeysRegistered()
    {
        if (IsHotkeysFullyRegistered())
            return true;
        RegisterHotkeys();
        return IsHotkeysFullyRegistered();
    }

    private bool IsHotkeysFullyRegistered() =>
        _registeredHotkeys.Count == Enum.GetValues<HotkeyAction>().Length;

    // ---- Sleep / resume -------------------------------------------------------------------
    // Microsoft.Win32.SystemEvents raises PowerModeChanged on its own thread. The handler never
    // touches WPF, the refresh gate, WMI, the EC or the fan: it only records the transition and
    // marshals an already-coalesced refresh onto the UI thread. Suspending does not write EC
    // automatic, so the user's control state survives the sleep unchanged; the Guardian lease
    // keeps its existing expiry as the only safety net.
    private void SubscribePowerEvents()
    {
        if (_powerEventsSubscribed)
            return;
        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerEventsSubscribed = true;
        }
        catch (Exception ex)
        {
            // Missing power notifications degrade to the previous behaviour (no resume refresh);
            // the app keeps sampling once per second regardless.
            AppLog.Write("power mode subscription failed", ex);
            _powerEventsSubscribed = false;
        }
    }

    private void UnsubscribePowerEvents()
    {
        if (!_powerEventsSubscribed)
            return;
        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            AppLog.Write("power mode unsubscription failed", ex);
        }
        finally
        {
            _powerEventsSubscribed = false;
        }
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        try
        {
            switch (PowerTransitionPolicy.Decide(MapPowerTransition(e.Mode)))
            {
                case PowerTransitionAction.RecordSuspendOnly:
                    CancelCalibrationRun(); // Thread-safe request only; the run restores under its existing gate.
                    AppLog.Write($"power: system suspending ({e.Mode})");
                    // Diagnostics only. No EC/fan write before sleep on purpose.
                    RecordDiagnosticEvent("系统睡眠：仅记录事件，不写入 EC/风扇，控制状态保持不变");
                    break;
                case PowerTransitionAction.RefreshAfterResume:
                    ScheduleResumeRefresh(e.Mode);
                    break;
                default:
                    AppLog.Write($"power: ignored power notification ({e.Mode})");
                    break;
            }
        }
        catch (Exception ex)
        {
            // A power notification must never take the app down.
            AppLog.Write("power mode change handling failed", ex);
        }
    }

    private static PowerTransitionKind MapPowerTransition(Microsoft.Win32.PowerModes mode) => mode switch
    {
        Microsoft.Win32.PowerModes.Suspend => PowerTransitionKind.Suspend,
        Microsoft.Win32.PowerModes.Resume => PowerTransitionKind.Resume,
        Microsoft.Win32.PowerModes.StatusChange => PowerTransitionKind.StatusChange,
        _ => PowerTransitionKind.Unknown
    };

    private void ScheduleResumeRefresh(Microsoft.Win32.PowerModes mode)
    {
        // Windows can raise more than one resume notification per wake-up; only the first one
        // queues a refresh and the rest are recorded as coalesced.
        if (!_resumeCoalescer.TrySchedule())
        {
            AppLog.Write($"power: duplicate resume notification coalesced ({mode})");
            return;
        }
        try
        {
            // Marshal to the UI thread (the gate, the closing flag and the taskbar windows are
            // all UI-thread state) and keep the priority low so a wake-up cannot stall the UI.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)RunResumeRefresh);
        }
        catch (Exception ex)
        {
            _resumeCoalescer.Complete();
            AppLog.Write("power: resume refresh scheduling failed", ex);
        }
    }

    private void RunResumeRefresh() => _ = RunResumeRefreshAsync();

    private async Task RunResumeRefreshAsync()
    {
        try
        {
            if (_closing)
                return;
            // Consumed by the next sampling pass: drop possibly stale LHM driver handles.
            if (!_demo)
            {
                _lhmReinitializeRequested = true;
                // Same signal for the optional GPU reader: drop the driver handles opened
                // before sleep and let the pass rebuild it.
                _lhmGpuReinitializeRequested = true;
                _lhmAuxTemperatureReinitializeRequested = true;
                _cpuClassFrequencyReinitializeRequested = true;
            }
            // The shell may have been re-created while asleep, so re-attach taskbar windows
            // on the next pass instead of waiting for the throttle interval.
            _taskbarRefresh.ResetInterval();
            RecordDiagnosticEvent("电源恢复：丢弃旧采样状态并刷新（沿用现有采样/Guardian 安全路径）");
            // Reuses the normal pass: EC read, fixed-RPM/curve and lease renewal all go through
            // the existing safety gates. Nothing here writes fans, restores EC automatic or
            // bypasses the lease, and the wait happens off the UI thread inside the pass.
            if (!await RefreshSnapshotAsync(waitForGate: true))
            {
                AppLog.Write("power: resume refresh skipped (gate timeout, closing, or pass failed)");
                return;
            }
            // The resume pass just sampled everything. Restarting the 1 s timer makes the next
            // periodic tick a full interval away instead of immediately repeating the same
            // sample; this is a cheap UI-thread timer reset and never blocks on the hardware.
            if (!_closing && IsLoaded)
            {
                _timer.Stop();
                _timer.Start();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("power: resume refresh failed", ex);
            RecordDiagnosticEvent($"电源恢复刷新失败：{ex.GetBaseException().Message}");
        }
        finally
        {
            // Always release the slot, even on failure, so a later resume can schedule again.
            _resumeCoalescer.Complete();
        }
    }

    private bool HasValidControlLease()
    {
        var lease = _leaseStore.Read();
        return lease?.ProcessId == Environment.ProcessId &&
               lease.ProcessStartUtc == GuardianProcessIdentity.CurrentStartUtc &&
               !lease.IsStale(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// Explicit curve takeover: publishes the control lease and opens the curve write gate for the
    /// single control step that follows. Only the interactive toggle and profile apply call this;
    /// the curve write path itself never publishes a lease, so an expired lease cannot be revived.
    /// Caller holds <see cref="_gate"/> and has already confirmed Guardian is running.
    /// </summary>
    private void TakeOverCurveLease()
    {
        _leaseStore.WriteActive(Environment.ProcessId, "fan-curve", DateTimeOffset.UtcNow,
            GuardianProcessIdentity.CurrentStartUtc);
        _curveLeaseGate.SetPass(true);
    }

    private void RefreshLease(bool fixedRpmFault)
    {
        if (_demo) return;
        try
        {
            if (_calibrationRecoveryRequired) return; // Keep failed calibration recovery lease untouched and unrenewed.
            if (!_restoreRequired && _curveController?.IsEnabled != true && _curveController?.RecoveryRequired != true)
            {
                _leaseStore.ClearOwned(Environment.ProcessId);
                return;
            }
            if (fixedRpmFault || (_curveController?.RecoveryRequired == true && !_curveController.IsEnabled))
                return; // Preserve failed-recovery lease so Guardian can retry after expiry.
            if (!HasValidControlLease()) return; // Never revive ownership after a slow call outlives the lease.
            var mode = _curveController?.IsEnabled == true ? "fan-curve" : "manual-duty";
            _leaseStore.WriteActive(Environment.ProcessId, mode, DateTimeOffset.UtcNow,
                GuardianProcessIdentity.CurrentStartUtc);
        }
        catch (Exception ex)
        {
            AppLog.Write("guardian lease heartbeat failed", ex);
        }
    }

    private void EnableCurveFromMenu()
    {
        ShowMainWindow();
        FanCurveToggle.IsChecked = true;
    }

    private void SetCurveToggle(bool value)
    {
        _synchronizingCurveToggle = true;
        FanCurveToggle.IsChecked = value;
        _synchronizingCurveToggle = false;
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开控制中心", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("静音模式", null, async (_, _) => await ExecuteFromTrayAsync("静音模式", h => h.SetPowerMode(PowerMode.Silence))) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripMenuItem("均衡模式", null, async (_, _) => await ExecuteFromTrayAsync("均衡模式", h => h.SetPowerMode(PowerMode.Balance))) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripMenuItem("性能模式", null, async (_, _) => await ExecuteFromTrayAsync("性能模式", h => h.SetPowerMode(PowerMode.Performance))) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("恢复 EC 自动", null, async (_, _) => await RestoreAutomaticAsync());
        var fullSpeedItem = new Forms.ToolStripMenuItem("风扇满速", null, async (_, _) => await ExecuteAsync("托盘：风扇满速", h => h.SetFanDuty(100), manual: true)) { Enabled = false };
        menu.Items.Add(fullSpeedItem);
        var fixedMenu = new Forms.ToolStripMenuItem("固定 RPM") { Enabled = false };
        foreach (var rpm in new[] { 2100, 2500, 2800, 3200, 3500, 4000 })
        {
            var item = new Forms.ToolStripMenuItem($"{rpm} RPM");
            item.Click += async (_, _) => await ExecuteAsync($"托盘：固定 RPM {rpm}", h => h.SetFixedRpm(rpm), manual: true, requiresSafeTemperature: true);
            item.Enabled = false;
            fixedMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(fixedMenu);
        menu.Items.Add(new Forms.ToolStripMenuItem("安全曲线", null, (_, _) => EnableCurveFromMenu()) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置与诊断", null, (_, _) => ShowDiagnosticsFromTray());
        // Minimal user switch for the OSD itself. The control paths keep running either way; only
        // the popup is silenced. Persisted by IsOsdEnabled (osd.json).
        _osdMenuItem = new Forms.ToolStripMenuItem("OSD 提示")
        {
            CheckOnClick = true,
            Checked = IsOsdEnabled
        };
        _osdMenuItem.Click += (_, _) => IsOsdEnabled = _osdMenuItem.Checked;
        menu.Items.Add(_osdMenuItem);
        // Theme presets sit next to the on/off switch. Checking is exclusive and read-only from the
        // user's point of view: picking a label can never introduce a custom color or a new window.
        _osdThemeMenu = new Forms.ToolStripMenuItem("OSD 主题");
        foreach (var preset in OsdThemeCatalog.Presets)
        {
            var presetItem = new Forms.ToolStripMenuItem(OsdThemeCatalog.DisplayName(preset))
            {
                Tag = preset,
                Checked = SelectedOsdTheme == preset
            };
            presetItem.Click += (_, _) => SelectedOsdTheme = preset;
            _osdThemeMenu.DropDownItems.Add(presetItem);
        }
        menu.Items.Add(_osdThemeMenu);
        // Appearance editor (colors / opacity / font size / position): opened in the main window so
        // the modal dialog has a visible owner; still presentation only (no hardware, no network).
        menu.Items.Add("OSD 外观设置…", null, (_, _) =>
        {
            ShowMainWindow();
            Dispatcher.BeginInvoke(new Action(() => OsdSettingsClick(this, new RoutedEventArgs())));
        });
        var startupItem = new Forms.ToolStripMenuItem("随 Windows 启动")
        {
            Checked = StartupRegistration.IsEnabled()
        };
        startupItem.Click += (_, _) => ToggleStartupFromTray(startupItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => RequestExit());
        menu.Opening += (_, _) =>
        {
            if (_osdMenuItem is not null)
                _osdMenuItem.Checked = IsOsdEnabled;
            UpdateOsdThemeChecks();
            if (_latestSnapshot is { } snapshot)
                UpdateTrayStatus(snapshot, GuardianAvailability.IsRunning());
        };

        var icon = new Forms.NotifyIcon
        {
            Icon = CreateBrandIcon(),
            Text = "MiniProToolkit",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowMainWindow();
        };
        return icon;
    }

    private async Task ExecuteHotkeyFullSpeedAsync()
    {
        if (_curveController?.IsEnabled == true)
        {
            ShowOsd("快捷键已忽略：安全曲线正在接管");
            return;
        }
        await ExecuteAsync("快捷键：风扇满速", h => h.SetFanDuty(100), manual: true);
    }

    private void ShowDiagnosticsFromTray()
    {
        ShowMainWindow();
        DiagnosticsClick(this, new RoutedEventArgs());
    }

    private void ToggleStartupFromTray(Forms.ToolStripMenuItem item)
    {
        try
        {
            StartupRegistration.SetEnabled(!item.Checked);
            item.Checked = StartupRegistration.IsEnabled();
            ShowOsd(item.Checked ? "已启用随 Windows 启动" : "已关闭随 Windows 启动");
        }
        catch (Exception ex)
        {
            AppLog.Write("startup registration failed", ex);
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "启动项设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExecuteFromTrayAsync(string label, Action<PowerSwitchHardware> action)
    {
        ShowMainWindow();
        await ExecuteAsync(label, action);
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private static string _formatRpmFeedback(HardwareSnapshot snapshot)
    {
        var error = snapshot.RpmError is { } value ? $"误差 {value:+#;-#;0} RPM" : "等待反馈";
        return snapshot.RpmFeedbackState switch
        {
            FanRpmFeedbackState.Stable => $"闭环稳定，{error}",
            FanRpmFeedbackState.Adjusting => $"闭环修正，{error}",
            FanRpmFeedbackState.TargetUnreachable => $"目标受限/暂不可达，{error}",
            FanRpmFeedbackState.Fault => $"闭环已停止，{error}（见保护状态）",
            FanRpmFeedbackState.WaitingForRpm => "闭环等待反馈",
            _ => "闭环未启用"
        };
    }

    private void UpdateTrayStatus(HardwareSnapshot snapshot, bool guardianRunning)
    {
        if (_trayIcon.ContextMenuStrip is { } menu)
        {
            foreach (Forms.ToolStripItem item in menu.Items)
            {
                var safeManual = !_demo && guardianRunning && snapshot.Error is null && snapshot.FanFault is null &&
                    snapshot.RpmFeedbackState != FanRpmFeedbackState.Fault && !_restoreRequired &&
                    _curveController?.RecoveryRequired != true && _hardware?.IsEmergencyFullSpeed != true &&
                    _curveController?.IsEnabled != true;
                var safeRpmOrCurve = safeManual && snapshot.CpuTemperatureC is < 90 &&
                    snapshot.FanFault is null && snapshot.RpmFeedbackState != FanRpmFeedbackState.Fault;
                if (item.Text is "静音模式" or "均衡模式" or "性能模式") item.Enabled = !_demo && snapshot.Error is null;
                if (item.Text == "风扇满速") item.Enabled = safeManual;
                if (item.Text is "固定 RPM" or "安全曲线") item.Enabled = safeRpmOrCurve;
                if (item is Forms.ToolStripMenuItem sub && item.Text == "固定 RPM")
                    foreach (Forms.ToolStripItem child in sub.DropDownItems) child.Enabled = item.Enabled;
            }
        }
        var temp = FormatTemperature(snapshot.EffectiveDisplayCpuTemperatureC, snapshot.DisplayTemperatureFromLhm);
        var rpm = snapshot.Fan1Rpm is { } fan ? $"{fan} RPM" : "-- RPM";
        var mode = FormatMode(snapshot.PowerMode);
        var guardian = guardianRunning ? "Guardian OK" : "Guardian !";
        var text = $"MiniProToolkit | {temp} | {rpm} | {mode} | {guardian}";
        _trayIcon.Text = text.Length <= 63 ? text : "MiniProToolkit";
    }

    private void ShowOsd(string message)
    {
        if (_closing) return;
        Dispatcher.InvokeAsync(() => _osd.ShowMessage(message));
    }

    /// <summary>
    /// Structured OSD entry point. Same non-throwing, UI-thread-marshalled path as the legacy
    /// text overload; <see cref="OsdWindow"/> drops the message when the user disabled the OSD
    /// or when it has no visible body, so callers never need to care.
    /// </summary>
    private void ShowOsd(OsdMessage message)
    {
        if (_closing) return;
        Dispatcher.InvokeAsync(() => _osd.ShowMessage(OsdEventTemplates.Apply(message, _osdPreferences.EventTemplates)));
    }

    /// <summary>
    /// Builds the OSD body values from the last sampled snapshot. Never samples and never touches
    /// hardware: a value that is missing from the snapshot renders as "--" instead of being read.
    /// </summary>
    private OsdSample DescribeOsdSample(HardwareSnapshot? snapshot = null)
    {
        var current = snapshot ?? _latestSnapshot;
        var controlState = _curveController?.IsEnabled == true
            ? $"安全曲线 {_curveController.CurrentDuty?.ToString() ?? "--"}%"
            : current?.FanControlMode switch
            {
                FanControlMode.FirmwareAutomatic => "EC 自动",
                FanControlMode.FixedRpm => "固定 RPM 闭环",
                FanControlMode.ManualDuty => "手动 duty",
                FanControlMode.Curve => "曲线",
                _ => null
            };
        // CpuTemperatureC (EC) is the control/protection truth; the target RPM falls back to the
        // requested value so a pending fixed-RPM request is still visible.
        return new OsdSample(current?.CpuTemperatureC, current?.Fan1Rpm,
            current?.TargetRpm ?? current?.RequestedTargetRpm, controlState);
    }

    /// <summary>
    /// Registers every configured hotkey independently, so one combination already owned by another
    /// process only costs that single action (per-action degradation) instead of all of them. The
    /// failures are reported through a log-safe summary (action label, gesture, fixed reason - never a
    /// path or a command line).
    /// </summary>
    private void RegisterHotkeys()
    {
        if (IsHotkeysFullyRegistered())
            return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            // The window is not created yet (or already destroyed); a later SourceInitialized/exit
            // resume retries instead of registering against an invalid handle.
            AppLog.Write("global hotkeys: window handle unavailable, registration deferred");
            return;
        }
        // The hook also carries the shell/display notifications, so it is attached (once) from
        // here as well; it is removed by ReleaseWindowHook in UnregisterHotkeys even when the
        // hotkey registration itself failed.
        EnsureWindowHook(handle);
        var failures = new List<HotkeyRegistrationFailure>();
        foreach (var binding in _hotkeySettings.Bindings)
        {
            var id = HotkeyId(binding.Action);
            if (_registeredHotkeys.Contains(id))
                continue;
            uint virtualKey;
            try
            {
                virtualKey = binding.Gesture.VirtualKey;
            }
            catch (ArgumentException)
            {
                failures.Add(new HotkeyRegistrationFailure(binding.Action, binding.Gesture.Describe(), "按键不受支持"));
                continue;
            }
            if (NativeMethods.RegisterHotKey(handle, id, binding.Gesture.NativeModifiers, virtualKey))
            {
                _registeredHotkeys.Add(id);
                continue;
            }
            failures.Add(new HotkeyRegistrationFailure(binding.Action, binding.Gesture.Describe(),
                HotkeyRegistrationReport.ReasonForWin32Error(System.Runtime.InteropServices.Marshal.GetLastWin32Error())));
        }
        if (HotkeyRegistrationReport.Summarize(failures) is { } summary)
            AppLog.Write(summary);
    }

    /// <summary>
    /// Re-registers the hotkeys with the current settings. Used after the settings dialog changed the
    /// bindings: it releases exactly the registrations it made and never touches the window hook, so
    /// the taskbar/shell notifications stay attached.
    /// </summary>
    private void ApplyHotkeySettings(HotkeySettings settings)
    {
        UnregisterHotkeyRegistrations();
        _hotkeySettings = settings;
        RegisterHotkeys();
    }

    /// <summary>Releases only the live hotkey registrations (the window hook stays attached).</summary>
    private void UnregisterHotkeyRegistrations()
    {
        if (_registeredHotkeys.Count == 0)
            return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            foreach (var id in _registeredHotkeys)
                NativeMethods.UnregisterHotKey(handle, id);
        }
        _registeredHotkeys.Clear();
    }

    private void UnregisterHotkeys()
    {
        UnregisterHotkeyRegistrations();
        // Independent of the hotkey state: the hook must never outlive the teardown, and the
        // TaskbarCreated token stops being processed with it.
        ReleaseWindowHook();
    }

    /// <summary>
    /// Attaches the main-window message hook and registers the shell's <c>TaskbarCreated</c>
    /// broadcast. Called at most once per HwndSource: a re-registration after a failed exit must
    /// not stack a second hook (the callback would then run twice per message).
    /// </summary>
    private void EnsureWindowHook(IntPtr handle)
    {
        _hotkeySource ??= HwndSource.FromHwnd(handle);
        if (_taskbarCreatedMessage == 0)
            _taskbarCreatedMessage = NativeMethods.RegisterTaskbarCreatedMessage();
        if (_windowHookAttached || _hotkeySource is not { } source)
            return;
        source.AddHook(HotkeyWindowProc);
        _windowHookAttached = true;
        if (_taskbarCreatedMessage == 0)
            AppLog.Write("taskbar refresh: RegisterWindowMessage(TaskbarCreated) unavailable; " +
                         "display changes still reposition the overlay");
    }

    /// <summary>
    /// Removes the hook and drops the TaskbarCreated token, so a queued shell broadcast cannot be
    /// processed after unregistration or window destruction. Safe to call when nothing is attached.
    /// </summary>
    private void ReleaseWindowHook()
    {
        if (_windowHookAttached)
        {
            _hotkeySource?.RemoveHook(HotkeyWindowProc);
            _windowHookAttached = false;
        }
        _taskbarCreatedMessage = 0;
        // Drop any pending slot so a later session is not blocked by a stale queue entry.
        _taskbarRefresh.Complete();
    }

    private IntPtr HotkeyWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Shell/display notifications first, and never marked handled: they are broadcasts that
        // other code may still want. Only window plumbing runs here (no WMI, no EC, no fan).
        if (TaskbarRefreshPolicy.ClassifyWindowMessage(message, _taskbarCreatedMessage) is { } trigger)
        {
            // WM_SETTINGCHANGE is broadcast for many unrelated settings; wParam == 0 is the
            // general shell/taskbar section used for taskbar moves, drops and auto-hide changes.
            if (trigger != TaskbarRefreshTrigger.TaskbarSettingChanged || wParam == IntPtr.Zero)
                ScheduleTaskbarRefresh(trigger);
            return IntPtr.Zero;
        }
        if ((uint)message != NativeMethods.WmHotKey)
            return IntPtr.Zero;
        // Only gestures that are currently registered may act: a WM_HOTKEY queued before an
        // unregister/re-register cycle must not trigger a mode change afterwards.
        var id = wParam.ToInt32();
        if (HotkeyActionFor(id) is not { } action || !_registeredHotkeys.Contains(id))
            return IntPtr.Zero;
        handled = true;
        _ = action switch
        {
            HotkeyAction.SilenceMode => ExecuteAsync("快捷键：静音模式", h => h.SetPowerMode(PowerMode.Silence)),
            HotkeyAction.BalanceMode => ExecuteAsync("快捷键：均衡模式", h => h.SetPowerMode(PowerMode.Balance)),
            HotkeyAction.PerformanceMode => ExecuteAsync("快捷键：性能模式", h => h.SetPowerMode(PowerMode.Performance)),
            HotkeyAction.AutomaticMode => RestoreAutomaticAsync(),
            HotkeyAction.FullSpeed => ExecuteHotkeyFullSpeedAsync(),
            _ => Task.CompletedTask
        };
        return IntPtr.Zero;
    }

    private static System.Drawing.Icon CreateBrandIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(37, 99, 235));
            graphics.FillEllipse(background, 1, 1, 30, 30);
            using var foreground = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            graphics.FillRectangle(foreground, 8, 9, 16, 4);
            graphics.FillRectangle(foreground, 8, 19, 16, 4);
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static string FormatMode(PowerMode mode) => mode switch
    {
        PowerMode.Balance => "均衡",
        PowerMode.Performance => "性能",
        PowerMode.Silence => "静音",
        _ => "未知"
    };
}
