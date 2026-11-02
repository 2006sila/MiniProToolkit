namespace MiniProToolkit.Core;

/// <summary>
/// Hardware family of one auxiliary (non-CPU) LibreHardwareMonitor reading. The
/// values are mapped from the LibreHardwareMonitor <c>HardwareType</c> in the
/// Hardware project, so this Core file never references the LHM package and the
/// selection rules stay unit-testable without a driver.
/// </summary>
public enum LhmGpuVendor
{
    /// <summary>Unknown or unsupported hardware family; never selected.</summary>
    Unknown = 0,

    Nvidia = 1,
    Amd = 2,
    Intel = 3
}

/// <summary>
/// One read-only GPU sensor value captured from LibreHardwareMonitor. The vendor
/// comes from the hardware node the sensor belongs to (not from the sensor name),
/// so a board or NVMe sensor can never be mistaken for a GPU reading.
/// </summary>
public readonly record struct LhmGpuSensorValue(
    string HardwareName,
    LhmGpuVendor Vendor,
    string SensorName,
    double? Value,
    LhmSensorCategory Category)
{
    /// <summary>
    /// Two-argument form for call sites without unit information. Such a reading
    /// is classified as <see cref="LhmSensorCategory.Other"/> and is never picked.
    /// </summary>
    public LhmGpuSensorValue(string HardwareName, LhmGpuVendor Vendor, string SensorName, double? Value)
        : this(HardwareName, Vendor, SensorName, Value, LhmSensorCategory.Other)
    {
    }
}

public enum LhmGpuSensorSelectionState
{
    /// <summary>A plausible GPU temperature was found.</summary>
    Selected = 0,

    /// <summary>No GPU-scoped sensor name matched the priority list.</summary>
    NoCandidate = 1,

    /// <summary>Names matched, but every value was null, NaN, infinite or out of range.</summary>
    RejectedValue = 2
}

public readonly record struct LhmGpuSensorSelection(
    LhmGpuSensorSelectionState State,
    string? HardwareName,
    string? SensorName,
    double? Value)
{
    public bool IsSelected => State == LhmGpuSensorSelectionState.Selected && Value is not null;

    public static LhmGpuSensorSelection NoCandidate { get; } = new(LhmGpuSensorSelectionState.NoCandidate, null, null, null);
    public static LhmGpuSensorSelection Rejected { get; } = new(LhmGpuSensorSelectionState.RejectedValue, null, null, null);
}

/// <summary>
/// Pure selection rules for the optional, read-only GPU temperature display. The
/// selection is deliberately narrow:
/// <list type="bullet">
/// <item>the hardware family must be a GPU (NVIDIA/AMD/Intel), which is what keeps
/// motherboard, NVMe, network and PSU sensors out of the result;</item>
/// <item>the sensor category must be a temperature, so load, clock, power and fan
/// readings that share the name "GPU Core" can never be reported as °C;</item>
/// <item>the name must be <c>GPU Core</c> or <c>GPU Temperature</c>, so hot spot,
/// memory junction and fan-related values are never substituted.</item>
/// </list>
/// Nothing here feeds fan control, the 90 °C protection or the Guardian lease; the
/// result only feeds the optional display field and telemetry.
/// </summary>
public static class LhmGpuSensorSelector
{
    // Same plausibility window as the CPU temperature selector: 0 °C is what a
    // driver-less LibreHardwareMonitor reports for sensors it cannot read, so the
    // floor is exclusive. A GPU above 150 °C is a broken reading, not a hot card.
    public const double MinimumExclusiveTemperatureC = 0;
    public const double MaximumTemperatureC = 150;

    private static readonly string[] TemperatureNames =
    [
        "GPU Core",
        "GPU Temperature"
    ];

    public static IReadOnlyList<string> GpuTemperaturePriority => TemperatureNames;

    public static bool IsSupportedVendor(LhmGpuVendor vendor) => vendor
        is LhmGpuVendor.Nvidia or LhmGpuVendor.Amd or LhmGpuVendor.Intel;

    public static LhmGpuSensorSelection SelectTemperature(IEnumerable<LhmGpuSensorValue>? sensors)
    {
        if (sensors is null)
            return LhmGpuSensorSelection.NoCandidate;

        // GPU hardware family first, then unit: a board/NVMe/network sensor cannot
        // reach the priority list even if someone names it "GPU Core".
        var candidates = sensors
            .Where(sensor => IsSupportedVendor(sensor.Vendor))
            .Where(sensor => sensor.Category == LhmSensorCategory.Temperature)
            .Where(sensor => !string.IsNullOrWhiteSpace(sensor.SensorName))
            .ToArray();
        if (candidates.Length == 0)
            return LhmGpuSensorSelection.NoCandidate;

        var sawRejectedValue = false;
        foreach (var token in TemperatureNames)
        {
            // Exact matches win over partial matches for the same priority token.
            foreach (var candidate in ExactMatches(candidates, token).Concat(PartialMatches(candidates, token)))
            {
                if (candidate.Value is { } raw && NormalizeTemperature(raw) is { } value)
                {
                    return new LhmGpuSensorSelection(
                        LhmGpuSensorSelectionState.Selected,
                        Trim(candidate.HardwareName),
                        candidate.SensorName.Trim(),
                        value);
                }
                sawRejectedValue = true;
            }
        }

        return sawRejectedValue ? LhmGpuSensorSelection.Rejected : LhmGpuSensorSelection.NoCandidate;
    }

    public static bool IsPlausibleTemperature(double value) =>
        double.IsFinite(value) && value > MinimumExclusiveTemperatureC && value <= MaximumTemperatureC;

    /// <summary>Keeps one decimal of precision, matching the CPU cross-check fields.</summary>
    public static double? NormalizeTemperature(double value) =>
        IsPlausibleTemperature(value) ? Math.Round(value, 1, MidpointRounding.AwayFromZero) : null;

    private static IEnumerable<LhmGpuSensorValue> ExactMatches(LhmGpuSensorValue[] candidates, string token) =>
        candidates.Where(candidate =>
            string.Equals(candidate.SensorName.Trim(), token, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<LhmGpuSensorValue> PartialMatches(LhmGpuSensorValue[] candidates, string token) =>
        candidates.Where(candidate =>
            candidate.SensorName.Trim().Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string? Trim(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
