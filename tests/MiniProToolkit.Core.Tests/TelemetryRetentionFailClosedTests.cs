using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// A corrupt/unreadable/unknown-version telemetry-retention.json must never delete telemetry
/// archives: the writer runs on non-destructive defaults, reports the policy as untrusted and
/// skips the periodic cleanup plan. When the active file reaches the size cap it cannot rotate
/// without dropping the tail archive, so the append is refused (bounded growth, files untouched)
/// instead of deleting history. Missing or valid files stay trusted, so the intended 60 s apply
/// semantics (including an explicit retention config deleting old archives) do not change.
/// </summary>
public sealed class TelemetryRetentionFailClosedTests : IDisposable
{
    private const int SeededArchives = 20;
    private const int MaximumFileBytes = 10 * 1048576;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-retention-failclosed", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    public TelemetryRetentionFailClosedTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private string PathFor(string name) => Path.Combine(_directory, name);
    private string SettingsPath => PathFor(TelemetryRetentionSettingsStore.FileName);
    private static TelemetryRetentionSettings Defaults => new();

    private static HardwareTelemetryRecord Record(DateTimeOffset? time = null) => new(time ?? Time,
        50, 2000, 0, null, 2200, 40, null, FanRpmFeedbackState.Disabled, null,
        20, 30, 12, PowerMode.Balance, FanControlMode.FirmwareAutomatic, null, null);

    private string JsonlArchive(int number) => PathFor($"telemetry.jsonl.{number}");
    private string CsvArchive(int number) => PathFor($"telemetry.csv.{number}");

    private void SeedHistory(int archives = SeededArchives, int activeJsonlBytes = 0)
    {
        File.WriteAllText(PathFor("telemetry.jsonl"), new string(' ', activeJsonlBytes) + "\n");
        File.WriteAllText(PathFor("telemetry.csv"), TelemetryRecorder.CsvHeader + "\n");
        foreach (var number in Enumerable.Range(1, archives))
        {
            File.WriteAllText(JsonlArchive(number), $"jsonl-archive-{number}\n");
            File.WriteAllText(CsvArchive(number), $"csv-archive-{number}\n");
        }
    }

    private void AssertAllArchivesIntact(int archives = SeededArchives)
    {
        foreach (var number in Enumerable.Range(1, archives))
        {
            Assert.Equal($"jsonl-archive-{number}\n", File.ReadAllText(JsonlArchive(number)));
            Assert.Equal($"csv-archive-{number}\n", File.ReadAllText(CsvArchive(number)));
        }
        // The archives above are the only extra files: no archive was renamed or duplicated.
        Assert.Equal(archives * 2 + 3, Directory.EnumerateFiles(_directory).Count());
    }

