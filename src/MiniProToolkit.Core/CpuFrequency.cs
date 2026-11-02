namespace MiniProToolkit.Core;

/// <summary>Which read-only Windows source produced the frequency reading.</summary>
public enum CpuFrequencySource
{
    /// <summary>No usable source: the value is unknown, not zero.</summary>
    Unavailable = 0,

    /// <summary><c>Processor Information\% Processor Performance</c> PerformanceCounter (Windows 7 and later).</summary>
    ProcessorInformationPercent = 1,

    /// <summary><c>Processor\% Processor Performance</c> PerformanceCounter (legacy fallback).</summary>
    ProcessorPercent = 2,

    /// <summary>An absolute MHz value supplied by the caller (for example a public API or WMI).</summary>
    AbsoluteMhz = 3
}

/// <summary>
/// One CPU frequency reading. Every numeric field is optional on purpose: the
/// current clock is only reported when a real measurement exists, so the UI can
/// show "--" instead of a fabricated number. <see cref="BaseMhz"/> is the nominal
/// clock taken from the platform, not a measurement.
/// </summary>
public sealed record CpuFrequencyReading(
    double? CurrentMhz,
    double? PercentOfBase,
    double? BaseMhz,
    CpuFrequencySource Source,
    string? Status)
{
    public static CpuFrequencyReading Unavailable(string? status = null, CpuFrequencySource source = CpuFrequencySource.Unavailable) =>
        new(null, null, null, source, status);

    public bool IsAvailable => CurrentMhz is not null;

    public double? Gigahertz => CurrentMhz is { } mhz ? Math.Round(mhz / 1000d, 2) : null;
}

/// <summary>
/// Pure plausibility rules shared by the calculator and the platform reader.
/// 0 and non-finite values are never treated as valid measurements: that is what
/// a PerformanceCounter returns before it has two samples.
/// </summary>
public static class CpuFrequencyNormalizer
{
    /// <summary>Lowest plausible core clock (MHz). Below this the value is a placeholder.</summary>
    public const double MinimumPlausibleMhz = 100d;

    /// <summary>Highest plausible core clock (MHz); above this the counter is corrupt.</summary>
    public const double MaximumPlausibleMhz = 10_000d;

    /// <summary>Lowest plausible "% Processor Performance" value; 0 means "counter not ready".</summary>
    public const double MinimumPlausiblePercent = 0.5d;

    /// <summary>Highest plausible percentage of the nominal clock (turbo headroom).</summary>
    public const double MaximumPlausiblePercent = 1_000d;

    public static double? NormalizeMhz(double? mhz)
    {
        if (mhz is not { } value || !double.IsFinite(value))
            return null;
        return value is < MinimumPlausibleMhz or > MaximumPlausibleMhz ? null : Math.Round(value, 0);
    }

    public static double? NormalizePercent(double? percent)
    {
        if (percent is not { } value || !double.IsFinite(value))
            return null;
        return value is < MinimumPlausiblePercent or > MaximumPlausiblePercent ? null : value;
    }

    /// <summary>Converts "% Processor Performance" (100 = nominal clock) to MHz, or null when either input is unusable.</summary>
    public static double? PercentToMhz(double? percent, double? baseMhz)
    {
        var cleanPercent = NormalizePercent(percent);
        var cleanBase = NormalizeMhz(baseMhz);
        if (cleanPercent is not { } p || cleanBase is not { } b)
            return null;
        return NormalizeMhz(b * p / 100d);
    }
}

