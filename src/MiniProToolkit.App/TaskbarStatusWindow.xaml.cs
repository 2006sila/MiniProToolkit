using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MiniProToolkit.Core;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace MiniProToolkit.App;

public partial class TaskbarStatusWindow : Window
{
    private readonly TaskbarDisplaySettings _settings;
    private IntPtr _handle;
    private IntPtr _taskbarHandle;
    private bool _attached;
    private bool _guardianRunning;
    private bool _manualFanControlAllowed;
    private bool _fixedRpmAndCurveAllowed;
    private bool _powerModeControlAllowed;
    // Effective layout direction for the current sample: the real taskbar edge wins, the saved
    // Vertical preference is only the fallback for when no edge can be read.
    private bool _verticalLayout;
    // True while the overlay is hidden because the taskbar reported no usable rectangle
    // (auto-hidden taskbar / shell restart). Avoids repeated Hide calls and stale coordinates.
    private bool _hiddenForUnusableTaskbar;

    public bool IsNativeWindowAlive => _handle != IntPtr.Zero && NativeMethods.IsWindow(_handle);
    public IntPtr TaskbarHandle => _taskbarHandle;

    public event Action? OpenRequested;
    public event Action? RestoreRequested;
    public event Action? ExitRequested;
    // Opens the already existing taskbar display-item settings (TaskbarSettingsWindow) from the
    // right-click menu. Same entry point as the main window button, no second settings path.
    public event Action? TaskbarSettingsRequested;
    public event Action<PowerMode>? PowerModeRequested;
    public event Action? FullSpeedRequested;
    public event Action<int>? FixedRpmRequested;
    public event Action? CurveRequested;
    private HardwareSnapshot? _snapshot;

