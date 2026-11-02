using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Guards for the curve write path against a lease that expired or was cleared by Guardian while a
/// pass was still running. Two layers are checked and neither touches hardware, EC, the GUI or a
/// real lease path: (1) the atomic renewal in <see cref="GuardianLeaseStore"/> refuses to publish a
/// record it does not own and cannot be raced by a compare-and-clear, and (2) the App source keeps
/// that renewal immediately before every non-takeover hardware write (curve target, polling
/// heartbeat, calibration loop), with no unconditional publication left in those paths while the
/// explicit takeover and the closing hand-back stay in place.
/// </summary>
public sealed class GuardianCurveLeaseWiringTests
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public void OpenGateWithAClearedLeaseWritesNothingAndRecreatesNothing()
    {
        // The gate holds the verdict of the pass that read a lease Guardian has since cleared.
        // Order is the production one (mirrored by the source guard below): gate -> atomic renewal
        // -> hardware write. The renewal fails, so the hardware write count stays zero.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "fan-curve", now.AddSeconds(-1), start);
            var taken = store.Read();
            Assert.NotNull(taken);
            Assert.True(store.ClearOwned(taken!));

            var gate = new FanCurveLeaseGate();
            gate.SetPass(true);
            var hardwareWrites = 0;
            var ran = gate.TryWrite(() =>
            {
                if (!store.TryRenewOwnedFresh(1234, start, "fan-curve", () => now, LeaseTimeout))
                    return;
                hardwareWrites++;
            });

            Assert.True(ran);
            Assert.Equal(0, hardwareWrites);
            Assert.Null(store.Read());
            Assert.False(File.Exists(path));
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void OpenGateWithAStaleLeaseWritesNothing()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        try
        {
            // The pass read this record while it was fresh; hardware access outlived the timeout.
            store.WriteActive(1234, "fan-curve", now.AddSeconds(-4), start);
            var late = now.AddSeconds(10);

            var gate = new FanCurveLeaseGate();
            gate.SetPass(true);
            var hardwareWrites = 0;
            gate.TryWrite(() =>
            {
                if (!store.TryRenewOwnedFresh(1234, start, "fan-curve", () => late, LeaseTimeout))
                    return;
                hardwareWrites++;
            });

            Assert.Equal(0, hardwareWrites);
            Assert.Equal(now.AddSeconds(-4), store.Read()?.HeartbeatUtc);
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CurveTargetRenewsTheLeaseImmediatelyBeforeTheHardwareWrite()
    {
        var source = ReadAppSource("GuardianLeasedFanTarget.cs");
        var writeDuty = SourceBetween(source, "private void WriteDuty(int dutyPercent)", "public void RestoreAutomatic");

        var renew = writeDuty.IndexOf("TryRenewOwnedFresh", StringComparison.Ordinal);
        var hardwareWrite = writeDuty.IndexOf("_hardware.SetCurveDuty", StringComparison.Ordinal);
        Assert.True(renew >= 0, "the curve target must renew the lease atomically before writing");
        Assert.True(hardwareWrite > renew, "the lease renewal must precede the hardware setter");
        // Compare-and-swap with a clock delegate: the store, not the target, decides when "now" is.
        Assert.Contains("() => DateTimeOffset.UtcNow", writeDuty, StringComparison.Ordinal);
        Assert.Contains("GuardianCurveWriteLeasePolicy.ControlLeaseTimeout", writeDuty, StringComparison.Ordinal);

        // The old unconditional publication is gone: a curve write cannot create or revive a lease.
        Assert.DoesNotContain("WriteActive", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteActive", writeDuty, StringComparison.Ordinal);
    }

    [Fact]
    public void CurveTargetKeepsThePassGateInFrontOfTheWrite()
    {
        var source = ReadAppSource("GuardianLeasedFanTarget.cs");
        var setCurveDuty = SourceBetween(source, "public void SetCurveDuty(int dutyPercent)", "private void WriteDuty");

        Assert.Contains("_leaseGate.TryWrite(() => WriteDuty(dutyPercent))", setCurveDuty, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteActive", setCurveDuty, StringComparison.Ordinal);
        // A refused write must stay on the existing fail path (restore automatic, gate closed).
        Assert.Contains("throw new InvalidOperationException", setCurveDuty, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitTakeoverStillPublishesTheLeaseAndOpensTheGate()
    {
        var source = ReadMainWindow();
        var takeover = SourceBetween(source, "private void TakeOverCurveLease()", "private void RefreshLease");

        var publish = takeover.IndexOf("_leaseStore.WriteActive", StringComparison.Ordinal);
        var open = takeover.IndexOf("_curveLeaseGate.SetPass(true)", StringComparison.Ordinal);
        Assert.True(publish >= 0, "the explicit toggle/profile takeover must publish the lease");
        Assert.True(open > publish, "the gate opens only after the takeover published the lease");
    }

    [Fact]
    public void PostPassHeartbeatUsesCompareAndSwapRenewalOnly()
    {
        var source = ReadMainWindow();
        var refresh = SourceBetween(source, "private void RefreshLease(bool fixedRpmFault)", "private void EnableCurveFromMenu");

        // The heartbeat no longer checks a lease and then publishes a new record: Guardian could
        // restore + clear between those two lines. The split is gone, so no unconditional
        // publication exists anywhere in the heartbeat.
        Assert.DoesNotContain("WriteActive", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("HasValidControlLease", refresh, StringComparison.Ordinal);

        var renew = refresh.IndexOf("_leaseStore.TryRenewOwnedFresh", StringComparison.Ordinal);
        Assert.True(renew >= 0, "the heartbeat must renew through the atomic compare-and-swap");
        Assert.True(refresh.IndexOf("GuardianCurveWriteLeasePolicy.ControlLeaseTimeout", renew,
            StringComparison.Ordinal) > renew, "the heartbeat keeps the shared 8 s policy");
        // The store reads the clock after it holds the lock; the App must therefore hand it a
        // delegate, never a timestamp computed before the call.
        Assert.Contains("static () => DateTimeOffset.UtcNow", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void CalibrationHeartbeatUsesCompareAndSwapRenewalOnly()
    {
        var source = ReadMainWindow();
        var calibration = SourceBetween(source, "if (!machine.LastObservationWasFresh)",
            "if (machine.State != FanCalibrationSamplingState.Completed");

        // The calibration loop starts with one explicit takeover and then only renews: publishing a
        // fresh record here would recreate a lease Guardian already recovered during sampling.
        Assert.DoesNotContain("WriteActive", calibration, StringComparison.Ordinal);
        var renew = calibration.IndexOf("_leaseStore.TryRenewOwnedFresh", StringComparison.Ordinal);
        Assert.True(renew >= 0, "the calibration heartbeat must renew atomically, not publish");
        Assert.Contains("\"fan-calibration\"", calibration[renew..], StringComparison.Ordinal);
        Assert.Contains("static () => DateTimeOffset.UtcNow", calibration, StringComparison.Ordinal);
        // A refused renewal must leave the loop on the existing fail path (restore + gate close).
        Assert.True(calibration.IndexOf("throw new InvalidOperationException", renew,
            StringComparison.Ordinal) > renew, "a refused calibration renewal must abort the run");
    }

    [Fact]
    public void OnlyExplicitTakeoversStillPublishTheLease()
    {
        // Three authorised takeovers: the curve toggle/profile entry point, an explicit fixed-RPM or
        // full-speed profile apply, and an explicit manual fan request. Every other lease write is a
        // compare-and-swap renewal, so an expired or recovered lease cannot be revived.
        var source = ReadMainWindow();
        var occurrences = 0;
        for (var index = source.IndexOf("_leaseStore.WriteActive", StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf("_leaseStore.WriteActive", index + 1, StringComparison.Ordinal))
            occurrences++;
        Assert.Equal(3, occurrences);
        Assert.Contains("\"fan-curve\", DateTimeOffset.UtcNow", source, StringComparison.Ordinal);
        Assert.Contains("\"profile-\" + profile.FanMode", source, StringComparison.Ordinal);
        Assert.Contains("\"manual-duty\", DateTimeOffset.UtcNow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingPathClosesTheGateAndClearsOnlyItsOwnRecord()
    {
        var source = ReadMainWindow();
        var closing = SourceBetween(source, "private async void OnClosing(object? sender, CancelEventArgs e)",
            "private void ResumeAfterFailedExit");

        var gateClose = closing.IndexOf("_curveLeaseGate.Close()", StringComparison.Ordinal);
        Assert.True(gateClose >= 0, "the exit path must close the curve write gate before hand-back");
        // Explicit lease check instead of a blind PID clear, and no publication on the way out.
        Assert.Contains("_leaseStore.Read() is { } leaving", closing, StringComparison.Ordinal);
        Assert.Contains("leaving.ProcessStartUtc == GuardianProcessIdentity.CurrentStartUtc", closing,
            StringComparison.Ordinal);
        Assert.Contains("_leaseStore.ClearOwned(leaving)", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteActive", closing, StringComparison.Ordinal);
    }

    [Fact]
    public void CurveTargetTakesOnlyTheStoreAndTheGate()
    {
        // No boolean "checked earlier" delegate: the only pre-write decision is the atomic renewal.
        var source = ReadMainWindow();
        Assert.Contains(
            "new GuardianLeasedFanTarget(_hardware!, _leaseStore, _curveLeaseGate)",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain("GuardianLeasedFanTarget(_hardware!, _leaseStore, _curveLeaseGate,",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void LeaseValidityUsesOneFreshReadAndThePurePolicy()
    {
        var source = ReadMainWindow();
        var validity = SourceBetween(source, "private bool HasValidControlLease()", "private void TakeOverCurveLease");

        var read = validity.IndexOf("_leaseStore.Read()", StringComparison.Ordinal);
        var policy = validity.IndexOf("GuardianCurveWriteLeasePolicy.CanWrite", StringComparison.Ordinal);
        Assert.True(read >= 0, "the pass must read the lease from disk");
        Assert.True(policy > read, "the verdict must come from the pure policy applied to that read");
        // The timeout stays at the Guardian value; no second, hand-written staleness rule.
        Assert.Contains("GuardianCurveWriteLeasePolicy.ControlLeaseTimeout", validity, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedLeaseTimeoutStaysAtTheGuardianValue()
    {
        // The 8 s threshold is what Guardian recovery already uses; the fix must not change it.
        Assert.Equal(TimeSpan.FromSeconds(8), GuardianCurveWriteLeasePolicy.ControlLeaseTimeout);
    }

    private static string ReadMainWindow() => ReadAppSource("MainWindow.xaml.cs");

    private static string SourceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker {endMarker} after {startMarker}");
        return source[start..end];
    }

    private static string ReadAppSource(string fileName)
    {
        // Start at the test output directory; works in any checkout without a user-specific path.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests", "MiniProToolkit.Core.Tests.csproj");
            var candidate = Path.Combine(directory.FullName, "src", "MiniProToolkit.App", fileName);
            if (File.Exists(project) && File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"Could not locate the App source {fileName} above the test directory.");
    }

    private static (string Directory, string Path) CreateLeaseDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        return (directory, Path.Combine(directory, "lease.json"));
    }
}
