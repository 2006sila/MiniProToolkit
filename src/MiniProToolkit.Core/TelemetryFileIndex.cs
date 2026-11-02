using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed record TelemetryFileStamp(long Length, long MtimeTicks, long CreationTicks, string Edges);
// The summary fields (Minimum/Maximum/Count/Trusted) are what let Load skip a file without
// reading it, so they are bound to the stamp by SummaryIntegrity. This is corruption
// detection, not an authorization mechanism: a deliberate editor can recompute the digest.
public sealed record TelemetryFileIndexEntry(string Path, TelemetryFileStamp Stamp,
    DateTimeOffset? Minimum, DateTimeOffset? Maximum, int Count, bool Trusted, string SummaryIntegrity);
public sealed record TelemetryFileIndexDocument(int Version, IReadOnlyList<TelemetryFileIndexEntry> Entries);

/// <summary>Only fixed local recorder files are indexed; failures always fall back to scanning.</summary>
public sealed class TelemetryFileIndex(string directory)
{
    public const int MaximumFiles = 64;
    public const int CurrentVersion = 2;
    private readonly string _directory = Path.GetFullPath(directory);
    public string SidecarPath => Path.Combine(_directory, "telemetry-index.v1.json");
    public int LastSkippedFiles { get; private set; }
    public int LastScannedFiles { get; private set; }
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string[] LocalFiles(string directory)
    {
        if (!Directory.Exists(directory) || !TelemetryMaintenance.IsSafeDirectory(directory)) return [];
        return Directory.EnumerateFiles(directory).Where(p =>
            (Path.GetFileName(p) == "telemetry.jsonl" ||
             (Path.GetFileName(p).StartsWith("telemetry.jsonl.", StringComparison.Ordinal) &&
              TelemetryMaintenance.TryArchiveNumber(Path.GetFileName(p), out _))) &&
            (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
            .Take(MaximumFiles + 1).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static TelemetryFileStamp? Stamp(string path)
    {
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists || (before.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var length = before.Length; var mtime = before.LastWriteTimeUtc.Ticks; var creation = before.CreationTimeUtc.Ticks;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[8192];
            var first = stream.Read(bytes, 0, 4096);
            stream.Position = Math.Max(0, length - 4096);
            var last = stream.Read(bytes, 4096, 4096);
            var after = new FileInfo(path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc.Ticks != mtime || after.CreationTimeUtc.Ticks != creation) return null;
            return new(length, mtime, creation, Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, Math.Max(first, 4096 + last)))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public TelemetryHistoryResult Load(DateTimeOffset? start = null, DateTimeOffset? end = null, CancellationToken token = default,
        int maximumRecords = TelemetryHistory.MaximumRecordsPerLoad)
    {
        token.ThrowIfCancellationRequested();
        TelemetryHistory.ValidateRecordBudget(maximumRecords);
        LastScannedFiles = LastSkippedFiles = 0;
        var warnings = new List<string>();
        var cached = ReadCache(warnings);
        var entries = new List<TelemetryFileIndexEntry>();
        var skipped = new List<(string Path, TelemetryFileStamp Stamp)>();
        var records = new List<HardwareTelemetryRecord>();
        var remaining = maximumRecords;
        var files = LocalFiles(_directory);
        if (files.Length > MaximumFiles) warnings.Add($"本机历史超过 {MaximumFiles} 份安全加载上限，本次结果不完整；请分批打开外部文件。");
        foreach (var file in files.Take(MaximumFiles))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(file);
            var before = Stamp(path);
            var old = cached.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (before is not null && old is { Trusted: true, Count: > 0, Minimum: not null, Maximum: not null } &&
                old.Stamp == before && ((start is { } s && old.Maximum < s) || (end is { } e && old.Minimum > e)))
            {
                // Recheck after the cache decision. Revalidated again after all other files are read.
                if (Stamp(path) == before)
                {
                    skipped.Add((path, before)); entries.Add(old); LastSkippedFiles++; continue;
                }
            }
            Scan(path, before);
        }
        foreach (var skip in skipped)
        {
            token.ThrowIfCancellationRequested();
            if (Stamp(skip.Path) != skip.Stamp)
            {
                LastSkippedFiles--;
                entries.RemoveAll(e => e.Path == skip.Path);
                warnings.Add($"{Path.GetFileName(skip.Path)}：索引检查期间文件变化，退回扫描。");
                Scan(skip.Path, null);
            }
        }
        if (remaining == 0) warnings.Add(TelemetryHistory.RecordBudgetWarning(maximumRecords));
        token.ThrowIfCancellationRequested();
        WriteCache(entries.Take(MaximumFiles).ToArray(), warnings);
        return new(records.OrderBy(r => r.Timestamp).ToArray(), warnings);

        void Scan(string path, TelemetryFileStamp? before)
        {
            token.ThrowIfCancellationRequested();
            // No scan and no cache entry when exhausted. In particular, do not carry a
            // previously trusted entry forward for a file that now requires rescanning.
            if (remaining == 0) return;
            LastScannedFiles++;
            var scan = TelemetryHistory.ScanFile(path, token, remaining);
            remaining -= scan.Records.Count;
            warnings.AddRange(scan.Warnings);
            records.AddRange(scan.Records.Where(r => (start is null || r.Timestamp >= start) && (end is null || r.Timestamp <= end)));
            var after = Stamp(path);
            var trusted = before is not null && before == after && scan.Complete && scan.Warnings.Count == 0;
            if (!trusted && (before is null || after is null || before != after))
                warnings.Add($"{Path.GetFileName(path)}：文件变化或无法确认稳定快照，本次结果不保证完整，索引不可信；下次将重新扫描。");
            if (after is not null)
                entries.Add(CreateEntry(path, after, scan.Records.Count == 0 ? null : scan.Records.Min(r => r.Timestamp),
                    scan.Records.Count == 0 ? null : scan.Records.Max(r => r.Timestamp), scan.Records.Count, trusted));
        }
    }

    private IReadOnlyList<TelemetryFileIndexEntry> ReadCache(List<string> warnings)
    {
        try
        {
            if (!File.Exists(SidecarPath) || !TelemetryMaintenance.IsSafeDirectory(_directory)) return [];
            if ((File.GetAttributes(SidecarPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(SidecarPath).Length > 512 * 1024)
                throw new InvalidDataException("索引过大或为链接");
            var doc = JsonSerializer.Deserialize<TelemetryFileIndexDocument>(File.ReadAllText(SidecarPath), Options);
            // An older sidecar is only a disposable cache: reject it (warning + rescan) and let
            // WriteCache atomically replace it. History files are never touched.
            if (doc is { Version: 1 }) throw new InvalidDataException("索引版本 1 已停用，本次不使用旧缓存");
            if (doc is null || doc.Version != CurrentVersion || doc.Entries is null || doc.Entries.Count > MaximumFiles ||
                doc.Entries.Any(e => !ValidEntry(e)) ||
                doc.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != doc.Entries.Count)
                throw new InvalidDataException("索引版本或条目无效");
            return doc.Entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        { warnings.Add("历史索引不可用，已退回扫描：" + ex.Message); return []; }
    }

    private bool ValidEntry(TelemetryFileIndexEntry? entry)
    {
        if (entry is null || entry.Stamp is not { } stamp || stamp.Length < 0 ||
            stamp.MtimeTicks < DateTime.MinValue.Ticks || stamp.MtimeTicks > DateTime.MaxValue.Ticks ||
            stamp.CreationTicks < DateTime.MinValue.Ticks || stamp.CreationTicks > DateTime.MaxValue.Ticks ||
            stamp.Edges is null || stamp.Edges.Length != 64 || !stamp.Edges.All(Uri.IsHexDigit) ||
            entry.SummaryIntegrity is null || entry.SummaryIntegrity.Length != 64 || !entry.SummaryIntegrity.All(Uri.IsHexDigit) ||
            entry.Count < 0 || entry.Count > TelemetryHistory.MaximumRecordsPerFile || entry.Minimum > entry.Maximum ||
            (entry.Count > 0 && (entry.Minimum is null || entry.Maximum is null)) ||
            (entry.Count == 0 && (entry.Minimum is not null || entry.Maximum is not null)) ||
            string.IsNullOrWhiteSpace(entry.Path) || !Path.IsPathFullyQualified(entry.Path)) return false;
        // Recompute from the stored summary fields: an accidental or partial edit of
        // Minimum/Maximum/Count/Trusted without a matching digest invalidates the cache.
        if (!string.Equals(entry.SummaryIntegrity, ComputeSummaryIntegrity(entry), StringComparison.Ordinal)) return false;
        var canonical = Path.GetFullPath(entry.Path);
        return string.Equals(canonical, entry.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(canonical), _directory, StringComparison.OrdinalIgnoreCase) &&
            (Path.GetFileName(canonical) == "telemetry.jsonl" ||
             (Path.GetFileName(canonical).StartsWith("telemetry.jsonl.", StringComparison.Ordinal) &&
              TelemetryMaintenance.TryArchiveNumber(Path.GetFileName(canonical), out _)));
    }

    private static TelemetryFileIndexEntry CreateEntry(string path, TelemetryFileStamp stamp,
        DateTimeOffset? minimum, DateTimeOffset? maximum, int count, bool trusted)
    {
        var entry = new TelemetryFileIndexEntry(path, stamp, minimum, maximum, count, trusted, string.Empty);
        return entry with { SummaryIntegrity = ComputeSummaryIntegrity(entry) };
    }

    /// <summary>Deterministic SHA-256 over the edge hash and the cached summary fields.</summary>
    public static string ComputeSummaryIntegrity(TelemetryFileIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Stamp is null) throw new ArgumentException("Stamp is required.", nameof(entry));
        var canonical = string.Join('\n',
            "telemetry-index-summary-v2",
            entry.Stamp.Edges ?? string.Empty,
            entry.Minimum?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture) ?? "null",
            entry.Maximum?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture) ?? "null",
            entry.Count.ToString(CultureInfo.InvariantCulture),
            entry.Trusted ? "trusted" : "untrusted");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private void WriteCache(IReadOnlyList<TelemetryFileIndexEntry> entries, List<string> warnings)
    {
        if (!Directory.Exists(_directory)) return;
        var temp = SidecarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (TelemetryMaintenance.Gate)
            {
                if (!TelemetryMaintenance.IsSafeDirectory(_directory) ||
                    (File.Exists(SidecarPath) && (File.GetAttributes(SidecarPath) & FileAttributes.ReparsePoint) != 0))
                    throw new IOException("索引路径是链接");
                File.WriteAllText(temp, JsonSerializer.Serialize(new TelemetryFileIndexDocument(CurrentVersion, entries), Options), Encoding.UTF8);
                File.Move(temp, SidecarPath, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { warnings.Add("历史索引写入失败（已保留扫描结果）：" + ex.Message); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }
}
