using System.Globalization;

namespace MiniProToolkit.Core;

/// <summary>
/// One formatted taskbar display item, kept as explicit label / value / unit parts instead of a
/// concatenated string. The taskbar renders these parts differently per layout mode (A adds a
/// colon, E puts the label above the value), so nothing ever has to split a localized string to
/// recover the label or the value.
/// <para>
/// <see cref="Text"/> is the exact flat form the taskbar used before the layout modes existed
/// (label, one space, value, unit). The regression tests compare it item by item with the old
/// formatter, which keeps every current item's wording stable. An unknown value stays "--" and
/// keeps its unit; a missing value is never rendered as 0.
/// </para>
/// </summary>
public readonly record struct TaskbarMetric(
    TaskbarDisplayItem Item,
    string Label,
    string Value,
    string Unit = "",
    string? Detail = null)
{
    /// <summary>Value with its unit, for example "58.3°C" or "-- RPM".</summary>
    public string ValueText => Value + Unit;

    /// <summary>The legacy flat form: "CPU 58.3°C", "-- RPM" (an empty label yields only the value).</summary>
    public string Text => Label.Length == 0 ? ValueText : Label + " " + ValueText;

    /// <summary>The A layout's colon row: "CPU:58.3°C". No colon when the item has no label.</summary>
    public string ColonText => Label.Length == 0 ? ValueText : Label + ":" + ValueText;

    /// <summary>A human-readable short label for the E card layout, independent of the A/C/D text.</summary>
    public string CardLabel => TaskbarMetricFormatter.CardLabelFor(Item);
}

