using System.Text.Json;
using System.Text.Json.Nodes;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class ProfileAutomationHistoryTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "automation-history-tests-" + Guid.NewGuid().ToString("N"));
    private string Path => System.IO.Path.Combine(_directory, "automation-executions.jsonl");
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);
    private const string Secret = "--token=secret-7192";
    private const string SecretPath = @"C:\private\secret\player.exe";

    public ProfileAutomationHistoryTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private bool Record(ProfileAutomationHistory? history = null, DateTimeOffset? timestamp = null,
        TimeSpan? duration = null) => (history ?? new(Path)).Record("  " + Secret + "  ", SecretPath,
            ProfileAutomationExecutionOutcome.Applied, ProfileAutomationExecutionReason.Applied,
            duration ?? TimeSpan.FromMilliseconds(27), timestamp ?? Now);

    private JsonObject Sample()
    {
        Assert.True(Record());
        var entry = JsonNode.Parse(File.ReadLines(Path).First())!.AsObject();
        File.Delete(Path);
        return entry;
    }

    [Fact]
    public void AnonymousIdsNormalizeNamesAndPersistNoNamesOrProcessConditions()
    {
        Assert.Equal(ProfileAutomationHistory.AnonymousId(" Alpha "), ProfileAutomationHistory.AnonymousId("ALPHA"));
        Assert.Matches("^[a-f0-9]{16}$", ProfileAutomationHistory.AnonymousId(Secret));
        Assert.True(Record());
        var text = File.ReadAllText(Path);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain("player.exe", text);
        Assert.DoesNotContain(SecretPath, text);
        var record = Assert.Single(new ProfileAutomationHistory(Path).ReadRecent());
        Assert.Equal(ProfileAutomationHistory.AnonymousId(Secret), record.RuleId);
        Assert.Equal(ProfileAutomationHistory.AnonymousId(SecretPath), record.ProfileId);
        Assert.Equal(27, record.DurationMilliseconds);
        Assert.Equal(Now, record.TimestampUtc);
        Assert.Equal(TimeSpan.Zero, record.TimestampUtc.Offset);
        var fields = JsonNode.Parse(text)!.AsObject().Select(pair => pair.Key).Order().ToArray();
        Assert.Equal(new[] { "durationMilliseconds", "outcome", "profileId", "reason", "ruleId", "timestampUtc", "version" }, fields);
    }

    [Theory]
    [InlineData(ProfileAutomationExecutionReason.MissingProfile)]
    [InlineData(ProfileAutomationExecutionReason.Rejected)]
    [InlineData(ProfileAutomationExecutionReason.Exception)]
    public void ClosedRejectedReasonsRoundTrip(ProfileAutomationExecutionReason reason)
    {
        Assert.True(new ProfileAutomationHistory(Path).Record(Secret, SecretPath,
            ProfileAutomationExecutionOutcome.Rejected, reason, TimeSpan.Zero, Now));
        var record = Assert.Single(new ProfileAutomationHistory(Path).ReadRecent());
        Assert.Equal(ProfileAutomationExecutionOutcome.Rejected, record.Outcome);
        Assert.Equal(reason, record.Reason);
    }

    [Fact]
    public void RestartReadsActiveAndBackupsSortedByUtcAndBoundsDuration()
    {
        Assert.True(Record(timestamp: Now.AddMinutes(3), duration: TimeSpan.FromDays(10)));
        File.Move(Path, Path + ".2");
        Assert.True(Record(timestamp: Now.AddMinutes(1), duration: TimeSpan.FromSeconds(-1)));
        File.Move(Path, Path + ".1");
        Assert.True(Record(timestamp: Now.AddMinutes(2).ToOffset(TimeSpan.FromHours(8))));
        var records = new ProfileAutomationHistory(Path).ReadRecent();
        Assert.Equal(new[] { Now.AddMinutes(3), Now.AddMinutes(2), Now.AddMinutes(1) }, records.Select(record => record.TimestampUtc));
        Assert.Equal(ProfileAutomationHistory.MaxDurationMilliseconds, records[0].DurationMilliseconds);
        Assert.Equal(0, records[2].DurationMilliseconds);
        Assert.Equal(2, new ProfileAutomationHistory(Path).ReadRecent(2).Count);
    }

    [Fact]
    public void RotationBoundsFilesAndKeepsOnlyThreeBackups()
    {
        var history = new ProfileAutomationHistory(Path);
        for (var i = 0; i < 6; i++)
        {
            // Exercise the real production cap without writing thousands of journal records per rotation.
            File.WriteAllBytes(Path, new byte[ProfileAutomationHistory.MaxFileBytes]);
            Assert.True(Record(history, Now.AddMinutes(i)));
            Assert.InRange(new FileInfo(Path).Length, 1, ProfileAutomationHistory.MaxFileBytes);
        }
        Assert.Equal(4, Directory.GetFiles(_directory).Length);
        for (var i = 1; i <= ProfileAutomationHistory.MaxBackups; i++)
        {
            Assert.True(File.Exists(Path + "." + i));
            Assert.InRange(new FileInfo(Path + "." + i).Length, 1, ProfileAutomationHistory.MaxFileBytes);
        }
        Assert.False(File.Exists(Path + ".4"));
        Assert.Single(history.ReadRecent());
    }

    [Fact]
    public async Task ConcurrentInstancesAppendWholeRecordsWithoutLoss()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var history = new ProfileAutomationHistory(Path);
            for (var index = 0; index < 40; index++)
                Assert.True(Record(history, Now.AddMilliseconds(worker * 40 + index)));
        })));
        Assert.Equal(320, File.ReadLines(Path).Count());
        var records = new ProfileAutomationHistory(Path).ReadRecent();
        Assert.Equal(320, records.Count);
        Assert.Equal(320, records.Select(record => record.TimestampUtc).Distinct().Count());
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("version", "0")]
    [InlineData("outcome", "\"FutureOutcome\"")]
    [InlineData("outcome", "42")]
    [InlineData("outcome", "\"Applied, Rejected\"")]
    [InlineData("reason", "\"Applied, Exception\"")]
    [InlineData("reason", "\"FutureReason\"")]
    [InlineData("reason", "0")]
    [InlineData("reason", "\"Rejected\"")]
    [InlineData("ruleId", "\"=formula-secret\"")]
    [InlineData("profileId", "\"C:\\\\private\"")]
    [InlineData("durationMilliseconds", "-1")]
    [InlineData("durationMilliseconds", "86400001")]
    [InlineData("timestampUtc", "\"0001-01-01T00:00:00+00:00\"")]
    public void UnsupportedOrUnsafeRowsAreIgnored(string field, string json)
    {
        var sample = Sample();
        sample[field] = JsonNode.Parse(json);
        File.WriteAllText(Path, sample.ToJsonString() + "\n");
        Assert.Empty(new ProfileAutomationHistory(Path).ReadRecent());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("timestampUtc")]
    [InlineData("ruleId")]
    [InlineData("profileId")]
    [InlineData("outcome")]
    [InlineData("reason")]
    [InlineData("durationMilliseconds")]
    public void MissingRequiredFieldsAreIgnored(string field)
    {
        var sample = Sample();
        sample.Remove(field);
        File.WriteAllText(Path, sample.ToJsonString() + "\n");
        Assert.Empty(new ProfileAutomationHistory(Path).ReadRecent());
    }

    [Fact]
    public void CorruptOversizedAndPartialRowsDoNotPreventValidReading()
    {
        var sample = Sample().ToJsonString();
        File.WriteAllText(Path, "bad json\nnull\n[]\n" + new string('x', 5000) + "\n" + sample + "\n{\"version\":");
        Assert.Single(new ProfileAutomationHistory(Path).ReadRecent());
        File.WriteAllText(Path, sample + "\r\n" + sample); // CRLF and no terminal newline
        Assert.Equal(2, new ProfileAutomationHistory(Path).ReadRecent().Count);
    }

    [Fact]
    public void UnknownFieldsNeverSurviveReExportAndCsvRejectsUnsafeIds()
    {
        var sample = Sample();
        sample["commandLine"] = Secret;
        sample["processPath"] = SecretPath;
        sample["rawRuleName"] = Secret;
        sample["nested"] = new JsonObject { ["originalName"] = SecretPath };
        File.WriteAllText(Path, sample.ToJsonString() + "\n");
        var record = Assert.Single(new ProfileAutomationHistory(Path).ReadRecent());
        var csv = ProfileAutomationHistory.ExportCsv([record]);
        var json = JsonSerializer.Serialize(record);
        foreach (var text in new[] { csv, json })
        {
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("private", text);
            Assert.DoesNotContain("commandLine", text);
            Assert.DoesNotContain("rawRuleName", text);
            Assert.DoesNotContain("nested", text);
        }
        Assert.Contains(record.RuleId, csv);
        var rejected = ProfileAutomationHistory.ExportCsv([record with { RuleId = "=SECRET-FORMULA!" }]);
        Assert.DoesNotContain("SECRET", rejected);
        Assert.Single(rejected.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void ReadExaminesOnlyLastFiveHundredPhysicalLinesAndCapsRequestedRows()
    {
        var sample = Sample();
        var old = sample.DeepClone().AsObject();
        old["timestampUtc"] = Now.AddDays(50).ToString("O"); // Would sort first if scanned.
        var line = sample.ToJsonString();
        File.WriteAllText(Path, old.ToJsonString() + "\n" + string.Concat(Enumerable.Repeat(line + "\n", 600)));
        var history = new ProfileAutomationHistory(Path);
        var records = history.ReadRecent(int.MaxValue);
        Assert.Equal(500, records.Count);
        Assert.All(records, record => Assert.Equal(Now, record.TimestampUtc));
        File.AppendAllText(Path, string.Concat(Enumerable.Repeat("corrupt\n", 500)));
        Assert.Empty(history.ReadRecent());
        Assert.Empty(history.ReadRecent(-1));
    }

    [Fact]
    public void ReadTailDoesNotScanAnUnboundedExternallyGrownFile()
    {
        var line = Sample().ToJsonString();
        File.WriteAllText(Path, line + "\n" + new string('x', ProfileAutomationHistory.MaxFileBytes + 1));
        Assert.Empty(new ProfileAutomationHistory(Path).ReadRecent());
    }

    [Fact]
    public void JournalUsesSharedHandlesAndStorageFailureIsBestEffort()
    {
        Assert.True(Record());
        using (var reader = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Assert.True(Record());
        var blockedPath = System.IO.Path.Combine(_directory, "a-directory");
        Directory.CreateDirectory(blockedPath);
        Assert.False(Record(new ProfileAutomationHistory(blockedPath)));
        Assert.False(new ProfileAutomationHistory(Path).Record(Secret, SecretPath,
            (ProfileAutomationExecutionOutcome)999, ProfileAutomationExecutionReason.Applied, TimeSpan.Zero));
    }

    [Fact]
    public void CancellationAndMissingFilesAreSafe()
    {
        var history = new ProfileAutomationHistory(Path);
        Assert.Empty(history.ReadRecent());
        Assert.True(Record(history));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => history.ReadRecent(200, cts.Token));
    }
}
