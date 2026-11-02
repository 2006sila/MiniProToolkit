using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed class FanCalibrationStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FanCalibrationStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiniProToolkit",
            "fan-calibration.json");
    }

    public string Path { get; }

    public FanCalibration LoadOrDefault(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path))
                return FanCalibration.Default;

            return Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            warning = $"Fan calibration was reset: {ex.GetBaseException().Message}";
            return FanCalibration.Default;
        }
    }

    public FanCalibration Load()
    {
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), JsonOptions)
            ?? throw new InvalidDataException("标定文件为空。");
        if (document.Version != CurrentVersion)
            throw new InvalidDataException("不支持的标定文件版本。");
        if (document.Points is null || document.Points.Any(point => point is null))
            throw new InvalidDataException("标定点缺失或包含空值。");
        var calibration = new FanCalibration(document.Points.Select(point =>
            new FanCalibrationPoint(point!.DutyPercent, point.Rpm)));
        calibration.ValidateForFixedRpm();
        return calibration;
    }

    public void Save(FanCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        calibration.ValidateForFixedRpm();
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Fan calibration path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        var document = new Document(CurrentVersion, calibration.Points
            .Select(point => (PointDocument?)new PointDocument(point.DutyPercent, point.Rpm))
            .ToList());
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            if (File.Exists(Path))
                File.Copy(Path, Path + ".bak", true);
            File.Move(temporary, Path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record Document(
        [property: System.Text.Json.Serialization.JsonRequired] int Version,
        [property: System.Text.Json.Serialization.JsonRequired] List<PointDocument?>? Points);
    private sealed record PointDocument(
        [property: System.Text.Json.Serialization.JsonRequired] int DutyPercent,
        [property: System.Text.Json.Serialization.JsonRequired] int Rpm);
}