/// <summary>
/// Pure formatting for every <see cref="TaskbarDisplayItem"/>. Values keep exactly the wording,
/// precision, culture and "--" placeholder the taskbar used before: this class is the single
/// source of truth for both the overlay and its regression tests. No hardware, WPF or file access.
/// </summary>
public static class TaskbarMetricFormatter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Formats one display item. <paramref name="guardianRunning"/> is the same flag the taskbar
    /// window keeps for its right-click menu gating, so the Guardian text cannot diverge.
    /// </summary>
    public static TaskbarMetric Format(TaskbarDisplayItem item, HardwareSnapshot snapshot, bool guardianRunning = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return item switch
        {
            TaskbarDisplayItem.CpuTemperature => Metric(item, "CPU",
                TemperatureValue(snapshot.EffectiveDisplayCpuTemperatureC, snapshot.DisplayTemperatureFromLhm), "°C"),
            TaskbarDisplayItem.FanRpm => Metric(item, "Fan", IntegerValue(snapshot.Fan1Rpm), " RPM"),
            TaskbarDisplayItem.CpuPackagePower => Metric(item, "功耗",
                PowerValue(snapshot.EffectiveDisplayCpuPackagePowerWatts), " W"),
            TaskbarDisplayItem.TargetRpm => Metric(item, "目标", IntegerValue(snapshot.TargetRpm), " RPM"),
            TaskbarDisplayItem.TargetDuty => Metric(item, "Duty", IntegerValue(snapshot.TargetDuty), "%"),
            TaskbarDisplayItem.RpmFeedback => Metric(item, "RPM", Feedback(snapshot.RpmFeedbackState)),
            TaskbarDisplayItem.PowerMode => Metric(item, string.Empty, Mode(snapshot.PowerMode)),
            TaskbarDisplayItem.CpuUsage => Metric(item, "CPU", IntegerValue(snapshot.CpuUsagePercent), "%"),
            TaskbarDisplayItem.MemoryUsage => Metric(item, "RAM", IntegerValue(snapshot.MemoryUsagePercent), "%"),
            TaskbarDisplayItem.Guardian => Metric(item, "Guardian", guardianRunning ? "OK" : "!",
                detail: guardianRunning ? null : "Guardian 未运行"),
            TaskbarDisplayItem.WmiStatus => Metric(item, "WMI", snapshot.Error is null ? "OK" : "!",
                detail: snapshot.Error),
            TaskbarDisplayItem.CpuFrequency => Metric(item, "CPU", FrequencyValue(snapshot.CpuFrequencyMhz), " MHz"),
            // Read-only GPU die temperature: labelled and never an input to any control path.
            TaskbarDisplayItem.GpuTemperature => Metric(item, "GPU",
                GpuTemperatureValue(snapshot.GpuTemperatureC), "°C"),
            _ => Metric(item, string.Empty, "--")
        };
    }

    /// <summary>
    /// Short, descriptive label used by the E card layout (and its accessible name). The wording
    /// deliberately contains no unit: the value line already carries it, so a card never reads
    /// "风扇 RPM / 2450 RPM". The CPU labels stay distinguishable (温度 / 占用 / 频率 / 功耗)
    /// instead of all reading "CPU".
    /// </summary>
    public static string CardLabelFor(TaskbarDisplayItem item) => item switch
    {
        TaskbarDisplayItem.CpuTemperature => "CPU 温度",
        TaskbarDisplayItem.FanRpm => "风扇转速",
        TaskbarDisplayItem.CpuPackagePower => "CPU 功耗",
        TaskbarDisplayItem.TargetRpm => "目标转速",
        TaskbarDisplayItem.TargetDuty => "目标占空比",
        TaskbarDisplayItem.RpmFeedback => "闭环状态",
        TaskbarDisplayItem.PowerMode => "性能模式",
        TaskbarDisplayItem.CpuUsage => "CPU 占用",
        TaskbarDisplayItem.MemoryUsage => "内存占用",
        TaskbarDisplayItem.Guardian => "Guardian",
        TaskbarDisplayItem.WmiStatus => "WMI",
        TaskbarDisplayItem.CpuFrequency => "CPU 频率",
        TaskbarDisplayItem.GpuTemperature => "GPU 温度",
        _ => item.ToString()
    };

    // The helpers below are public so the taskbar tooltip (BuildDetails) and the overlay use the
    // same formatting. They intentionally mirror the previous private window helpers.
    public static string Nullable<T>(T? value, string suffix) where T : struct =>
        value is { } actual ? $"{actual}{suffix}" : $"--{suffix}";

    public static string Power(double? watts) =>
        watts is { } value && double.IsFinite(value) ? $"{value:0.0} W" : "-- W";

    public static string GpuTemperature(double? celsius) =>
        celsius is { } value && double.IsFinite(value) ? $"{value:0.0}°C" : "--°C";

    public static string Temperature(double? celsius, bool oneDecimal) =>
        celsius is { } value && double.IsFinite(value)
            ? oneDecimal ? $"{value:0.0}°C" : $"{value:0}°C"
            : "--°C";

    public static string Feedback(FanRpmFeedbackState state) => state switch
    {
        FanRpmFeedbackState.Stable => "稳定",
        FanRpmFeedbackState.Adjusting => "修正",
        FanRpmFeedbackState.TargetUnreachable => "目标受限/暂不可达",
        FanRpmFeedbackState.Fault => "故障/闭环停止",
        FanRpmFeedbackState.WaitingForRpm => "等待反馈",
        _ => "未启用"
    };

    public static string Mode(PowerMode mode) => mode switch
    {
        PowerMode.Balance => "均衡",
        PowerMode.Performance => "性能",
        PowerMode.Silence => "静音",
        _ => "--"
    };

    private static TaskbarMetric Metric(TaskbarDisplayItem item, string label, string value, string unit = "", string? detail = null) =>
        new(item, label, value, unit, detail);

    private static string IntegerValue(int? value) =>
        value is { } actual ? actual.ToString(CultureInfo.CurrentCulture) : "--";

    private static string TemperatureValue(double? celsius, bool oneDecimal) =>
        celsius is { } value && double.IsFinite(value)
            ? value.ToString(oneDecimal ? "0.0" : "0", CultureInfo.CurrentCulture)
            : "--";

    private static string PowerValue(double? watts) =>
        watts is { } value && double.IsFinite(value)
            ? value.ToString("0.0", CultureInfo.CurrentCulture)
            : "--";

    private static string GpuTemperatureValue(double? celsius) =>
        celsius is { } value && double.IsFinite(value)
            ? value.ToString("0.0", CultureInfo.CurrentCulture)
            : "--";

    // Same positive-only rule as MetricDisplayFormat.Megahertz: 0/negative/unknown all mean
    // "no usable reading", so the overlay never shows a fabricated clock.
    private static string FrequencyValue(double? megahertz) =>
        megahertz is { } value && double.IsFinite(value) && value > 0
            ? value.ToString("0", Invariant)
            : "--";
}
