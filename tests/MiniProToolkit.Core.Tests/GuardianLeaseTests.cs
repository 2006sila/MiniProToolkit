using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class GuardianLeaseTests
{
    [Fact]
    public void HealthyLeaseDoesNotRequestRecovery()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new GuardianRecoveryCoordinator(TimeSpan.FromSeconds(8));
        var lease = new GuardianLease(1234, "manual-duty", now.AddSeconds(-1), now.AddMinutes(-1));

        var result = coordinator.Evaluate(lease, now, _ => true, _ => now.AddMinutes(-1));

        Assert.Equal(GuardianRecoveryAction.None, result.Action);
    }

    [Fact]
    public void DeadOwnerRequestsRecoveryEvenWithFreshHeartbeat()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new GuardianRecoveryCoordinator(TimeSpan.FromSeconds(8));
        var lease = new GuardianLease(1234, "manual-duty", now, now);

        var result = coordinator.Evaluate(lease, now, _ => false, _ => now);

        Assert.Equal(GuardianRecoveryAction.RecoverAutomatic, result.Action);
    }

    [Fact]
    public void StaleLeaseRequestsRecoveryWithoutProcessLookup()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new GuardianRecoveryCoordinator(TimeSpan.FromSeconds(8));
        var lease = new GuardianLease(1234, "manual-duty", now.AddSeconds(-9), now);

        var result = coordinator.Evaluate(lease, now, _ => throw new InvalidOperationException(), _ => now);

        Assert.Equal(GuardianRecoveryAction.RecoverAutomatic, result.Action);
    }

    [Fact]
    public void PIDReuseRequestsRecovery()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new GuardianRecoveryCoordinator(TimeSpan.FromSeconds(8));
        var lease = new GuardianLease(1234, "manual-duty", now, now.AddMinutes(-1));

        var result = coordinator.Evaluate(lease, now, _ => true, _ => now);

        Assert.Equal(GuardianRecoveryAction.RecoverAutomatic, result.Action);
    }

    [Fact]
    public void LeaseStoreRoundTripsAndOnlyOwnerCanClear()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "lease.json");
        Directory.CreateDirectory(directory);
        var store = new GuardianLeaseStore(path);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "manual-duty", now, now);
            Assert.Equal(1234, store.Read()?.ProcessId);

            store.ClearOwned(5678);
            Assert.NotNull(store.Read());
            store.ClearOwned(1234);
            Assert.Null(store.Read());
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CompareAndClearKeepsALeaseWrittenByARestartedOwner()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "manual-duty", now, now.AddMinutes(-5));
            var stale = store.Read();
            Assert.NotNull(stale);

            // The owner restarted (new PID + start time + heartbeat) while recovery was running.
            store.WriteActive(5678, "fan-curve", now.AddSeconds(1), now.AddMinutes(-1));

            Assert.False(store.ClearOwned(stale!));
            var current = store.Read();
            Assert.NotNull(current);
            Assert.Equal(5678, current!.ProcessId);
            Assert.Equal("fan-curve", current.ControlMode);

            Assert.True(store.ClearOwned(current));
            Assert.Null(store.Read());
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("heartbeat")]
    [InlineData("mode")]
    [InlineData("start")]
    [InlineData("pid")]
    public void CompareAndClearRequiresTheWholeRecord(string changedField)
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "manual-duty", now, now.AddMinutes(-5));
            var expected = store.Read();
            Assert.NotNull(expected);
            var changed = changedField switch
            {
                "heartbeat" => expected! with { HeartbeatUtc = expected.HeartbeatUtc.AddSeconds(5) },
                "mode" => expected! with { ControlMode = "fan-curve" },
                "start" => expected! with { ProcessStartUtc = now.AddMinutes(-1) },
                _ => expected! with { ProcessId = 5678 }
            };
            store.WriteActive(changed.ProcessId, changed.ControlMode, changed.HeartbeatUtc,
                changed.ProcessStartUtc);

            Assert.False(store.ClearOwned(expected!));
            Assert.Equal(changed.ProcessId, store.Read()?.ProcessId);
            Assert.True(store.ClearOwned(store.Read()!));
            Assert.Null(store.Read());
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CompareAndClearIsANoOpWhenTheLeaseIsAlreadyGone()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var now = DateTimeOffset.UtcNow;
        try
        {
            var expected = new GuardianLease(1234, "manual-duty", now, now.AddMinutes(-5));
            Assert.False(store.ClearOwned(expected));
            Assert.Null(store.Read());
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WriterRacingAnOldClearNeverLosesTheNewLease()
    {
        // Guardian decided about the old lease (stale owner) and is about to clear it while the app
        // restarts and publishes a new one. The cross-process lock serialises both, so an old clear
        // can still remove the old record but can never remove a record it did not evaluate.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path, TimeSpan.FromSeconds(2));
        var now = DateTimeOffset.UtcNow;
        try
        {
            for (var round = 0; round < 25; round++)
            {
                store.WriteActive(1234, "manual-duty", now, now.AddMinutes(-5));
                var stale = store.Read();
                Assert.NotNull(stale);

                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var clear = Task.Run(async () =>
                {
                    await gate.Task;
                    for (var i = 0; i < 20; i++)
                    {
                        // Either removes the old record (stale owner) or is a no-op because the new
                        // record is already on disk; the invariant asserted below is that the new
                        // record is still there afterwards.
                        store.ClearOwned(stale!);
                        store.ClearOwned(1234);
                    }
                });
                var write = Task.Run(async () =>
                {
                    await gate.Task;
                    for (var i = 0; i < 20; i++)
                        store.WriteActive(5678, "fan-curve", now.AddSeconds(1), now.AddMinutes(-1));
                });
                gate.SetResult();
                await Task.WhenAll(clear, write);

                var current = store.Read();
                Assert.NotNull(current);
                Assert.Equal(5678, current!.ProcessId);
                Assert.Equal("fan-curve", current.ControlMode);
                store.Clear();
            }
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WriteActiveFailsFastAndKeepsTheOldLeaseWhenTheLockIsBusy()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path, TimeSpan.FromMilliseconds(50));
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "manual-duty", now, now.AddMinutes(-5));
            using (HoldLock(store))
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                Assert.Throws<IOException>(() =>
                    store.WriteActive(5678, "fan-curve", now.AddSeconds(1), now.AddMinutes(-1)));
                elapsed.Stop();

                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"lock wait was not bounded: {elapsed.Elapsed}");
                var unchanged = store.Read();
                Assert.NotNull(unchanged);
                Assert.Equal(1234, unchanged!.ProcessId);
            }

            // The lock is released again: the same store publishes normally.
            store.WriteActive(5678, "fan-curve", now.AddSeconds(2), now.AddMinutes(-1));
            Assert.Equal(5678, store.Read()?.ProcessId);
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ClearOwnedIsASafeNoOpWhenTheLockIsBusy()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path, TimeSpan.FromMilliseconds(50));
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "manual-duty", now, now.AddMinutes(-5));
            var expected = store.Read();
            Assert.NotNull(expected);

            using (HoldLock(store))
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                Assert.False(store.ClearOwned(expected!));
                Assert.False(store.ClearOwned(1234));
                elapsed.Stop();

                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"lock wait was not bounded: {elapsed.Elapsed}");
                Assert.Equal(1234, store.Read()?.ProcessId);
            }

            // Once the lock is free the same compare-and-clear still works.
            Assert.True(store.ClearOwned(expected!));
            Assert.Null(store.Read());
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void StaleLockFileFromACrashedHolderDoesNotBlockTheLease()
    {
        // A crashed writer leaves the (never deleted) lock file behind. The exclusive handle is gone
        // with the process, so publishing and clearing must keep working - no cleanup step needed.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path, TimeSpan.FromMilliseconds(200));
        var now = DateTimeOffset.UtcNow;
        try
        {
            File.WriteAllText(store.LockPath, "left behind by a dead holder");

            store.WriteActive(1234, "manual-duty", now, now.AddMinutes(-5));
            var lease = store.Read();
            Assert.NotNull(lease);
            Assert.True(store.ClearOwned(lease!));
            Assert.Null(store.Read());
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void StoreWithoutALeaseDirectoryDoesNotCreateIt()
    {
        // Guardian runs as SYSTEM: creating the shared directory (and with it a SYSTEM-owned ACL)
        // before the app ever published a lease would break the app's later writes.
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "lease.json");
        var store = new GuardianLeaseStore(path);
        var now = DateTimeOffset.UtcNow;
        try
        {
            Assert.Null(store.Read());
            Assert.False(store.ClearOwned(new GuardianLease(1234, "manual-duty", now, now)));
            Assert.False(store.ClearOwned(1234));
            store.Clear();
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LockFileNeedsNoWriteAccessSoACrossAccountLockStillWorks()
    {
        // The lock file may have been created by the SYSTEM Guardian, which leaves the interactive
        // user only the inherited read access. Taking the lock must therefore never require write
        // access to the lock file itself.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path, TimeSpan.FromMilliseconds(200));
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "manual-duty", now, now.AddMinutes(-5));
            Assert.True(File.Exists(store.LockPath));
            File.SetAttributes(store.LockPath, FileAttributes.ReadOnly);

            store.WriteActive(5678, "fan-curve", now.AddSeconds(1), now.AddMinutes(-1));
            var lease = store.Read();
            Assert.Equal(5678, lease?.ProcessId);
            Assert.True(store.ClearOwned(lease!));
            Assert.Null(store.Read());
        }
        finally
        {
            if (File.Exists(store.LockPath))
                File.SetAttributes(store.LockPath, FileAttributes.Normal);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // ---- Fresh pre-write renewal: the curve may only write while it still owns a fresh record ----

    private static readonly TimeSpan RenewTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public void RenewUpdatesTheHeartbeatOfAnOwnedFreshRecord()
    {
        // The normal trusted path: TakeOverCurveLease published the record, later passes renew it.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "fan-curve", now.AddSeconds(-1), start);

            Assert.True(store.TryRenewOwnedFresh(1234, start, "fan-curve", () => now, RenewTimeout));

            var renewed = store.Read();
            Assert.NotNull(renewed);
            Assert.Equal(1234, renewed!.ProcessId);
            Assert.Equal(start, renewed.ProcessStartUtc);
            Assert.Equal(now, renewed.HeartbeatUtc);
            Assert.Equal("fan-curve", renewed.ControlMode);
            Assert.False(renewed.IsStale(now, RenewTimeout));
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewRefusesAStaleRecordAndKeepsItUnchanged()
    {
        // A pass that started inside the lease window can finish after it: renewal must fail closed
        // instead of publishing a new heartbeat that would revive a lapsed lease.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        var expired = now.AddSeconds(-9);
        try
        {
            store.WriteActive(1234, "fan-curve", expired, start);

            Assert.False(store.TryRenewOwnedFresh(1234, start, "fan-curve", () => now, RenewTimeout));

            var after = store.Read();
            Assert.NotNull(after);
            Assert.Equal(expired, after!.HeartbeatUtc); // untouched, not republished
            Assert.True(after.IsStale(now, RenewTimeout));
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewAfterGuardianClearedTheLeaseNeverRecreatesIt()
    {
        // The race the fix targets: the pass read a valid lease, Guardian recovered and cleared it
        // (or its compare-and-clear landed), and the write path must then refuse without recreating.
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

            Assert.False(store.TryRenewOwnedFresh(1234, start, "fan-curve", () => now, RenewTimeout));

            Assert.Null(store.Read());
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewRefusesAnotherIdentityEvenWithAFreshHeartbeat()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "fan-curve", now, start);

            Assert.False(store.TryRenewOwnedFresh(5678, start, "fan-curve", () => now, RenewTimeout));
            Assert.False(store.TryRenewOwnedFresh(1234, start.AddSeconds(30), "fan-curve", () => now, RenewTimeout));
            Assert.False(store.TryRenewOwnedFresh(1234, null, "fan-curve", () => now, RenewTimeout));

            var after = store.Read();
            Assert.NotNull(after);
            Assert.Equal(1234, after!.ProcessId);
            Assert.Equal(start, after.ProcessStartUtc);
            Assert.Equal(now, after.HeartbeatUtc);
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewRefusesAFutureHeartbeatBeyondTheClockTolerance()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "fan-curve", now.AddSeconds(5), start);

            Assert.False(store.TryRenewOwnedFresh(1234, start, "fan-curve", () => now, RenewTimeout));
            Assert.Equal(now.AddSeconds(5), store.Read()?.HeartbeatUtc);
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewWithoutALeaseDirectoryDoesNotCreateOne()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var store = new GuardianLeaseStore(System.IO.Path.Combine(directory, "lease.json"));
        var now = DateTimeOffset.UtcNow;
        try
        {
            Assert.False(store.TryRenewOwnedFresh(1234, now.AddMinutes(-5), "fan-curve", () => now, RenewTimeout));
            Assert.False(Directory.Exists(directory));
            Assert.Null(store.Read());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewFailsClosedWhileAnotherHolderOwnsTheLock()
    {
        // A separate check-then-write would pass here; the atomic renewal cannot prove ownership.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path, TimeSpan.FromMilliseconds(100));
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "fan-curve", now.AddSeconds(-1), start);
            using (HoldLock(store))
            {
                Assert.False(store.TryRenewOwnedFresh(1234, start, "fan-curve", () => now, RenewTimeout));
            }
            Assert.Equal(now.AddSeconds(-1), store.Read()?.HeartbeatUtc);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewNeverReadsTheClockWhenItCannotTakeTheLock()
    {
        // Ordering proof for the fix: the caller no longer supplies the timestamp. The store takes the
        // lock first and only then asks for the clock, so a renewal that fails on the lock never even
        // consults a time source (and therefore cannot judge or publish anything).
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path, TimeSpan.FromMilliseconds(100));
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        var clockCalls = 0;
        try
        {
            store.WriteActive(1234, "fan-curve", now.AddSeconds(-1), start);
            using (HoldLock(store))
            {
                Assert.False(store.TryRenewOwnedFresh(1234, start, "fan-curve", () =>
                {
                    clockCalls++;
                    return now;
                }, RenewTimeout));
            }

            Assert.Equal(0, clockCalls);
            Assert.Equal(now.AddSeconds(-1), store.Read()?.HeartbeatUtc);
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewJudgesTheHeartbeatWithThePostLockClock()
    {
        // The reported defect: the caller computed "now" before the bounded lock wait. A record that
        // was fresh at that instant is already past the timeout once the lock is finally held, so
        // judging with the caller's old timestamp would publish an expired heartbeat. The verdict must
        // use the time taken under the lock and therefore refuse here, leaving the record untouched.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var callerNow = DateTimeOffset.UtcNow;
        var afterLockWait = callerNow.AddSeconds(9); // what the clock says once the lock is held
        try
        {
            // Fresh for the caller (1 s old), stale (10 s old) for the post-lock clock.
            store.WriteActive(1234, "fan-curve", callerNow.AddSeconds(-1), start);
            Assert.True(GuardianCurveWriteLeasePolicy.CanWrite(store.Read(), 1234, start,
                callerNow, RenewTimeout));

            Assert.False(store.TryRenewOwnedFresh(1234, start, "fan-curve",
                () => afterLockWait, RenewTimeout));

            Assert.Equal(callerNow.AddSeconds(-1), store.Read()?.HeartbeatUtc);
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewUsesOneLockedTimestampForTheVerdictAndTheHeartbeat()
    {
        // The same locked timestamp decides staleness and becomes the published heartbeat; the clock
        // is consulted exactly once, so the record can never be stamped with a time the verdict did
        // not see.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        var lockedNow = now.AddSeconds(2);
        var clockCalls = 0;
        try
        {
            store.WriteActive(1234, "fan-curve", now.AddSeconds(-1), start);

            Assert.True(store.TryRenewOwnedFresh(1234, start, "fan-curve", () =>
            {
                clockCalls++;
                return lockedNow;
            }, RenewTimeout));

            Assert.Equal(1, clockCalls);
            var renewed = store.Read();
            Assert.NotNull(renewed);
            Assert.Equal(lockedNow, renewed!.HeartbeatUtc);
            Assert.False(renewed.IsStale(lockedNow, RenewTimeout));
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewRejectsAMissingClockSource()
    {
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        try
        {
            Assert.Throws<ArgumentNullException>(() =>
                store.TryRenewOwnedFresh(1234, DateTimeOffset.UtcNow, "fan-curve", null!, RenewTimeout));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RenewAcceptsExactlyTheTimeoutAndRefusesOneTickPast()
    {
        // Boundary of the unchanged 8 s policy: an age of exactly the timeout is still inside the
        // window (IsStale is strictly greater), one tick beyond it is refused and stays unpublished.
        var (directory, path) = CreateLeaseDirectory();
        var store = new GuardianLeaseStore(path);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;
        try
        {
            store.WriteActive(1234, "fan-curve", now - RenewTimeout, start);
            Assert.True(store.TryRenewOwnedFresh(1234, start, "fan-curve", () => now, RenewTimeout));
            Assert.Equal(now, store.Read()?.HeartbeatUtc);

            var edge = now.AddTicks(-RenewTimeout.Ticks - 1);
            store.WriteActive(1234, "fan-curve", edge, start);
            Assert.False(store.TryRenewOwnedFresh(1234, start, "fan-curve", () => now, RenewTimeout));
            Assert.Equal(edge, store.Read()?.HeartbeatUtc);
        }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // ---- Pure pre-write policy used by the store and by the control pass ----

    [Fact]
    public void WritePolicyAcceptsOnlyTheOwnersFreshRecord()
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-5);
        var fresh = new GuardianLease(1234, "fan-curve", now.AddSeconds(-1), start);

        Assert.True(GuardianCurveWriteLeasePolicy.CanWrite(fresh, 1234, start, now, RenewTimeout));
        Assert.False(GuardianCurveWriteLeasePolicy.CanWrite(null, 1234, start, now, RenewTimeout));
        Assert.False(GuardianCurveWriteLeasePolicy.CanWrite(fresh, 5678, start, now, RenewTimeout));
        Assert.False(GuardianCurveWriteLeasePolicy.CanWrite(fresh, 1234, start.AddSeconds(30), now, RenewTimeout));
        Assert.False(GuardianCurveWriteLeasePolicy.CanWrite(fresh, 1234, null, now, RenewTimeout));
        Assert.False(GuardianCurveWriteLeasePolicy.CanWrite(
            fresh with { HeartbeatUtc = now.AddSeconds(-9) }, 1234, start, now, RenewTimeout));
        Assert.False(GuardianCurveWriteLeasePolicy.CanWrite(
            fresh with { ProcessStartUtc = null }, 1234, start, now, RenewTimeout));
        Assert.False(GuardianCurveWriteLeasePolicy.CanWrite(
            fresh with { HeartbeatUtc = now.AddSeconds(5) }, 1234, start, now, RenewTimeout));
        // Same strict boundary as the store renewal: exactly the timeout is inside the window.
        Assert.True(GuardianCurveWriteLeasePolicy.CanWrite(
            fresh with { HeartbeatUtc = now - RenewTimeout }, 1234, start, now, RenewTimeout));
        Assert.False(GuardianCurveWriteLeasePolicy.CanWrite(
            fresh with { HeartbeatUtc = now - RenewTimeout - TimeSpan.FromTicks(1) }, 1234, start, now,
            RenewTimeout));
    }

    private static FileStream HoldLock(GuardianLeaseStore store) => new(
        store.LockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);

    private static (string Directory, string Path) CreateLeaseDirectory()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        System.IO.Directory.CreateDirectory(directory);
        return (directory, System.IO.Path.Combine(directory, "lease.json"));
    }
}
