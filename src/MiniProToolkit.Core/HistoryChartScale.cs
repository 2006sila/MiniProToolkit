namespace MiniProToolkit.Core;

public enum HistoryChartUnit { Celsius, Fahrenheit, Rpm, Percent, Watts }
public enum HistoryChartSeries { CpuTemperature, ActualRpm, TargetRpm, Duty, CpuUsage, CpuPower }
public sealed record HistoryAxis(HistoryChartUnit Unit, double Minimum, double Maximum, string Label)
{
    public double Normalize(double value) => Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
}

/// <summary>Independent physical units; stored/exported temperatures always remain Celsius.</summary>
public static class HistoryChartScale
{
    public static HistoryChartUnit Unit(HistoryChartSeries series, bool fahrenheit = false) => series switch
    {
        HistoryChartSeries.CpuTemperature => fahrenheit ? HistoryChartUnit.Fahrenheit : HistoryChartUnit.Celsius,
        HistoryChartSeries.ActualRpm or HistoryChartSeries.TargetRpm => HistoryChartUnit.Rpm,
        HistoryChartSeries.Duty or HistoryChartSeries.CpuUsage => HistoryChartUnit.Percent,
        HistoryChartSeries.CpuPower => HistoryChartUnit.Watts,
        _ => throw new ArgumentOutOfRangeException(nameof(series))
    };

    public static double? Value(HardwareTelemetryRecord record, HistoryChartSeries series, bool fahrenheit = false)
    {
        double? value = series switch
        {
            HistoryChartSeries.CpuTemperature => record.EffectiveDisplayCpuTemperatureC,
            HistoryChartSeries.ActualRpm => record.Fan1Rpm,
            HistoryChartSeries.TargetRpm => record.CurveTargetRpm ?? record.TargetRpm,
            HistoryChartSeries.Duty => record.TargetDuty,
            HistoryChartSeries.CpuUsage => record.CpuUsagePercent,
            HistoryChartSeries.CpuPower => record.EffectiveDisplayCpuPackagePowerWatts,
            _ => null
        };
        if (value is not { } number || !double.IsFinite(number)) return null;
        return series == HistoryChartSeries.CpuTemperature && fahrenheit ? number * 9 / 5 + 32 : number;
    }

    public static HistoryAxis Axis(HistoryChartUnit unit, IEnumerable<double?> values)
    {
        var observed = values.Where(v => v is { } n && double.IsFinite(n)).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var (baseline, step, cap, label) = unit switch
        {
            HistoryChartUnit.Celsius => (100d, 20d, 200d, "°C"),
            HistoryChartUnit.Fahrenheit => (212d, 20d, 392d, "°F"),
            HistoryChartUnit.Rpm => (5000d, 1000d, 30000d, "RPM"),
            HistoryChartUnit.Percent => (100d, 25d, 100d, "%"),
            HistoryChartUnit.Watts => (80d, 20d, 1000d, "W"),
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
        return new(unit, 0, Math.Clamp(Math.Ceiling(Math.Max(baseline, observed) / step) * step, baseline, cap), label);
    }
}
