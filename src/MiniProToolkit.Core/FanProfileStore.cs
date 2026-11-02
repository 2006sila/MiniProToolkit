using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed class FanProfileStore
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FanProfileStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiniProToolkit",
            "profiles.json");
    }

    public string Path { get; }

    public IReadOnlyList<FanProfile> LoadOrDefault(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path))
                return FanProfile.Defaults;

            return Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            warning = $"Profiles were reset: {ex.GetBaseException().Message}";
            return FanProfile.Defaults;
        }
    }

    // Strict import; unlike startup fallback, malformed input is never silently accepted.
    public IReadOnlyList<FanProfile> Load()
    {
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), JsonOptions)
            ?? throw new InvalidDataException("方案文件为空。");
        if (document.Version is not (1 or CurrentVersion))
            throw new InvalidDataException("不支持的方案文件版本。");
        if (document.Profiles is null || document.Profiles.Any(p => p is null || p.Points is null || p.Points.Any(point => point is null)))
            throw new InvalidDataException("方案或曲线点缺失。");
        if (document.Version == 2 && document.Profiles.Any(p => p.FanMode is null || p.FixedRpm is null))
            throw new InvalidDataException("方案缺少风扇模式或目标 RPM。");
        var profiles = document.Profiles.Select(ToProfile).ToArray();
        ValidateCollection(profiles);
        return profiles;
    }

    public static void ValidateCollection(IEnumerable<FanProfile> profiles)
    {
        var list = profiles.ToArray();
        if (list.Length is < 1 or > 100) throw new ArgumentException("需要保留 1–100 个方案。");
        foreach (var profile in list) profile.Validate();
        if (list.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Length)
            throw new ArgumentException("方案名称不能重复。");
    }

    public void Save(IEnumerable<FanProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var list = profiles.ToArray();
        ValidateCollection(list);

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Profile path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new Document(CurrentVersion, list.Select(FromProfile).ToList()), JsonOptions));
            if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true);
            File.Move(temporary, Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FanProfile ToProfile(ProfileDocument profile) => new(
        profile.Name,
        (PowerMode)profile.PowerMode,
        new FanCurveSettings(
            profile.Points.Select(point => new FanCurvePoint(
                point.TemperatureC,
                point.DutyPercent,
                point.RampDownTemperatureC,
                point.TargetRpm)).ToArray(),
            profile.MinimumDutyPercent,
            profile.MaximumDutyPercent,
            profile.EmergencyDutyPercent,
            profile.HysteresisC,
            profile.MaximumDutyChangePerTick,
            profile.EmergencyTemperatureC,
            profile.MaximumDutyIncreasePerTick,
            profile.MaximumDutyDecreasePerTick,
            profile.RpmRateLimit,
            profile.WarningTier,
            profile.PreEmergencyTier,
            profile.RpmFeedback,
            profile.LatchCurveEmergency ?? false),
        profile.FanMode is { } mode ? (ProfileFanMode)mode : ProfileFanMode.Automatic,
        profile.FixedRpm ?? 2500);

    private static ProfileDocument FromProfile(FanProfile profile) => new(
        profile.Name,
        (int)profile.PowerMode,
        profile.FanCurve.Points.Select(point => new PointDocument(
            point.TemperatureC,
            point.DutyPercent,
            point.RampDownTemperatureC,
            point.TargetRpm)).ToList(),
        profile.FanCurve.MinimumDutyPercent,
        profile.FanCurve.MaximumDutyPercent,
        profile.FanCurve.EmergencyDutyPercent,
        profile.FanCurve.HysteresisC,
        profile.FanCurve.MaximumDutyChangePerTick,
        profile.FanCurve.EmergencyTemperatureC,
        profile.FanCurve.MaximumDutyIncreasePerTick,
        profile.FanCurve.MaximumDutyDecreasePerTick,
        profile.FanCurve.RpmRateLimit,
        profile.FanCurve.WarningTier,
        profile.FanCurve.PreEmergencyTier,
        (int)profile.FanMode,
        profile.FixedRpm,
        profile.FanCurve.RpmFeedback,
        profile.FanCurve.LatchCurveEmergency);

    private sealed record Document(int Version, List<ProfileDocument> Profiles);

    // Version stays 2: the optional curve safety fields below are additive, so a file written by an
    // older build still loads unchanged (missing = feedback off, latch off) and a file written now
    // stays readable for those builds (System.Text.Json ignores unknown members). They are never
    // allowed to reset the whole profiles file on their own.
    private sealed record ProfileDocument(
        string Name,
        int PowerMode,
        List<PointDocument> Points,
        int MinimumDutyPercent,
        int MaximumDutyPercent,
        int EmergencyDutyPercent,
        double HysteresisC,
        int MaximumDutyChangePerTick,
        double EmergencyTemperatureC,
        int? MaximumDutyIncreasePerTick,
        int? MaximumDutyDecreasePerTick,
        FanRpmRateLimit? RpmRateLimit = null,
        FanTemperatureTier? WarningTier = null,
        FanTemperatureTier? PreEmergencyTier = null,
        int? FanMode = null,
        int? FixedRpm = null,
        FanCurveRpmFeedbackSettings? RpmFeedback = null,
        bool? LatchCurveEmergency = null);

    private sealed record PointDocument(
        double TemperatureC,
        int DutyPercent,
        double? RampDownTemperatureC,
        int? TargetRpm = null);
}
