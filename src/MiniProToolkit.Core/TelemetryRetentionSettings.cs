using System.Globalization;
using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed record TelemetryRetentionSettings
{
    public int Version { get; init; } = 1;
    public int RetainedArchiveCount { get; init; } = 3;
    public long MaximumFileBytes { get; init; } = 10 * 1024 * 1024;
    public int? MaximumArchiveAgeDays { get; init; }

    public bool IsValid => Version == 1 && RetainedArchiveCount is >= 1 and <= 30 &&
        MaximumFileBytes is >= 1048576 and <= 104857600 &&
        (MaximumArchiveAgeDays is null or (>= 1 and <= 365));
}

public sealed class TelemetryRetentionSettingsStore(string directory)
{
    public const string FileName = "telemetry-retention.json";
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), FileName);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Compatibility overload; callers that only show the warning can ignore trust.</summary>
    public TelemetryRetentionSettings Load(out string? warning) => Load(out warning, out _);

    /// <summary>
    /// Loads the effective retention policy and reports whether it may drive destructive maintenance.
    /// Missing file = defaults are the intended policy (trusted: no user preference ever existed).
    /// Valid file = explicit user intent (trusted). Unreadable/unknown-version/out-of-range file =
    /// <paramref name="trusted"/> false: the returned defaults are only a non-destructive placeholder
    /// and archive deletion must not run on them.
    /// </summary>
    public TelemetryRetentionSettings Load(out string? warning, out bool trusted)
    {
        warning = null;
        trusted = true;
        try
        {
            if (!File.Exists(FilePath)) return new();
            if (new FileInfo(FilePath).Length > 16384) throw new InvalidDataException("配置文件过大");
            var settings = JsonSerializer.Deserialize<TelemetryRetentionSettings>(File.ReadAllText(FilePath), Options);
            if (settings is null || !settings.IsValid) throw new InvalidDataException("版本或保留范围无效");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            trusted = false;
            warning = "历史保留配置无效，已暂停自动清理归档，暂按 10 MiB / 3 份归档 / 不按年龄清理写入：" + ex.Message;
            return new();
        }
    }

    public void Save(TelemetryRetentionSettings settings)
    {
        if (!settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        lock (TelemetryMaintenance.Gate)
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            if (!TelemetryMaintenance.IsSafeDirectory(directory) ||
                (File.Exists(FilePath) && (File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("拒绝通过链接写入历史配置。");
            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
                File.Move(temp, FilePath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

public sealed record TelemetryArchive(string Path, int Number, long Length, DateTime LastWriteTimeUtc);
public sealed record TelemetryCleanupPlan(string Directory, IReadOnlyList<TelemetryArchive> Files);

/// <summary>One process-wide writer/maintenance gate. Age is file mtime, not per-record retention.</summary>
public static class TelemetryMaintenance
{
    internal static readonly object Gate = new();

    public static bool IsSafeDirectory(string directory)
    {
        for (var info = new DirectoryInfo(Path.GetFullPath(directory)); info is not null; info = info.Parent)
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    public static bool TryArchiveNumber(string name, out int number)
    {
        number = 0;
        var prefix = name.StartsWith("telemetry.jsonl.", StringComparison.Ordinal) ? "telemetry.jsonl." :
            name.StartsWith("telemetry.csv.", StringComparison.Ordinal) ? "telemetry.csv." : null;
        if (prefix is null) return false;
        var suffix = name[prefix.Length..];
        return suffix.Length > 0 && suffix.All(c => c is >= '0' and <= '9') &&
            int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0 &&
            suffix == number.ToString(CultureInfo.InvariantCulture);
    }

    // Pure selection: no IO and no deletion; callers can display the complete plan before executing it.
    public static IReadOnlyList<TelemetryArchive> SelectArchives(IEnumerable<TelemetryArchive> archives,
        TelemetryRetentionSettings settings, DateTimeOffset now)
    {
        if (!settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        var cutoff = settings.MaximumArchiveAgeDays is { } days ? now.UtcDateTime.AddDays(-days) : (DateTime?)null;
        return archives.Where(a => a.Number > settings.RetainedArchiveCount ||
            (cutoff is { } time && a.LastWriteTimeUtc < time)).ToArray();
    }

    public static TelemetryCleanupPlan Plan(string directory, TelemetryRetentionSettings settings, DateTimeOffset now)
    {
        lock (Gate)
        {
            var full = Path.GetFullPath(directory);
            if (!Directory.Exists(full) || !IsSafeDirectory(full)) return new(full, []);
            var archives = new List<TelemetryArchive>();
            foreach (var file in Directory.EnumerateFiles(full))
            {
                if (!TryArchiveNumber(Path.GetFileName(file), out var number)) continue;
                var info = new FileInfo(file);
                if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                archives.Add(new(info.FullName, number, info.Length, info.LastWriteTimeUtc));
            }
            return new(full, SelectArchives(archives, settings, now));
        }
    }

    public static int Execute(TelemetryCleanupPlan plan)
    {
        lock (Gate)
        {
            var count = 0;
            var directory = Path.GetFullPath(plan.Directory);
            if (!IsSafeDirectory(directory)) return 0;
            foreach (var file in plan.Files)
            {
                var path = Path.GetFullPath(file.Path);
                if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase) ||
                    !TryArchiveNumber(Path.GetFileName(path), out var number) || number != file.Number) continue;
                var info = new FileInfo(path);
                // A rotation after the preview must not delete the new occupant of the same name.
                if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                    info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteTimeUtc) continue;
                File.Delete(path);
                count++;
            }
            return count;
        }
    }
}
