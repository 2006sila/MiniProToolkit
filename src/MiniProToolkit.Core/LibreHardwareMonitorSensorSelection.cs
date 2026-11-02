namespace MiniProToolkit.Core;

/// <summary>
/// Sensor class of one LibreHardwareMonitor reading. The category is the only
/// unit information the selector trusts: a name alone cannot prove that a value
/// is a temperature and not a wattage.
/// </summary>
public enum LhmSensorCategory
{
    /// <summary>Unknown or unclassified reading; never selected as temperature or power.</summary>
    Other = 0,

    /// <summary>Degrees Celsius.</summary>
    Temperature = 1,

    /// <summary>Watts.</summary>
    Power = 2
}

/// <summary>
/// One read-only name/value pair captured from LibreHardwareMonitor.
/// The selector works on these plain values so the naming rules can be tested
/// without loading any driver or touching real hardware.
/// </summary>
public readonly record struct LhmSensorValue(string Name, double? Value, LhmSensorCategory Category)
{
    /// <summary>
    /// Two-argument form kept for call sites that carry no unit information. Such a
    /// reading is classified as <see cref="LhmSensorCategory.Other"/> and is never
    /// picked as either temperature or power.
    /// </summary>
    public LhmSensorValue(string Name, double? Value)
        : this(Name, Value, LhmSensorCategory.Other)
    {
    }
}

public enum LhmSensorSelectionState
{
    /// <summary>A plausible sensor value was found.</summary>
    Selected = 0,

    /// <summary>No CPU-scoped sensor name matched the priority list.</summary>
    NoCandidate = 1,

    /// <summary>Names matched, but every value was null, NaN, infinite or out of range.</summary>
    RejectedValue = 2
}

public readonly record struct LhmSensorSelection(LhmSensorSelectionState State, string? SensorName, double? Value)
{
    public bool IsSelected => State == LhmSensorSelectionState.Selected && Value is not null;

    public static LhmSensorSelection NoCandidate { get; } = new(LhmSensorSelectionState.NoCandidate, null, null);
    public static LhmSensorSelection Rejected { get; } = new(LhmSensorSelectionState.RejectedValue, null, null);
}

/// <summary>
/// Pure selection rules for the optional LibreHardwareMonitor CPU cross-check.
/// Candidates are filtered by sensor category first, so a temperature can never
/// be reported as power and a wattage can never be reported as a temperature.
/// Temperature prefers <c>CPU Package</c>/<c>Package</c>, then the common core and
/// AMD Tctl/Tdie names. Package power only accepts CPU package sensors: it never
/// falls back to a GPU, board or system power sensor.
/// </summary>
public static class LhmSensorSelector
{
    // Plausibility windows. Values outside of these are treated as missing instead
    // of being forwarded to telemetry or the UI.
    //
    // The temperature floor is exclusive and sits at 0 °C on purpose: without its
    // optional kernel driver LibreHardwareMonitor still enumerates CPU temperature
    // sensors but reports exactly 0.0 °C for them. A live CPU never measures 0 °C,
    // so accepting that value would turn "driver unavailable" into a seemingly
    // healthy cross-check reading (observed in the 60 s read-only comparison:
    // 60/60 samples of 0.0 °C on a machine without PawnIO).
    public const double MinimumExclusiveTemperatureC = 0;
    public const double MaximumTemperatureC = 150;
    public const double MaximumPowerWatts = 1000;

    private static readonly string[] TemperatureNames =
    [
        "CPU Package",
        "Package",
        "Core Max",
        "CPU Core",
        "Tctl",
        "Tdie"
    ];

    private static readonly string[] PowerNames =
    [
        "CPU Package",
        "Package"
    ];

    // Names that can never describe the CPU package even if they contain a
    // package-like token (for example "GPU Package").
    private static readonly string[] NonCpuNameTokens =
    [
        "gpu",
        "graphic",
        "display",
        "nvidia",
        "radeon",
        "board",
        "vrm",
        "battery",
        "psu",
        "nvme",
        "ssd",
        "hdd",
        "drive",
        "network"
    ];

    public static IReadOnlyList<string> TemperaturePriority => TemperatureNames;
    public static IReadOnlyList<string> PowerPriority => PowerNames;

    public static LhmSensorSelection SelectTemperature(IEnumerable<LhmSensorValue>? sensors) =>
        Select(sensors, LhmSensorCategory.Temperature, TemperatureNames, NormalizeTemperature);