    [Theory]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":2,\"retainedArchiveCount\":3}")]
    [InlineData("{not-json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"retainedArchiveCount\":0}")]
    [InlineData("{\"maximumFileBytes\":1024}")]
    [InlineData("{\"maximumArchiveAgeDays\":999}")]
    public void CorruptSettingsLoadAsUntrustedDefaults(string content)
    {
        var store = new TelemetryRetentionSettingsStore(_directory);
        File.WriteAllText(SettingsPath, content);
        var loaded = store.Load(out var warning, out var trusted);
        Assert.Equal(Defaults, loaded);
        Assert.False(trusted);
        Assert.NotNull(warning);
        // The warning-only overload keeps its previous contract.
        Assert.Equal(loaded, store.Load(out var legacyWarning));
        Assert.Equal(warning, legacyWarning);
    }

    [Fact]
    public void OversizedSettingsFileLoadsAsUntrustedDefaults()
    {
        var store = new TelemetryRetentionSettingsStore(_directory);
        File.WriteAllText(SettingsPath, "{\"version\":1,\"pad\":\"" + new string('x', 20000) + "\"}");
        Assert.Equal(Defaults, store.Load(out var warning, out var trusted));
        Assert.False(trusted);
        Assert.NotNull(warning);
    }

    [Theory]
    [InlineData("{\"version\":2}")]
    [InlineData("{not-json")]
    [InlineData("{\"retainedArchiveCount\":0}")]
    [InlineData("")]
    public void CorruptSettingsNeverDeleteArchivesOnFirstAppend(string content)
    {
        SeedHistory();
        File.WriteAllText(SettingsPath, content);
        using var recorder = new TelemetryRecorder(_directory);
        recorder.Append(Record());
        Assert.False(recorder.RetentionTrusted);
        Assert.NotNull(recorder.RetentionWarning);
        Assert.Contains("历史保留配置无效", recorder.RetentionWarning);
        // No archive (.1 .. .20, including the old .4 and the boundary .20) is deleted or renamed.
        AssertAllArchivesIntact();
        // The append itself still works and lands in the active file only.
        Assert.Single(TelemetryHistory.Load([recorder.JsonlPath]).Records);
    }

    [Fact]
    public void CorruptSettingsProtectArchivesAcrossRepeatedAppends()
    {
        SeedHistory(archives: 6);
        File.WriteAllText(SettingsPath, "{\"version\":2,\"retainedArchiveCount\":1,\"maximumFileBytes\":1048576}");
        using var recorder = new TelemetryRecorder(_directory);
        for (var index = 0; index < 3; index++)
        {
            recorder.Append(Record(Time.AddSeconds(index)));
            Assert.False(recorder.RetentionTrusted);
            AssertAllArchivesIntact(archives: 6);
        }
    }

    [Fact]
    public void MissingSettingsFileStaysTrustedAndAppliesDefaultCleanup()
    {
        SeedHistory();
        Assert.False(File.Exists(SettingsPath));
        using var recorder = new TelemetryRecorder(_directory);
        recorder.Append(Record());
        Assert.True(recorder.RetentionTrusted);
        Assert.Null(recorder.RetentionWarning);
        // Defaults were always the intended policy here, so the existing cleanup still runs.
        foreach (var number in Enumerable.Range(1, 3))
        {
            Assert.Equal($"jsonl-archive-{number}\n", File.ReadAllText(JsonlArchive(number)));
            Assert.Equal($"csv-archive-{number}\n", File.ReadAllText(CsvArchive(number)));
        }
        Assert.False(File.Exists(JsonlArchive(4)));
        Assert.False(File.Exists(CsvArchive(4)));
        Assert.False(File.Exists(JsonlArchive(SeededArchives)));
    }

    [Fact]
    public void ValidSavedSettingsStayTrustedAndDeleteBeyondTheExplicitCount()
    {
        SeedHistory();
        new TelemetryRetentionSettingsStore(_directory).Save(new() { RetainedArchiveCount = 5, MaximumFileBytes = 1048576 });
        var store = new TelemetryRetentionSettingsStore(_directory);
        Assert.True(store.Load(out var warning, out var trusted) is { RetainedArchiveCount: 5 } && trusted);
        Assert.Null(warning);
        using var recorder = new TelemetryRecorder(_directory);
        recorder.Append(Record());
        Assert.True(recorder.RetentionTrusted);
        Assert.Null(recorder.RetentionWarning);
        foreach (var number in Enumerable.Range(1, 5))
        {
            Assert.Equal($"jsonl-archive-{number}\n", File.ReadAllText(JsonlArchive(number)));
            Assert.Equal($"csv-archive-{number}\n", File.ReadAllText(CsvArchive(number)));
        }
        Assert.False(File.Exists(JsonlArchive(6)));
        Assert.False(File.Exists(CsvArchive(SeededArchives)));
    }

    [Fact]
    public void UntrustedPolicyRefusesToRotateAndLeavesEveryFileUntouched()
    {
        SeedHistory(archives: 5, activeJsonlBytes: MaximumFileBytes);
        File.WriteAllText(SettingsPath, "{not-json");
        var activeBefore = File.ReadAllBytes(PathFor("telemetry.jsonl"));
        var csvBefore = File.ReadAllBytes(PathFor("telemetry.csv"));
        using var recorder = new TelemetryRecorder(_directory);
        var error = Assert.Throws<IOException>(() => recorder.Append(Record()));
        Assert.Contains("历史保留配置不可用", error.Message);
        Assert.Contains("重新保存保留设置", error.Message);
        Assert.False(recorder.RetentionTrusted);
        // Nothing was deleted, renamed or written: the over-cap active file is still over cap.
        Assert.Equal(activeBefore, File.ReadAllBytes(PathFor("telemetry.jsonl")));
        Assert.Equal(csvBefore, File.ReadAllBytes(PathFor("telemetry.csv")));
        AssertAllArchivesIntact(archives: 5);
        Assert.Equal(5 * 2 + 3, Directory.EnumerateFiles(_directory).Count());
    }

    [Fact]
    public void UntrustedPolicyWithoutOverCapFileKeepsWritingNormallyUntilTheCap()
    {
        SeedHistory(archives: 5, activeJsonlBytes: MaximumFileBytes - 200);
        File.WriteAllText(SettingsPath, "{\"version\":3}");
        using var recorder = new TelemetryRecorder(_directory);
        recorder.Append(Record());
        Assert.False(recorder.RetentionTrusted);
        AssertAllArchivesIntact(archives: 5);
        Assert.Equal(2, File.ReadAllLines(PathFor("telemetry.jsonl")).Length);
    }

    [Fact]
    public void TrustedSettingsRestoreRotationAndCleanupForTheSameDirectory()
    {
        SeedHistory(archives: 5, activeJsonlBytes: MaximumFileBytes);
        File.WriteAllText(SettingsPath, "{not-json");
        using (var broken = new TelemetryRecorder(_directory))
            Assert.Throws<IOException>(() => broken.Append(Record()));
        // Re-saving a valid policy (what the history window does) restores the normal behaviour.
        new TelemetryRetentionSettingsStore(_directory).Save(new() { RetainedArchiveCount = 2, MaximumFileBytes = MaximumFileBytes });
        using var recorder = new TelemetryRecorder(_directory);
        recorder.Append(Record());
        Assert.True(recorder.RetentionTrusted);
        Assert.Null(recorder.RetentionWarning);
        // Cleanup removed the archives above the explicit count, then rotation moved the full
        // active file into the two-slot ring: the documented, user-configured behaviour.
        Assert.True(new FileInfo(JsonlArchive(1)).Length > MaximumFileBytes);
        Assert.Equal("jsonl-archive-1\n", File.ReadAllText(JsonlArchive(2)));
        Assert.False(File.Exists(JsonlArchive(3)));
    }
}
