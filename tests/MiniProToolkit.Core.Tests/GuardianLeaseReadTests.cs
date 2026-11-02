using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Guards for the typed lease read: "there is no lease" (Missing) and "the lease could not be read"
/// (Unreadable) must stay distinguishable, because only the first one is a decision the recovery
/// coordinator may act on. The unreadable reasons are a fixed, bounded set that never leaks the
/// lease path or the file content. Everything runs in a throw-away temp directory: no hardware, no
/// EC, no GUI, no service, no real ProgramData/APPDATA.
/// </summary>
public sealed class GuardianLeaseReadTests
{
    // ---- Typed outcomes ----

    [Fact]
    public void MissingLeaseIsTypedMissingAndCreatesNothing()
    {
        // Directory absent and file absent are both "no lease", never "unreadable".
        var missingDirectory = NewTempPath();
        var store = new GuardianLeaseStore(Path.Combine(missingDirectory, "lease.json"));
        var result = store.ReadResult();

        Assert.Equal(GuardianLeaseReadStatus.Missing, result.Status);
        Assert.Null(result.Lease);
        Assert.False(result.IsUnreadable);
        Assert.Null(store.Read());
        Assert.False(Directory.Exists(missingDirectory));

        var (directory, path) = CreateLeaseDirectory();
        try
        {
            var presentDirectory = new GuardianLeaseStore(path).ReadResult();
            Assert.Equal(GuardianLeaseReadStatus.Missing, presentDirectory.Status);
            Assert.Null(presentDirectory.Lease);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ValidLeaseIsTypedSuccess()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "fan-curve", now, now.AddMinutes(-5));

            var result = store.ReadResult();

            Assert.Equal(GuardianLeaseReadStatus.Success, result.Status);
            Assert.True(result.IsSuccess);
            Assert.False(result.IsUnreadable);
            Assert.NotNull(result.Lease);
            Assert.Equal(1234, result.Lease!.ProcessId);
            Assert.Equal("fan-curve", result.Lease.ControlMode);
            // Read() stays source-compatible and reports the same record.
            Assert.Equal(result.Lease, store.Read());
        }
        finally
        {
            store.Clear();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ZeroByteLeaseIsUnreadableNotMissing()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        try
        {
            File.WriteAllText(path, string.Empty);

            var result = store.ReadResult();

            Assert.Equal(GuardianLeaseReadStatus.Unreadable, result.Status);
            Assert.Equal(GuardianLeaseReadResult.EmptyReason, result.Reason);
            Assert.Null(result.Lease);
            // Fail closed for the legacy API and for the locked renewal path.
            Assert.Null(store.Read());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void MalformedJsonIsUnreadableNotMissing()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        try
        {
            File.WriteAllText(path, "{ \"processId\": 1234, \"controlMode\": ");

            var result = store.ReadResult();

            Assert.Equal(GuardianLeaseReadStatus.Unreadable, result.Status);
            Assert.Equal(GuardianLeaseReadResult.InvalidJsonReason, result.Reason);
            Assert.Null(result.Lease);
            Assert.Null(store.Read());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("[{\"processId\":1234}]")]
    [InlineData("42")]
    [InlineData("\"fan-curve\"")]
    [InlineData("null")]
    public void NonObjectJsonIsUnreadableForANonObjectReason(string content)
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        try
        {
            File.WriteAllText(path, content);

            var result = store.ReadResult();

            Assert.Equal(GuardianLeaseReadStatus.Unreadable, result.Status);
            Assert.Equal(GuardianLeaseReadResult.NotAnObjectReason, result.Reason);
            Assert.Null(result.Lease);
            // A non-object root is reported differently from malformed JSON.
            Assert.NotEqual(GuardianLeaseReadResult.InvalidJsonReason, result.Reason);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"processId\":0,\"controlMode\":\"fan-curve\",\"heartbeatUtc\":\"2026-11-02T10:00:00+00:00\"}")]
    [InlineData("{\"processId\":1234,\"controlMode\":\"\",\"heartbeatUtc\":\"2026-11-02T10:00:00+00:00\"}")]
    [InlineData("{\"processId\":1234,\"controlMode\":\"fan-curve\"}")]
    public void ObjectWithoutAUsableOwnerIdentityIsUnreadable(string content)
    {
        // An unknown owner must never look like a readable lease: Guardian would otherwise evaluate
        // (and potentially recover) an identity it cannot see.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        try
        {
            File.WriteAllText(path, content);

            var result = store.ReadResult();

            Assert.Equal(GuardianLeaseReadStatus.Unreadable, result.Status);
            Assert.Equal(GuardianLeaseReadResult.NotALeaseReason, result.Reason);
            Assert.Null(result.Lease);
            Assert.Null(store.Read());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ShareNoneLockedLeaseIsUnreadableNotMissing()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "fan-curve", now, now.AddMinutes(-5));

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = store.ReadResult();

                Assert.Equal(GuardianLeaseReadStatus.Unreadable, result.Status);
                Assert.Equal(GuardianLeaseReadResult.IoReason, result.Reason);
                Assert.Null(result.Lease);
                // Fail closed while the record cannot be inspected.
                Assert.Null(store.Read());
                Assert.False(store.TryRenewOwnedFresh(1234, now.AddMinutes(-5), "fan-curve", () => now,
                    TimeSpan.FromSeconds(8)));
            }

            // The same store reads normally again once the lock is released.
            Assert.Equal(GuardianLeaseReadStatus.Success, store.ReadResult().Status);
        }
        finally
        {
            store.Clear();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void UnreadableReasonsNeverLeakThePathOrTheContent()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        const string secret = "TOP-SECRET-LEASE-BODY";
        try
        {
            foreach (var content in new[] { string.Empty, $"{{ {secret} ", $"[\"{secret}\"]", $"\"{secret}\"" })
            {
                File.WriteAllText(path, content);
                var result = store.ReadResult();
                Assert.Equal(GuardianLeaseReadStatus.Unreadable, result.Status);
                Assert.DoesNotContain(secret, result.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain(directory, result.Reason, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("MiniProToolkit", result.Reason, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void LockedRenewalStaysFailClosedOnAnUnreadableRecord()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var now = DateTimeOffset.UtcNow;
        try
        {
            const string corrupt = "{ \"processId\": 1234 ";
            File.WriteAllText(path, corrupt);

            Assert.False(store.TryRenewOwnedFresh(1234, now.AddMinutes(-5), "fan-curve", () => now,
                TimeSpan.FromSeconds(8)));

            // Nothing was written and nothing was invented.
            Assert.Equal(corrupt, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void MissingIsDecidedWithoutAFileExistsPrecheck()
    {
        // File.Exists swallows permission errors and would report "no lease" for an unreadable file.
        var core = ReadSource("MiniProToolkit.Core", "GuardianLease.cs");
        var start = core.IndexOf("public GuardianLeaseReadResult ReadResult()", StringComparison.Ordinal);
        Assert.True(start >= 0, "the store must expose a typed ReadResult");
        var end = core.IndexOf("public void Clear()", StringComparison.Ordinal);
        Assert.True(end > start, "ReadResult() must stay a self-contained method");
        var body = core[start..end];

        // Read() is kept unchanged for callers that only need the record (App reads stay
        // fail-closed) and is the single nullable projection of the typed result.
        Assert.Contains("public GuardianLease? Read() => ReadCore();", core, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", body, StringComparison.Ordinal);
        Assert.Contains("FileMode.Open", body, StringComparison.Ordinal);
        Assert.Contains("FileNotFoundException", body, StringComparison.Ordinal);
        Assert.Contains("DirectoryNotFoundException", body, StringComparison.Ordinal);
    }

    // ---- Bounded warning throttle shared with Guardian ----

    [Fact]
    public void ThrottleReportsTheFirstFailureThenAtMostOncePerWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new GuardianLeaseReadWarningThrottle();

        Assert.Equal(TimeSpan.FromSeconds(30), throttle.RepeatInterval);
        Assert.False(throttle.IsFaulted);
        Assert.True(throttle.ShouldReport(GuardianLeaseReadResult.IoReason, now));
        Assert.True(throttle.IsFaulted);

        // A 2 s poll would log 15 times before the window elapses; all of those are suppressed.
        for (var seconds = 2; seconds < 30; seconds += 2)
            Assert.False(throttle.ShouldReport(GuardianLeaseReadResult.IoReason, now.AddSeconds(seconds)));

        Assert.True(throttle.ShouldReport(GuardianLeaseReadResult.IoReason, now.AddSeconds(30)));
        // A clock that jumps backwards suppresses entries instead of emitting a burst.
        Assert.False(throttle.ShouldReport(GuardianLeaseReadResult.IoReason, now.AddSeconds(-60)));
    }

    [Fact]
    public void ThrottleReportsAChangedReasonImmediately()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new GuardianLeaseReadWarningThrottle();

        Assert.True(throttle.ShouldReport(GuardianLeaseReadResult.IoReason, now));
        Assert.True(throttle.ShouldReport(GuardianLeaseReadResult.InvalidJsonReason, now.AddSeconds(2)));
        Assert.False(throttle.ShouldReport(GuardianLeaseReadResult.InvalidJsonReason, now.AddSeconds(4)));
    }

    [Fact]
    public void ThrottleClearsAfterAReadableLeaseAndReportsTheRecoveryOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new GuardianLeaseReadWarningThrottle();

        Assert.True(throttle.ShouldReport(GuardianLeaseReadResult.IoReason, now));
        // A readable poll clears the state...
        Assert.True(throttle.Clear());
        Assert.False(throttle.IsFaulted);
        // ...and the recovery is reported only once, not on every healthy poll.
        Assert.False(throttle.Clear());
        // The next failure is reported immediately, without waiting for the window.
        Assert.True(throttle.ShouldReport(GuardianLeaseReadResult.IoReason, now.AddSeconds(2)));
    }

    // ---- Guardian wiring ----

    [Fact]
    public void GuardianCheckLeaseUsesTwoTypedReadsAndDefersOnUnreadable()
    {
        var worker = ReadSource("MiniProToolkit.Guardian", "GuardianWorker.cs");
        var check = SourceBetween(worker,
            "private async Task CheckLeaseAsync(CancellationToken cancellationToken)",
            "private static DateTimeOffset? GetProcessStartUtc");

        Assert.DoesNotContain("_leaseStore.Read()", check, StringComparison.Ordinal);
        var first = check.IndexOf("_leaseStore.ReadResult()", StringComparison.Ordinal);
        var evaluate = check.IndexOf("_coordinator.Evaluate(", StringComparison.Ordinal);
        var second = check.IndexOf("_leaseStore.ReadResult()", first + 1, StringComparison.Ordinal);
        var restore = check.IndexOf("RestoreAutomatic", StringComparison.Ordinal);
        var clear = check.IndexOf("_leaseStore.ClearOwned(lease)", StringComparison.Ordinal);
        var firstUnreadable = check.IndexOf("GuardianLeaseReadStatus.Unreadable", StringComparison.Ordinal);
        var secondUnreadable = check.IndexOf("GuardianLeaseReadStatus.Unreadable",
            firstUnreadable + 1, StringComparison.Ordinal);
        var secondReport = check.IndexOf("ReportUnreadableLease(\"second\"", StringComparison.Ordinal);

        Assert.True(first >= 0, "the poll must read the lease with the typed API");
        Assert.True(firstUnreadable > first, "an unreadable first read must be handled right after the read");
        Assert.True(evaluate > firstUnreadable, "an unreadable lease must never reach the coordinator");
        Assert.True(second > evaluate, "the pre-write re-read must stay after the decision");
        Assert.True(secondUnreadable > second, "the second read must be typed as well");
        Assert.True(secondReport > secondUnreadable, "an unreadable second read must be reported");
        Assert.True(restore > secondReport, "no hardware write while the owner identity is unknown");
        Assert.True(clear > restore, "the compare-and-clear must still follow the restore");

        // Observability: a bounded logger warning plus the bounded recovery log / diagnostic entry,
        // gated by the throttle so a 2 s poll cannot flood them.
        Assert.Contains("ReportUnreadableLease(", check, StringComparison.Ordinal);
        Assert.Contains("_leaseReadWarnings.ShouldReport(", worker, StringComparison.Ordinal);
        Assert.Contains("_logger.LogWarning(", worker, StringComparison.Ordinal);
        Assert.Contains("AppendRecoveryLine(", worker, StringComparison.Ordinal);
        Assert.Contains("AppendDiagnostic(\"Recovery\"", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardianTimingAndLeaseCasAreUnchanged()
    {
        var options = ReadSource("MiniProToolkit.Guardian", "GuardianWorkerOptions.cs");
        var program = ReadSource("MiniProToolkit.Guardian", "Program.cs");

        Assert.Contains("TimeSpan.FromSeconds(2)", options, StringComparison.Ordinal);
        Assert.Contains("new GuardianRecoveryCoordinator(TimeSpan.FromSeconds(8))", program, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(8), GuardianCurveWriteLeasePolicy.ControlLeaseTimeout);
    }

    // ---- Helpers ----

    private static string NewTempPath() => Path.Combine(
        Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString(), "lease.json");

    private static (string Directory, string Path) CreateLeaseDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        return (directory, Path.Combine(directory, "lease.json"));
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private static string SourceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker {endMarker} after {startMarker}");
        return source[start..end];
    }

    private static string ReadSource(string project, string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests",
                "MiniProToolkit.Core.Tests.csproj");
            var candidate = Path.Combine(directory.FullName, "src", project, fileName);
            if (File.Exists(marker) && File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"Could not locate {project}/{fileName} above the test output directory.");
    }
}