    public static LhmSensorSelection SelectPower(IEnumerable<LhmSensorValue>? sensors) =>
        Select(sensors, LhmSensorCategory.Power, PowerNames, NormalizePower);

    public static bool IsPlausibleTemperature(double value) =>
        double.IsFinite(value) && value > MinimumExclusiveTemperatureC && value <= MaximumTemperatureC;

    public static bool IsPlausiblePower(double value) =>
        double.IsFinite(value) && value > 0 && value <= MaximumPowerWatts;

    /// <summary>Keeps one decimal of precision; the EC reading stays integer-only.</summary>
    public static double? NormalizeTemperature(double value) =>
        IsPlausibleTemperature(value) ? Math.Round(value, 1, MidpointRounding.AwayFromZero) : null;

    public static double? NormalizePower(double value) =>
        IsPlausiblePower(value) ? Math.Round(value, 1, MidpointRounding.AwayFromZero) : null;

    private static LhmSensorSelection Select(
        IEnumerable<LhmSensorValue>? sensors,
        LhmSensorCategory category,
        string[] priority,
        Func<double, double?> normalize)
    {
        if (sensors is null)
            return LhmSensorSelection.NoCandidate;

        // Category first: a matching name is not enough, the unit has to match too.
        var candidates = sensors
            .Where(sensor => sensor.Category == category)
            .Where(sensor => !string.IsNullOrWhiteSpace(sensor.Name) && IsCpuScopedName(sensor.Name))
            .ToArray();
        if (candidates.Length == 0)
            return LhmSensorSelection.NoCandidate;

        var sawRejectedValue = false;
        foreach (var token in priority)
        {
            // Exact matches win over partial matches for the same priority token.
            foreach (var candidate in ExactMatches(candidates, token).Concat(PartialMatches(candidates, token)))
            {
                if (candidate.Value is { } raw && normalize(raw) is { } value)
                    return new LhmSensorSelection(LhmSensorSelectionState.Selected, candidate.Name.Trim(), value);
                sawRejectedValue = true;
            }
        }

        return sawRejectedValue ? LhmSensorSelection.Rejected : LhmSensorSelection.NoCandidate;
    }

    private static IEnumerable<LhmSensorValue> ExactMatches(LhmSensorValue[] candidates, string token) =>
        candidates.Where(candidate =>
            string.Equals(candidate.Name.Trim(), token, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<LhmSensorValue> PartialMatches(LhmSensorValue[] candidates, string token) =>
        candidates.Where(candidate =>
            candidate.Name.Trim().Contains(token, StringComparison.OrdinalIgnoreCase));

    public static bool IsCpuScopedName(string name) =>
        !NonCpuNameTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Pure attempt gating for the optional LibreHardwareMonitor reader. The rules live
/// in Core so the backoff can be unit-tested without opening LibreHardwareMonitor,
/// loading a driver or touching any hardware.
/// </summary>
public static class LhmRetryPolicy
{
    // Base delays. Failure paths are multiplied by the consecutive failure count.
    public static readonly TimeSpan OpenFailureBaseDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan UpdateFailureBaseDelay = TimeSpan.FromSeconds(30);

    // Nothing to sample: the reader keeps the computer open and simply waits.
    public static readonly TimeSpan NoCpuSensorDelay = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan NoUsableSelectionDelay = TimeSpan.FromSeconds(60);

    public const int MaximumBackoffMultiplier = 5;

    /// <summary>No pending delay: the next call may sample immediately.</summary>
    public static DateTimeOffset Immediate { get; } = DateTimeOffset.MinValue;

    /// <summary>
    /// True when <paramref name="now"/> reached the scheduled attempt time. Callers
    /// must consult this before every sample, not only before opening the computer.
    /// </summary>
    public static bool ShouldAttempt(DateTimeOffset now, DateTimeOffset nextAttemptUtc) =>
        now >= nextAttemptUtc;

    public static DateTimeOffset Schedule(DateTimeOffset now, TimeSpan delay) => now + delay;

    public static TimeSpan Backoff(TimeSpan baseDelay, int consecutiveFailures)
    {
        var multiplier = Math.Clamp(consecutiveFailures, 1, MaximumBackoffMultiplier);
        return TimeSpan.FromSeconds(baseDelay.TotalSeconds * multiplier);
    }
}
