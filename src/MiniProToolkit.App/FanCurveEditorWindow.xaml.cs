using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MiniProToolkit.Core;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace MiniProToolkit.App;

public partial class FanCurveEditorWindow : Window
{
    private readonly FanCalibration _calibration;
    private readonly Func<HardwareSnapshot?>? _snapshotSource;
    private readonly ObservableCollection<EditablePoint> _points = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading = true;
    private EditablePoint? _dragPoint;
    private bool _dragDown;
    private HardwareSnapshot? _snapshot;
    // Sample window of the optional RPM/s limit. It has no dedicated control; the value is
    // preserved from the loaded configuration (Core default is 1 s) so editing the curve
    // never silently rewrites it.
    private double _rpmMinimumSampleSeconds = 1;
    // Same pattern for the optional RPM feedback loop: the stall sample count and the bad-sample
    // tolerance have no control, so the loaded values are preserved instead of being rewritten.
    private int _rpmFeedbackStallSampleCount = RpmFeedbackDefaults.StallSampleCount;
    private int _rpmFeedbackBadSampleTolerance = RpmFeedbackDefaults.BadSampleTolerance;
    private const double LeftPad = 48, RightPad = 72, TopPad = 16, BottomPad = 28;
    public FanCurveSettings? Result { get; private set; }

