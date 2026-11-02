using System.Text;
using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed record DiagnosticEventRecord(DateTimeOffset Timestamp, string Category, string Message, string Source);

/// <summary>
/// Append-only, bounded local journal for control and recovery diagnostics.
/// The whole serialized line (not just the message) participates in the size budget: rotation is
/// decided from the byte size the append will actually add, so new data can never push the live
/// file past its quota. Paths are validated against reparse points before any write or rename.
/// </summary>
public sealed class DiagnosticEventStore
{
    /// <summary>
    /// One event message is capped before serialization. A single multi-megabyte stack trace or
    /// exception message would otherwise break the file quota (an event is appended whole, the
    /// rotation check only looks at the size before the append) and push every older event out of
    /// the retained window in one step.
    /// </summary>
    public const int MaximumMessageBytes = 8 * 1024;
    internal const string MessageTruncationMarker = "[诊断消息超过8192字节，已截断]";

    /// <summary>Category is bounded (not silently truncated) so the caller sees invalid input.</summary>
    public const int MaximumCategoryBytes = 128;

    /// <summary>Source is bounded as well; it is validated, never trimmed or rewritten.</summary>
    public const int MaximumSourceBytes = 256;

    /// <summary>
    /// Upper bound for one serialized line including the newline. Worst case for a capped message is
    /// ~48 KiB (every byte escaped as <c>\uXXXX</c>) plus the bounded category/source and the fixed
    /// timestamp/property overhead, which stays below 64 KiB. Exceeding it is rejected, never written.
    /// </summary>
    public const int MaximumLineBytes = 64 * 1024;

    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int RetainedFiles = 4;
    private const string UnsafeDirectoryMessage = "诊断事件目录或其父目录不能是链接。";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly object Sync = new();
    public DiagnosticEventStore(string? path = null) => Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniProToolkit", "history", "diagnostic-events.jsonl");
    public string Path { get; }

    public void Append(string category, string message, string source = "MiniProToolkit")
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("事件类别不能为空。", nameof(category));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("事件内容不能为空。", nameof(message));
        var trimmedCategory = category.Trim();
        EnsureWithinBytes(trimmedCategory, MaximumCategoryBytes, nameof(category));
        EnsureWithinBytes(source, MaximumSourceBytes, nameof(source));
        var record = new DiagnosticEventRecord(DateTimeOffset.UtcNow, trimmedCategory, BoundMessage(message), source);
        // Serialize first: the rotation decision uses the exact number of bytes this append adds.
        var line = EncodeLine(record);
        if (line.Length > MaximumLineBytes)
            throw new ArgumentException($"单条诊断事件序列化后为{line.Length}字节，超过{MaximumLineBytes}字节上限。", nameof(message));
        lock (Sync)
        {
            var directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("诊断事件路径无效。");
            Directory.CreateDirectory(directory);
            EnsureWriteTargetIsSafe(directory);
            var length = File.Exists(Path) ? new FileInfo(Path).Length : 0;
            // A live file left oversized by an older build is archived whole (never trimmed or deleted)
            // and the new line lands in a fresh file, so the live file stays inside the quota.
            if (length + line.Length > MaxFileBytes) Rotate(directory);
            // Rotation renamed files; re-validate the live path right before the actual write.
            EnsureWriteTargetIsSafe(directory);
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(line, 0, line.Length);
        }
    }

    /// <summary>Trims and truncates on a UTF-8 rune boundary so the stored message stays at/under the cap.</summary>
    internal static string BoundMessage(string message)
    {
        var trimmed = message.Trim();
        if (Utf8.GetByteCount(trimmed) <= MaximumMessageBytes) return trimmed;
        var marker = MessageTruncationMarker;
        var budget = MaximumMessageBytes - Utf8.GetByteCount(marker);
        return (budget > 0 ? BoundedTextLog.TruncateUtf8Boundary(trimmed, budget) : string.Empty) + marker;
    }

    /// <summary>
    /// Reads the retained window oldest file first. An unsafe directory or a linked file is skipped
    /// instead of being read through: the result is empty for that file and no exception escapes to
    /// the GUI (the signature intentionally carries no warning channel).
    /// </summary>
    public IReadOnlyList<DiagnosticEventRecord> ReadRecent(int maximum = 200)
    {
        if (maximum is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (Sync)
        {
            var events = new List<DiagnosticEventRecord>();
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is null || !Directory.Exists(directory) || !IsSafeDirectory(directory)) return events;
            foreach (var file in JournalFiles())
            {
                if (IsReparsePoint(file)) continue;
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        try { if (JsonSerializer.Deserialize<DiagnosticEventRecord>(line, Options) is { } item) events.Add(item); }
                        catch (JsonException) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return events.OrderByDescending(e => e.Timestamp).Take(maximum).OrderBy(e => e.Timestamp).ToArray();
        }
    }

    private IEnumerable<string> JournalFiles() =>
        Enumerable.Range(1, RetainedFiles).Reverse().Select(i => $"{Path}.{i}").Append(Path);

    /// <summary>
    /// Write gate: every existing parent directory must be a real directory (same walk as
    /// <see cref="TelemetryMaintenance.IsSafeDirectory"/>) and neither the live file nor any archive
    /// may be a reparse point. Checked for the complete archive set before any rename, so a partial
    /// rotation cannot happen.
    /// </summary>
    private void EnsureWriteTargetIsSafe(string directory)
    {
        if (!IsSafeDirectory(directory)) throw new IOException(UnsafeDirectoryMessage);
        foreach (var file in JournalFiles())
            if (IsReparsePoint(file)) throw new IOException("诊断事件文件或归档不能是链接。");
    }

    private static bool IsSafeDirectory(string directory)
    {
        try { return TelemetryMaintenance.IsSafeDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for symlinks/junctions, including dangling links whose target does not exist yet (a
    /// plain <see cref="File.Exists"/> check would miss those and the append would create the target
    /// outside the journal). Unreadable metadata fails closed for the write path.
    /// </summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null) return true;
            if (file.Exists) return (file.Attributes & FileAttributes.ReparsePoint) != 0;
            var directory = new DirectoryInfo(path);
            if (directory.LinkTarget is not null) return true;
            return directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static void EnsureWithinBytes(string value, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var bytes = Utf8.GetByteCount(value);
        if (bytes > maximumBytes)
            throw new ArgumentException($"{parameterName}为{bytes}字节，超过{maximumBytes}字节上限（不会静默截断）。", parameterName);
    }

    private static byte[] EncodeLine(DiagnosticEventRecord record) =>
        Utf8.GetBytes(JsonSerializer.Serialize(record, Options) + Environment.NewLine);

    private void Rotate(string directory)
    {
        // Defence in depth: the whole archive set is validated here too, so this method can never
        // start renaming and then hit a reparse point halfway (partial rotation).
        EnsureWriteTargetIsSafe(directory);
        var oldest = $"{Path}.{RetainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = RetainedFiles - 1; i >= 1; i--)
        {
            var source = $"{Path}.{i}";
            if (File.Exists(source)) File.Move(source, $"{Path}.{i + 1}", true);
        }
        File.Move(Path, $"{Path}.1", true);
    }
}
