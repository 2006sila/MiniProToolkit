using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorBrush = System.Windows.Media.SolidColorBrush;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

/// <summary>
/// 方案 A「曜石控制舱」的纯展示层代码：新增大数字/指标控件的内容更新、主题画笔查找、
/// 深色原生标题栏与首屏工作区适配。
/// 这里只读已经发布的 <see cref="HardwareSnapshot"/> 字段做格式化，不采样、不写 EC、
/// 不改变任何控制阈值或 Guardian/租约逻辑，也不从已有字符串反向解析数值。
/// </summary>
public partial class MainWindow
{
    // Last display-source sentence the trend legend was built from. Pure UI cache: only the
    // caption's Inlines depend on it, and an unchanged value means an identical legend.
    private string? _trendLegendSignature;

    /// <summary>
    /// 窗口源创建后的一次性展示回调（与原 RegisterHotkeys 并列，不替换它）：
    /// 深色原生标题栏 + 不超出当前工作区的首屏尺寸。任何失败都只影响外观。
    /// </summary>
    private void ApplyObsidianPresentation()
    {
        try
        {
            ApplyDarkTitleBar();
        }
        catch (Exception)
        {
            // 标题栏着色是可选装饰：DWM 不可用时保持系统默认外观，启动不受影响。
        }

        try
        {
            ClampWindowToWorkArea();
            UpdateContentScrollMode();
        }
        catch (Exception)
        {
            // 工作区读取失败时保留 XAML 里的默认尺寸。
        }
    }

    /// <summary>
    /// 纯展示：980 DIP 以上不出现水平滚动条（内容按 * 列自适应）；
    /// 工作区被高 DPI 压到 980 以下时允许水平滚动，避免风扇卡被硬裁切。
    /// </summary>
    private void UpdateContentScrollMode()
    {
        if (ContentScrollViewer is null)
            return;
        ContentScrollViewer.HorizontalScrollBarVisibility = Width < 980
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
    }

    /// <summary>
    /// 深色原生标题栏：DWMWA_* 常量与 dwmapi 调用集中在 <see cref="ObsidianWindowChrome"/>，
    /// 与其余功能窗共用同一份实现，这里只做「现在已拿到 HWND」的即时调用。
    /// </summary>
    private void ApplyDarkTitleBar() => ObsidianWindowChrome.ApplyTitleBar(this, this);

