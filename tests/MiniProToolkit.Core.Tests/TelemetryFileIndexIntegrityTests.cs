using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Sidecar summary fields (Minimum/Maximum/Count/Trusted) decide whether a file is skipped
/// without reading it. These tests pin the summary-integrity binding so a corrupt or edited
/// sidecar can never turn a real history file into a silent "no data" result.
/// </summary>
public sealed class TelemetryFileIndexIntegrityTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-index-integrity", Guid.NewGuid().ToString("N"));

    public TelemetryFileIndexIntegrityTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private string PathFor(string name) => Path.Combine(_directory, name);
    private string HistoryPath => PathFor("telemetry.jsonl");

    private static HardwareTelemetryRecord Record(DateTimeOffset? time = null) => new(time ?? Time,
        50, 2000, 0, null, 2200, 40, null, FanRpmFeedbackState.Disabled, null,
        20, 30, 12, PowerMode.Balance, FanControlMode.FirmwareAutomatic, null, null);

    private void Write(DateTimeOffset? time = null) =>
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(Record(time)) + "\n");

    private static TelemetryFileIndexDocument ReadDocument(TelemetryFileIndex index) =>
        JsonSerializer.Deserialize<TelemetryFileIndexDocument>(File.ReadAllText(index.SidecarPath), WebJson)!;

    private static void WriteDocument(TelemetryFileIndex index, TelemetryFileIndexDocument document) =>
        File.WriteAllText(index.SidecarPath, JsonSerializer.Serialize(document, WebJson));

    [Fact]
    public void EditedSummaryRangeCannotHideInRangeRecordsFromLoad()
    {
        Write();
        var index = new TelemetryFileIndex(_directory);
        Assert.Single(index.Load().Records);

        var original = ReadDocument(index);
        Assert.Equal(TelemetryFileIndex.CurrentVersion, original.Version);
        var entry = Assert.Single(original.Entries);
        Assert.True(entry.Trusted);
        Assert.Equal(TelemetryFileIndex.ComputeSummaryIntegrity(entry), entry.SummaryIntegrity);
        var historyBytes = File.ReadAllBytes(HistoryPath);

        // Move the cached window entirely before the query while keeping the stamp, count and
        // integrity string untouched: a plain sidecar edit of only min/max. If this were
        // honored, Load would skip the file and silently return no history.
        var tampered = entry with { Minimum = Time.AddDays(-2), Maximum = Time.AddDays(-1) };
        WriteDocument(index, original with { Entries = [tampered] });

        var result = index.Load(Time.AddMinutes(-1));

        var record = Assert.Single(result.Records);
        Assert.Equal(Time, record.Timestamp);
        Assert.Equal(0, index.LastSkippedFiles);
        Assert.Equal(1, index.LastScannedFiles);
        Assert.Contains(result.Warnings, w => w.Contains("退回扫描"));
        Assert.Equal(historyBytes, File.ReadAllBytes(HistoryPath));

        // The invalid cache is replaced (same path, atomic temp move) by a consistent v2 entry.
        var repaired = ReadDocument(index);
        Assert.Equal(TelemetryFileIndex.CurrentVersion, repaired.Version);
        var repairedEntry = Assert.Single(repaired.Entries);
        Assert.Equal(TelemetryFileIndex.ComputeSummaryIntegrity(repairedEntry), repairedEntry.SummaryIntegrity);
        Assert.Equal(entry.Stamp, repairedEntry.Stamp);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("trusted")]
    [InlineData("edges")]
    [InlineData("minimum")]
    [InlineData("maximum")]
    [InlineData("summary-missing")]
    [InlineData("summary-short")]
    [InlineData("summary-not-hex")]
    public void TamperedSummaryFieldsAreDetectedAsCorruption(string corruption)
    {
        Write();
        var index = new TelemetryFileIndex(_directory);
        Assert.Single(index.Load().Records);
        var original = ReadDocument(index);
        var entry = Assert.Single(original.Entries);
        var tampered = corruption switch
        {
            "count" => entry with { Count = entry.Count + 1 },
            "trusted" => entry with { Trusted = !entry.Trusted },
            "edges" => entry with { Stamp = entry.Stamp with { Edges = new string('A', 64) } },
            "minimum" => entry with { Minimum = entry.Minimum!.Value.AddTicks(-1) },
            "maximum" => entry with { Maximum = entry.Maximum!.Value.AddTicks(1) },
            "summary-missing" => entry with { SummaryIntegrity = null! },
            "summary-short" => entry with { SummaryIntegrity = entry.SummaryIntegrity[..32] },
            "summary-not-hex" => entry with { SummaryIntegrity = new string('Z', 64) },
            _ => throw new InvalidOperationException(corruption)
        };
        WriteDocument(index, original with { Entries = [tampered] });

        var result = index.Load();

        // Detection is by warning + rescan; the real file is still read, so no record is lost.
        Assert.Single(result.Records);
        Assert.Equal(0, index.LastSkippedFiles);
        Assert.Equal(1, index.LastScannedFiles);
        Assert.Contains(result.Warnings, w => w.Contains("退回扫描"));
        var repaired = ReadDocument(index);
        Assert.Equal(TelemetryFileIndex.CurrentVersion, repaired.Version);
        Assert.Equal(TelemetryFileIndex.ComputeSummaryIntegrity(Assert.Single(repaired.Entries)),
            Assert.Single(repaired.Entries).SummaryIntegrity);
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
    public void InvalidEntriesInVersionTwoSidecarFallBackToScan(string corruption)
    {
        // Same entry-shape checks the v1 schema used to exercise; kept here because the
        // shared TelemetryRetentionAndIndexTests file still writes v1 documents.
        Write();
        var index = new TelemetryFileIndex(_directory);
        index.Load();
        var doc = ReadDocument(index);
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
        var entries = corruption == "duplicate-path" ? new[] { entry, entry } : new[] { entry };
        WriteDocument(index, new TelemetryFileIndexDocument(TelemetryFileIndex.CurrentVersion, entries));

        var result = index.Load(Time.AddDays(1), Time.AddDays(2));

        Assert.Empty(result.Records);
        Assert.Equal(1, index.LastScannedFiles);
        Assert.Equal(0, index.LastSkippedFiles);
        Assert.Contains(result.Warnings, w => w.Contains("退回扫描"));
        Assert.Equal(TelemetryFileIndex.CurrentVersion, ReadDocument(index).Version);
    }

    [Fact]
    public void VersionOneSidecarIsCacheOnlyAndUpgradedWithoutTouchingHistory()
    {
        Write();
        var index = new TelemetryFileIndex(_directory);
        Assert.Single(index.Load().Records);
        var upgraded = ReadDocument(index);
        var entry = Assert.Single(upgraded.Entries);
        var historyBytes = File.ReadAllBytes(HistoryPath);

        // Simulated v1 sidecar on the same path: no integrity field and a stale window that
        // would otherwise let Load skip the file for an in-range query.
        var legacy = new TelemetryFileIndexDocument(1,
            [entry with { Minimum = Time.AddDays(-2), Maximum = Time.AddDays(-1), SummaryIntegrity = null! }]);
        WriteDocument(index, legacy);

        var result = index.Load(Time.AddMinutes(-1));

        Assert.Single(result.Records);
        Assert.Equal(0, index.LastSkippedFiles);
        Assert.Contains(result.Warnings, w => w.Contains("退回扫描") && w.Contains("版本 1"));
        // Only the disposable cache is replaced; the recorder file is byte-identical.
        Assert.Equal(historyBytes, File.ReadAllBytes(HistoryPath));
        var migrated = ReadDocument(index);
        Assert.Equal(TelemetryFileIndex.CurrentVersion, migrated.Version);
        var migratedEntry = Assert.Single(migrated.Entries);
        Assert.Equal(TelemetryFileIndex.ComputeSummaryIntegrity(migratedEntry), migratedEntry.SummaryIntegrity);
        Assert.Equal(entry.Stamp, migratedEntry.Stamp);
    }

    [Fact]
    public void UntouchedCacheStillSkipsOutOfRangeFilesAndWritesStableDigest()
    {
        Write();
        var index = new TelemetryFileIndex(_directory);
        Assert.Single(index.Load().Records);
        var firstWrite = File.ReadAllText(index.SidecarPath);

        var skipped = index.Load(Time.AddDays(1), Time.AddDays(2));
        Assert.Empty(skipped.Records);
        Assert.Empty(skipped.Warnings);
        Assert.Equal(1, index.LastSkippedFiles);
        Assert.Equal(0, index.LastScannedFiles);
        // A skip-only load must not churn the sidecar contents.
        Assert.Equal(firstWrite, File.ReadAllText(index.SidecarPath));

        var entry = Assert.Single(ReadDocument(index).Entries);
        Assert.Equal(entry.SummaryIntegrity, TelemetryFileIndex.ComputeSummaryIntegrity(entry));
        // Digest covers every summary field, not just the stamp edge hash.
        Assert.NotEqual(entry.SummaryIntegrity, TelemetryFileIndex.ComputeSummaryIntegrity(entry with { Minimum = entry.Minimum!.Value.AddTicks(-1) }));
        Assert.NotEqual(entry.SummaryIntegrity, TelemetryFileIndex.ComputeSummaryIntegrity(entry with { Maximum = entry.Maximum!.Value.AddTicks(1) }));
        Assert.NotEqual(entry.SummaryIntegrity, TelemetryFileIndex.ComputeSummaryIntegrity(entry with { Count = entry.Count + 1 }));
        Assert.NotEqual(entry.SummaryIntegrity, TelemetryFileIndex.ComputeSummaryIntegrity(entry with { Trusted = false }));
        Assert.NotEqual(entry.SummaryIntegrity, TelemetryFileIndex.ComputeSummaryIntegrity(entry with { Stamp = entry.Stamp with { Edges = new string('B', 64) } }));
        // Utc ticks keep the digest stable across equivalent offsets and JSON round-trips.
        var offsetEntry = entry with { Minimum = entry.Minimum!.Value.ToOffset(TimeSpan.FromHours(8)) };
        var roundTripped = JsonSerializer.Deserialize<TelemetryFileIndexEntry>(JsonSerializer.Serialize(entry, WebJson), WebJson)!;
        Assert.Equal(entry.SummaryIntegrity, TelemetryFileIndex.ComputeSummaryIntegrity(offsetEntry));
        Assert.Equal(entry.SummaryIntegrity, TelemetryFileIndex.ComputeSummaryIntegrity(roundTripped));
    }
}
