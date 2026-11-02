using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniProToolkit.Core;

/// <summary>
/// User-selected source for the *displayed* CPU values. This never changes the
/// control fields: <see cref="HardwareSnapshot.CpuTemperatureC"/> stays the EC
/// reading used by the fan curve, the 90 °C emergency limit, fixed-RPM control
/// and the Guardian lease. Only the main window labels, the taskbar overlay, the
/// live trend and newly recorded telemetry display fields follow this setting.
/// </summary>
public enum CpuDisplaySource
{
    /// <summary>
    /// EC temperature plus Windows Energy Meter package power. Numeric value 0 and
    /// the default, so configurations written before this setting existed keep the
    /// previous behavior.
    /// </summary>
    EcEnergyMeter = 0,

    /// <summary>
    /// LibreHardwareMonitor temperature/power. Each metric falls back on its own to
    /// the EC/Energy Meter value when LHM has no usable reading, so a missing
    /// temperature does not discard an available power value.
    /// </summary>
    LibreHardwareMonitor = 1
}

public sealed record CpuDisplaySourceSettings(CpuDisplaySource Source)
{
    /// <summary>Compatible default: EC temperature + Energy Meter power.</summary>
    public static CpuDisplaySourceSettings Default { get; } = new(CpuDisplaySource.EcEnergyMeter);

    public bool UsesLhm => Source == CpuDisplaySource.LibreHardwareMonitor;

    public void Validate()
    {
        if (!Enum.IsDefined(Source))
            throw new ArgumentException("Unknown CPU display source.", nameof(Source));
    }
}