/// <summary>
/// Pure CPU frequency state machine with injectable samples. The platform reader
/// only has to call <see cref="Observe"/> with the raw PerformanceCounter value;
/// every rule below is unit-testable without a CPU counter:
/// <list type="bullet">
/// <item>The first raw sample is a baseline only: the rate counter has no interval
/// yet, so the reading is null with a "first sample" status, never 0 MHz.</item>
/// <item>A missing or implausible base clock keeps <see cref="CpuFrequencyReading.CurrentMhz"/>
/// null while <see cref="CpuFrequencyReading.PercentOfBase"/> stays available.</item>
/// <item>A rejected sample (0, NaN, ±∞, out of range) reports null instead of
/// keeping a stale value as if it were current.</item>
/// </list>
/// </summary>
public sealed class CpuFrequencyCalculator
{
    public const string FirstSampleStatus = "CPU 频率首个样本只建立基线，暂无可用读数。";
    public const string InvalidSampleStatus = "CPU 频率读数无效（0 或超出合理范围），本次不可用。";
    public const string MissingBaseClockStatus = "缺少 CPU 基准频率，只能提供相对百分比，不提供 MHz。";

    private bool _hasPercentBaseline;

    public CpuFrequencyCalculator(double? baseFrequencyMhz = null) => BaseFrequencyMhz = CpuFrequencyNormalizer.NormalizeMhz(baseFrequencyMhz);

    /// <summary>Nominal clock in MHz, or null when the platform did not expose one.</summary>
    public double? BaseFrequencyMhz { get; private set; }

    public bool HasBaseFrequency => BaseFrequencyMhz is not null;

    /// <summary>True once one raw percent sample has been consumed; from then on values are real measurements.</summary>
    public bool HasPercentBaseline => _hasPercentBaseline;

    /// <summary>
    /// Sets the nominal clock. A null/implausible value clears it instead of
    /// throwing, and the percent baseline is kept because it belongs to the counter,
    /// not to the base clock.
    /// </summary>
    public void SetBaseFrequency(double? baseFrequencyMhz) => BaseFrequencyMhz = CpuFrequencyNormalizer.NormalizeMhz(baseFrequencyMhz);

    /// <summary>Drops the percent baseline (for example after the counter was recreated).</summary>
    public void Reset() => _hasPercentBaseline = false;

    /// <summary>
    /// Consumes one raw "<c>% Processor Performance</c>" sample. Returns a null
    /// current clock for the first sample and for any unusable value.
    /// </summary>
    public CpuFrequencyReading Observe(double? processorPerformancePercent, CpuFrequencySource source = CpuFrequencySource.ProcessorInformationPercent)
    {
        if (source == CpuFrequencySource.AbsoluteMhz)
            return ObserveMhz(processorPerformancePercent, source);

        if (!_hasPercentBaseline)
        {
            // Keep the source so the UI can show which counter path is in use.
            _hasPercentBaseline = true;
            return new CpuFrequencyReading(null, null, BaseFrequencyMhz, source, FirstSampleStatus);
        }

        var percent = CpuFrequencyNormalizer.NormalizePercent(processorPerformancePercent);
        if (percent is not { } cleanPercent)
            return new CpuFrequencyReading(null, null, BaseFrequencyMhz, source, InvalidSampleStatus);

        var mhz = CpuFrequencyNormalizer.PercentToMhz(cleanPercent, BaseFrequencyMhz);
        return new CpuFrequencyReading(
            mhz,
            Math.Round(cleanPercent, 1),
            BaseFrequencyMhz,
            source,
            mhz is null ? MissingBaseClockStatus : null);
    }

    /// <summary>
    /// Reports an absolute clock value (MHz) that came from another read-only
    /// source. A missing base clock only removes <see cref="CpuFrequencyReading.PercentOfBase"/>.
    /// </summary>
    public CpuFrequencyReading ObserveMhz(double? currentMhz, CpuFrequencySource source = CpuFrequencySource.AbsoluteMhz)
    {
        var clean = CpuFrequencyNormalizer.NormalizeMhz(currentMhz);
        if (clean is not { } mhz)
            return new CpuFrequencyReading(null, null, BaseFrequencyMhz, source, InvalidSampleStatus);

        double? percent = BaseFrequencyMhz is { } baseMhz
            ? Math.Round(mhz / baseMhz * 100d, 1)
            : null;
        return new CpuFrequencyReading(mhz, percent, BaseFrequencyMhz, source, null);
    }
}
