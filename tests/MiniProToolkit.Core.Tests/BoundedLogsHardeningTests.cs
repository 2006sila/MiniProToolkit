using System.Globalization;
using System.Text;
using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Guards for the bounded-log hardening: the Guardian recovery log is written through the same
/// <see cref="BoundedTextLog"/> contract as the app log, the history window reads the rotated
/// archives instead of only the live file (and says so), and one huge diagnostic message cannot
/// break the per-file event quota. Everything here uses temp directories; it never touches a real
/// ProgramData/AppData path, the service, hardware or the GUI.
/// </summary>
public sealed class BoundedLogsHardeningTests
{
    private const int DiagnosticFileQuota = 2 * 1024 * 1024;

    [Fact]
    public void ASingleHugeDiagnosticMessageStaysWithinTheEventQuotaAndIsStillValidJson()
    {
        var directory = NewDirectory("diag-huge");
        var path = Path.Combine(directory, "diagnostic-events.jsonl");
        try
        {
            var store = new DiagnosticEventStore(path);
            // ~6 MiB of UTF-8 in one call: previously this one line burst the 2 MiB file quota.
            store.Append("Error", new string('温', 2 * 1024 * 1024), "Guardian");

            Assert.True(new FileInfo(path).Length <= DiagnosticFileQuota,
                $"diagnostic file grew to {new FileInfo(path).Length} bytes");
            var line = Assert.Single(File.ReadAllLines(path));
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty("message", out var messageProperty));
            var message = messageProperty.GetString()!;
            Assert.True(Encoding.UTF8.GetByteCount(message) <= DiagnosticEventStore.MaximumMessageBytes,
                $"message kept {Encoding.UTF8.GetByteCount(message)} bytes");
            Assert.EndsWith(DiagnosticEventStore.MessageTruncationMarker, message, StringComparison.Ordinal);
            // Strict decoding fails if the cut split a multi-byte character.
            _ = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
            Assert.Equal(message, Assert.Single(store.ReadRecent()).Message);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void NormalDiagnosticMessagesAreNotRewritten()
    {
        var directory = NewDirectory("diag-normal");
        var path = Path.Combine(directory, "diagnostic-events.jsonl");
        try
        {
            var store = new DiagnosticEventStore(path);
            store.Append("Recovery", "  automatic restore requested  ", "Guardian");
            store.Append("Error", new string('x', DiagnosticEventStore.MaximumMessageBytes), "Guardian");

            var all = store.ReadRecent();
            Assert.Equal(2, all.Count);
            Assert.Equal("automatic restore requested", all[0].Message);
            Assert.Equal(DiagnosticEventStore.MaximumMessageBytes, Encoding.UTF8.GetByteCount(all[1].Message));
            Assert.DoesNotContain(DiagnosticEventStore.MessageTruncationMarker, all[1].Message, StringComparison.Ordinal);
            Assert.Equal(DiagnosticEventStore.MaximumMessageBytes,
                Encoding.UTF8.GetByteCount(DiagnosticEventStore.BoundMessage(new string('x', 4 * DiagnosticEventStore.MaximumMessageBytes))));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ManyCappedDiagnosticMessagesStayBoundedAcrossRotationAndReadRecent()
    {
        var directory = NewDirectory("diag-rotate");
        var path = Path.Combine(directory, "diagnostic-events.jsonl");
        try
        {
            var store = new DiagnosticEventStore(path);
            var message = new string('温', 3 * DiagnosticEventStore.MaximumMessageBytes);
            for (var index = 0; index < 400; index++) store.Append("Error", message, "Guardian");

            // 400 capped entries (~8 KiB each) rotate the 2 MiB live file several times.
            Assert.True(File.Exists(path + ".1"));
            Assert.False(File.Exists(path + ".5"));
            foreach (var file in Directory.GetFiles(directory, "diagnostic-events.jsonl*"))
            {
                // Rotation checks the size before appending, so a live file can exceed by one entry.
                Assert.True(new FileInfo(file).Length <= DiagnosticFileQuota + 64 * 1024, file);
                foreach (var line in File.ReadLines(file))
                    Assert.True(JsonSerializer.Deserialize<DiagnosticEventRecord>(line,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Message.Length > 0);
            }
            var recent = store.ReadRecent(200);
            Assert.Equal(200, recent.Count);
            Assert.All(recent, item =>
                Assert.True(Encoding.UTF8.GetByteCount(item.Message) <= DiagnosticEventStore.MaximumMessageBytes));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RepeatedGuardianRecoveryFailuresStayInsideThreeArchives()
    {
        var directory = NewDirectory("guardian-log");
        var path = Path.Combine(directory, "guardian.log");
        try
        {
            // Mirrors GuardianWorker's configuration and line shape without starting the service.
            var log = new BoundedTextLog(path,
                BoundedTextLog.DefaultMaximumFileBytes,
                BoundedTextLog.DefaultRetainedFiles,
                BoundedTextLog.DefaultMaximumEntryBytes);
            var reason = new string('异', 40 * 1024);
            for (var index = 0; index < 300; index++)
                log.Append($"{DateTimeOffset.UtcNow:O} guardian poll failed: {reason}");

            Assert.True(File.Exists(path + ".1"));
            Assert.True(File.Exists(path + ".3"));
            Assert.False(File.Exists(path + ".4"));
            var files = new[] { path, path + ".1", path + ".2", path + ".3" };
            long total = 0;
            foreach (var file in files.Where(File.Exists))
            {
                var info = new FileInfo(file);
                Assert.True(info.Length <= BoundedTextLog.DefaultMaximumFileBytes, file);
                total += info.Length;
                foreach (var line in File.ReadLines(file))
                {
                    Assert.True(Encoding.UTF8.GetByteCount(line) <= BoundedTextLog.DefaultMaximumEntryBytes, file);
                    var separator = line.IndexOf(' ');
                    Assert.True(separator > 0);
                    Assert.True(DateTimeOffset.TryParse(line[..separator], CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out _), line[..Math.Min(40, line.Length)]);
                    Assert.Contains("guardian poll failed", line, StringComparison.Ordinal);
                    Assert.Contains("已截断", line, StringComparison.Ordinal);
                }
            }
            Assert.True(total <= 4L * BoundedTextLog.DefaultMaximumFileBytes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void GuardianWorkerWritesRecoveryLogThroughBoundedTextLog()
    {
        var source = File.ReadAllText(FindSource("MiniProToolkit.Guardian", "GuardianWorker.cs"));
        Assert.Contains("private readonly BoundedTextLog _recoveryLog;", source, StringComparison.Ordinal);
        Assert.Contains("new BoundedTextLog(", source, StringComparison.Ordinal);
        Assert.Contains("BoundedTextLog.DefaultMaximumFileBytes", source, StringComparison.Ordinal);
        Assert.Contains("BoundedTextLog.DefaultRetainedFiles", source, StringComparison.Ordinal);
        Assert.Contains("BoundedTextLog.DefaultMaximumEntryBytes", source, StringComparison.Ordinal);
        Assert.Contains("_recoveryLog.Append($\"{DateTimeOffset.UtcNow:O} {message}: {exception.GetBaseException().Message}\")",
            source, StringComparison.Ordinal);
        // The unbounded append is gone and the swallow that keeps the poll loop alive stays.
        Assert.DoesNotContain("File.AppendAllText(_options.LogPath", source, StringComparison.Ordinal);
        Assert.Contains("// The service logger remains the fallback", source, StringComparison.Ordinal);
        // The log path/directory is unchanged: still under the configured (ProgramData) LogPath.
        Assert.DoesNotContain("guardian.log", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryWindowReadsRotatedArchivesOldestFirstWithBoundedTailsAndSaysSo()
    {
        var source = File.ReadAllText(FindSource("MiniProToolkit.App", "HistoryWindow.xaml.cs"));
        var projection = "Enumerable.Range(1, LogRetainedArchives).Reverse()";
        var appendLive = ".Append(path)";
        var projectionIndex = source.IndexOf(projection, StringComparison.Ordinal);
        var liveIndex = source.IndexOf(appendLive, projectionIndex, StringComparison.Ordinal);
        Assert.True(projectionIndex >= 0, "archive projection missing");
        Assert.True(liveIndex > projectionIndex, "live file must be read after the archives (oldest first)");

        Assert.Contains("BoundedTextLog.DefaultRetainedFiles", source, StringComparison.Ordinal);
        Assert.Contains("TelemetryHistory.DefaultLogTailBytes", source, StringComparison.Ordinal);
        Assert.Contains("TelemetryHistory.DefaultLogTailEntries", source, StringComparison.Ordinal);
        Assert.Contains("File.Exists(file)", source, StringComparison.Ordinal);
        Assert.Contains("TelemetryHistory.ReadLog(file, log.Source", source, StringComparison.Ordinal);
        // ReadLog's own truncation warning is forwarded to the StatusText warning list.
        Assert.Contains("message => warnings.Add(", source, StringComparison.Ordinal);
        Assert.Contains("已轮转的日志归档按保留数量有限，最早内容可能已淘汰", source, StringComparison.Ordinal);
        Assert.Contains("日志读取总预算", source, StringComparison.Ordinal);
        // Both sources are still read, including the ProgramData guardian log the service rotates.
        Assert.Contains("guardian.log", source, StringComparison.Ordinal);
        Assert.Contains("AppLog.Path", source, StringComparison.Ordinal);
    }

    private static string NewDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitBoundedLogs", label,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FindSource(string project, string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests",
                "MiniProToolkit.Core.Tests.csproj");
            var candidate = Path.Combine(directory.FullName, "src", project, fileName);
            if (File.Exists(marker) && File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate {project}/{fileName} above the test directory.");
    }
}