/// <summary>
/// Persisted at <c>%LOCALAPPDATA%\MiniProToolkit\display-source.json</c>. Missing
/// fields, unknown enum values and unreadable files all fall back to the compatible
/// default instead of throwing, so a bad file can never block startup.
/// </summary>
public sealed class CpuDisplaySourceSettingsStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public CpuDisplaySourceSettingsStore(string? path = null) => Path = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniProToolkit",
        "display-source.json");

    public string Path { get; }

    public CpuDisplaySourceSettings LoadOrDefault(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path))
                return CpuDisplaySourceSettings.Default;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), JsonOptions)
                ?? throw new InvalidDataException("Display source settings are empty.");
            // A missing version is read as the current schema: version was added after the
            // source field and early/hand-written files without it are still readable.
            if (document.Version is { } version && (version < 1 || version > CurrentVersion))
                throw new InvalidDataException("Unsupported display source settings version.");
            // A missing field keeps the compatible default: old JSON never switches to LHM.
            var settings = new CpuDisplaySourceSettings(document.Source ?? CpuDisplaySource.EcEnergyMeter);
            if (!Enum.IsDefined(settings.Source))
                throw new InvalidDataException($"Unknown CPU display source value '{document.Source}'.");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or NotSupportedException or ArgumentException)
        {
            warning = $"主显示源设置无效，已回退 EC 温度 + Energy Meter 功耗：{ex.GetBaseException().Message}";
            return CpuDisplaySourceSettings.Default;
        }
    }

    public void Save(CpuDisplaySourceSettings settings)
    {
        settings.Validate();
        var directory = System.IO.Path.GetDirectoryName(Path) ??
            throw new InvalidOperationException("Display source path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(new Document(CurrentVersion, settings.Source), JsonOptions));
            File.Move(temporary, Path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record Document(int? Version, CpuDisplaySource? Source = null);
}

/// <summary>
/// Resolved display values for one sample. <see cref="TemperatureFromLhm"/> and
/// <see cref="PowerFromLhm"/> record which source each metric actually came from,
/// so a per-metric fallback stays visible in the UI and in history.
/// </summary>
public readonly record struct CpuDisplayResolution(
    double? TemperatureC,
    double? PackagePowerWatts,
    bool TemperatureFromLhm,
    bool PowerFromLhm,
    string? Status)
{
    public bool UsesLhmValues => TemperatureFromLhm || PowerFromLhm;
}

/// <summary>
/// Pure display-source rules. Kept in Core so source selection, per-metric fallback
/// and the control-field isolation can be unit-tested without opening
/// LibreHardwareMonitor or touching hardware.
/// </summary>
public static class CpuDisplaySourceSelector
{
    public const string FullFallbackStatus =
        "LHM 不可用，已回退系统源（温度用 EC，功耗用 Energy Meter）；请以管理员运行并确认 PawnIO/传感器可用。";

    public const string TemperatureFallbackStatus =
        "LHM 温度不可用，温度已回退 EC；请以管理员运行并确认 PawnIO/传感器可用。";

    public const string PowerFallbackStatus =
        "LHM 功耗不可用，功耗已回退 Energy Meter；请以管理员运行并确认 PawnIO/传感器可用。";

    public static CpuDisplayResolution Resolve(
        CpuDisplaySource source,
        double? ecTemperatureC,
        double? energyMeterPowerWatts,
        double? lhmTemperatureC,
        double? lhmPowerWatts)
    {
        // Unknown values (for example a hand-edited enum number) behave like the default.
        if (source != CpuDisplaySource.LibreHardwareMonitor)
            return new(ecTemperatureC, energyMeterPowerWatts, false, false, null);

        // Plausibility is re-applied here so a placeholder 0 °C / 0 W value that
        // slipped past the reader can never become the primary reading.
        var lhmTemperature = lhmTemperatureC is { } rawTemperature
            ? LhmSensorSelector.NormalizeTemperature(rawTemperature)
            : null;
        var lhmPower = lhmPowerWatts is { } rawPower
            ? LhmSensorSelector.NormalizePower(rawPower)
            : null;

        var temperatureFromLhm = lhmTemperature is not null;
        var powerFromLhm = lhmPower is not null;
        return new(
            lhmTemperature ?? ecTemperatureC,
            lhmPower ?? energyMeterPowerWatts,
            temperatureFromLhm,
            powerFromLhm,
            DescribeFallback(temperatureFromLhm, powerFromLhm));
    }

    /// <summary>
    /// Returns a copy of the snapshot with the display-only fields resolved from the
    /// already-sampled EC/Energy Meter and LHM values. The control fields
    /// (<c>CpuTemperatureC</c>, <c>CpuPackagePowerWatts</c>) are copied unchanged, so
    /// this method cannot influence fan control, protection or the Guardian lease.
    /// </summary>
    public static HardwareSnapshot Apply(HardwareSnapshot snapshot, CpuDisplaySource source)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var resolution = Resolve(
            source,
            snapshot.CpuTemperatureC,
            snapshot.CpuPackagePowerWatts,
            snapshot.LhmCpuPackageTemperatureC,
            snapshot.LhmCpuPackagePowerWatts);
        return snapshot with
        {
            DisplayCpuSource = NormalizeSource(source),
            DisplayCpuTemperatureC = resolution.TemperatureC,
            DisplayCpuPackagePowerWatts = resolution.PackagePowerWatts,
            DisplayTemperatureFromLhm = resolution.TemperatureFromLhm,
            DisplayPowerFromLhm = resolution.PowerFromLhm,
            DisplaySourceStatus = resolution.Status
        };
    }

    /// <summary>
    /// Recomputes the display-only fields of an already recorded telemetry row from its
    /// original EC / Energy Meter / LHM values. Pure: no hardware read, no I/O, no
    /// control pass, so switching the display source can re-project the in-memory trend
    /// without touching the machine. The original EC, Energy Meter and LHM fields are
    /// copied unchanged.
    /// </summary>
    public static HardwareTelemetryRecord Reproject(HardwareTelemetryRecord record, CpuDisplaySource source)
    {
        ArgumentNullException.ThrowIfNull(record);
        var resolution = Resolve(
            source,
            record.CpuTemperatureC,
            record.CpuPackagePowerWatts,
            record.LhmCpuPackageTemperatureC,
            record.LhmCpuPackagePowerWatts);
        return record with
        {
            DisplayCpuSource = NormalizeSource(source),
            DisplayCpuTemperatureC = resolution.TemperatureC,
            DisplayCpuPackagePowerWatts = resolution.PackagePowerWatts,
            DisplayTemperatureFromLhm = resolution.TemperatureFromLhm,
            DisplayPowerFromLhm = resolution.PowerFromLhm,
            DisplaySourceStatus = resolution.Status
        };
    }

    public static string DescribeSource(CpuDisplaySource source) => source switch
    {
        CpuDisplaySource.LibreHardwareMonitor => "LHM（LibreHardwareMonitor）",
        _ => "EC 温度 + Energy Meter 功耗"
    };

    private static CpuDisplaySource NormalizeSource(CpuDisplaySource source) =>
        source == CpuDisplaySource.LibreHardwareMonitor
            ? CpuDisplaySource.LibreHardwareMonitor
            : CpuDisplaySource.EcEnergyMeter;

    private static string? DescribeFallback(bool temperatureFromLhm, bool powerFromLhm) =>
        (temperatureFromLhm, powerFromLhm) switch
        {
            (true, true) => null,
            (false, false) => FullFallbackStatus,
            (false, true) => TemperatureFallbackStatus,
            _ => PowerFallbackStatus
        };
}
