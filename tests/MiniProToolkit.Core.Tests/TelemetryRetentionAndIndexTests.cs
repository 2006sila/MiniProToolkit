using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class TelemetryRetentionAndIndexTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-history-completion", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    public TelemetryRetentionAndIndexTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private string PathFor(string name) => Path.Combine(_directory, name);
    private static HardwareTelemetryRecord Record(DateTimeOffset? time = null) => new(time ?? Time,
        50, 2000, 0, null, 2200, 40, null, FanRpmFeedbackState.Disabled, null,
        20, 30, 12, PowerMode.Balance, FanControlMode.FirmwareAutomatic, null, null);
    private void Write(string name, DateTimeOffset? time = null, bool newline = true) =>
        File.WriteAllText(PathFor(name), JsonSerializer.Serialize(Record(time)) + (newline ? "\n" : ""));

    [Fact]
    public void RetentionDefaultsAndVersionedValidation()
    {
        var store = new TelemetryRetentionSettingsStore(_directory);
        var defaults = store.Load(out var warning);
        Assert.Null(warning);
        Assert.Equal(3, defaults.RetainedArchiveCount);
        Assert.Equal(10 * 1048576, defaults.MaximumFileBytes);
        Assert.Null(defaults.MaximumArchiveAgeDays);
        foreach (var bad in new[] { "{bad", "null", "{\"version\":99}", "{\"retainedArchiveCount\":0}" })
        {
            File.WriteAllText(store.FilePath, bad);
            Assert.Equal(defaults, store.Load(out warning));
            Assert.NotNull(warning);
        }
        var maximum = new TelemetryRetentionSettings { RetainedArchiveCount = 30, MaximumFileBytes = 100 * 1048576, MaximumArchiveAgeDays = 365 };
        store.Save(maximum);
        Assert.Equal(maximum, store.Load(out warning));
        Assert.Null(warning);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(maximum with { MaximumArchiveAgeDays = 0 }));
        Assert.False((maximum with { RetainedArchiveCount = 31 }).IsValid);
        Assert.False((maximum with { MaximumFileBytes = 1048575 }).IsValid);
        Assert.False((maximum with { MaximumArchiveAgeDays = 366 }).IsValid);
    }

    [Fact]
    public void RetentionPlanAndExecutionOnlyRemoveExactUnchangedArchives()
    {
        foreach (var name in new[] { "telemetry.jsonl", "telemetry.csv", "telemetry.jsonl.1", "telemetry.csv.3", "telemetry.jsonl.4", "telemetry.csv.4", "telemetry.jsonl.01", "telemetry.jsonl.0", "telemetry.jsonl.-1", "telemetry.csv.schema-old.bak", "other.4" })
            File.WriteAllText(PathFor(name), name);
        Directory.CreateDirectory(PathFor("telemetry.csv.9"));
        var settings = new TelemetryRetentionSettings();
        var plan = TelemetryMaintenance.Plan(_directory, settings, Time);
        Assert.Equal(2, plan.Files.Count);
        File.AppendAllText(PathFor("telemetry.csv.4"), "changed after preview");
        Assert.Equal(1, TelemetryMaintenance.Execute(plan));
        Assert.False(File.Exists(PathFor("telemetry.jsonl.4")));
        Assert.True(File.Exists(PathFor("telemetry.csv.4")));
        Assert.True(File.Exists(PathFor("telemetry.csv.schema-old.bak")));
        Assert.True(File.Exists(PathFor("telemetry.jsonl")));
        var unsafePlan = new TelemetryCleanupPlan(_directory, [new(PathFor("telemetry.csv"), 1, 13, DateTime.UtcNow)]);
        Assert.Equal(0, TelemetryMaintenance.Execute(unsafePlan));
        var outsideDirectory = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outsideDirectory);
        var outside = Path.Combine(outsideDirectory, "telemetry.csv.8");
        File.WriteAllText(outside, "outside");
        var info = new FileInfo(outside);
        Assert.Equal(0, TelemetryMaintenance.Execute(new(_directory, [new(outside, 8, info.Length, info.LastWriteTimeUtc)])));
    }

    [Fact]
    public void RetentionAgeUsesMtimeStrictBoundaryAndDisabledByDefault()
    {
        var cutoff = Time.UtcDateTime.AddDays(-7);
        var archives = new[] { new TelemetryArchive("a", 1, 1, cutoff.AddTicks(-1)), new TelemetryArchive("b", 2, 1, cutoff), new TelemetryArchive("c", 3, 1, cutoff.AddTicks(1)) };
        Assert.Empty(TelemetryMaintenance.SelectArchives(archives, new(), Time));
        Assert.Equal("a", Assert.Single(TelemetryMaintenance.SelectArchives(archives, new() { MaximumArchiveAgeDays = 7 }, Time)).Path);
    }

    [Fact]
    public void RecorderUsesSavedSizeAndCountAndKeepsCsv31Columns()
    {
        new TelemetryRetentionSettingsStore(_directory).Save(new() { MaximumFileBytes = 1048576, RetainedArchiveCount = 1 });
        File.WriteAllText(PathFor("telemetry.jsonl"), new string(' ', 1048576));
        Write("telemetry.jsonl.1"); Write("telemetry.jsonl.2");
        using var recorder = new TelemetryRecorder(_directory);
        recorder.Append(Record());
        Assert.Equal(1048576, new FileInfo(PathFor("telemetry.jsonl.1")).Length);
        Assert.False(File.Exists(PathFor("telemetry.jsonl.2")));
        Assert.Single(TelemetryHistory.Load([recorder.JsonlPath]).Records);
        Assert.Equal(31, TelemetryRecorder.CsvHeader.Split(',').Length);
        // Settings changes do not cause a read on every sampling tick.
        new TelemetryRetentionSettingsStore(_directory).Save(new() { MaximumFileBytes = 1048576, RetainedArchiveCount = 3 });
        recorder.Append(Record(Time.AddSeconds(1)));
        Assert.Null(recorder.RetentionWarning);
    }

    [Fact]
    public void DefaultRecorderStillRotatesAt10MiBAndRetainsThreeArchives()
    {
        File.WriteAllText(PathFor("telemetry.jsonl"), new string(' ', 10 * 1048576));
        foreach (var number in Enumerable.Range(1, 4)) Write($"telemetry.jsonl.{number}");
        using var recorder = new TelemetryRecorder(_directory);
        recorder.Append(Record());
        Assert.Equal(10 * 1048576, new FileInfo(PathFor("telemetry.jsonl.1")).Length);
        Assert.True(File.Exists(PathFor("telemetry.jsonl.3")));
        Assert.False(File.Exists(PathFor("telemetry.jsonl.4")));
        Assert.Single(TelemetryHistory.Load([recorder.JsonlPath]).Records);
    }

    [Fact]
    public void RangeBoundariesAreInclusiveAndCachedMinMaxCanSkipBeforeOrAfter()
    {
        Write("telemetry.jsonl");
        var index = new TelemetryFileIndex(_directory);
        Assert.Single(index.Load(Time, Time).Records);
        Assert.Empty(index.Load(Time.AddSeconds(1), Time.AddSeconds(2)).Records);
        Assert.Equal(1, index.LastSkippedFiles);
        Assert.Empty(index.Load(Time.AddSeconds(-2), Time.AddSeconds(-1)).Records);
        Assert.Equal(1, index.LastSkippedFiles);
    }

    [Fact]
    public void LocalScanBoundsAndWarningsAreExplicit()
    {
        foreach (var number in Enumerable.Range(1, TelemetryFileIndex.MaximumFiles + 3)) Write($"telemetry.jsonl.{number}");
        var result = new TelemetryFileIndex(_directory).Load();
        Assert.Equal(TelemetryFileIndex.MaximumFiles, result.Records.Count);
        Assert.Contains(result.Warnings, w => w.Contains("结果不完整"));
    }

    [Fact]
    public void IndexHitsOnlyTrustedOutOfRangeFilesAndReloadsLengthOrMtimeChange()
    {
        Write("telemetry.jsonl");
        var index = new TelemetryFileIndex(_directory);
        Assert.Single(index.Load().Records);
        Assert.Empty(index.Load(Time.AddDays(1), Time.AddDays(2)).Records);
        Assert.Equal(1, index.LastSkippedFiles);
        File.AppendAllText(PathFor("telemetry.jsonl"), JsonSerializer.Serialize(Record(Time.AddDays(1))) + "\n");
        Assert.Single(index.Load(Time.AddDays(1), Time.AddDays(2)).Records);
        Assert.Equal(0, index.LastSkippedFiles);
        Write("telemetry.jsonl", Time.AddDays(3));
        File.SetLastWriteTimeUtc(PathFor("telemetry.jsonl"), DateTime.UtcNow.AddMinutes(2));
        Assert.Single(index.Load(Time.AddDays(3), Time.AddDays(4)).Records);
        Assert.Equal(1, index.LastScannedFiles);
    }

    [Fact]
    public void IndexDoesNotReuseOldPathAfterRotationOrSameLengthRewrite()
    {
        Write("telemetry.jsonl");
        var index = new TelemetryFileIndex(_directory);
        index.Load();
        var oldMtime = File.GetLastWriteTimeUtc(PathFor("telemetry.jsonl"));
        Write("telemetry.jsonl", Time.AddDays(2));
        File.SetLastWriteTimeUtc(PathFor("telemetry.jsonl"), oldMtime);
        Assert.Single(index.Load(Time.AddDays(2), Time.AddDays(3)).Records);
        File.Move(PathFor("telemetry.jsonl"), PathFor("telemetry.jsonl.1"));
        Write("telemetry.jsonl", Time.AddDays(4));
        Assert.Single(index.Load(Time.AddDays(4), Time.AddDays(5)).Records);
        Assert.Equal(2, index.LastScannedFiles);
    }

    [Fact]
    public void PartialAndBadRowsNeverMakeTrustedSkippingIndex()
    {
        Write("telemetry.jsonl", newline: false);
        var index = new TelemetryFileIndex(_directory);
        Assert.Single(index.Load().Records);
        index.Load(Time.AddDays(1), Time.AddDays(2));
        Assert.Equal(0, index.LastSkippedFiles);
        File.AppendAllText(PathFor("telemetry.jsonl"), "\n{broken");
        Assert.Contains(index.Load().Warnings, w => w.Contains("无效/未写完"));
        index.Load(Time.AddDays(1), Time.AddDays(2));
        Assert.Equal(0, index.LastSkippedFiles);
    }

    [Fact]
    public void InvalidCacheAndWriteFailureFallBackWithoutLosingRecords()
    {
        Write("telemetry.jsonl");
        var index = new TelemetryFileIndex(_directory);
        foreach (var invalid in new[] { "broken", "{\"version\":100,\"entries\":[]}", "{\"version\":1,\"entries\":[null]}" })
        {
            File.WriteAllText(index.SidecarPath, invalid);
            var result = index.Load();
            Assert.Single(result.Records);
            Assert.Contains(result.Warnings, w => w.Contains("退回扫描"));
        }
        File.Delete(index.SidecarPath);
        Directory.CreateDirectory(index.SidecarPath);
        var fallback = index.Load();
        Assert.Single(fallback.Records);
        Assert.Contains(fallback.Warnings, w => w.Contains("写入失败"));
    }

    [Theory]
    [InlineData("null-edge")]
    [InlineData("short-edge")]
    [InlineData("not-hex")]
    [InlineData("negative-length")]
    [InlineData("negative-mtime")]
    [InlineData("oversized-creation")]
    [InlineData("inverted-range")]
    [InlineData("duplicate-path")]
    public void InvalidIndexStampOrRangeOrDuplicatePathFallsBack(string corruption)
    {
        Write("telemetry.jsonl");
        var index = new TelemetryFileIndex(_directory);
        index.Load();
        var doc = JsonSerializer.Deserialize<TelemetryFileIndexDocument>(File.ReadAllText(index.SidecarPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var entry = Assert.Single(doc.Entries);
        entry = corruption switch
        {
            "null-edge" => entry with { Stamp = entry.Stamp with { Edges = null! } },
            "short-edge" => entry with { Stamp = entry.Stamp with { Edges = "123" } },
            "not-hex" => entry with { Stamp = entry.Stamp with { Edges = new string('Z', 64) } },
            "negative-length" => entry with { Stamp = entry.Stamp with { Length = -1 } },
            "negative-mtime" => entry with { Stamp = entry.Stamp with { MtimeTicks = -1 } },
            "oversized-creation" => entry with { Stamp = entry.Stamp with { CreationTicks = long.MaxValue } },
            "inverted-range" => entry with { Minimum = Time.AddDays(1), Maximum = Time },
            _ => entry
        };
        var entries = corruption == "duplicate-path" ? new[] { entry, entry with { Minimum = Time.AddDays(5), Maximum = Time.AddDays(5) } } : new[] { entry };
        File.WriteAllText(index.SidecarPath, JsonSerializer.Serialize(new TelemetryFileIndexDocument(1, entries), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var result = index.Load(Time.AddDays(1), Time.AddDays(2));
        Assert.Equal(1, index.LastScannedFiles);
        Assert.Equal(0, index.LastSkippedFiles);
        Assert.Contains(result.Warnings, w => w.Contains("退回扫描"));
    }

    [Fact]
    public void ExternalLoadSharesScanBudgetAcrossFilesBeforeFiltering()
    {
        Write("telemetry.jsonl", Time.AddDays(-1));
        File.WriteAllText(PathFor("telemetry.jsonl.1"), string.Concat(Enumerable.Repeat(JsonSerializer.Serialize(Record()) + "\n", 5)));
        var loaded = TelemetryHistory.Load([PathFor("telemetry.jsonl"), PathFor("telemetry.jsonl.1")],
            start: Time, end: Time, maximumRecords: 3);
        // The first out-of-range record still consumed scan memory/CPU budget.
        Assert.Equal(2, loaded.Records.Count);
        Assert.Contains(loaded.Warnings, w => w.Contains("总扫描 3") && w.Contains("不完整") && w.Contains("请缩小时段"));
        Assert.Contains(loaded.Warnings, w => w.Contains("telemetry.jsonl.1") && w.Contains("上限 2"));
        Assert.False(File.Exists(new TelemetryFileIndex(_directory).SidecarPath));
    }

    [Fact]
    public void IndexBudgetTruncatesSecondFileWithoutTrustedMinMaxAndDoesNotScanThird()
    {
        Write("telemetry.jsonl");
        File.WriteAllText(PathFor("telemetry.jsonl.1"), string.Concat(Enumerable.Repeat(JsonSerializer.Serialize(Record(Time.AddSeconds(1))) + "\n", 5)));
        Write("telemetry.jsonl.2", Time.AddSeconds(2));
        var index = new TelemetryFileIndex(_directory);
        var loaded = index.Load(maximumRecords: 3);
        Assert.Equal(3, loaded.Records.Count);
        Assert.Equal(2, index.LastScannedFiles);
        Assert.Contains(loaded.Warnings, w => w.Contains("总扫描 3") && w.Contains("不完整") && w.Contains("请缩小时段"));
        var doc = JsonSerializer.Deserialize<TelemetryFileIndexDocument>(File.ReadAllText(index.SidecarPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, doc.Entries.Count);
        Assert.True(doc.Entries.Single(e => e.Path == PathFor("telemetry.jsonl")).Trusted);
        var truncated = doc.Entries.Single(e => e.Path == PathFor("telemetry.jsonl.1"));
        Assert.Equal(2, truncated.Count);
        Assert.False(truncated.Trusted);
        // Truncated min/max must not permit an out-of-range skip on the next load.
        index.Load(Time.AddDays(1), Time.AddDays(2), maximumRecords: 3);
        Assert.Equal(1, index.LastSkippedFiles);
        Assert.Equal(1, index.LastScannedFiles);
    }

    [Fact]
    public void TrustedRangeSkipsDoNotSpendRecordBudget()
    {
        Write("telemetry.jsonl", Time.AddDays(-2));
        Write("telemetry.jsonl.1");
        var index = new TelemetryFileIndex(_directory);
        index.Load();
        var loaded = index.Load(Time, Time, maximumRecords: 1);
        Assert.Single(loaded.Records);
        Assert.Equal(1, index.LastSkippedFiles);
        Assert.Equal(1, index.LastScannedFiles);
        Assert.Contains(loaded.Warnings, w => w.Contains("总扫描 1"));
    }

    [Fact]
    public void BudgetedLoadsObserveCancellationAndValidateLimit()
    {
        Write("telemetry.jsonl");
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> CancelBetweenFiles()
        {
            yield return PathFor("telemetry.jsonl");
            cancellation.Cancel();
            yield return PathFor("telemetry.jsonl.1");
        }
        Assert.Throws<OperationCanceledException>(() => TelemetryHistory.Load(CancelBetweenFiles(), cancellation.Token, maximumRecords: 2));
        var index = new TelemetryFileIndex(_directory);
        Assert.Throws<OperationCanceledException>(() => index.Load(token: cancellation.Token, maximumRecords: 2));
        Assert.False(File.Exists(index.SidecarPath));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Load(maximumRecords: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TelemetryHistory.Load([], maximumRecords: TelemetryHistory.MaximumRecordsPerLoad + 1));
    }

    [Fact]
    public void IndexCancellationAndExternalLoadDoNotWriteSidecars()
    {
        Write("telemetry.jsonl");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var index = new TelemetryFileIndex(_directory);
        Assert.Throws<OperationCanceledException>(() => index.Load(token: cancellation.Token));
        Assert.False(File.Exists(index.SidecarPath));
        Assert.Single(TelemetryHistory.Load([PathFor("telemetry.jsonl")]).Records);
        Assert.False(File.Exists(index.SidecarPath));
    }

    [Fact]
    public async Task ConcurrentAppendCannotCreateTrustedEntryForChangedSnapshot()
    {
        // A large finite scan leaves time for a real concurrent append. The stamp must be
        // revalidated after the scan; either changed metadata or partial EOF forces fallback.
        var line = JsonSerializer.Serialize(Record()) + "\n";
        File.WriteAllText(PathFor("telemetry.jsonl"), string.Concat(Enumerable.Repeat(line, 18000)));
        var index = new TelemetryFileIndex(_directory);
        using var done = new CancellationTokenSource();
        var append = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                using var stream = new FileStream(PathFor("telemetry.jsonl"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Position = stream.Length;
                stream.WriteByte((byte)' ');
                File.SetLastWriteTimeUtc(PathFor("telemetry.jsonl"), DateTime.UtcNow);
                Thread.Yield();
            }
        });
        TelemetryHistoryResult loaded;
        try { loaded = index.Load(); }
        finally { done.Cancel(); await append; }
        Assert.NotEmpty(loaded.Records);
        Assert.Contains(loaded.Warnings, w => w.Contains("变化") || w.Contains("不完整"));
        using var document = JsonDocument.Parse(File.ReadAllText(index.SidecarPath));
        Assert.All(document.RootElement.GetProperty("entries").EnumerateArray(), entry => Assert.False(entry.GetProperty("trusted").GetBoolean()));
        index.Load(Time.AddDays(1), Time.AddDays(2));
        Assert.Equal(0, index.LastSkippedFiles);
    }
}

public sealed class HistoryChartScaleTests
{
    private static HardwareTelemetryRecord Record => new(DateTimeOffset.UtcNow, 50, 2500, 0, null, 2000, 40,
        null, FanRpmFeedbackState.Disabled, null, 25, 40, 120, PowerMode.Balance, FanControlMode.FirmwareAutomatic, null, null);

    [Fact]
    public void UnitsAndTemperatureConversionAreExplicitAndExportIndependent()
    {
        Assert.Equal(HistoryChartUnit.Celsius, HistoryChartScale.Unit(HistoryChartSeries.CpuTemperature));
        Assert.Equal(HistoryChartUnit.Fahrenheit, HistoryChartScale.Unit(HistoryChartSeries.CpuTemperature, true));
        Assert.Equal(HistoryChartUnit.Rpm, HistoryChartScale.Unit(HistoryChartSeries.TargetRpm));
        Assert.Equal(HistoryChartUnit.Percent, HistoryChartScale.Unit(HistoryChartSeries.Duty));
        Assert.Equal(HistoryChartUnit.Watts, HistoryChartScale.Unit(HistoryChartSeries.CpuPower));
        Assert.Equal(122, HistoryChartScale.Value(Record, HistoryChartSeries.CpuTemperature, true));
        Assert.Equal(50, Record.CpuTemperatureC);
        Assert.Equal(0, HistoryChartScale.Axis(HistoryChartUnit.Fahrenheit, []).Minimum);
    }

    [Fact]
    public void IndependentRangesAndCurveTargetPriorityPreservePhysicalUnits()
    {
        Assert.Equal(3300, HistoryChartScale.Value(Record with { CurveTargetRpm = 3300 }, HistoryChartSeries.TargetRpm));
        Assert.Equal(2000, HistoryChartScale.Value(Record, HistoryChartSeries.TargetRpm));
        Assert.Equal(120, HistoryChartScale.Axis(HistoryChartUnit.Watts, [120]).Maximum);
        Assert.Equal(100, HistoryChartScale.Axis(HistoryChartUnit.Percent, [120]).Maximum);
        Assert.Equal(6000, HistoryChartScale.Axis(HistoryChartUnit.Rpm, [5500]).Maximum);
        Assert.Equal(0.5, HistoryChartScale.Axis(HistoryChartUnit.Percent, []).Normalize(50));
        Assert.Null(HistoryChartScale.Value(Record with { CpuTemperatureC = double.NaN }, HistoryChartSeries.CpuTemperature));
        Assert.Equal(HistoryChartUnit.Percent, HistoryChartScale.Unit(HistoryChartSeries.CpuUsage));
    }
}
