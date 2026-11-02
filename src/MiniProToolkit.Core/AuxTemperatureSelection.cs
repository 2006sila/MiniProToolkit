namespace MiniProToolkit.Core;

/// <summary>Display/JSONL only; never a CPU temperature or a control input.</summary>
public enum AuxTemperatureKind { Unknown, Motherboard, Storage }

public readonly record struct AuxTemperatureCandidate(
    AuxTemperatureKind Kind, string HardwareId, string SensorId, string SensorName,
    double? Value, LhmSensorCategory Category, bool IsNvme = false);

public readonly record struct AuxTemperatureSelection(
    AuxTemperatureKind Kind, string? SensorId, string? SensorName, double? TemperatureC)
{
    public bool IsKnown => Kind != AuxTemperatureKind.Unknown && TemperatureC is not null;
    public static AuxTemperatureSelection Unknown => new(AuxTemperatureKind.Unknown, null, null, null);
}

/// <summary>
/// Pure, deterministic selection of optional temperatures. Hardware scope and units are
/// supplied by the adapter, never guessed from a sensor name. Invalid values remain unknown.
/// </summary>
public static class AuxTemperatureSelector
{
    public static bool IsPlausible(double value) => double.IsFinite(value) && value > 0 && value <= 125;

    public static AuxTemperatureSelection Select(
        IEnumerable<AuxTemperatureCandidate>? sensors, AuxTemperatureKind kind, string? previousSensorId = null)
    {
        if (sensors is null || kind is not (AuxTemperatureKind.Motherboard or AuxTemperatureKind.Storage))
            return AuxTemperatureSelection.Unknown;

        var selected = sensors
            .Where(s => s.Kind == kind && s.Category == LhmSensorCategory.Temperature)
            .Where(s => !string.IsNullOrWhiteSpace(s.HardwareId) && !string.IsNullOrWhiteSpace(s.SensorId))
            .Where(s => s.Value is { } value && IsPlausible(value))
            .Select(s => (Sensor: s, Priority: Priority(s)))
            .Where(s => s.Priority >= 0)
            .OrderBy(s => s.Priority)
            // Keep a valid prior identifier within its priority tier; never depend on enumeration order.
            .ThenBy(s => string.Equals(s.Sensor.SensorId, previousSensorId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(s => s.Sensor.HardwareId, StringComparer.Ordinal)
            .ThenBy(s => s.Sensor.SensorId, StringComparer.Ordinal)
            .ThenBy(s => s.Sensor.SensorName, StringComparer.Ordinal)
            .ThenBy(s => s.Sensor.Value)
            .Select(s => (AuxTemperatureCandidate?)s.Sensor)
            .FirstOrDefault();
        return selected is { } candidate
            ? new(kind, candidate.SensorId, candidate.SensorName.Trim(),
                Math.Round(candidate.Value!.Value, 1, MidpointRounding.AwayFromZero))
            : AuxTemperatureSelection.Unknown;
    }

    private static int Priority(AuxTemperatureCandidate sensor)
    {
        var name = sensor.SensorName?.Trim() ?? string.Empty;
        // Even a mislabeled board/disk sensor must not advertise CPU temperature.
        if (name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)) return -1;
        if (sensor.Kind == AuxTemperatureKind.Motherboard)
        {
            if (Is(name, "Motherboard", "Mainboard", "System", "System Board", "Board")) return 0;
            if (Is(name, "PCH", "Chipset", "Ambient")) return 1;
            // Generic Temperature #n / VRM are not a trustworthy board-wide temperature.
            return -1;
        }
        if (name.Contains("Composite", StringComparison.OrdinalIgnoreCase)) return 0;
        // LHM NVMe exposes its composite temperature as "Temperature", followed by numbered sensors.
        // The adapter derives NVMe identity from the hardware identifier, never the device model/name.
        if (sensor.IsNvme && Is(name, "Temperature")) return 0;
        if (Is(name, "Temperature", "Drive Temperature", "Disk Temperature", "SSD Temperature", "HDD Temperature")) return 1;
        if (name.StartsWith("Temperature #", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Temperature Sensor", StringComparison.OrdinalIgnoreCase)) return 2;
        return -1;
    }

    private static bool Is(string value, params string[] names) =>
        names.Any(name => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Shared pure timing policy. Failure results are cached too; callers cannot busy-loop.</summary>
public static class AuxTemperatureRetryPolicy
{
    public static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromSeconds(5);

    // A permanently absent group must never slow down the other, healthy group.
    public static (int Failures, TimeSpan Delay) AfterSample(bool boardKnown, bool storageKnown, int previousFailures)
    {
        if (boardKnown || storageKnown) return (0, MinimumSampleInterval);
        var failures = Math.Clamp(previousFailures, 0, 6) + 1;
        return (failures, FailureDelay(failures));
    }

    public static TimeSpan FailureDelay(int failures) => TimeSpan.FromSeconds(5 * (1 << Math.Clamp(failures - 1, 0, 6)));
}
