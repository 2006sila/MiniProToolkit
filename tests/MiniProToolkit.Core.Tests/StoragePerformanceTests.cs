using System.Text;
using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Storage-performance regression tests: CSV schema cache (no per-tick header read),
/// bounded AppLog-style rotation and bounded tail reading of legacy plain-text logs.
/// Every test writes only under a per-test temp directory.
/// </summary>
public sealed class StoragePerformanceTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static string NewDirectory(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-storage-tests",
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static HardwareTelemetryRecord Record(DateTimeOffset time, string? eventName = null, string? error = null) =>
        new(time, 52.5, 2180, 0, 2300, 2400, 42, 100, FanRpmFeedbackState.Stable,
            null, 18, 40, 12.5, PowerMode.Balance, FanControlMode.FixedRpm, error, eventName);

    // ---------------------------------------------------------------- CSV schema cache

    [Fact]
    public void ConsecutiveAppendsReadTheCsvHeaderOnlyOnceAndKeepOrder()
    {
        var directory = NewDirectory("csv-cache");
        try
        {
            var first = DateTimeOffset.UtcNow.AddSeconds(-3);
            var second = first.AddSeconds(1);
            using var recorder = new TelemetryRecorder(directory);
            // A pre-existing, already-correct CSV forces the first append to validate the header.
            File.WriteAllText(recorder.CsvPath, TelemetryRecorder.CsvHeader + Environment.NewLine);

            recorder.Append(Record(first, "start"));
            recorder.Append(Record(second, "stop"));

            Assert.Equal(1, recorder.CsvHeaderValidationCount);
            Assert.Equal(31, TelemetryRecorder.CsvHeader.Split(',').Length);
            Assert.Equal(3, File.ReadAllLines(recorder.CsvPath).Length);
            Assert.Empty(Directory.GetFiles(directory, "*.schema-*.bak"));

            // Records are written in order and both files are flushed/closed when Append returns.
            var rows = File.ReadAllLines(recorder.JsonlPath);
            Assert.Equal(2, rows.Length);
            var parsed = rows.Select(row => JsonSerializer.Deserialize<HardwareTelemetryRecord>(row, WebOptions)!).ToArray();
            Assert.Equal(first, parsed[0].Timestamp);
            Assert.Equal("start", parsed[0].Event);
            Assert.Equal(second, parsed[1].Timestamp);
            Assert.Equal("stop", parsed[1].Event);
            using (new FileStream(recorder.CsvPath, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            using (new FileStream(recorder.JsonlPath, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExternalWriteWithoutHeaderChangeRevalidatesButKeepsTheFile()
    {
        var directory = NewDirectory("csv-external-append");
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(Record(DateTimeOffset.UtcNow));
            // First file was created by the recorder: no header line had to be read back.
            Assert.Equal(0, recorder.CsvHeaderValidationCount);

            File.AppendAllText(recorder.CsvPath, "2026-01-01T00:00:00.0000000+00:00,external" + Environment.NewLine);
            recorder.Append(Record(DateTimeOffset.UtcNow.AddSeconds(1)));

            Assert.Equal(1, recorder.CsvHeaderValidationCount);
            Assert.Empty(Directory.GetFiles(directory, "*.schema-*.bak"));
            // header + own first row + externally appended row + own second row
            Assert.Equal(4, File.ReadAllLines(recorder.CsvPath).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void IncompatibleExternalHeaderIsArchivedIntactBeforeThe31ColumnHeaderIsRestored()
    {
        var directory = NewDirectory("csv-external-schema");
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(Record(DateTimeOffset.UtcNow));
            const string oldCsv = "Timestamp,Fan1Rpm,Error\r\nold,2200,legacy\r\n";
            File.WriteAllText(recorder.CsvPath, oldCsv);

            recorder.Append(Record(DateTimeOffset.UtcNow.AddSeconds(1)));

            var archive = Assert.Single(Directory.GetFiles(directory, "telemetry.csv.schema-*.bak"));
            Assert.Equal(oldCsv, File.ReadAllText(archive));
            Assert.Equal(1, recorder.CsvHeaderValidationCount);
            var lines = File.ReadAllLines(recorder.CsvPath);
            Assert.Equal(2, lines.Length);
            Assert.Equal(TelemetryRecorder.CsvHeader, lines[0]);
            Assert.Equal(31, lines[0].Split(',').Length);
            Assert.Equal(31, lines[1].Split(',').Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExternalTruncateOrDeleteRecreatesTheHeader()
    {
        var directory = NewDirectory("csv-external-truncate");
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(Record(DateTimeOffset.UtcNow));

            File.WriteAllText(recorder.CsvPath, string.Empty);
            recorder.Append(Record(DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.Equal(TelemetryRecorder.CsvHeader, File.ReadAllLines(recorder.CsvPath)[0]);
            Assert.Equal(2, File.ReadAllLines(recorder.CsvPath).Length);

            File.Delete(recorder.CsvPath);
            recorder.Append(Record(DateTimeOffset.UtcNow.AddSeconds(2)));
            Assert.Equal(TelemetryRecorder.CsvHeader, File.ReadAllLines(recorder.CsvPath)[0]);
            Assert.Equal(2, File.ReadAllLines(recorder.CsvPath).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExternalRotationStartsTheNewCsvWithTheCurrentHeader()
    {
        var directory = NewDirectory("csv-external-rotation");
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(Record(DateTimeOffset.UtcNow));
            var archived = File.ReadAllText(recorder.CsvPath);
            File.Move(recorder.CsvPath, recorder.CsvPath + ".1");

            recorder.Append(Record(DateTimeOffset.UtcNow.AddSeconds(1)));

            Assert.Equal(archived, File.ReadAllText(recorder.CsvPath + ".1"));
            var lines = File.ReadAllLines(recorder.CsvPath);
            Assert.Equal(TelemetryRecorder.CsvHeader, lines[0]);
            Assert.Equal(2, lines.Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RecorderRotationInvalidatesTheCacheAndWritesTheHeaderBeforeTheFirstRow()
    {
        var directory = NewDirectory("csv-own-rotation");
        try
        {
            File.WriteAllText(Path.Combine(directory, "telemetry-retention.json"),
                "{\"version\":1,\"retainedArchiveCount\":1,\"maximumFileBytes\":1048576}");
            using var recorder = new TelemetryRecorder(directory);
            var legacyRow = new string('x', 1024 * 1024);
            File.WriteAllText(recorder.CsvPath, TelemetryRecorder.CsvHeader + Environment.NewLine + legacyRow + Environment.NewLine);

            recorder.Append(Record(DateTimeOffset.UtcNow));

            Assert.True(File.Exists(recorder.CsvPath + ".1"));
            Assert.Equal(new string('x', 1024 * 1024), File.ReadAllLines(recorder.CsvPath + ".1")[1]);
            var lines = File.ReadAllLines(recorder.CsvPath);
            Assert.Equal(2, lines.Length);
            Assert.Equal(TelemetryRecorder.CsvHeader, lines[0]);
            Assert.Equal(31, lines[1].Split(',').Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("truncate")]
    public void CsvRaceBetweenThePreAppendCheckAndTheCsvWriteCannotPoisonTheCache(string race)
    {
        var directory = NewDirectory("csv-append-race-" + race);
        try
        {
            var injected = 0;
            using var recorder = new TelemetryRecorder(directory, (path, line) =>
            {
                // Reproduces the external writer exactly in the window the pre-append check cannot cover.
                if (Interlocked.Exchange(ref injected, 1) == 0)
                {
                    if (race == "delete") File.Delete(path);
                    else File.WriteAllText(path, string.Empty);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            });
            var first = DateTimeOffset.UtcNow.AddSeconds(-2);
            recorder.Append(Record(first, "orphan-row"));

            // The race really produced a headerless file (only the data row).
            var raced = File.ReadAllLines(recorder.CsvPath);
            Assert.Single(raced);
            Assert.NotEqual(TelemetryRecorder.CsvHeader, raced[0]);

            // The unauthenticated stamp must not persist: the next append validates again,
            // archives the orphaned row intact and starts a correct 31-column CSV.
            recorder.Append(Record(first.AddSeconds(1), "recovered-row"));
            Assert.Equal(1, recorder.CsvHeaderValidationCount);
            var lines = File.ReadAllLines(recorder.CsvPath);
            Assert.Equal(TelemetryRecorder.CsvHeader, lines[0]);
            Assert.Equal(2, lines.Length);
            Assert.Equal(31, lines[0].Split(',').Length);
            Assert.Equal(31, lines[1].Split(',').Length);
            var archive = Assert.Single(Directory.GetFiles(directory, "telemetry.csv.schema-*.bak"));
            Assert.Contains("orphan-row", File.ReadAllText(archive), StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SameLengthExternalHeaderReplacementInvalidatesTheCache()
    {
        var directory = NewDirectory("csv-same-length");
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            var first = DateTimeOffset.UtcNow.AddSeconds(-2);
            recorder.Append(Record(first, "before"));
            var originalLength = new FileInfo(recorder.CsvPath).Length;
            var original = File.ReadAllText(recorder.CsvPath);
            // Same character/byte length, different first line (a lowercase column name).
            var replacement = original.Replace("Timestamp", "timestamp", StringComparison.Ordinal);
            Assert.Equal(original.Length, replacement.Length);
            File.WriteAllText(recorder.CsvPath, replacement, new UTF8Encoding(true));
            Assert.Equal(originalLength, new FileInfo(recorder.CsvPath).Length);
            // External writes change mtime; force it so the test never depends on timer ticks.
            File.SetLastWriteTimeUtc(recorder.CsvPath, DateTime.UtcNow.AddMinutes(5));

            recorder.Append(Record(first.AddSeconds(1), "after"));

            Assert.Equal(1, recorder.CsvHeaderValidationCount);
            var archive = Assert.Single(Directory.GetFiles(directory, "telemetry.csv.schema-*.bak"));
            Assert.Equal(replacement, File.ReadAllText(archive));
            var lines = File.ReadAllLines(recorder.CsvPath);
            Assert.Equal(TelemetryRecorder.CsvHeader, lines[0]);
            Assert.Equal(2, lines.Length);
            Assert.Equal(31, lines[1].Split(',').Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    // ---------------------------------------------------------------- BoundedTextLog

    [Fact]
    public void BoundedLogRotatesBySizeAndLeavesUnrelatedFilesAlone()
    {
        var directory = NewDirectory("bounded-rotate");
        try
        {
            var path = Path.Combine(directory, "app.log");
            var unrelated = Path.Combine(directory, "telemetry.csv.schema-old.bak");
            File.WriteAllText(unrelated, "keep-me");
            var log = new BoundedTextLog(path, maximumFileBytes: 1024, retainedFiles: 2, maximumEntryBytes: 256);
            var line = new string('a', 200);
            for (var index = 0; index < 30; index++) log.Append(line);

            Assert.True(File.Exists(path + ".1"));
            Assert.True(File.Exists(path + ".2"));
            Assert.False(File.Exists(path + ".3"));
            Assert.True(new FileInfo(path).Length <= 1024);
            Assert.True(new FileInfo(path + ".1").Length <= 1024);
            Assert.Equal("keep-me", File.ReadAllText(unrelated));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OversizedEntryIsTruncatedOnAUtf8BoundaryAndKeepsTimeAndErrorHead()
    {
        var directory = NewDirectory("bounded-entry");
        try
        {
            var path = Path.Combine(directory, "app.log");
            var log = new BoundedTextLog(path, maximumFileBytes: 4096, retainedFiles: 1, maximumEntryBytes: 512);
            var prefix = "2026-11-02T17:00:00.0000000+08:00 失败：硬盘错误 ";
            log.Append(prefix + string.Concat(Enumerable.Repeat("温度异常", 400)));

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length <= 512);
            // Strict decoding throws if truncation split a multi-byte character.
            var content = new UTF8Encoding(false, true).GetString(bytes);
            Assert.StartsWith("2026-11-02T17:00:00", content, StringComparison.Ordinal);
            Assert.Contains("失败", content, StringComparison.Ordinal);
            Assert.Contains("已截断", content, StringComparison.Ordinal);
            Assert.EndsWith(Environment.NewLine, content, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Utf8BoundaryHelperNeverSplitsAMultiByteCharacter()
    {
        Assert.Equal(string.Empty, BoundedTextLog.TruncateUtf8Boundary("中文", 0));
        Assert.Equal(string.Empty, BoundedTextLog.TruncateUtf8Boundary("中文", 2));
        Assert.Equal("中", BoundedTextLog.TruncateUtf8Boundary("中文", 3));
        Assert.Equal("中", BoundedTextLog.TruncateUtf8Boundary("中文", 5));
        Assert.Equal("中文", BoundedTextLog.TruncateUtf8Boundary("中文", 6));
    }

    [Fact]
    public void ConcurrentAppendsProduceWholeLines()
    {
        var directory = NewDirectory("bounded-concurrent");
        try
        {
            var path = Path.Combine(directory, "app.log");
            var log = new BoundedTextLog(path, maximumFileBytes: 1024 * 1024, retainedFiles: 2, maximumEntryBytes: 4096);
            Parallel.For(0, 500, index => log.Append($"line-{index:D3}"));

            var lines = File.ReadAllLines(path);
            Assert.Equal(500, lines.Length);
            Assert.All(lines, line => Assert.Matches("^line-[0-9]{3}$", line));
            Assert.Equal(500, lines.Distinct().Count());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PreExistingOversizedLogIsRotatedOnceWithoutShrinkingTheArchiveInMemory()
    {
        var directory = NewDirectory("bounded-legacy");
        try
        {
            var path = Path.Combine(directory, "app.log");
            var legacy = new string('x', 8192);
            File.WriteAllText(path, legacy);
            var log = new BoundedTextLog(path, maximumFileBytes: 1024, retainedFiles: 2, maximumEntryBytes: 256);

            log.Append("after-rotation");

            Assert.True(File.Exists(path + ".1"));
            Assert.Equal(legacy, File.ReadAllText(path + ".1"));
            Assert.True(new FileInfo(path).Length <= 1024);
            Assert.Contains("after-rotation", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    // ---------------------------------------------------------------- ReadLog bounded tail

    private static string LogLine(int index, string message = "fan curve applied") =>
        $"2026-11-02T10:{index / 60 % 60:D2}:{index % 60:D2}.0000000+08:00 {message}-{index}";

    private static void WriteLog(string path, int count, string newline, bool trailingNewline, bool bom)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < count; index++) builder.Append(LogLine(index)).Append(newline);
        if (!trailingNewline) builder.Length -= newline.Length;
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(bom));
    }

    [Fact]
    public void TailReadReturnsNewestEntriesAndReportsTruncationThroughTheCallback()
    {
        var directory = NewDirectory("readlog-callback");
        try
        {
            var path = Path.Combine(directory, "app.log");
            WriteLog(path, 200, "\n", trailingNewline: true, bom: false);
            var messages = new List<string>();

            var events = TelemetryHistory.ReadLog(path, "应用", maximumBytes: 1024, maximumEntries: 5,
                warning: message => messages.Add(message));

            Assert.InRange(events.Count, 1, 5);
            Assert.All(events, item => Assert.Equal("应用", item.Source));
            Assert.EndsWith("-199", events[^1].Message, StringComparison.Ordinal);
            Assert.DoesNotContain(events, item => item.Kind == HistoryEventKind.Other && item.Message.Contains("仅加载最近"));
            var warning = Assert.Single(messages);
            Assert.Contains("仅加载最近", warning, StringComparison.Ordinal);
            Assert.Contains("5 条", warning, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TailReadReturnsTheTruncationFlagWithoutInjectingEvents()
    {
        var directory = NewDirectory("readlog-flag");
        try
        {
            var path = Path.Combine(directory, "app.log");
            WriteLog(path, 200, "\n", trailingNewline: true, bom: false);

            var events = TelemetryHistory.ReadLog(path, "应用", 1024, 7, out var truncated);

            Assert.True(truncated);
            Assert.InRange(events.Count, 1, 7);
            Assert.All(events, item => Assert.DoesNotContain("仅加载最近", item.Message));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DefaultReadLogIsBoundedButDoesNotInjectAFakeEvent()
    {
        var directory = NewDirectory("readlog-default");
        try
        {
            var path = Path.Combine(directory, "app.log");
            // ~3 MiB, comfortably above the 2 MiB default tail.
            WriteLog(path, 40000, "\n", trailingNewline: true, bom: false);
            Assert.True(new FileInfo(path).Length > TelemetryHistory.DefaultLogTailBytes);

            var events = TelemetryHistory.ReadLog(path, "应用");
            Assert.True(events.Count <= TelemetryHistory.DefaultLogTailEntries);
            Assert.All(events, item => Assert.True(item.Timestamp.Year == 2026));

            var warnings = new List<string>();
            _ = TelemetryHistory.ReadLog(path, "应用", warning: message => warnings.Add(message));
            Assert.Single(warnings);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TailReadHandlesBomCrlfAndAMissingFinalNewline()
    {
        var directory = NewDirectory("readlog-encoding");
        try
        {
            var path = Path.Combine(directory, "app.log");
            WriteLog(path, 3, "\r\n", trailingNewline: false, bom: true);

            var events = TelemetryHistory.ReadLog(path, "Guardian", 1024 * 1024, 100, out var truncated);

            Assert.False(truncated);
            Assert.Equal(3, events.Count);
            Assert.StartsWith("fan curve applied-0", events[0].Message, StringComparison.Ordinal);
            Assert.EndsWith("fan curve applied-2", events[^1].Message, StringComparison.Ordinal);
            Assert.Equal(HistoryEventKind.Control, events[0].Kind);
            Assert.All(events, item => Assert.Equal("Guardian", item.Source));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TailReadDropsThePartialFirstLineAtTheByteBoundary()
    {
        var directory = NewDirectory("readlog-partial");
        try
        {
            var path = Path.Combine(directory, "app.log");
            var lines = Enumerable.Range(0, 60).Select(index => LogLine(index)).ToArray();
            WriteLog(path, 60, "\n", trailingNewline: true, bom: false);
            // Start the tail inside line 56 so the first line is provably incomplete.
            long total = 0;
            var offsets = new long[lines.Length];
            for (var index = 0; index < lines.Length; index++)
            {
                offsets[index] = total;
                total += Encoding.UTF8.GetByteCount(lines[index]) + 1;
            }
            var maximumBytes = (int)(total - (offsets[56] + 2));

            var events = TelemetryHistory.ReadLog(path, "应用", maximumBytes, 100, out var truncated);

            Assert.True(truncated);
            Assert.Equal(3, events.Count);
            Assert.All(events, item => Assert.Matches("^fan curve applied-[0-9]+$", item.Message));
            Assert.EndsWith("-59", events[^1].Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TailReadKeepsAWholeLineWhenTheByteWindowStartsOnALineBoundary()
    {
        var directory = NewDirectory("readlog-exact-boundary");
        try
        {
            var path = Path.Combine(directory, "app.log");
            var lines = Enumerable.Range(0, 40)
                .Select(index => $"2026-11-02T10:00:00.0000000+08:00 message-{index:D4}").ToArray();
            File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            var lineBytes = Encoding.UTF8.GetByteCount(lines[0] + "\n");
            Assert.All(lines, line => Assert.Equal(lineBytes, Encoding.UTF8.GetByteCount(line + "\n")));
            const int keep = 5;

            var events = TelemetryHistory.ReadLog(path, "应用", lineBytes * keep, 100, out var truncated);

            Assert.True(truncated);
            Assert.Equal(keep, events.Count);
            Assert.Equal($"message-{40 - keep:D4}", events[0].Message);
            Assert.Equal("message-0039", events[^1].Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TailReadKeepsTheErrorClassificationAndStopsWhenAlreadyCancelled()
    {
        var directory = NewDirectory("readlog-cancel");
        try
        {
            var path = Path.Combine(directory, "app.log");
            File.WriteAllText(path, LogLine(0, "hardware read error") + Environment.NewLine);
            var events = TelemetryHistory.ReadLog(path, "应用", 1024 * 1024, 10, out _);
            Assert.Equal(HistoryEventKind.Error, Assert.Single(events).Kind);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => TelemetryHistory.ReadLog(path, "应用", cancelled.Token));
            Assert.Throws<OperationCanceledException>(() =>
                TelemetryHistory.ReadLog(path, "应用", 1024, 10, out _, cancelled.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingFileReturnsNoEntriesAndNoWarning()
    {
        var directory = NewDirectory("readlog-missing");
        try
        {
            var warnings = new List<string>();
            var events = TelemetryHistory.ReadLog(Path.Combine(directory, "missing.log"), "应用",
                warning: message => warnings.Add(message));
            Assert.Empty(events);
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(directory, true); }
    }
}