    private void ClampWindowToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width < 1 || workArea.Height < 1)
            return;

        // 高 DPI 下主屏工作区可能小于 980x620 DIP：先把最小尺寸降到工作区能容纳的值，
        // 再缩 Width/Height，避免窗口超出屏幕后退出/拖动不可达。低于 980 时内容改用水平滚动。
        var maxWidth = Math.Max(360, workArea.Width - 24);
        var maxHeight = Math.Max(300, workArea.Height - 32);
        if (MinWidth > maxWidth)
            MinWidth = maxWidth;
        if (MinHeight > maxHeight)
            MinHeight = maxHeight;
        if (Width > maxWidth)
            Width = maxWidth;
        if (Height > maxHeight)
            Height = maxHeight;
    }

    /// <summary>主题画笔查找；字典缺失时退回给定颜色，保证深色底上不出现系统默认黑字。</summary>
    private MediaBrush ObsidianBrush(string key, MediaColor fallback) =>
        TryFindResource(key) as MediaBrush ?? Freeze(new MediaColorBrush(fallback));

    private static MediaColorBrush Freeze(MediaColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 导航「概览」是真实入口：把内容区滚回顶部（纯视图操作，不动任何控制状态）。
    /// </summary>
    private void OverviewClick(object sender, RoutedEventArgs e)
    {
        ContentScrollViewer?.ScrollToHome();
    }

    /// <summary>
    /// 大数字/指标卡片的展示更新。只读取 snapshot 的显示字段：
    /// EffectiveDisplayCpuTemperatureC / EffectiveDisplayCpuPackagePowerWatts / CpuUsagePercent /
    /// Fan1Rpm / CpuFrequencyMhz。未知一律 "--"，精度与单位沿用原有格式化口径。
    /// </summary>
    private void UpdateDashboardPresentation(HardwareSnapshot snapshot)
    {
        UpdateDashboardTemperature(snapshot);
        UpdateDashboardMetrics(snapshot);
        UpdateDashboardModeSelection(snapshot.PowerMode);
        UpdateDashboardFanRequest(snapshot);

        if (DashboardDemoPill is not null)
            DashboardDemoPill.Visibility = _demo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDashboardTemperature(HardwareSnapshot snapshot)
    {
        if (DashboardTemperatureValue is null)
            return;

        var temperature = snapshot.EffectiveDisplayCpuTemperatureC;
        var usable = temperature is { } value && double.IsFinite(value);
        DashboardTemperatureValue.Text = usable
            ? temperature!.Value.ToString(snapshot.DisplayTemperatureFromLhm ? "0.0" : "0", CultureInfo.InvariantCulture)
            : "--";
        // 单位始终保留，未知只用大数字 "--" 表示。
        DashboardTemperatureUnit.Text = "°C";

        // 告警颜色只看原始 EC 温度（风扇控制与 90 °C 保护的真实输入），
        // 显示源为 LHM 时展示值不会把保护状态“染色”成已触发。
        var ecTemperature = snapshot.CpuTemperatureC;
        var ecUsable = ecTemperature is { } ec && double.IsFinite(ec);
        var atThreshold = ecUsable && ecTemperature!.Value >= 90;
        if (DashboardTemperatureBar is not null)
        {
            DashboardTemperatureBar.Value = usable ? Math.Clamp(temperature!.Value, 0, 100) : 0;
            DashboardTemperatureBar.Foreground = atThreshold
                ? ObsidianBrush("ObsidianDangerBrush", MediaColor.FromRgb(0xF0, 0x8A, 0x7C))
                : ObsidianBrush("ObsidianAccentBrush", MediaColor.FromRgb(0x66, 0xDE, 0xC4));
        }

        if (DashboardTemperatureNoteText is not null)
        {
            // 只陈述阈值与原始 EC 读数，不声称保护动作已经发生（未知时不宣称任何结论）。
            DashboardTemperatureNoteText.Text = atThreshold
                ? "原始 EC 温度已达 90 °C 阈值 · 是否已满速以风扇保护状态为准"
                : ecUsable
                    ? "原始 EC 温度未达 90 °C 阈值 · 启动不自动接管风扇"
                    : "90 °C 触发满速保护 · 原始 EC 温度未知";
            DashboardTemperatureNoteText.Foreground = atThreshold
                ? ObsidianBrush("ObsidianDangerBrush", MediaColor.FromRgb(0xF0, 0x8A, 0x7C))
                : ObsidianBrush("ObsidianTextSecondaryBrush", MediaColor.FromRgb(0x98, 0xAB, 0xC0));
        }

        if (DashboardTemperatureSourceText is not null)
        {
            // 显示源只影响展示；风扇控制与 90 °C 保护始终使用原始 EC 温度。
            DashboardTemperatureSourceText.Text = snapshot.DisplayTemperatureFromLhm
                ? "显示源 LHM · 风扇控制使用原始 EC 温度"
                : "显示源 EC · 风扇控制使用原始 EC 温度";
        }
    }

    private void UpdateDashboardMetrics(HardwareSnapshot snapshot)
    {
        if (DashboardFanValue is not null)
            DashboardFanValue.Text = snapshot.Fan1Rpm?.ToString(CultureInfo.InvariantCulture) ?? "--";

        if (DashboardPowerValue is not null)
            DashboardPowerValue.Text = FormatDisplayNumber(snapshot.EffectiveDisplayCpuPackagePowerWatts, "0.0");

        if (DashboardUsageValue is not null)
            DashboardUsageValue.Text = snapshot.CpuUsagePercent?.ToString(CultureInfo.InvariantCulture) ?? "--";

        if (DashboardFrequencyValue is not null)
            DashboardFrequencyValue.Text = FormatDisplayNumber(snapshot.CpuFrequencyMhz, "0");
    }

    private static string FormatDisplayNumber(double? value, string format) =>
        value is { } number && double.IsFinite(number)
            ? number.ToString(format, CultureInfo.InvariantCulture)
            : "--";

    /// <summary>
    /// 性能模式选中态只反映最近一次读取到的 snapshot.PowerMode；点击本身不改选中态，
    /// 也不代表硬件写入成功。
    /// </summary>
    private void UpdateDashboardModeSelection(PowerMode mode)
    {
        SetSegmentState(SilenceModeButton, mode == PowerMode.Silence);
        SetSegmentState(BalanceModeButton, mode == PowerMode.Balance);
        SetSegmentState(PerformanceModeButton, mode == PowerMode.Performance);
    }

    private static void SetSegmentState(FrameworkElement? button, bool selected)
    {
        if (button is null)
            return;
        button.Tag = selected ? "active" : null;
    }

    /// <summary>
    /// 风扇请求归属（短标签）。与旧 FanText 同一优先级：先看已启用的安全曲线
    /// （_curveController），再回退到 snapshot.FanControlMode/TargetDuty。
    /// 不代表硬件写入成功；EC 自动模式本身不可回读。
    /// </summary>
    private void UpdateDashboardFanRequest(HardwareSnapshot snapshot)
    {
        var curveEnabled = _curveController?.IsEnabled == true;
        var request = curveEnabled
            ? $"已请求安全曲线（duty {_curveController!.CurrentDuty?.ToString(CultureInfo.InvariantCulture) ?? "--"}%）"
            : snapshot.FanControlMode switch
            {
                FanControlMode.FirmwareAutomatic => "已请求 EC 自动（模式不可回读）",
                FanControlMode.ManualDuty => $"已请求 duty {snapshot.TargetDuty?.ToString(CultureInfo.InvariantCulture) ?? "--"}%",
                FanControlMode.FixedRpm => $"已请求固定 RPM（duty {snapshot.TargetDuty?.ToString(CultureInfo.InvariantCulture) ?? "--"}%）",
                FanControlMode.Curve => $"已请求安全曲线（duty {snapshot.TargetDuty?.ToString(CultureInfo.InvariantCulture) ?? "--"}%）",
                _ => "控制归属未知"
            };

        if (snapshot.FanFault is not null)
            request = $"风扇保护：{snapshot.FanFault}";

        if (DashboardFanRequestText is not null)
        {
            DashboardFanRequestText.Text = request;
            DashboardFanRequestText.Foreground = snapshot.FanFault is not null
                ? ObsidianBrush("ObsidianWarningBrush", MediaColor.FromRgb(0xED, 0xBA, 0x68))
                : ObsidianBrush("ObsidianTextSecondaryBrush", MediaColor.FromRgb(0x98, 0xAB, 0xC0));
        }

        if (DashboardEcPillText is not null)
        {
            DashboardEcPillText.Text = curveEnabled
                ? "控制归属: 本程序安全曲线"
                : snapshot.FanControlMode switch
                {
                    FanControlMode.FirmwareAutomatic => "控制归属: EC 自动（已请求）",
                    FanControlMode.ManualDuty => "控制归属: 本程序 duty 请求",
                    FanControlMode.FixedRpm => "控制归属: 本程序固定 RPM",
                    FanControlMode.Curve => "控制归属: 本程序安全曲线",
                    _ => "控制归属: --"
                };
            DashboardEcPillText.Foreground = !curveEnabled && snapshot.FanControlMode == FanControlMode.Unknown
                ? ObsidianBrush("ObsidianTextSecondaryBrush", MediaColor.FromRgb(0x98, 0xAB, 0xC0))
                : ObsidianBrush("ObsidianAccentBrush", MediaColor.FromRgb(0x66, 0xDE, 0xC4));
        }
    }

    /// <summary>
    /// 趋势图例与曲线颜色一一对应（温度青绿 / 实际 RPM 蓝 / 目标 RPM 虚线灰蓝 /
    /// duty 紫 / 封装功耗琥珀）。文本仍是原来那句「最近 2 分钟 + 主显示源」，仅把色块换成 Run。
    /// </summary>
    private void SetTrendLegend(string sourceDescription)
    {
        if (TrendLegendText is null)
            return;

        // The legend text depends only on this one sentence (the chart colours are static main-window
        // theme resources), so an unchanged description must not clear and rebuild six Runs: that
        // rebuild re-measured the caption on every sampling tick. Each new source still rebuilds
        // once, so a display-source switch is reflected immediately.
        if (string.Equals(_trendLegendSignature, sourceDescription, StringComparison.Ordinal))
            return;
        _trendLegendSignature = sourceDescription;

        var secondary = ObsidianBrush("ObsidianTextSecondaryBrush", MediaColor.FromRgb(0x98, 0xAB, 0xC0));
        TrendLegendText.Inlines.Clear();
        TrendLegendText.Inlines.Add(new Run(
            $"实时趋势（最近 2 分钟；温度/封装功耗曲线按主显示源：{sourceDescription}）  ")
        { Foreground = secondary });
        AddLegendRun("温度 ━  ", "ObsidianChartTemperatureBrush", MediaColor.FromRgb(0x66, 0xDE, 0xC4));
        AddLegendRun("实际 RPM ━  ", "ObsidianChartRpmBrush", MediaColor.FromRgb(0x7F, 0xA7, 0xFF));
        AddLegendRun("目标 RPM - -  ", "ObsidianChartTargetRpmBrush", MediaColor.FromRgb(0x5E, 0x77, 0x91));
        AddLegendRun("duty · ·  ", "ObsidianChartDutyBrush", MediaColor.FromRgb(0xC7, 0x92, 0xEA));
        AddLegendRun("CPU 封装功耗 - -", "ObsidianChartPowerBrush", MediaColor.FromRgb(0xF0, 0xA8, 0x68));
    }

    private void AddLegendRun(string text, string brushKey, MediaColor fallback)
    {
        TrendLegendText.Inlines.Add(new Run(text) { Foreground = ObsidianBrush(brushKey, fallback) });
    }
}
