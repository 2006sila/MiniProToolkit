using System.Globalization;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Regression tests for the structured taskbar metric formatter. The legacy formatter is copied
/// here verbatim (it used to live in TaskbarStatusWindow.FormatItem) and every one of the 13 items
/// is compared for a populated, an unknown and a special-state snapshot. This pins the exact
/// existing wording/precision while the overlay is free to render label and value separately.
/// </summary>
public sealed class TaskbarMetricFormatterTests
{
    private static readonly TaskbarDisplayItem[] AllItems = Enum.GetValues<TaskbarDisplayItem>();

    [Fact]
    public void EveryItemMatchesTheLegacyFormattingForAPopulatedSnapshot()
    {
        var snapshot = PopulatedSnapshot();
        Assert.Equal(13, AllItems.Length);
        foreach (var item in AllItems)
            Assert.Equal(LegacyFormatItem(item, snapshot), TaskbarMetricFormatter.Format(item, snapshot).Text);
    }

    [Fact]
    public void EveryItemMatchesTheLegacyFormattingWhenValuesAreUnknown()
    {
        var snapshot = UnknownSnapshot();
        foreach (var item in AllItems)
            Assert.Equal(LegacyFormatItem(item, snapshot), TaskbarMetricFormatter.Format(item, snapshot).Text);
    }

    [Theory]
    [InlineData(FanRpmFeedbackState.Disabled)]
    [InlineData(FanRpmFeedbackState.WaitingForRpm)]
    [InlineData(FanRpmFeedbackState.Stable)]
    [InlineData(FanRpmFeedbackState.Adjusting)]
    [InlineData(FanRpmFeedbackState.TargetUnreachable)]
    [InlineData(FanRpmFeedbackState.Fault)]
    public void FeedbackStatesMatchTheLegacyWording(FanRpmFeedbackState state)
    {
        var snapshot = UnknownSnapshot() with { RpmFeedbackState = state };
        Assert.Equal(LegacyFormatItem(TaskbarDisplayItem.RpmFeedback, snapshot),
            TaskbarMetricFormatter.Format(TaskbarDisplayItem.RpmFeedback, snapshot).Text);
    }

    [Fact]
    public void GuardianAndWmiFlagsMatchTheLegacyWording()
    {
        var running = PopulatedSnapshot();
        Assert.Equal("Guardian OK", TaskbarMetricFormatter.Format(TaskbarDisplayItem.Guardian, running, guardianRunning: true).Text);
        Assert.Equal("Guardian !", TaskbarMetricFormatter.Format(TaskbarDisplayItem.Guardian, running, guardianRunning: false).Text);
        Assert.Equal("WMI OK", TaskbarMetricFormatter.Format(TaskbarDisplayItem.WmiStatus, running).Text);
        Assert.Equal("WMI !", TaskbarMetricFormatter.Format(TaskbarDisplayItem.WmiStatus, UnknownSnapshot()).Text);
        // The failing WMI detail stays available for the cell tooltip without changing the text.
        Assert.Equal("EC read failed", TaskbarMetricFormatter.Format(TaskbarDisplayItem.WmiStatus, UnknownSnapshot()).Detail);
    }

    [Fact]
    public void LabelValueAndUnitStayStructuredInsteadOfBeingSplitFromText()
    {
        var snapshot = PopulatedSnapshot();
        var cpu = TaskbarMetricFormatter.Format(TaskbarDisplayItem.CpuUsage, snapshot);
        Assert.Equal("CPU", cpu.Label);
        Assert.Equal("13", cpu.Value);
        Assert.Equal("%", cpu.Unit);
        Assert.Equal("13%", cpu.ValueText);
        Assert.Equal("CPU 13%", cpu.Text);
        Assert.Equal("CPU:13%", cpu.ColonText);

        var fan = TaskbarMetricFormatter.Format(TaskbarDisplayItem.FanRpm, snapshot);
        Assert.Equal("Fan", fan.Label);
        Assert.Equal("2480", fan.Value);
        Assert.Equal(" RPM", fan.Unit);

        // An item without a legacy label must not start with a space or a colon.
        var mode = TaskbarMetricFormatter.Format(TaskbarDisplayItem.PowerMode, snapshot);
        Assert.Equal(string.Empty, mode.Label);
        Assert.Equal("均衡", mode.Text);
        Assert.Equal("均衡", mode.ColonText);
        Assert.False(mode.Text.StartsWith(' '));
        Assert.False(mode.ColonText.StartsWith(':'));
    }

