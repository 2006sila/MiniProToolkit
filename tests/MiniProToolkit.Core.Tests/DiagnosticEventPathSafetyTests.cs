using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;
using Xunit.Abstractions;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Guards for the diagnostic journal path/size contract: the fully serialized line (not only the
/// message) drives rotation so the live file stays inside its quota, category/source are bounded
/// instead of being silently rewritten, and reparse points (junction/symlink) are refused before any
/// write or rename and never read through. Everything runs in temp directories; it never touches a
/// real AppData/ProgramData path, the service, hardware or the GUI. No elevation is attempted:
/// a file-symlink case that needs SeCreateSymbolicLinkPrivilege is reported as skipped on hosts that
/// refuse it, while the directory-junction cases run with normal user rights.
/// </summary>
public sealed class DiagnosticEventPathSafetyTests
{
    private const long DiagnosticFileQuota = 2 * 1024 * 1024;
    private readonly ITestOutputHelper _output;

    public DiagnosticEventPathSafetyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OversizedCategoryAndSourceAreRejectedInsteadOfBeingRewritten()
    {
        var directory = NewDirectory("diag-bounds");
        var path = Path.Combine(directory, "diagnostic-events.jsonl");
        try
        {
            var store = new DiagnosticEventStore(path);
            var maximumCategory = new string('c', DiagnosticEventStore.MaximumCategoryBytes);
            var maximumSource = new string('s', DiagnosticEventStore.MaximumSourceBytes);
            store.Append(maximumCategory, "boundary event", maximumSource);

            var item = Assert.Single(store.ReadRecent());
            Assert.Equal(maximumCategory, item.Category);
            Assert.Equal(maximumSource, item.Source);

            Assert.Throws<ArgumentException>(() =>
                store.Append(new string('c', DiagnosticEventStore.MaximumCategoryBytes + 1), "too long"));
            // Multi-byte characters are counted as UTF-8 bytes, not chars.
            Assert.Throws<ArgumentException>(() =>
                store.Append(new string('类', DiagnosticEventStore.MaximumCategoryBytes / 3 + 1), "too long"));
            Assert.Throws<ArgumentException>(() =>
                store.Append("Error", "too long", new string('源', DiagnosticEventStore.MaximumSourceBytes / 3 + 1)));
            Assert.Throws<ArgumentNullException>(() => store.Append("Error", "null source", null!));

            // Rejected appends wrote nothing: still exactly the one boundary event.
            Assert.Single(File.ReadAllLines(path));
            Assert.Single(store.ReadRecent());
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void SerializedLineSizeDrivesRotationSoTheLiveFileStaysInsideTheQuota()
    {
        var directory = NewDirectory("diag-line-budget");
        var path = Path.Combine(directory, "diagnostic-events.jsonl");
        try
        {
            var store = new DiagnosticEventStore(path);
            // 8 KiB of control characters: the worst case for JSON escaping (6 bytes per input byte),
            // so the serialized line is ~48 KiB even though the message is at the documented cap.
            var escaped = new string('\u0001', DiagnosticEventStore.MaximumMessageBytes);
            store.Append("Error", escaped, "Guardian");
            var singleLine = Assert.Single(File.ReadAllLines(path));
            Assert.True(Encoding.UTF8.GetByteCount(singleLine) + Environment.NewLine.Length <= DiagnosticEventStore.MaximumLineBytes,
                $"serialized line kept {Encoding.UTF8.GetByteCount(singleLine)} bytes");

            // Capped messages (~8 KiB each) rotate the live file several times.
            var message = new string('温', 3 * DiagnosticEventStore.MaximumMessageBytes);
            for (var index = 0; index < 400; index++) store.Append("Error", message, "Guardian");

            Assert.True(File.Exists(path + ".1"));
            Assert.False(File.Exists(path + ".5"));
            foreach (var file in Directory.GetFiles(directory, "diagnostic-events.jsonl*"))
            {
                // No "quota + one entry" slack any more: rotation uses the real appended byte count.
                Assert.True(new FileInfo(file).Length <= DiagnosticFileQuota,
                    $"{file} grew to {new FileInfo(file).Length} bytes");
                foreach (var line in File.ReadLines(file))
                    Assert.True(Encoding.UTF8.GetByteCount(line) <= DiagnosticEventStore.MaximumLineBytes, file);
            }
            Assert.All(store.ReadRecent(200), item =>
                Assert.True(Encoding.UTF8.GetByteCount(item.Message) <= DiagnosticEventStore.MaximumMessageBytes));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void LegacyOversizedLiveFileIsArchivedWholeAndNeverTrimmed()
    {
        var directory = NewDirectory("diag-legacy-oversize");
        var path = Path.Combine(directory, "diagnostic-events.jsonl");
        try
        {
            // A live file written by an older build that could exceed the quota (no cap applied).
            var legacyLine = JsonSerializer.Serialize(new DiagnosticEventRecord(DateTimeOffset.UnixEpoch,
                "Error", new string('x', 4000), "Guardian"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var builder = new StringBuilder();
            while (builder.Length < DiagnosticFileQuota + 1024 * 1024) builder.Append(legacyLine).Append(Environment.NewLine);
            var legacyBytes = new UTF8Encoding(false).GetBytes(builder.ToString());
            File.WriteAllBytes(path, legacyBytes);
            Assert.True(legacyBytes.Length > DiagnosticFileQuota);

            new DiagnosticEventStore(path).Append("Recovery", "after legacy oversize", "Guardian");

            // The oversized file is moved to .1 byte-for-byte (archived, never truncated or deleted).
            Assert.True(File.Exists(path + ".1"));
            Assert.Equal(legacyBytes, File.ReadAllBytes(path + ".1"));
            Assert.True(new FileInfo(path).Length <= DiagnosticFileQuota);
            var live = Assert.Single(File.ReadAllLines(path));
            Assert.Contains("after legacy oversize", live, StringComparison.Ordinal);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void JournalDirectoryThatIsAJunctionIsRefusedAndNotWrittenThrough()
    {
        var root = NewDirectory("diag-junction-dir");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "journal");
        Directory.CreateDirectory(target);
        try
        {
            if (!TryCreateJunction(link, target))
            {
                _output.WriteLine("skipped: this host refused to create a directory junction (no elevation attempted).");
                return;
            }
            var store = new DiagnosticEventStore(Path.Combine(link, "diagnostic-events.jsonl"));
            Assert.Throws<IOException>(() => store.Append("Error", "must not be written"));
            Assert.Empty(Directory.GetFiles(target));
            // Read side: an unsafe directory is not read through and does not throw to the GUI.
            Assert.Empty(store.ReadRecent());
        }
        finally
        {
            RemoveJunction(link);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ReparsePointArchiveBlocksRotationBeforeAnyRename()
    {
        var root = NewDirectory("diag-junction-archive");
        var target = Path.Combine(root, "target");
        var journal = Path.Combine(root, "journal");
        var path = Path.Combine(journal, "diagnostic-events.jsonl");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(journal);
        try
        {
            var live = JsonSerializer.Serialize(new DiagnosticEventRecord(DateTimeOffset.UnixEpoch,
                "Error", new string('a', 4000), "Guardian"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var builder = new StringBuilder();
            while (builder.Length < DiagnosticFileQuota + 1024 * 1024) builder.Append(live).Append(Environment.NewLine);
            var legacyBytes = new UTF8Encoding(false).GetBytes(builder.ToString());
            File.WriteAllBytes(path, legacyBytes);
            // Rotating this oversized live file is unavoidable, so the linked archive would be the
            // first rename destination if the guard were missing.
            File.WriteAllText(path + ".2", "archive-two" + Environment.NewLine);
            if (!TryCreateJunction(path + ".1", target))
            {
                _output.WriteLine("skipped: this host refused to create a directory junction (no elevation attempted).");
                return;
            }

            var store = new DiagnosticEventStore(path);
            Assert.Throws<IOException>(() => store.Append("Recovery", "must not rotate", "Guardian"));

            // No partial rotation: the live file and the untouched archive keep their content and the
            // linked archive name was never used as a rename destination.
            Assert.Equal(legacyBytes, File.ReadAllBytes(path));
            Assert.Equal("archive-two" + Environment.NewLine, File.ReadAllText(path + ".2"));
            Assert.False(File.Exists(path + ".3"));
            Assert.True(DiagnosticEventStore.IsReparsePoint(path + ".1"));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            RemoveJunction(path + ".1");
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void LiveFilePathThatIsAReparsePointIsRefusedWithoutWritingThrough()
    {
        var root = NewDirectory("diag-junction-live");
        var journal = Path.Combine(root, "journal");
        var target = Path.Combine(root, "target");
        var path = Path.Combine(journal, "diagnostic-events.jsonl");
        Directory.CreateDirectory(journal);
        Directory.CreateDirectory(target);
        try
        {
            // Same reparse-point check as a file symlink, but creatable with normal user rights
            // (the symlink variant of this case is covered by the test below where the host allows it).
            if (!TryCreateJunction(path, target))
            {
                _output.WriteLine("skipped: this host refused to create a directory junction (no elevation attempted).");
                return;
            }

            var store = new DiagnosticEventStore(path);
            Assert.Empty(store.ReadRecent());
            Assert.Throws<IOException>(() => store.Append("Error", "must not be written"));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            RemoveJunction(path);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void LinkedLiveFileIsNotReadThroughAndIsRefusedForWriting()
    {
        var root = NewDirectory("diag-file-symlink");
        var journal = Path.Combine(root, "journal");
        var outside = Path.Combine(root, "outside.jsonl");
        var path = Path.Combine(journal, "diagnostic-events.jsonl");
        Directory.CreateDirectory(journal);
        try
        {
            File.WriteAllText(outside, "outside" + Environment.NewLine);
            try { File.CreateSymbolicLink(path, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Needs SeCreateSymbolicLinkPrivilege; never elevate, just report the skip for this case.
                _output.WriteLine("skipped: this host refused a file symlink (" + ex.GetType().Name + "); no elevation attempted.");
                return;
            }

            var store = new DiagnosticEventStore(path);
            Assert.Empty(store.ReadRecent());
            Assert.Throws<IOException>(() => store.Append("Error", "must not follow the link"));
            Assert.Equal("outside" + Environment.NewLine, File.ReadAllText(outside));
        }
        finally { DeleteDirectory(root); }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo);
        if (process is null) return false;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000) || process.ExitCode != 0) return false;
        return Directory.Exists(linkPath) && DiagnosticEventStore.IsReparsePoint(linkPath);
    }

    private static void RemoveJunction(string linkPath)
    {
        // Removes the link only; the target directory is deleted separately by the caller.
        try { if (Directory.Exists(linkPath)) Directory.Delete(linkPath, false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string NewDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitDiagnosticPath", label,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
