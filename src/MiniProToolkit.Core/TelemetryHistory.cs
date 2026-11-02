using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MiniProToolkit.Core;

public enum HistoryEventKind { Mode, Control, Recovery, Error, Other }
public sealed record HistoryEvent(DateTimeOffset Timestamp, HistoryEventKind Kind, string Message, string Source);
public sealed record TelemetryHistoryResult(IReadOnlyList<HardwareTelemetryRecord> Records, IReadOnlyList<string> Warnings);

public static class TelemetryHistory
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public const int MaximumRecordsPerFile = 250000;
    public const int MaximumRecordsPerLoad = 500000;
    // Tailing the plain-text application/guardian logs is bounded so a legacy multi-hundred-MB
    // log cannot spike memory or block the history window. Only complete lines in the tail are
    // decoded; the original file is never modified.
    public const int DefaultLogTailBytes = 2 * 1024 * 1024;
    public const int DefaultLogTailEntries = 2000;
    private const int MaximumLogTailBytes = 64 * 1024 * 1024;
    private const int MaximumLogTailEntries = 200000;
    internal static string RecordBudgetWarning(int maximumRecords) =>
        $"达到总扫描 {maximumRecords} 条记录安全预算，本次结果不完整；请缩小时段后重新加载。";
    internal static void ValidateRecordBudget(int maximumRecords)
    {
        if (maximumRecords is < 1 or > MaximumRecordsPerLoad)
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
    }
    internal sealed record FileScan(IReadOnlyList<HardwareTelemetryRecord> Records, IReadOnlyList<string> Warnings, bool Complete);

    public static TelemetryHistoryResult Load(IEnumerable<string> files, CancellationToken token = default,
        DateTimeOffset? start = null, DateTimeOffset? end = null, int maximumRecords = MaximumRecordsPerLoad)
    {
        token.ThrowIfCancellationRequested();
        ValidateRecordBudget(maximumRecords);
        var records = new List<HardwareTelemetryRecord>();
        var warnings = new List<string>();
        var remaining = maximumRecords;
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (remaining == 0) break;
            var scan = ScanFile(file, token, remaining);
            remaining -= scan.Records.Count;
            records.AddRange(scan.Records.Where(r => (start is null || r.Timestamp >= start) && (end is null || r.Timestamp <= end)));
            warnings.AddRange(scan.Warnings);
        }
        if (remaining == 0) warnings.Add(RecordBudgetWarning(maximumRecords));
        token.ThrowIfCancellationRequested();
        return new(records.OrderBy(r => r.Timestamp).ToArray(), warnings);
    }

    public static TelemetryHistoryResult LoadLocal(string directory, DateTimeOffset? start = null,
        DateTimeOffset? end = null, CancellationToken token = default) => new TelemetryFileIndex(directory).Load(start, end, token);

    internal static FileScan ScanFile(string file, CancellationToken token, int remainingRecords = MaximumRecordsPerFile)
    {
        token.ThrowIfCancellationRequested();
        if (remainingRecords <= 0) throw new ArgumentOutOfRangeException(nameof(remainingRecords));
        var maximumRecords = Math.Min(MaximumRecordsPerFile, remainingRecords);
        var records = new List<HardwareTelemetryRecord>();
        var warnings = new List<string>();
        var malformed = 0;
        var complete = true;
        try
        {
            token.ThrowIfCancellationRequested();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var initialLength = stream.Length;
            if (initialLength > 128L * 1024 * 1024)
            {
                warnings.Add($"{Path.GetFileName(file)}：文件超过 128 MiB 安全扫描上限，未加载且未建立可信索引。");
                return new(records, warnings, false);
            }
            if (initialLength > 0)
            {
                stream.Position = initialLength - 1;
                complete = stream.ReadByte() == '\n';
                stream.Position = 0;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var line = new StringBuilder();
            var oversized = false;
            var chars = 0;
            while (true)
            {
                if ((chars++ & 4095) == 0) token.ThrowIfCancellationRequested();
                var c = reader.Read();
                if (c < 0 && line.Length == 0 && !oversized) break;
                if (c is not (-1 or '\n'))
                {
                    if (line.Length < 1024 * 1024) line.Append((char)c); else oversized = true;
                    // An append-heavy external file cannot keep this scan alive indefinitely.
                    if (chars > 128 * 1024 * 1024) { complete = false; warnings.Add($"{Path.GetFileName(file)}：达到扫描字符上限。"); break; }
                    continue;
                }
                if (oversized) malformed++;
                else if (line.Length > 0 && !string.IsNullOrWhiteSpace(line.ToString()))
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<HardwareTelemetryRecord>(line.ToString(), Options);
                        if (record is null || record.Timestamp == default) malformed++; else records.Add(record);
                    }
                    catch (JsonException) { malformed++; }
                }
                line.Clear(); oversized = false;
                if (records.Count >= maximumRecords)
                { complete = false; warnings.Add($"{Path.GetFileName(file)}：达到剩余扫描预算/单文件上限 {maximumRecords} 条，结果不完整、索引不可信；请缩小时段。"); break; }
                if (c < 0) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { complete = false; warnings.Add($"{Path.GetFileName(file)}：{ex.Message}"); }
        if (malformed > 0) { complete = false; warnings.Add($"{Path.GetFileName(file)}：跳过 {malformed} 行无效/未写完的数据。"); }
        if (!complete && warnings.Count == 0) warnings.Add($"{Path.GetFileName(file)}：末行未以换行结束，可能仍在追加；当前快照不完整且索引不可信。");
        return new(records, warnings, complete);
    }

    public static IReadOnlyList<HistoryEvent> Events(IReadOnlyList<HardwareTelemetryRecord> records)
    {
        var events = new List<HistoryEvent>();
        HardwareTelemetryRecord? previous = null;
        foreach (var r in records.OrderBy(r => r.Timestamp))
        {
            if (r.Error is not null && r.Error != previous?.Error)
                events.Add(new(r.Timestamp, HistoryEventKind.Error, "硬件读取：" + r.Error, "遥测"));
            if (r.FanFault is not null && r.FanFault != previous?.FanFault)
                events.Add(new(r.Timestamp, HistoryEventKind.Error, "风扇保护：" + r.FanFault, "遥测"));
            if (previous is not null && r.PowerMode != previous.PowerMode)
                events.Add(new(r.Timestamp, HistoryEventKind.Mode, $"性能模式：{previous.PowerMode} → {r.PowerMode}", "遥测"));
            if (previous is not null && r.DisplayCpuSource != previous.DisplayCpuSource)
                events.Add(new(r.Timestamp, HistoryEventKind.Control,
                    $"主显示源：{CpuDisplaySourceSelector.DescribeSource(previous.DisplayCpuSource)} → " +
                    CpuDisplaySourceSelector.DescribeSource(r.DisplayCpuSource), "遥测"));
            if (previous is not null && r.FanControlMode != previous.FanControlMode)
                events.Add(new(r.Timestamp, r.FanControlMode == FanControlMode.FirmwareAutomatic ? HistoryEventKind.Recovery : HistoryEventKind.Control,
                    $"风扇控制：{previous.FanControlMode} → {r.FanControlMode}（自动模式仅为请求状态）", "遥测"));
            if (!string.IsNullOrWhiteSpace(r.Event) && r.Event != previous?.Event && r.Event is not ("NoChange" or "SetDuty") && !r.Event.StartsWith("fan-fault:", StringComparison.Ordinal))
                events.Add(new(r.Timestamp, Classify(r.Event), r.Event, "遥测"));
            // Curve status transitions (graded level, safety-gate restore, failure) are recorded so
            // the history answers "why did the curve stop" with the reason instead of a guess.
            if (!string.IsNullOrWhiteSpace(r.CurveStatus) && r.CurveStatus != previous?.CurveStatus)
                events.Add(new(r.Timestamp, CurveEventKind(r.CurveStatus),
                    string.IsNullOrWhiteSpace(r.CurveReason)
                        ? $"曲线状态：{r.CurveStatus}"
                        : $"曲线状态：{r.CurveStatus} · {r.CurveReason}", "遥测"));
            previous = r;
        }
        return events;
    }

    private sealed record LogTail(List<HistoryEvent> Events, bool Truncated, int MaximumBytes, int MaximumEntries);

    internal static string LogTailTruncationWarning(int maximumBytes, int maximumEntries) =>
        $"仅加载最近 {Math.Max(1, maximumBytes / 1024)} KiB、最多 {maximumEntries} 条日志，更早内容未加载（原日志文件未被修改）。";

    /// <summary>Bounded tail read used by the history window: newest bytes/entries only.</summary>
    public static IReadOnlyList<HistoryEvent> ReadLog(string path, string source, CancellationToken token = default) =>
        ReadLog(path, source, DefaultLogTailBytes, DefaultLogTailEntries, null, token);

    /// <summary>Bounded tail read that reports a truncation warning instead of hiding the cap.</summary>
    public static IReadOnlyList<HistoryEvent> ReadLog(string path, string source, Action<string>? warning,
        CancellationToken token = default) =>
        ReadLog(path, source, DefaultLogTailBytes, DefaultLogTailEntries, warning, token);

    /// <summary>Explicitly bounded tail read; <paramref name="warning"/> receives a truncation notice when capped.</summary>
    public static IReadOnlyList<HistoryEvent> ReadLog(string path, string source, int maximumBytes,
        int maximumEntries, Action<string>? warning = null, CancellationToken token = default)
    {
        var tail = ReadLogTail(path, source, maximumBytes, maximumEntries, token);
        if (tail.Truncated) warning?.Invoke(LogTailTruncationWarning(tail.MaximumBytes, tail.MaximumEntries));
        return tail.Events;
    }

    /// <summary>Explicitly bounded tail read that returns the truncation flag to the caller.</summary>
    public static IReadOnlyList<HistoryEvent> ReadLog(string path, string source, int maximumBytes,
        int maximumEntries, out bool truncated, CancellationToken token = default)
    {
        var tail = ReadLogTail(path, source, maximumBytes, maximumEntries, token);
        truncated = tail.Truncated;
        return tail.Events;
    }

    private static LogTail ReadLogTail(string path, string source, int maximumBytes, int maximumEntries,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (maximumBytes is < 1 or > MaximumLogTailBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumEntries is < 1 or > MaximumLogTailEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var events = new List<HistoryEvent>();
        if (!File.Exists(path)) return new(events, false, maximumBytes, maximumEntries);
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Snapshot length: bytes appended after this point are outside the bound and never read.
        var initialLength = stream.Length;
        if (initialLength <= 0) return new(events, false, maximumBytes, maximumEntries);
        var start = Math.Max(0L, initialLength - maximumBytes);
        var byteCount = (int)Math.Min(initialLength - start, maximumBytes);
        var buffer = new byte[byteCount];
        // A non-zero tail only drops a first line when the byte before the window is not a
        // newline, i.e. the window really starts in the middle of a line. When the window
        // starts exactly on a line boundary the complete line is kept.
        var startsMidLine = false;
        if (start > 0)
        {
            token.ThrowIfCancellationRequested();
            stream.Position = start - 1;
            startsMidLine = stream.ReadByte() != '\n';
        }
        stream.Position = start;
        var read = 0;
        while (read < byteCount)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, read, byteCount - read);
            if (count == 0) break;
            read += count;
        }
        var truncated = start > 0;
        using var reader = new StreamReader(new MemoryStream(buffer, 0, read, writable: false),
            new UTF8Encoding(false), detectEncodingFromByteOrderMarks: start == 0);
        // The first line of a non-zero tail is probably cut mid-line: only complete lines count.
        if (startsMidLine) reader.ReadLine();
        var scanned = 0;
        while (reader.ReadLine() is { } line)
        {
            if ((scanned++ & 255) == 0) token.ThrowIfCancellationRequested();
            var separator = line.IndexOf(' ');
            if (separator <= 0) continue;
            if (!DateTimeOffset.TryParse(line[..separator], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
                continue;
            var message = line[(separator + 1)..];
            events.Add(new(time, Classify(message), message, source));
            // Trim in chunks so a dense tail stays O(n) instead of shifting per line.
            if (events.Count > maximumEntries * 2)
            {
                events.RemoveRange(0, events.Count - maximumEntries);
                truncated = true;
            }
        }
        if (events.Count > maximumEntries)
        {
            events.RemoveRange(0, events.Count - maximumEntries);
            truncated = true;
        }
        token.ThrowIfCancellationRequested();
        return new(events, truncated, maximumBytes, maximumEntries);
    }

    public static HistoryEventKind Classify(string text)
    {
        if (Contains(text, "fail", "error", "异常", "失败", "故障")) return HistoryEventKind.Error;
        if (Contains(text, "restore", "recover", "automatic", "恢复", "自动")) return HistoryEventKind.Recovery;
        if (Contains(text, "mode", "模式")) return HistoryEventKind.Mode;
        if (Contains(text, "fan", "curve", "profile", "风扇", "曲线", "方案")) return HistoryEventKind.Control;
        return HistoryEventKind.Other;
    }
    private static HistoryEventKind CurveEventKind(string curveStatus) => curveStatus switch
    {
        nameof(FanCurveStatusKind.ApplyFailed) or nameof(FanCurveStatusKind.SensorInvalid)
            or nameof(FanCurveStatusKind.RpmFeedbackFault) => HistoryEventKind.Error,
        nameof(FanCurveStatusKind.GuardianUnavailable) or nameof(FanCurveStatusKind.LeaseInvalid)
            or nameof(FanCurveStatusKind.RecoveryPending) => HistoryEventKind.Recovery,
        _ => HistoryEventKind.Control
    };

    private static bool Contains(string text, params string[] values) => values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

    public static void ExportRecords(string path, IEnumerable<HardwareTelemetryRecord> records, bool csv)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        if (csv) writer.WriteLine(TelemetryRecorder.CsvHeader);
        foreach (var record in records)
        {
            var r = record with
            {
                FanFault = RedactOptional(record.FanFault),
                Error = RedactOptional(record.Error),
                Event = RedactOptional(record.Event),
                CurveReason = RedactOptional(record.CurveReason)
            };
            writer.WriteLine(csv ? TelemetryRecorder.ToCsv(r) : JsonSerializer.Serialize(r, Options));
        }
    }
    public static void ExportEvents(string path, IEnumerable<HistoryEvent> events)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Timestamp,Kind,Source,Message");
        foreach (var e in events) writer.WriteLine(string.Join(",", Escape(e.Timestamp.ToString("O")), Escape(e.Kind.ToString()), Escape(DiagnosticRedactor.Redact(e.Source)), Escape(DiagnosticRedactor.Redact(e.Message))));
    }
    private static string? RedactOptional(string? value) => value is null ? null : DiagnosticRedactor.Redact(value);
    private static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