    public FanCurveEditorWindow(FanCurveSettings settings, FanCalibration? calibration = null,
        Func<HardwareSnapshot?>? snapshotSource = null)
    {
        _calibration = calibration ?? FanCalibration.Default;
        _snapshotSource = snapshotSource;
        InitializeComponent();
        // 纯展示：与其他功能窗共用同一份深色原生标题栏实现（DWM 不可用时静默跳过）。
        ObsidianWindowChrome.Apply(this);
        PointsGrid.ItemsSource = _points;
        LoadSettings(settings);
        _liveTimer.Tick += (_, _) => RefreshLive();
        Loaded += (_, _) => { RefreshLive(); _liveTimer.Start(); Render(); };
        Closed += (_, _) => _liveTimer.Stop();
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private void LoadSettings(FanCurveSettings settings)
    {
        settings.Validate();
        _loading = true;
        _points.Clear();
        MinimumDutyText.Text = Number(settings.MinimumDutyPercent);
        HysteresisText.Text = Number(settings.HysteresisC);
        IncreaseLimitText.Text = Number(settings.EffectiveDutyIncreasePerTick);
        DecreaseLimitText.Text = Number(settings.EffectiveDutyDecreasePerTick);
        EmergencyTemperatureText.Text = Number(settings.EmergencyTemperatureC);
        RpmRateLimitCheck.IsChecked = settings.RpmRateLimit is not null;
        RpmRiseText.Text = settings.RpmRateLimit is { } limit ? limit.MaximumRiseRpmPerSecond.ToString(CultureInfo.InvariantCulture) : "";
        RpmFallText.Text = settings.RpmRateLimit is { } limit2 ? limit2.MaximumFallRpmPerSecond.ToString(CultureInfo.InvariantCulture) : "";
        _rpmMinimumSampleSeconds = settings.RpmRateLimit?.MinimumSampleSeconds ?? 1;
        WarningTierCheck.IsChecked = settings.WarningTier is not null;
        WarningTemperatureText.Text = settings.WarningTier is { } warning ? Number(warning.TemperatureC) : "";
        WarningDutyText.Text = settings.WarningTier is { } warning2 ? warning2.DutyPercent.ToString(CultureInfo.InvariantCulture) : "";
        PreEmergencyTierCheck.IsChecked = settings.PreEmergencyTier is not null;
        PreEmergencyTemperatureText.Text = settings.PreEmergencyTier is { } pre ? Number(pre.TemperatureC) : "";
        PreEmergencyDutyText.Text = settings.PreEmergencyTier is { } pre2 ? pre2.DutyPercent.ToString(CultureInfo.InvariantCulture) : "";
        RpmTrackingCheck.IsChecked = settings.HasExplicitRpmTargets;
        RpmFeedbackCheck.IsChecked = settings.RpmFeedback is not null;
        RpmFeedbackDeadbandText.Text = settings.RpmFeedback is { } feedback ? feedback.DeadbandRpm.ToString(CultureInfo.InvariantCulture) : "";
        RpmFeedbackMinimumExpectedText.Text = settings.RpmFeedback is { } feedback2 ? feedback2.MinimumExpectedRpm.ToString(CultureInfo.InvariantCulture) : "";
        RpmFeedbackStallRatioText.Text = settings.RpmFeedback is { } feedback3 ? Number(feedback3.StallRatio) : "";
        RpmFeedbackCorrectionStepText.Text = settings.RpmFeedback is { } feedback4 ? feedback4.MaximumCorrectionStep.ToString(CultureInfo.InvariantCulture) : "";
        RpmFeedbackCorrectionDutyText.Text = settings.RpmFeedback is { } feedback5 ? feedback5.MaximumCorrectionDuty.ToString(CultureInfo.InvariantCulture) : "";
        _rpmFeedbackStallSampleCount = settings.RpmFeedback?.StallSampleCount ?? RpmFeedbackDefaults.StallSampleCount;
        _rpmFeedbackBadSampleTolerance = settings.RpmFeedback?.BadSampleTolerance ?? RpmFeedbackDefaults.BadSampleTolerance;
        LatchCurveEmergencyCheck.IsChecked = settings.LatchCurveEmergency;
        foreach (var p in settings.Points)
            _points.Add(CreatePoint(p.TemperatureC, p.DutyPercent, p.RampDownTemperatureC ?? p.TemperatureC - settings.HysteresisC, p.TargetRpm));
        _loading = false;
        if (_points.Count > 0) PointsGrid.SelectedIndex = 0;
        Render();
    }

    private EditablePoint CreatePoint(double temperature, int duty, double down, int? targetRpm = null)
    {
        var point = new EditablePoint(_calibration, temperature, duty, down, targetRpm);
        point.PropertyChanged += (_, _) => { if (!_loading) Render(); };
        return point;
    }

    private FanCurveSettings BuildSettings(bool commit = true)
    {
        if (commit && (!PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !PointsGrid.CommitEdit(DataGridEditingUnit.Row, true)))
            throw new ArgumentException("请先修正正在编辑的单元格。");
        var up = ParseInt(IncreaseLimitText.Text, "升速限制");
        var down = ParseInt(DecreaseLimitText.Text, "降速限制");
        var hysteresis = ParseDouble(HysteresisText.Text, "迟滞");
        var emergencyTemperature = ParseDouble(EmergencyTemperatureText.Text, "紧急温度");
        var baseSettings = new FanCurveSettings(_points.Select(p =>
        {
            if (p.ConversionError is not null) throw new ArgumentException(p.ConversionError);
            return new FanCurvePoint(ParseDouble(p.TemperatureC, "升温温度"), ParseInt(p.DutyPercent, "duty"),
                ParseDouble(p.RampDownTemperatureC, "降温阈值"),
                RpmTrackingCheck.IsChecked == true ? ParseInt(p.TargetRpm, "显式目标 RPM") : null);
        }).ToArray(), ParseInt(MinimumDutyText.Text, "最低 duty"), 100, 100,
            hysteresis, Math.Max(up, down), emergencyTemperature, up, down);
        baseSettings.Validate();

        // Optional safety settings: disabled means null, i.e. exactly the legacy behaviour.
        var warning = ParseTier(WarningTierCheck, "预警", WarningTemperatureText, WarningDutyText, baseSettings);
        var preEmergency = ParseTier(PreEmergencyTierCheck, "紧急前级", PreEmergencyTemperatureText, PreEmergencyDutyText, baseSettings);
        if (warning is not null && preEmergency is not null)
        {
            if (preEmergency.TemperatureC <= warning.TemperatureC)
                throw new ArgumentException("紧急前级温度必须高于预警温度。");
            if (warning.DutyPercent > preEmergency.DutyPercent)
                throw new ArgumentException("预警 duty 不能高于紧急前级 duty。");
        }

        var settings = baseSettings with
        {
            RpmRateLimit = RpmRateLimitCheck.IsChecked == true
                ? new FanRpmRateLimit(
                    ParseIntInRange(RpmRiseText.Text, "RPM 上升限制", 50, 10000, " RPM/s"),
                    ParseIntInRange(RpmFallText.Text, "RPM 下降限制", 50, 10000, " RPM/s"),
                    _rpmMinimumSampleSeconds)
                : null,
            WarningTier = warning,
            PreEmergencyTier = preEmergency,
            RpmFeedback = ParseRpmFeedback(),
            LatchCurveEmergency = LatchCurveEmergencyCheck.IsChecked == true
        };
        settings.ValidateCalibration(_calibration);
        return settings;
    }

    // Conservative defaults used when the option is switched on and when a loaded configuration did
    // not carry a value: these match the already-validated Core record defaults.
    private static class RpmFeedbackDefaults
    {
        public const int DeadbandRpm = 150;
        public const int MinimumExpectedRpm = 600;
        public const double StallRatio = 0.5;
        public const int StallSampleCount = 4;
        public const int MaximumCorrectionDuty = 12;
        public const int MaximumCorrectionStep = 2;
        public const int BadSampleTolerance = 2;
    }

    // The feedback loop is pure configuration here: it only describes how far above the curve the
    // controller may add duty. Nothing in this window writes hardware.
    private FanCurveRpmFeedbackSettings? ParseRpmFeedback()
    {
        if (RpmFeedbackCheck.IsChecked != true)
            return null;
        var maximumDuty = ParseIntInRange(RpmFeedbackCorrectionDutyText.Text, "RPM 反馈累计补 duty 上限", 1, 30, "%");
        var step = ParseIntInRange(RpmFeedbackCorrectionStepText.Text, "RPM 反馈单次补 duty", 1, 20, "%");
        if (step > maximumDuty)
            throw new ArgumentException("RPM 反馈单次补 duty 不能大于累计补 duty 上限。");
        return new FanCurveRpmFeedbackSettings(
            ParseIntInRange(RpmFeedbackDeadbandText.Text, "RPM 反馈死区", 0, 2000, " RPM"),
            ParseIntInRange(RpmFeedbackMinimumExpectedText.Text, "RPM 反馈最低期望转速", 0, 6000, " RPM"),
            ParseDoubleInRange(RpmFeedbackStallRatioText.Text, "RPM 反馈失速比例", 0.05, 0.95, ""),
            _rpmFeedbackStallSampleCount,
            maximumDuty,
            step,
            _rpmFeedbackBadSampleTolerance);
    }

    // A tier in the UI maps 1:1 onto the Core record; the extra checks only replace the
    // English Core exception text with the Chinese messages used elsewhere in this window.
    private static FanTemperatureTier? ParseTier(System.Windows.Controls.CheckBox toggle, string name,
        System.Windows.Controls.TextBox temperatureText, System.Windows.Controls.TextBox dutyText,
        FanCurveSettings baseSettings)
    {
        if (toggle.IsChecked != true)
            return null;
        var temperature = ParseDoubleInRange(temperatureText.Text, $"{name}温度", 1, 90, " °C");
        if (temperature >= baseSettings.EmergencyTemperatureC)
            throw new ArgumentException(
                $"{name}温度必须低于紧急温度 {Number(baseSettings.EmergencyTemperatureC)} °C；90 °C 紧急满速为固定保护，不可关闭。");
        var duty = ParseIntInRange(dutyText.Text, $"{name} duty", 40, 100, "%");
        var curveDuty = baseSettings.CurveDutyAt(temperature);
        if (duty < curveDuty)
            throw new ArgumentException($"{name} duty 不能低于曲线在该温度的 {curveDuty}%（分级只能增加散热）。");
        return new FanTemperatureTier(temperature, duty);
    }

    private void AddPointClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = BuildSettings();
            if (_points.Count >= 100) throw new ArgumentException("最多支持 100 个节点。");
            if (_points.Count == 1)
            {
                var p = settings.Points[0];
                if (p.TemperatureC + 1 > settings.EmergencyTemperatureC) throw new ArgumentException("请先降低现有节点温度，为新节点留出空间。");
                _points.Add(CreatePoint(p.TemperatureC + 1, p.DutyPercent, p.RampDownTemperatureC!.Value + 1, p.TargetRpm));
                PointsGrid.SelectedIndex = 1;
            }
            else
            {
                var index = Enumerable.Range(1, _points.Count - 1)
                    .MaxBy(i => settings.Points[i].TemperatureC - settings.Points[i - 1].TemperatureC);
                var a = settings.Points[index - 1]; var b = settings.Points[index];
                if (b.TemperatureC - a.TemperatureC < 1) throw new ArgumentException("节点过密，请先拉开温度间距。");
                _points.Insert(index, CreatePoint((a.TemperatureC + b.TemperatureC) / 2,
                    (a.DutyPercent + b.DutyPercent) / 2, (a.RampDownTemperatureC!.Value + b.RampDownTemperatureC!.Value) / 2,
                    a.TargetRpm is { } ar && b.TargetRpm is { } br ? (ar + br) / 2 : null));
                PointsGrid.SelectedIndex = index;
            }
            Render();
        }
        catch (Exception ex) when (IsInputError(ex)) { ShowError(ex); }
    }

    private void RemovePointClick(object sender, RoutedEventArgs e)
    {
        if (PointsGrid.SelectedItem is EditablePoint p && _points.Count > 1) { _points.Remove(p); Render(); }
    }
    private void ResetClick(object sender, RoutedEventArgs e) => LoadSettings(FanCurveSettings.Default);
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "曲线 JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { LoadSettings(new FanCurveConfigurationStore(dialog.FileName).Load()); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = BuildSettings();
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "曲线 JSON (*.json)|*.json", FileName = "fan-curve.json" };
            if (dialog.ShowDialog(this) == true) new FanCurveConfigurationStore(dialog.FileName).Save(settings);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try { Result = BuildSettings(); DialogResult = true; }
        catch (Exception ex) when (IsInputError(ex)) { ShowError(ex); }
    }
    private void ShowError(Exception ex) { FeedbackText.Text = ex.Message; FeedbackText.Foreground = ObsidianTheme.BrushOf(this, "ObsidianWarningBrush"); }
    private static bool IsInputError(Exception ex) => ex is FormatException or ArgumentException or OverflowException;
    private static int ParseInt(string text, string name) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException($"{name}必须是整数。");
    private static double ParseDouble(string text, string name) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException($"{name}必须是有效数字。");
    private static int ParseIntInRange(string text, string name, int min, int max, string unit)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"{name}必须是整数。");
        return value >= min && value <= max
            ? value : throw new ArgumentException($"{name}需要在 {min}–{max}{unit} 之间。");
    }
    private static double ParseDoubleInRange(string text, string name, double min, double max, string unit)
    {
        var value = ParseDouble(text, name);
        return value >= min && value <= max
            ? value : throw new ArgumentException($"{name}需要在 {Number(min)}–{Number(max)}{unit} 之间。");
    }
    // Enabling a safety option pre-fills a usable, valid value so the option is never left in a
    // half-configured state that can only fail on save. Unchecking keeps the text for re-enabling
    // but always saves as "disabled".
    private void SafetyToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (RpmRateLimitCheck.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(RpmRiseText.Text)) RpmRiseText.Text = "600";
            if (string.IsNullOrWhiteSpace(RpmFallText.Text)) RpmFallText.Text = "1200";
        }
        if (WarningTierCheck.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(WarningTemperatureText.Text)) WarningTemperatureText.Text = "70";
            if (string.IsNullOrWhiteSpace(WarningDutyText.Text)) WarningDutyText.Text = "80";
        }
        if (PreEmergencyTierCheck.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(PreEmergencyTemperatureText.Text)) PreEmergencyTemperatureText.Text = "82";
            if (string.IsNullOrWhiteSpace(PreEmergencyDutyText.Text)) PreEmergencyDutyText.Text = "100";
        }
        if (RpmFeedbackCheck.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(RpmFeedbackDeadbandText.Text))
                RpmFeedbackDeadbandText.Text = RpmFeedbackDefaults.DeadbandRpm.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(RpmFeedbackMinimumExpectedText.Text))
                RpmFeedbackMinimumExpectedText.Text = RpmFeedbackDefaults.MinimumExpectedRpm.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(RpmFeedbackStallRatioText.Text))
                RpmFeedbackStallRatioText.Text = Number(RpmFeedbackDefaults.StallRatio);
            if (string.IsNullOrWhiteSpace(RpmFeedbackCorrectionStepText.Text))
                RpmFeedbackCorrectionStepText.Text = RpmFeedbackDefaults.MaximumCorrectionStep.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(RpmFeedbackCorrectionDutyText.Text))
                RpmFeedbackCorrectionDutyText.Text = RpmFeedbackDefaults.MaximumCorrectionDuty.ToString(CultureInfo.InvariantCulture);
        }
        Render();
    }
    private void TrackingToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        if (RpmTrackingCheck.IsChecked == true)
        {
            RpmFeedbackCheck.IsChecked = false;
            foreach (var point in _points)
                if (string.IsNullOrWhiteSpace(point.TargetRpm) && int.TryParse(point.DutyPercent, out var duty))
                    point.TargetRpm = _calibration.RpmForDuty(duty).ToString(CultureInfo.InvariantCulture);
        }
        _loading = false;
        Render();
    }
    private void SettingsChanged(object sender, TextChangedEventArgs e) { if (!_loading) Render(); }
    private void ChartSizeChanged(object sender, SizeChangedEventArgs e) { if (!_loading) Render(); }
    private void PointSelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) Render(); }

    private double PlotWidth => Math.Max(1, CurveCanvas.ActualWidth - LeftPad - RightPad);
    private double PlotHeight => Math.Max(1, CurveCanvas.ActualHeight - TopPad - BottomPad);
    private Point Position(double temperature, double duty) => new(LeftPad + (temperature - 1) / 94 * PlotWidth, TopPad + (100 - duty) / 100 * PlotHeight);

    private void Render()
    {
        if (_loading || CurveCanvas is null || CurveCanvas.ActualWidth < 100) return;
        CurveCanvas.Children.Clear();
        try
        {
            var settings = BuildSettings(false);
            FeedbackText.Foreground = ObsidianTheme.BrushOf(this, "ObsidianAccentBrush");
            FeedbackText.Text = "曲线有效。" + SafetySummary(settings) +
                (settings.HasExplicitRpmTargets
                    ? "显式目标 RPM 独立保存；图中 duty 是不可削弱的温度安全线，安全线超速只报告限制。"
                    : "未启用显式跟踪；标定 RPM→duty 仅换算安全 duty。") +
                "拖动只编辑安全线，不改显式目标 RPM。";
            foreach (var temp in new[] { 1, 20, 40, 60, 80, 95 })
            {
                var x = Position(temp, 0).X;
                AddLine(CurveCanvas, new(x, TopPad), new(x, TopPad + PlotHeight), ObsidianTheme.BrushOf(this, "ObsidianChartGuideBrush"));
                AddText(CurveCanvas, $"{temp}°", x - 10, TopPad + PlotHeight + 4);
            }
            foreach (var duty in new[] { 0, 40, 60, 80, 100 })
            {
                var y = Position(1, duty).Y;
                AddLine(CurveCanvas, new(LeftPad, y), new(LeftPad + PlotWidth, y), ObsidianTheme.BrushOf(this, "ObsidianChartGuideBrush"));
                AddText(CurveCanvas, $"{duty}%", 4, y - 8);
                if (duty >= 40) AddText(CurveCanvas, $"≈{_calibration.RpmForDuty(duty)}", LeftPad + PlotWidth + 6, y - 8);
            }
            AddText(CurveCanvas, "估计 RPM", LeftPad + PlotWidth + 6, 0);
            var emergency = Position(settings.EmergencyTemperatureC, 0).X;
            AddLine(CurveCanvas, new(emergency, TopPad), new(emergency, TopPad + PlotHeight), ObsidianTheme.BrushOf(this, "ObsidianDangerBrush"), true);
            AddTierLine(settings.WarningTier, "预警");
            AddTierLine(settings.PreEmergencyTier, "前级");
            var minimum = Position(1, settings.MinimumDutyPercent).Y;
            AddLine(CurveCanvas, new(LeftPad, minimum), new(LeftPad + PlotWidth, minimum), ObsidianTheme.BrushOf(this, "ObsidianChartTargetRpmBrush"), true);
            var rising = new Polyline { Stroke = ObsidianTheme.BrushOf(this, "ObsidianChartRpmBrush"), StrokeThickness = 2 };
            var falling = new Polyline { Stroke = ObsidianTheme.BrushOf(this, "ObsidianChartPowerBrush"), StrokeThickness = 2, StrokeDashArray = [4, 3] };
            foreach (var p in settings.Points)
            {
                rising.Points.Add(Position(p.TemperatureC, p.DutyPercent));
                falling.Points.Add(Position(p.RampDownTemperatureC!.Value, p.DutyPercent));
            }
            CurveCanvas.Children.Add(rising); CurveCanvas.Children.Add(falling);
            for (var i = 0; i < settings.Points.Count; i++)
            {
                var p = settings.Points[i];
                AddNode(_points[i], Position(p.RampDownTemperatureC!.Value, p.DutyPercent), true);
                AddNode(_points[i], Position(p.TemperatureC, p.DutyPercent), false);
            }
            RenderCursor(settings);
            RenderCalibration();
        }
        catch (Exception ex) when (IsInputError(ex)) { ShowError(ex); RenderCalibration(); }
    }

    // Optional tiers are drawn as thin guides only; the red 90 °C emergency line is never
    // replaced by them.
    private void AddTierLine(FanTemperatureTier? tier, string label)
    {
        if (tier is null) return;
        var x = Position(tier.TemperatureC, 0).X;
        AddLine(CurveCanvas, new(x, TopPad), new(x, TopPad + PlotHeight), ObsidianTheme.BrushOf(this, "ObsidianChartDutyBrush"), true);
        AddText(CurveCanvas, $"{label} {tier.DutyPercent}%", x + 2, TopPad + PlotHeight - 14);
    }

    private static string SafetySummary(FanCurveSettings settings)
    {
        var enabled = new List<string>();
        if (settings.RpmRateLimit is { } limit)
            enabled.Add($"RPM/s 限速 升 {limit.MaximumRiseRpmPerSecond} / 降 {limit.MaximumFallRpmPerSecond} RPM/s");
        if (settings.WarningTier is { } warning)
            enabled.Add($"预警 {Number(warning.TemperatureC)}°C/{warning.DutyPercent}%");
        if (settings.PreEmergencyTier is { } pre)
            enabled.Add($"前级 {Number(pre.TemperatureC)}°C/{pre.DutyPercent}%");
        if (settings.HasExplicitRpmTargets)
            enabled.Add("显式目标 RPM 跟踪（安全底线优先）");
        if (settings.RpmFeedback is { } feedback)
            enabled.Add($"RPM 反馈 死区 {feedback.DeadbandRpm} RPM · 补 ≤{feedback.MaximumCorrectionDuty}%");
        if (settings.LatchCurveEmergency)
            enabled.Add("90°C 锁存（仅“恢复自动/停止曲线”释放）");
        return enabled.Count == 0
            ? "可选安全设置：全部关闭（与旧版行为一致）。"
            : $"可选安全设置：{string.Join(" · ", enabled)}。";
    }

    private void AddNode(EditablePoint point, Point position, bool down)
    {
        var node = new Ellipse
        {
            Width = 12, Height = 12,
            Fill = ObsidianTheme.BrushOf(this, down ? "ObsidianChartPowerBrush" : "ObsidianChartRpmBrush"),
            Stroke = ObsidianTheme.BrushOf(this,
                ReferenceEquals(point, PointsGrid.SelectedItem) ? "ObsidianAccentBrush" : "ObsidianTextPrimaryBrush"),
            StrokeThickness = 2, Tag = (point, down), Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = $"{(down ? "降温" : "升温")} { (down ? point.RampDownTemperatureC : point.TemperatureC)}°C · {point.DutyPercent}% · 标定约 {point.CalibrationRpm} RPM · 显式目标 {point.TargetRpm} RPM"
        };
        Canvas.SetLeft(node, position.X - 6); Canvas.SetTop(node, position.Y - 6);
        CurveCanvas.Children.Add(node);
    }

    private void ChartMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Ellipse { Tag: ValueTuple<EditablePoint, bool> tag }) return;
        try { BuildSettings(); }
        catch (Exception ex) when (IsInputError(ex)) { ShowError(ex); return; }
        _dragPoint = tag.Item1; _dragDown = tag.Item2;
        PointsGrid.SelectedItem = _dragPoint;
        CurveCanvas.CaptureMouse();
        e.Handled = true;
    }
    private void ChartMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragPoint is null || e.LeftButton != MouseButtonState.Pressed) return;
        MoveNode(_dragPoint, _dragDown, e.GetPosition(CurveCanvas));
    }
    private void MoveNode(EditablePoint point, bool down, Point mouse)
    {
        try
        {
            var settings = BuildSettings(false);
            var index = _points.IndexOf(point);
            if (index < 0) return;
            var current = settings.Points[index];
            var before = index > 0 ? settings.Points[index - 1] : null;
            var after = index < settings.Points.Count - 1 ? settings.Points[index + 1] : null;
            var temperature = Math.Round(1 + (mouse.X - LeftPad) / PlotWidth * 94, 1);
            var minTemp = down ? (before?.RampDownTemperatureC ?? 0.9) + 0.1 : Math.Max((before?.TemperatureC ?? 0.9) + 0.1, current.RampDownTemperatureC!.Value);
            var maxTemp = down ? Math.Min(current.TemperatureC, (after?.RampDownTemperatureC ?? 125) - 0.1) : Math.Min(settings.EmergencyTemperatureC, (after?.TemperatureC ?? 125) - 0.1);
            if (minTemp > maxTemp) return;
            var duty = Math.Clamp((int)Math.Round(100 - (mouse.Y - TopPad) / PlotHeight * 100),
                before?.DutyPercent ?? settings.MinimumDutyPercent, after?.DutyPercent ?? 100);
            _loading = true;
            if (down) point.RampDownTemperatureC = Number(Math.Clamp(temperature, minTemp, maxTemp));
            else point.TemperatureC = Number(Math.Clamp(temperature, minTemp, maxTemp));
            point.DutyPercent = duty.ToString(CultureInfo.InvariantCulture);
            _loading = false;
            Render();
        }
        catch (Exception ex) when (IsInputError(ex)) { _loading = false; ShowError(ex); }
    }
    private void ChartMouseUp(object sender, MouseButtonEventArgs e) { _dragPoint = null; CurveCanvas.ReleaseMouseCapture(); }
    private void ChartLostCapture(object sender, System.Windows.Input.MouseEventArgs e) => _dragPoint = null;

    private void RefreshLive()
    {
        _snapshot = _snapshotSource?.Invoke(); // Cached snapshot supplied by owner: no WMI reads here.
        Render();
    }
    private void RenderCursor(FanCurveSettings settings)
    {
        if (_snapshot is not { } s) { LiveText.Text = "暂无实时数据；编辑器不会主动读取或写入硬件。"; return; }
        var age = DateTimeOffset.Now - s.Timestamp;
        if (s.Error is not null || age > TimeSpan.FromSeconds(5) || age < TimeSpan.FromSeconds(-2) ||
            s.CpuTemperatureC is not { } temperature || !double.IsFinite(temperature) || s.Fan1Rpm is not { } rpm || rpm < 0)
        { LiveText.Text = "实时数据无效或已过期，游标已隐藏。"; return; }
        var cursorDuty = _calibration.DutyForRpm(rpm);
        var position = Position(temperature, cursorDuty);
        AddLine(CurveCanvas, new(position.X, TopPad), new(position.X, TopPad + PlotHeight), ObsidianTheme.BrushOf(this, "ObsidianAccentBrush"), true);
        var dot = new Ellipse { Width = 10, Height = 10, Fill = ObsidianTheme.BrushOf(this, "ObsidianAccentBrush"), IsHitTestVisible = false };
        Canvas.SetLeft(dot, position.X - 5); Canvas.SetTop(dot, position.Y - 5); CurveCanvas.Children.Add(dot);
        LiveText.Text = $"实时：{temperature:0.#}°C · 实际 {rpm} RPM · 请求 duty {s.TargetDuty?.ToString() ?? "--"}%（绿点纵轴为 RPM 反算参考，不是实测 duty）";
    }

    private void RenderCalibration()
    {
        if (CalibrationCanvas.ActualWidth < 100) return;
        CalibrationCanvas.Children.Clear();
        var width = CalibrationCanvas.ActualWidth; var height = CalibrationCanvas.ActualHeight;
        AddText(CalibrationCanvas, $"本机标定参考（横轴 duty，纵轴 RPM）：40% ≈ {_calibration.RpmForDuty(40)} / 100% ≈ {_calibration.RpmForDuty(100)} RPM", 8, 2);
        var line = new Polyline { Stroke = ObsidianTheme.BrushOf(this, "ObsidianChartDutyBrush"), StrokeThickness = 2 };
        var maximum = Math.Max(1, _calibration.Points[^1].Rpm);
        foreach (var p in _calibration.Points)
            line.Points.Add(new(LeftPad + p.DutyPercent / 100.0 * (width - LeftPad - RightPad), height - 8 - p.Rpm / (double)maximum * (height - 30)));
        CalibrationCanvas.Children.Add(line);
        if (PointsGrid.SelectedItem is EditablePoint point && int.TryParse(point.DutyPercent, out var duty) && duty is >= 0 and <= 100)
        {
            var x = LeftPad + duty / 100.0 * (width - LeftPad - RightPad);
            AddLine(CalibrationCanvas, new(x, 24), new(x, height - 4), ObsidianTheme.BrushOf(this, "ObsidianChartRpmBrush"), true);
        }
    }
    private static void AddLine(Canvas canvas, Point start, Point end, System.Windows.Media.Brush brush, bool dashed = false)
    {
        var line = new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false };
        if (dashed) line.StrokeDashArray = [4, 3];
        canvas.Children.Add(line);
    }
    private void AddText(Canvas canvas, string text, double x, double y)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = ObsidianTheme.BrushOf(this, "ObsidianTextSecondaryBrush"),
            FontSize = 11,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); canvas.Children.Add(label);
    }

    public sealed class EditablePoint : INotifyPropertyChanged
    {
        private readonly FanCalibration _calibration;
        private string _temperature, _duty, _rpm, _down, _targetRpm;
        public EditablePoint(FanCalibration calibration, double temperature, int duty, double down, int? targetRpm = null)
        {
            _calibration = calibration; _temperature = Number(temperature); _duty = duty.ToString();
            _rpm = calibration.RpmForDuty(duty).ToString(); _down = Number(down);
            _targetRpm = targetRpm?.ToString(CultureInfo.InvariantCulture) ?? "";
        }
        public string? ConversionError { get; private set; }
        public string TemperatureC { get => _temperature; set { _temperature = value; Changed(nameof(TemperatureC)); } }
        public string RampDownTemperatureC { get => _down; set { _down = value; Changed(nameof(RampDownTemperatureC)); } }
        public string DutyPercent
        {
            get => _duty;
            set
            {
                _duty = value;
                ConversionError = null;
                if (int.TryParse(value, out var duty) && duty is >= 0 and <= 100) _rpm = _calibration.RpmForDuty(duty).ToString();
                Changed(nameof(DutyPercent)); Changed(nameof(CalibrationRpm));
            }
        }
        // Explicit tracking input is deliberately independent of the duty safety line.
        public string TargetRpm { get => _targetRpm; set { _targetRpm = value; Changed(nameof(TargetRpm)); } }
        public string CalibrationRpm
        {
            get => _rpm;
            set
            {
                _rpm = value;
                if (!int.TryParse(value, out var rpm) || rpm < _calibration.RpmForDuty(40) || rpm > _calibration.RpmForDuty(100))
                    ConversionError = $"标定换算 RPM 必须在本机标定范围 {_calibration.RpmForDuty(40)}–{_calibration.RpmForDuty(100)} 内。";
                else
                {
                    ConversionError = null;
                    _duty = Math.Clamp(_calibration.DutyForRpm(rpm), 40, 100).ToString();
                }
                Changed(nameof(CalibrationRpm)); Changed(nameof(DutyPercent));
            }
        }
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