    [Fact]
    public void CardLabelsAreDescriptiveForEveryItem()
    {
        foreach (var item in AllItems)
            Assert.False(string.IsNullOrWhiteSpace(TaskbarMetricFormatter.CardLabelFor(item)));
    }

    [Theory]
    [InlineData(TaskbarDisplayItem.FanRpm, "RPM")]
    [InlineData(TaskbarDisplayItem.TargetRpm, "RPM")]
    [InlineData(TaskbarDisplayItem.CpuTemperature, "°C")]
    [InlineData(TaskbarDisplayItem.GpuTemperature, "°C")]
    [InlineData(TaskbarDisplayItem.CpuPackagePower, " W")]
    [InlineData(TaskbarDisplayItem.CpuFrequency, "MHz")]
    [InlineData(TaskbarDisplayItem.TargetDuty, "%")]
    public void CardLabelsDoNotRepeatTheUnitOfTheValueLine(TaskbarDisplayItem item, string unit)
    {
        // The E card prints the label above the value; the value already carries the unit, so a
        // label such as "风扇 RPM" would read "风扇 RPM / 2450 RPM".
        var label = TaskbarMetricFormatter.CardLabelFor(item);
        Assert.DoesNotContain(unit.Trim(), label);
    }

    [Fact]
    public void CpuCardLabelsStayUnambiguous()
    {
        var labels = new[]
        {
            TaskbarMetricFormatter.CardLabelFor(TaskbarDisplayItem.CpuTemperature),
            TaskbarMetricFormatter.CardLabelFor(TaskbarDisplayItem.CpuUsage),
            TaskbarMetricFormatter.CardLabelFor(TaskbarDisplayItem.CpuFrequency),
            TaskbarMetricFormatter.CardLabelFor(TaskbarDisplayItem.CpuPackagePower)
        };
        Assert.Equal(4, labels.Distinct().Count());
        Assert.All(labels, label => Assert.Contains("CPU", label));
    }

    [Fact]
    public void TemperatureKeepsTheDisplaySourcePrecision()
    {
        // EC display source: whole degrees; LHM: one decimal. Unknown keeps "--°C", never 0 °C.
        var ec = PopulatedSnapshot() with
        {
            DisplayTemperatureFromLhm = false,
            DisplayCpuTemperatureC = 58.4,
            CpuTemperatureC = 61.4
        };
        Assert.Equal("CPU 58°C", TaskbarMetricFormatter.Format(TaskbarDisplayItem.CpuTemperature, ec).Text);
        var lhm = ec with { DisplayTemperatureFromLhm = true };
        Assert.Equal("CPU 58.4°C", TaskbarMetricFormatter.Format(TaskbarDisplayItem.CpuTemperature, lhm).Text);
        var unknown = ec with { DisplayCpuTemperatureC = null, CpuTemperatureC = null };
        Assert.Equal("CPU --°C", TaskbarMetricFormatter.Format(TaskbarDisplayItem.CpuTemperature, unknown).Text);
    }

    private static HardwareSnapshot PopulatedSnapshot() => new(
        DateTimeOffset.UnixEpoch,
        CpuTemperatureC: 61.4,
        Fan1Rpm: 2480,
        Fan2Rpm: null,
        PowerMode: PowerMode.Balance,
        CpuUsagePercent: 13,
        MemoryUsagePercent: 58,
        FanControlMode: FanControlMode.Curve,
        TargetDuty: 42,
        Error: null)
    {
        DisplayCpuTemperatureC = 58.4,
        DisplayTemperatureFromLhm = true,
        CpuPackagePowerWatts = 18.6,
        DisplayCpuPackagePowerWatts = 18.6,
        DisplayCpuSource = CpuDisplaySource.LibreHardwareMonitor,
        CpuFrequencyMhz = 4200.4,
        TargetRpm = 2500,
        RpmFeedbackState = FanRpmFeedbackState.Stable,
        GpuTemperatureC = 56.34
    };

