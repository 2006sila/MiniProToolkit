namespace MiniProToolkit.Core;

public enum PowerMode
{
    Balance = 0,
    Performance = 1,
    Silence = 2,
    Unknown = 255
}

public enum FanControlMode
{
    FirmwareAutomatic,
    ManualDuty,
    FixedRpm,
    Curve,
    Unknown
}

public enum FanRpmFeedbackState
{
    Disabled,
    WaitingForRpm,
    Stable,
    Adjusting,
    TargetUnreachable,
    Fault
}

public sealed record HardwareSnapshot(
    DateTimeOffset Timestamp,
    double? CpuTemperatureC,
    int? Fan1Rpm,
    int? Fan2Rpm,
    PowerMode PowerMode,
    int? CpuUsagePercent,
    int? MemoryUsagePercent,
    FanControlMode FanControlMode,
    int? TargetDuty,
    string? Error)
{
    // CPU package power reported by Windows Energy Meter, in watts. This is
    // not a whole-system wall measurement and is null when the platform does
    // not expose a usable energy counter.
    public double? CpuPackagePowerWatts { get; init; }

    // Optional read-only LibreHardwareMonitor cross-check of the CPU package.
    // CpuTemperatureC above stays the EC reading; these values never feed fan
    // control, curve thresholds or the Guardian lease, they only get logged
    // next to the existing fields for parallel comparison.
    public double? LhmCpuPackageTemperatureC { get; init; }
    public double? LhmCpuPackagePowerWatts { get; init; }
    // Short, path-redacted state of the optional LHM reader (null when absent).
    public string? LhmStatus { get; init; }

    // Optional read-only GPU die temperature from the LibreHardwareMonitor GPU group.
    // Displayed/logged only: it never feeds fan control, the fan curve thresholds, the
    // 90 °C protection or the Guardian lease, and the CSV columns are unchanged.
    // Absent or unusable readings stay null; the status carries the redacted reason.
    public double? GpuTemperatureC { get; init; }
    public string? GpuSensorName { get; init; }
    public string? GpuSensorStatus { get; init; }

    // Optional board/storage temperatures: display and JSONL only, never CPU/control inputs.
    // Unknown stays null. Stable LHM identifiers contain no device serial/model information.
    public double? MotherboardTemperatureC { get; init; }
    public string? MotherboardTemperatureSensorId { get; init; }
    public string? MotherboardTemperatureSensorName { get; init; }
    public double? StorageTemperatureC { get; init; }
    public string? StorageTemperatureSensorId { get; init; }
    public string? StorageTemperatureSensorName { get; init; }
    public string? AuxTemperatureStatus { get; init; }

    // Display-only view of the CPU values, resolved from the user's display-source
    // setting by CpuDisplaySourceSelector. CpuTemperatureC above stays the EC
    // reading and remains the only value used by fan control, the 90 °C protection
    // and the Guardian lease. These fields only feed labels, the taskbar, the live
    // trend and new telemetry rows, so the underlying sources are never lost.
    public CpuDisplaySource DisplayCpuSource { get; init; } = CpuDisplaySource.EcEnergyMeter;
    public double? DisplayCpuTemperatureC { get; init; }
    public double? DisplayCpuPackagePowerWatts { get; init; }
    public bool DisplayTemperatureFromLhm { get; init; }
    public bool DisplayPowerFromLhm { get; init; }
    // Per-metric fallback notice (null when nothing fell back or LHM is not selected).
    public string? DisplaySourceStatus { get; init; }

    /// <summary>Displayed temperature, falling back to the EC reading for snapshots built without a source pass.</summary>
    public double? EffectiveDisplayCpuTemperatureC => DisplayCpuTemperatureC ?? CpuTemperatureC;

    /// <summary>Displayed package power, falling back to the Energy Meter reading for snapshots built without a source pass.</summary>
    public double? EffectiveDisplayCpuPackagePowerWatts => DisplayCpuPackagePowerWatts ?? CpuPackagePowerWatts;

    // Read-only CPU clock, derived from the Windows "% Processor Performance" counter and
    // the nominal clock in HKLM\HARDWARE\DESCRIPTION. Null means "unknown", never 0 MHz.
    public double? CpuFrequencyMhz { get; init; }
    public double? CpuFrequencyPercentOfBase { get; init; }
    public double? CpuFrequencyBaseMhz { get; init; }
    public CpuFrequencySource CpuFrequencySource { get; init; } = CpuFrequencySource.Unavailable;
    public string? CpuFrequencyStatus { get; init; }

    // Optional Windows efficiency-class MHz samples: display/JSON history only, never EC/control input.
    public CpuClassFrequencyReading? CpuClassFrequency { get; init; }

    public int? RequestedTargetRpm { get; init; }
    public int? TargetRpm { get; init; }
    public int? RpmError { get; init; }
    public FanRpmFeedbackState RpmFeedbackState { get; init; } = FanRpmFeedbackState.Disabled;
    public string? FanFault { get; init; }

    public static HardwareSnapshot ErrorSnapshot(string error) => new(
        DateTimeOffset.Now,
        null,
        null,
        null,
        PowerMode.Unknown,
        null,
        null,
        FanControlMode.Unknown,
        null,
        error);
}
