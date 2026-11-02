using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Input;
using MiniProToolkit.Core;
using MiniProToolkit.Hardware;

namespace MiniProToolkit.App;

public partial class DiagnosticsWindow : Window
{
    private readonly bool _demo;
    private readonly string _summary;
    private readonly string _historyDirectory;
    private readonly string _eventPath;
    private bool _systemControlBusy;
    private SystemControlAvailabilitySnapshot _controlAvailability = SystemControlAvailabilitySnapshot.Unavailable("控制尚未检查。");

    public DiagnosticsWindow(string curveConfigPath, string telemetryJsonlPath, string telemetryCsvPath, bool demo = false, string? diagnosticEventsPath = null)
    {
        _demo = demo;
        _historyDirectory = System.IO.Path.GetDirectoryName(telemetryJsonlPath) ?? string.Empty;
        _eventPath = diagnosticEventsPath ?? System.IO.Path.Combine(_historyDirectory, "diagnostic-events.jsonl");
        InitializeComponent();
        var leasePath = GuardianLeaseStore.DefaultPath;
        var appLogPath = AppLog.Path;
        var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        AdminText.Text = isAdmin ? "是" : "否（硬件控制可能失败）";
        GuardianText.Text = GetGuardianStatus();
        LeaseText.Text = File.Exists(leasePath) ? $"存在：{leasePath}" : "不存在（正常待命）";
        CurveConfigText.Text = curveConfigPath;
        JsonlText.Text = telemetryJsonlPath;
        CsvText.Text = telemetryCsvPath;
        AppLogText.Text = appLogPath;
        VersionText.Text = typeof(DiagnosticsWindow).Assembly.GetName().Version?.ToString() ?? "开发版";
        RecoveryText.Text = ReadRecoverySummary(leasePath, appLogPath, _eventPath);
        RecentText.Text = BuildRecentText(leasePath, appLogPath);
        _summary = BuildSummary(curveConfigPath, telemetryJsonlPath, telemetryCsvPath, leasePath, appLogPath, _eventPath);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void CopySummaryClick(object sender, RoutedEventArgs e) { System.Windows.Clipboard.SetText(_summary); RecentText.Text = "已复制脱敏诊断摘要。\n\n" + _summary; }
    private void ExportSummaryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "文本文件 (*.txt)|*.txt", FileName = "miniprotoolkit-diagnostics.txt" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, _summary);
    }
    private void HistoryClick(object sender, RoutedEventArgs e) { new HistoryWindow(_historyDirectory) { Owner = this }.ShowDialog(); }

    private async void ProbeCapabilitiesClick(object sender, RoutedEventArgs e)
    {
        CapabilitiesText.Text = _demo
            ? "演示模式：不访问硬件，仅列出已纳入产品的硬件能力。"
            : "正在只读探测 WMI 类与实例……";
        try
        {
            var capabilities = _demo
                ? HardwareCapabilities.Demo
                : await Task.Run(() => new HardwareCapabilityProbe().Probe());
            CapabilitiesText.Text = string.Join(Environment.NewLine,
                capabilities.Items.Select(item =>
                    $"{item.DisplayName}: {StateText(item.State)} — {item.Details}"));
        }
        catch (Exception ex)
        {
            CapabilitiesText.Text = $"探测失败：{ex.GetBaseException().Message}";
        }
    }

    /// <summary>
    /// Probes explicit write-control availability without writing. All platform work is
    /// dispatched from the UI to background workers; demo mode keeps every control disabled.
    /// </summary>
    private async void ProbeSystemControlsClick(object sender, RoutedEventArgs e)
    {
        if (_demo)
        {
            SystemControlsText.Text = "演示模式：不访问硬件，写入控制保持禁用。";
            SetSystemControlEnabled(false, false, false);
            return;
        }

        if (_systemControlBusy)
            return;
        _systemControlBusy = true;
        SetSystemControlEnabled(false, false, false);
        ProbeSystemControlsButton.IsEnabled = false;
        SystemControlsText.Text = "正在检查控制可用性（不写入真实硬件）……";
        try
        {
            var result = await Task.Run(() =>
            {
                var audio = new AudioEndpointReader().ProbeControls();
                var brightness = new BrightnessReader().ProbeControl();
                return new SystemControlAvailabilitySnapshot(audio.OutputVolume, audio.MicrophoneMute, brightness);
            });
            _controlAvailability = result;
            SetSystemControlEnabled(result.OutputVolume.IsAvailable, result.MicrophoneMute.IsAvailable, result.Brightness.IsAvailable);
            SystemControlsText.Text = string.Join(Environment.NewLine,
                $"输出音量：{AvailabilityText(result.OutputVolume)}",
                $"麦克风静音：{AvailabilityText(result.MicrophoneMute)}",
                $"内建屏幕亮度：{AvailabilityText(result.Brightness)}");
        }
        catch (Exception ex)
        {
            _controlAvailability = SystemControlAvailabilitySnapshot.Unavailable(SafeStatus(ex));
            SetSystemControlEnabled(false, false, false);
            SystemControlsText.Text = $"控制可用性检查失败：{SafeStatus(ex)}";
        }
        finally
        {
            _systemControlBusy = false;
            ProbeSystemControlsButton.IsEnabled = true;
        }
    }

    private async void SetOutputVolumeClick(object sender, RoutedEventArgs e)
    {
        var percent = (int)Math.Round(OutputVolumeTarget.Value, MidpointRounding.AwayFromZero);
        await RunControlWriteAsync(
            "输出音量",
            () => new AudioEndpointReader().SetOutputVolume(percent),
            SetOutputVolumeButton);
    }

    private async void SetMicrophoneMuteClick(object sender, RoutedEventArgs e)
    {
        var muted = MicrophoneMuteTarget.IsChecked == true;
        await RunControlWriteAsync(
            "麦克风静音",
            () => new AudioEndpointReader().SetMicrophoneMute(muted),
            SetMicrophoneMuteButton);
    }

    private async void SetBrightnessClick(object sender, RoutedEventArgs e)
    {
        var percent = (int)Math.Round(BrightnessTarget.Value, MidpointRounding.AwayFromZero);
        await RunControlWriteAsync(
            "内建屏幕亮度",
            () => new BrightnessReader().SetBrightness(percent),
            SetBrightnessButton);
    }

    private async Task RunControlWriteAsync(
        string displayName,
        Func<SystemControlWriteResult> operation,
        System.Windows.Controls.Button button)
    {
        if (_demo || _systemControlBusy || !button.IsEnabled)
            return;

        _systemControlBusy = true;
        ProbeSystemControlsButton.IsEnabled = false;
        SetSystemControlEnabled(false, false, false);
        SystemControlsText.Text = $"正在设置{displayName}并读回确认……";
        try
        {
            var result = await Task.Run(operation);
            if (!result.IsSuccess)
            {
                DisableSystemControl(displayName);
                SystemControlsText.Text = $"{displayName}：{WriteResultText(result)}（需重新检查可用性；不会重试写入）。";
            }
            else
            {
                SystemControlsText.Text = $"{displayName}：{WriteResultText(result)}";
            }
        }
        catch (Exception ex)
        {
            DisableSystemControl(displayName);
            SystemControlsText.Text = $"{displayName}：写入失败或未确认：{SafeStatus(ex)}";
        }
        finally
        {
            _systemControlBusy = false;
            ProbeSystemControlsButton.IsEnabled = true;
            SetSystemControlEnabled(_controlAvailability.OutputVolume.IsAvailable,
                _controlAvailability.MicrophoneMute.IsAvailable, _controlAvailability.Brightness.IsAvailable);
        }
    }

    private void DisableSystemControl(string displayName)
    {
        switch (displayName)
        {
            case "输出音量":
                _controlAvailability = _controlAvailability with
                { OutputVolume = SystemControlAvailability.Unavailable("输出音量控制已不可用。") };
                OutputVolumeTarget.IsEnabled = false;
                SetOutputVolumeButton.IsEnabled = false;
                break;
            case "麦克风静音":
                _controlAvailability = _controlAvailability with
                { MicrophoneMute = SystemControlAvailability.Unavailable("麦克风静音控制已不可用。") };
                MicrophoneMuteTarget.IsEnabled = false;
                SetMicrophoneMuteButton.IsEnabled = false;
                break;
            default:
                _controlAvailability = _controlAvailability with
                { Brightness = SystemControlAvailability.Unavailable("内建屏幕亮度控制已不可用。") };
                BrightnessTarget.IsEnabled = false;
                SetBrightnessButton.IsEnabled = false;
                break;
        }
    }

    private void SetSystemControlEnabled(bool outputVolume, bool microphoneMute, bool brightness)
    {
        OutputVolumeTarget.IsEnabled = outputVolume;
        SetOutputVolumeButton.IsEnabled = outputVolume;
        MicrophoneMuteTarget.IsEnabled = microphoneMute;
        SetMicrophoneMuteButton.IsEnabled = microphoneMute;
        BrightnessTarget.IsEnabled = brightness;
        SetBrightnessButton.IsEnabled = brightness;
    }

    private static string AvailabilityText(SystemControlAvailability availability) =>
        availability.IsAvailable ? $"可用（{DiagnosticRedactor.Redact(availability.Status)}）" :
        $"不可用（{DiagnosticRedactor.Redact(availability.Status)}）";

    private static string WriteResultText(SystemControlWriteResult result) =>
        result.IsSuccess
            ? $"已写入并读回匹配：{DiagnosticRedactor.Redact(result.Status)}"
            : $"{result.State switch { SystemControlWriteState.Unconfirmed => "未确认", SystemControlWriteState.Unavailable => "不可用", _ => "失败" }}：{DiagnosticRedactor.Redact(result.Status)}";

    private static string SafeStatus(Exception ex) =>
        $"系统指示操作失败（HRESULT 0x{ex.GetBaseException().HResult:X8}）。";

    private async void ReadSystemIndicatorsClick(object sender, RoutedEventArgs e)
    {
        if (_demo)
        {
            SystemIndicatorsText.Text = string.Join(Environment.NewLine,
                SystemIndicatorSnapshot.Unavailable("演示模式不采集系统指示：不读取键盘切换状态、屏幕亮度或音频端点。").Lines());
            return;
        }

        SystemIndicatorsText.Text = "正在只读读取内建屏幕亮度与默认音频端点（各操作最多约 3 秒）……";
        var lockKeys = ReadLockKeys();
        try
        {
            var snapshot = await Task.Run(() => HardwareCapabilityProbe.ReadIndicators(lockKeys));
            SystemIndicatorsText.Text = string.Join(Environment.NewLine, snapshot.Lines());
        }
        catch (Exception ex)
        {
            // A read failure must never break the window or fabricate a value: keep the
            // real lock-key state and mark the rest unknown (status text is redacted).
            var unavailable = SystemIndicatorSnapshot
                .Unavailable(ex.GetBaseException().Message)
                .Lines()
                .Skip(1);
            SystemIndicatorsText.Text = string.Join(Environment.NewLine,
                new[] { SystemIndicatorText.LockKeys(lockKeys) }.Concat(unavailable));
        }
    }

    /// <summary>Reads the keyboard toggle state; must run on the UI thread.</summary>
    private static SystemLockKeys ReadLockKeys() => new(
        Keyboard.IsKeyToggled(Key.CapsLock),
        Keyboard.IsKeyToggled(Key.NumLock));

    private static string StateText(CapabilityState state) => state switch
    {
        CapabilityState.Confirmed => "已确认",
        CapabilityState.Unsupported => "不支持",
        CapabilityState.NotVerified => "未验证（未开放）",
        CapabilityState.Error => "错误",
        _ => "未检查"
    };

    private static string GetGuardianStatus()
    {
        try
        {
            using var service = new ServiceController(GuardianAvailability.ServiceName);
            return $"{service.Status} / {service.StartType}";
        }
        catch (Exception ex)
        {
            return $"不可用：{ex.GetBaseException().Message}";
        }
    }

    private static string ReadRecoverySummary(string leasePath, string appLogPath, string eventPath)
    {
        var lines = new List<string>();
        if (File.Exists(leasePath))
        {
            var lease = new GuardianLeaseStore(leasePath).Read();
            lines.Add(lease is null ? "租约存在但无法读取" : $"租约：PID {lease.ProcessId} · {lease.ControlMode} · 心跳 {lease.HeartbeatUtc:O}");
        }
        var log = File.Exists(appLogPath) ? File.ReadLines(appLogPath).Where(line => TelemetryHistory.Classify(line) == HistoryEventKind.Recovery).TakeLast(3) : [];
        lines.AddRange(log);
        try
        {
            var guardianPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiniProToolkit", "diagnostic-events.jsonl");
            lines.AddRange(new DiagnosticEventStore(eventPath).ReadRecent(50)
                .Concat(new DiagnosticEventStore(guardianPath).ReadRecent(50))
                .Where(e => e.Category == nameof(HistoryEventKind.Recovery) || e.Message.Contains("恢复", StringComparison.OrdinalIgnoreCase))
                .Select(e => $"{e.Timestamp:O} {e.Source}/{e.Category}: {e.Message}"));
        }
        catch (Exception ex) { lines.Add($"诊断事件读取失败：{ex.GetBaseException().Message}"); }
        return lines.Count == 0 ? "暂无恢复记录。" : DiagnosticRedactor.Redact(string.Join(Environment.NewLine, lines.Distinct().TakeLast(20)));
    }

    private string BuildSummary(string curve, string jsonl, string csv, string lease, string log, string eventPath)
    {
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var guardian = GetGuardianStatus();
        var leaseState = File.Exists(lease) ? "存在（不要直接删除）" : "不存在";
        var recent = BuildRecentText(lease, log);
        var events = ReadDiagnosticEvents(eventPath)
            .Concat(ReadDiagnosticEvents(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiniProToolkit", "diagnostic-events.jsonl")))
            .OrderByDescending(e => e.Timestamp).Take(30).OrderBy(e => e.Timestamp)
            .Select(e => $"{e.Timestamp:O} [{e.Source}/{e.Category}] {e.Message}");
        return DiagnosticRedactor.Redact($"MiniProToolkit 诊断摘要\n时间：{DateTimeOffset.Now:O}\n版本：{typeof(DiagnosticsWindow).Assembly.GetName().Version}\n演示模式：{_demo}\n管理员：{admin}\nGuardian：{guardian}\n租约：{leaseState}\n曲线配置：{curve}\n历史 JSONL：{jsonl}\n历史 CSV：{csv}\n应用日志：{log}\n诊断事件：{eventPath}\n最近信息：\n{recent}\n最近持久化事件：\n{string.Join(Environment.NewLine, events)}");
    }

    private static IReadOnlyList<DiagnosticEventRecord> ReadDiagnosticEvents(string path)
    {
        try { return new DiagnosticEventStore(path).ReadRecent(100); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string BuildRecentText(string leasePath, string appLogPath)
    {
        var lines = new List<string>();
        if (File.Exists(leasePath))
            lines.Add("当前存在 Guardian 租约，请勿直接删除。先使用恢复 EC 自动。");
        if (File.Exists(appLogPath))
        {
            lines.Add("--- 应用日志末尾 ---");
            lines.AddRange(File.ReadLines(appLogPath).TakeLast(12));
        }
        return lines.Count == 0 ? "暂无诊断事件。" : string.Join(Environment.NewLine, lines);
    }
}