    public TaskbarStatusWindow(TaskbarDisplaySettings settings, IntPtr taskbarHandle)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.Validate();
        _taskbarHandle = taskbarHandle;
        _verticalLayout = _settings.Vertical;
        InitializeComponent();
        // Restoring the overlay after an auto-hidden taskbar reappears must never steal focus.
        ShowActivated = false;
        ApplyLayout();
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonUp += OnStatusMouseLeftButtonUp;
        var menu = new ContextMenu();
        AddMenuItem(menu, "打开 MiniProToolkit", () => OpenRequested?.Invoke());
        AddMenuItem(menu, "任务栏显示项…", () => TaskbarSettingsRequested?.Invoke());
        menu.Items.Add(new Separator());
        var modes = new MenuItem { Header = "性能模式" };
        AddModeMenuItem(modes, "静音", PowerMode.Silence);
        AddModeMenuItem(modes, "均衡", PowerMode.Balance);
        AddModeMenuItem(modes, "性能", PowerMode.Performance);
        menu.Items.Add(modes);
        var fan = new MenuItem { Header = "风扇控制" };
        AddMenuItem(fan, "恢复 EC 自动", () => RestoreRequested?.Invoke());
        var full = AddMenuItem(fan, "满速", () => FullSpeedRequested?.Invoke());
        var curve = AddMenuItem(fan, "安全曲线", () => CurveRequested?.Invoke());
        var fixedRpmMenu = new MenuItem { Header = "固定 RPM" };
        foreach (var rpm in new[] { 2100, 2500, 2800, 3200, 3500, 4000 })
            AddMenuItem(fixedRpmMenu, $"{rpm} RPM", () => FixedRpmRequested?.Invoke(rpm));
        fan.Items.Add(fixedRpmMenu);
        var fixedRpm = fixedRpmMenu;
        menu.Items.Add(fan);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "退出", () => ExitRequested?.Invoke());
        ContextMenu = menu;
        ContextMenuOpening += (_, _) =>
        {
            var allowed = _guardianRunning && _manualFanControlAllowed;
            foreach (var mode in modes.Items.OfType<MenuItem>())
                mode.IsEnabled = _powerModeControlAllowed;
            full.IsEnabled = allowed;
            var rpmAllowed = allowed && _fixedRpmAndCurveAllowed &&
                _snapshot is { Error: null, CpuTemperatureC: < 90, FanFault: null } snapshot &&
                snapshot.RpmFeedbackState != FanRpmFeedbackState.Fault;
            curve.IsEnabled = rpmAllowed; fixedRpm.IsEnabled = rpmAllowed;
            foreach (var mode in modes.Items.OfType<MenuItem>())
                mode.IsChecked = _snapshot?.PowerMode == (PowerMode)mode.Tag!;
        };
    }

    /// <summary>
    /// Left click opens the main window. A double click means exactly the same thing, so only the
    /// first release of a click sequence raises <see cref="OpenRequested"/>: WPF reports
    /// <c>ClickCount == 1</c> for the first pair and 2/3... for the extra pairs of a multi-click, and
    /// the extra ones are dropped instead of opening/activating twice. This adds no second gesture
    /// and no hardware action - it only makes the existing single-click path idempotent per click
    /// sequence, so the right-click menu and its gating are untouched.
    /// </summary>
    private void OnStatusMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!TaskbarClickGesture.RequestsOpen(e.ClickCount)) return;
        OpenRequested?.Invoke();
    }

    private void AddModeMenuItem(ItemsControl menu, string label, PowerMode mode)
    {
        var item = AddMenuItem(menu, label, () => PowerModeRequested?.Invoke(mode));
        item.IsCheckable = true;
        item.Tag = mode;
    }

    private static MenuItem AddMenuItem(ItemsControl menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    public void UpdateSnapshot(HardwareSnapshot snapshot, bool guardianRunning, bool manualFanControlAllowed = true, bool fixedRpmAndCurveAllowed = true, bool powerModeControlAllowed = true)
    {
        _guardianRunning = guardianRunning;
        _manualFanControlAllowed = manualFanControlAllowed;
        _fixedRpmAndCurveAllowed = fixedRpmAndCurveAllowed;
        _powerModeControlAllowed = powerModeControlAllowed;
        _snapshot = snapshot;
        RenderStatusText(snapshot);
        ToolTip = string.Join(Environment.NewLine, BuildDetails(snapshot));
        if (_attached)
            PositionOnTaskbar();
    }

    // Separator follows the effective direction so a vertical strip on a side taskbar never
    // renders as a single "a | b" line.
    private void RenderStatusText(HardwareSnapshot snapshot) =>
        StatusText.Text = string.Join(_verticalLayout ? Environment.NewLine : " | ",
            _settings.Items.Select(item => FormatItem(item, snapshot)));

    /// <summary>
    /// Re-renders an existing sample after the user changed the display source. Keeps
    /// the current control-gating flags and never touches hardware.
    /// </summary>
    public void UpdateDisplaySnapshot(HardwareSnapshot snapshot) =>
        UpdateSnapshot(snapshot, _guardianRunning, _manualFanControlAllowed,
            _fixedRpmAndCurveAllowed, _powerModeControlAllowed);

    public void TryAttachToTaskbar()
    {
        if (_handle == IntPtr.Zero || !NativeMethods.IsWindow(_handle) ||
            _taskbarHandle == IntPtr.Zero || !NativeMethods.IsWindow(_taskbarHandle))
            return;

        if (!_attached)
        {
            // Keep this as a real WPF top-level window. Reparenting its HWND into
            // Explorer can yield a correctly positioned child that WPF never paints.
            // A no-activate topmost tool window provides the same taskbar placement
            // without crossing the Explorer process / integrity boundary.
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlStyle,
                new IntPtr(NativeMethods.WsPopup | NativeMethods.WsVisible));
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle,
                new IntPtr(NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow));
            _attached = true;
            AppLog.Write("taskbar status using topmost non-activating overlay");
        }

        PositionOnTaskbar();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        TryAttachToTaskbar();
    }

    private void ApplyLayout()
    {
        StatusPanel.Orientation = _verticalLayout
            ? WpfOrientation.Vertical
            : WpfOrientation.Horizontal;
        RootBorder.Padding = _settings.Compact ? new Thickness(5, 1, 5, 1) : new Thickness(9, 3, 9, 3);
        try { StatusText.FontFamily = new System.Windows.Media.FontFamily(_settings.FontFamily); }
        catch (ArgumentException) { StatusText.FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); }
        StatusText.FontSize = _settings.Compact ? _settings.FontSize : _settings.FontSize + 1;
        RootBorder.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(_settings.BackgroundColor)!;
        StatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(_settings.TextColor)!;
    }

    private void PositionOnTaskbar()
    {
        if (_handle == IntPtr.Zero) return;

        var appBar = NativeMethods.GetTaskbarPosition(_taskbarHandle);
        var dpi = NativeMethods.GetDpiForWindow(_taskbarHandle);
        var scale = dpi > 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;

        // Pure placement math (MiniProToolkit.Core.TaskbarOverlayLayout): the real appbar edge
        // decides the direction and an empty rect aborts the placement.
        if (!TaskbarOverlayLayout.TryComputePlacement(
                ToTaskbarEdge(appBar.Edge),
                appBar.Rect.Left,
                appBar.Rect.Top,
                appBar.Rect.Right,
                appBar.Rect.Bottom,
                _settings.Vertical,
                ActualWidth,
                ActualHeight,
                scale,
                out var placement))
        {
            HideForUnusableTaskbar();
            return;
        }

        if (placement.Vertical != _verticalLayout)
        {
            _verticalLayout = placement.Vertical;
            ApplyLayout();
            if (_snapshot is { } snapshot) RenderStatusText(snapshot);
        }

        // Coming back from a hidden state (taskbar popped up again) must not activate the
        // window; WS_EX_NOACTIVATE plus ShowActivated=false already keep focus where it was.
        RestoreFromUnusableTaskbar();

        // Make the overlay topmost but non-activating so it paints above the taskbar
        // without stealing focus. Its small window stays confined to the appbar area.
        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpShowWindow |
            NativeMethods.SwpFrameChanged);
    }

    /// <summary>
    /// Maps the native appbar edge to the pure layout enum. An unexpected value maps to null so
    /// the layout falls back to the saved Vertical preference instead of guessing.
    /// </summary>
    private static TaskbarEdge? ToTaskbarEdge(NativeMethods.AppBarEdge edge) => edge switch
    {
        NativeMethods.AppBarEdge.Left => TaskbarEdge.Left,
        NativeMethods.AppBarEdge.Top => TaskbarEdge.Top,
        NativeMethods.AppBarEdge.Right => TaskbarEdge.Right,
        NativeMethods.AppBarEdge.Bottom => TaskbarEdge.Bottom,
        _ => null
    };

    /// <summary>
    /// Hides the overlay while the taskbar has no usable rectangle (auto-hide in effect, shell
    /// restarting). The window keeps its handle, events and last snapshot, so the next valid
    /// rectangle can show it again at the new position; no control logic runs in between.
    /// </summary>
    private void HideForUnusableTaskbar()
    {
        if (_hiddenForUnusableTaskbar) return;
        // Still inside the first Show() (this runs from SourceInitialized): a Hide() here would
        // be undone by the pending Show, so defer to the next refresh tick.
        if (!IsLoaded) return;
        _hiddenForUnusableTaskbar = true;
        AppLog.Write("taskbar status hidden: taskbar rect unavailable (auto-hidden or empty)");
        try { Hide(); }
        catch (InvalidOperationException) { _hiddenForUnusableTaskbar = false; }
    }

    /// <summary>
    /// Shows the overlay again after <see cref="HideForUnusableTaskbar"/>. Uses the WPF Show so
    /// the normal window state is restored before it is repositioned.
    /// </summary>
    private void RestoreFromUnusableTaskbar()
    {
        if (!_hiddenForUnusableTaskbar) return;
        _hiddenForUnusableTaskbar = false;
        try { Show(); }
        catch (InvalidOperationException) { return; }
        AppLog.Write("taskbar status shown again: taskbar rect restored");
    }

    private IEnumerable<string> BuildDetails(HardwareSnapshot snapshot)
    {
        var sourceLabel = snapshot.DisplayCpuSource == CpuDisplaySource.LibreHardwareMonitor
            ? "LHM"
            : "EC/Energy Meter";
        yield return $"主显示源: {sourceLabel}（温度 {(snapshot.DisplayTemperatureFromLhm ? "LHM" : "EC")}，功耗 {(snapshot.DisplayPowerFromLhm ? "LHM" : "Energy Meter")}）";
        yield return $"CPU 温度（显示）: {FormatTemperature(snapshot.EffectiveDisplayCpuTemperatureC, snapshot.DisplayTemperatureFromLhm)}";
        yield return $"CPU 温度（EC，风扇控制/保护）: {FormatNullable(snapshot.CpuTemperatureC, "°C")}";
        yield return $"CPU 封装功耗（显示，非整机）: {FormatPower(snapshot.EffectiveDisplayCpuPackagePowerWatts)}";
        yield return $"Energy Meter 功耗: {FormatPower(snapshot.CpuPackagePowerWatts)}";
        if (snapshot.DisplaySourceStatus is { Length: > 0 } displayStatus)
            yield return $"显示源提示: {displayStatus}";
        yield return $"CPU 频率: {MetricDisplayFormat.Frequency(snapshot.CpuFrequencyMhz, snapshot.CpuFrequencyBaseMhz, snapshot.CpuFrequencyPercentOfBase)}";
        if (snapshot.CpuFrequencyStatus is { Length: > 0 } frequencyStatus)
            yield return $"频率提示: {frequencyStatus}";
        // GPU is an optional read-only LHM value; the sensor name and the redacted reader state
        // are shown here so an unusable reading is distinguishable from "not sampled yet".
        yield return $"GPU 温度（LHM 只读，不参与控制）: {FormatGpuTemperature(snapshot.GpuTemperatureC)}";
        if (snapshot.GpuSensorName is { Length: > 0 } gpuSensor)
            yield return $"GPU 传感器: {gpuSensor}";
        if (snapshot.GpuSensorStatus is { Length: > 0 } gpuStatus)
            yield return $"GPU 提示: {gpuStatus}";
        yield return $"实际 RPM: {FormatNullable(snapshot.Fan1Rpm, " RPM")}";
        yield return $"目标 RPM: {FormatNullable(snapshot.TargetRpm, " RPM")}";
        if (snapshot.RequestedTargetRpm is { } requested && requested != snapshot.TargetRpm)
            yield return $"请求 RPM: {requested} RPM";
        yield return $"RPM 误差: {FormatNullable(snapshot.RpmError, " RPM")}";
        yield return $"闭环: {FormatFeedback(snapshot.RpmFeedbackState)}";
        yield return $"目标 duty: {FormatNullable(snapshot.TargetDuty, "%")}";
        if (snapshot.FanFault is not null)
            yield return $"风扇保护: {snapshot.FanFault}";
        yield return $"模式: {FormatMode(snapshot.PowerMode)}";
        yield return $"控制: {snapshot.FanControlMode}";
        yield return $"Guardian: {(_guardianRunning ? "正常" : "未运行")}";
        yield return $"WMI: {(snapshot.Error ?? "正常")}";
    }

    private string FormatItem(TaskbarDisplayItem item, HardwareSnapshot snapshot) => item switch
    {
        TaskbarDisplayItem.CpuTemperature => $"CPU {FormatTemperature(snapshot.EffectiveDisplayCpuTemperatureC, snapshot.DisplayTemperatureFromLhm)}",
        TaskbarDisplayItem.FanRpm => $"Fan {FormatNullable(snapshot.Fan1Rpm, " RPM")}",
        TaskbarDisplayItem.CpuPackagePower => $"功耗 {FormatPower(snapshot.EffectiveDisplayCpuPackagePowerWatts)}",
        TaskbarDisplayItem.TargetRpm => $"目标 {FormatNullable(snapshot.TargetRpm, " RPM")}",
        TaskbarDisplayItem.TargetDuty => $"Duty {FormatNullable(snapshot.TargetDuty, "%")}",
        TaskbarDisplayItem.RpmFeedback => $"RPM {FormatFeedback(snapshot.RpmFeedbackState)}",
        TaskbarDisplayItem.PowerMode => FormatMode(snapshot.PowerMode),
        TaskbarDisplayItem.CpuUsage => $"CPU {FormatNullable(snapshot.CpuUsagePercent, "%")}",
        TaskbarDisplayItem.MemoryUsage => $"RAM {FormatNullable(snapshot.MemoryUsagePercent, "%")}",
        TaskbarDisplayItem.Guardian => _guardianRunning ? "Guardian OK" : "Guardian !",
        TaskbarDisplayItem.WmiStatus => snapshot.Error is null ? "WMI OK" : "WMI !",
        TaskbarDisplayItem.CpuFrequency => $"CPU {MetricDisplayFormat.Megahertz(snapshot.CpuFrequencyMhz)}",
        // Read-only GPU die temperature: labelled and never an input to any control path.
        TaskbarDisplayItem.GpuTemperature => $"GPU {FormatGpuTemperature(snapshot.GpuTemperatureC)}",
        _ => "--"
    };

    private static string FormatNullable<T>(T? value, string suffix) where T : struct =>
        value is { } actual ? $"{actual}{suffix}" : $"--{suffix}";

    private static string FormatPower(double? watts) =>
        watts is { } value && double.IsFinite(value) ? $"{value:0.0} W" : "-- W";

    // GPU temperature keeps one decimal (58.3 °C vs 58 °C matters for a comparison value) and
    // falls back to a plain "--°C" so "unknown" is never rendered as 0 °C.
    private static string FormatGpuTemperature(double? celsius) =>
        celsius is { } value && double.IsFinite(value) ? $"{value:0.0}°C" : "--°C";

    private static string FormatTemperature(double? celsius, bool oneDecimal) =>
        celsius is { } value && double.IsFinite(value)
            ? oneDecimal ? $"{value:0.0}°C" : $"{value:0}°C"
            : "--°C";

    private static string FormatFeedback(FanRpmFeedbackState state) => state switch
    {
        FanRpmFeedbackState.Stable => "稳定",
        FanRpmFeedbackState.Adjusting => "修正",
        FanRpmFeedbackState.TargetUnreachable => "目标受限/暂不可达",
        FanRpmFeedbackState.Fault => "故障/闭环停止",
        FanRpmFeedbackState.WaitingForRpm => "等待反馈",
        _ => "未启用"
    };

    private static string FormatMode(PowerMode mode) => mode switch
    {
        PowerMode.Balance => "均衡",
        PowerMode.Performance => "性能",
        PowerMode.Silence => "静音",
        _ => "--"
    };
}