    private static HardwareSnapshot UnknownSnapshot() => new(
        DateTimeOffset.UnixEpoch,
        CpuTemperatureC: null,
        Fan1Rpm: null,
        Fan2Rpm: null,
        PowerMode: PowerMode.Unknown,
        CpuUsagePercent: null,
        MemoryUsagePercent: null,
        FanControlMode: FanControlMode.Unknown,
        TargetDuty: null,
        Error: "EC read failed")
    {
        RpmFeedbackState = FanRpmFeedbackState.Disabled
    };

    // Verbatim copy of the previous TaskbarStatusWindow.FormatItem and its helpers. It is
    // intentionally not shared with production code: this is the independent oracle.
    private static string LegacyFormatItem(TaskbarDisplayItem item, HardwareSnapshot snapshot, bool guardianRunning = true) => item switch
    {
        TaskbarDisplayItem.CpuTemperature => $"CPU {LegacyTemperature(snapshot.EffectiveDisplayCpuTemperatureC, snapshot.DisplayTemperatureFromLhm)}",
        TaskbarDisplayItem.FanRpm => $"Fan {LegacyNullable(snapshot.Fan1Rpm, " RPM")}",
        TaskbarDisplayItem.CpuPackagePower => $"功耗 {LegacyPower(snapshot.EffectiveDisplayCpuPackagePowerWatts)}",
        TaskbarDisplayItem.TargetRpm => $"目标 {LegacyNullable(snapshot.TargetRpm, " RPM")}",
        TaskbarDisplayItem.TargetDuty => $"Duty {LegacyNullable(snapshot.TargetDuty, "%")}",
        TaskbarDisplayItem.RpmFeedback => $"RPM {LegacyFeedback(snapshot.RpmFeedbackState)}",
        TaskbarDisplayItem.PowerMode => LegacyMode(snapshot.PowerMode),
        TaskbarDisplayItem.CpuUsage => $"CPU {LegacyNullable(snapshot.CpuUsagePercent, "%")}",
        TaskbarDisplayItem.MemoryUsage => $"RAM {LegacyNullable(snapshot.MemoryUsagePercent, "%")}",
        TaskbarDisplayItem.Guardian => guardianRunning ? "Guardian OK" : "Guardian !",
        TaskbarDisplayItem.WmiStatus => snapshot.Error is null ? "WMI OK" : "WMI !",
        TaskbarDisplayItem.CpuFrequency => $"CPU {MetricDisplayFormat.Megahertz(snapshot.CpuFrequencyMhz)}",
        TaskbarDisplayItem.GpuTemperature => $"GPU {LegacyGpuTemperature(snapshot.GpuTemperatureC)}",
        _ => "--"
    };

    private static string LegacyNullable<T>(T? value, string suffix) where T : struct =>
        value is { } actual ? string.Create(CultureInfo.CurrentCulture, $"{actual}{suffix}") : $"--{suffix}";

    private static string LegacyPower(double? watts) =>
        watts is { } value && double.IsFinite(value) ? string.Create(CultureInfo.CurrentCulture, $"{value:0.0} W") : "-- W";

    private static string LegacyGpuTemperature(double? celsius) =>
        celsius is { } value && double.IsFinite(value) ? string.Create(CultureInfo.CurrentCulture, $"{value:0.0}°C") : "--°C";

    private static string LegacyTemperature(double? celsius, bool oneDecimal) =>
        celsius is { } value && double.IsFinite(value)
            ? oneDecimal
                ? string.Create(CultureInfo.CurrentCulture, $"{value:0.0}°C")
                : string.Create(CultureInfo.CurrentCulture, $"{value:0}°C")
            : "--°C";

    private static string LegacyFeedback(FanRpmFeedbackState state) => state switch
    {
        FanRpmFeedbackState.Stable => "稳定",
        FanRpmFeedbackState.Adjusting => "修正",
        FanRpmFeedbackState.TargetUnreachable => "目标受限/暂不可达",
        FanRpmFeedbackState.Fault => "故障/闭环停止",
        FanRpmFeedbackState.WaitingForRpm => "等待反馈",
        _ => "未启用"
    };

    private static string LegacyMode(PowerMode mode) => mode switch
    {
        PowerMode.Balance => "均衡",
        PowerMode.Performance => "性能",
        PowerMode.Silence => "静音",
        _ => "--"
    };
}
