using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed class FanCurveConfigurationStore
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FanCurveConfigurationStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiniProToolkit",
            "fan-curve.json");
    }

    public string Path { get; }

    public FanCurveSettings LoadOrDefault(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path))
                return FanCurveSettings.Default;

            return Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            warning = $"Fan curve configuration was reset: {ex.GetBaseException().Message}";
            return FanCurveSettings.Default;
        }
    }

    public FanCurveSettings Load()
    {
        var document = JsonSerializer.Deserialize<ConfigurationDocument>(File.ReadAllText(Path), JsonOptions);
        if (document is null || document.Version is < 1 or > CurrentVersion)
            throw new InvalidDataException("不支持的曲线文件版本。");
        if (document.Points is null || document.Points.Any(point => point is null))
            throw new InvalidDataException("曲线节点缺失。");
        var settings = document.ToSettings();
        settings.Validate();
        return settings;
    }

    public void Save(FanCurveSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Fan curve configuration path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(
            ConfigurationDocument.FromSettings(settings), JsonOptions));
        File.Move(temporary, Path, true);
    }

    // Version stays 2: every graded-tier, RPM/s and RPM-feedback field below is optional, so
    // files written by older builds still load unchanged (missing = legacy behaviour) and files
    // written now stay readable by those builds (unknown members are ignored by System.Text.Json).
    private sealed record ConfigurationDocument(
        int Version,
        List<FanCurvePoint> Points,
        int MinimumDutyPercent,
        int MaximumDutyPercent,
        int EmergencyDutyPercent,
        double HysteresisC,
        int MaximumDutyChangePerTick,
        double EmergencyTemperatureC,
        int? MaximumDutyIncreasePerTick = null,
        int? MaximumDutyDecreasePerTick = null,
        FanRpmRateLimit? RpmRateLimit = null,
        FanTemperatureTier? WarningTier = null,
        FanTemperatureTier? PreEmergencyTier = null,
        FanCurveRpmFeedbackSettings? RpmFeedback = null,
        bool? LatchCurveEmergency = null)
    {
        public FanCurveSettings ToSettings() => new(
            Points.AsReadOnly(),
            MinimumDutyPercent,
            MaximumDutyPercent,
            EmergencyDutyPercent,
            HysteresisC,
            MaximumDutyChangePerTick,
            EmergencyTemperatureC,
            MaximumDutyIncreasePerTick,
            MaximumDutyDecreasePerTick,
            RpmRateLimit,
            WarningTier,
            PreEmergencyTier,
            RpmFeedback,
            LatchCurveEmergency ?? false);

        public static ConfigurationDocument FromSettings(FanCurveSettings settings) => new(
            CurrentVersion,
            settings.Points.ToList(),
            settings.MinimumDutyPercent,
            settings.MaximumDutyPercent,
            settings.EmergencyDutyPercent,
            settings.HysteresisC,
            settings.MaximumDutyChangePerTick,
            settings.EmergencyTemperatureC,
            settings.MaximumDutyIncreasePerTick,
            settings.MaximumDutyDecreasePerTick,
            settings.RpmRateLimit,
            settings.WarningTier,
            settings.PreEmergencyTier,
            settings.RpmFeedback,
            settings.LatchCurveEmergency);
    }
}
