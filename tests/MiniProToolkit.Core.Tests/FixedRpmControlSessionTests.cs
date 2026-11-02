using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FixedRpmControlSessionTests
{
    private readonly FakeClock _clock = new();
    private readonly List<int> _writes = [];
    private int _restores;
    private bool _failWrite;
    private bool _failRestore;
    private FixedRpmControlSession Create() => new(FanCalibration.Default, duty =>
    {
        _writes.Add(duty);
        if (_failWrite) throw new IOException("write failure");
    }, () =>
    {
        _restores++;
        if (_failRestore) throw new IOException("restore failure");
    }, _clock);

    private HardwareSnapshot Snapshot(int rpm = 1800) => new(
        _clock.GetUtcNow(), 60, rpm, 0, PowerMode.Balance, 20, 40, FanControlMode.FixedRpm, 50, null);

    private void Tick(FixedRpmControlSession session, HardwareSnapshot? snapshot = null, bool guardian = true, bool lease = true)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        session.Tick(snapshot ?? Snapshot(), guardian, lease);
    }

    [Fact]
    public void ExtraUiRefreshesCannotAccelerateFeedbackButNeverDelaySafetyGuards()
    {
        var session = Create();
        session.Start(3000);
        for (var index = 0; index < 20; index++) session.Tick(Snapshot(), true);
        Assert.Single(_writes);
        Tick(session);
        Assert.Equal(2, _writes.Count);
        Assert.Equal(FanRpmFeedbackState.Adjusting, session.State);
        session.Tick(Snapshot(), true);
        Assert.Equal(2, _writes.Count);

        // An extra refresh carrying the 90 °C sample is a safety gate, not a control cadence
        // event: it trips at once instead of waiting for the next interval.
        session.Tick(Snapshot() with { CpuTemperatureC = 90.1 }, true);
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(3, _writes.Count);
        Assert.Equal(100, _writes[^1]);
    }

    // Audit finding: the control cadence early-exit used to sit in front of every guard, so a
    // 90 °C sample, a stopped Guardian or an expired lease could be ignored for up to two
    // control intervals. The fail-safe gates must react within one completed sample.
    [Fact]
    public void EmergencyTripFiresOnTheFirstSampleBeforeTheControlInterval()
    {
        var session = Create();
        session.Start(2500);
        var writesAfterStart = _writes.Count;

        _clock.Advance(TimeSpan.FromMilliseconds(950));
        session.Tick(Snapshot() with { CpuTemperatureC = 90.1 }, true, true);

        Assert.True(session.IsEmergencyFullSpeed);
        Assert.False(session.IsActive);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Equal(writesAfterStart + 1, _writes.Count);
        Assert.Equal(100, _writes[^1]);
        Assert.Equal(0, _restores); // The latch owns the fan; no EC hand-off on its own.

        // Already latched: an immediate follow-up refresh neither lowers duty nor writes again.
        session.Tick(Snapshot() with { CpuTemperatureC = 60 }, true, true);
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(writesAfterStart + 1, _writes.Count);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("guardian")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("sensor")]
    [InlineData("wmi")]
    public void FailSafeGuardsTripOnTheFirstSampleBeforeTheControlInterval(string scenario)
    {
        var session = Create();
        session.Start(3000);
        var snapshot = Snapshot();
        var guardian = scenario != "guardian";
        var lease = scenario != "lease";
        snapshot = scenario switch
        {
            "stale" => snapshot with { Timestamp = snapshot.Timestamp.AddSeconds(-10) },
            "future" => snapshot with { Timestamp = snapshot.Timestamp.AddMinutes(1) },
            "sensor" => snapshot with { CpuTemperatureC = null },
            "wmi" => snapshot with { Error = "WMI unavailable" },
            _ => snapshot
        };

        _clock.Advance(TimeSpan.FromMilliseconds(950));
        session.Tick(snapshot, guardian, lease);

        Assert.False(session.IsActive);
        Assert.False(session.IsEmergencyFullSpeed);
        Assert.False(session.RecoveryRequired);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Single(_writes); // No closed-loop adjustment before ControlInterval elapsed.
        Assert.Equal(1, _restores); // Fail-closed hand-off to EC automatic, exactly once.

        // Re-delivering the same failing sample must not turn into a WMI restore storm.
        for (var index = 0; index < 3; index++) session.Tick(snapshot, guardian, lease);
        Assert.Equal(1, _restores);
        Assert.Single(_writes);
    }

    [Fact]
    public void ClosedLoopAdjustmentKeepsTheControlInterval()
    {
        var session = Create();
        session.Start(3000);

        _clock.Advance(TimeSpan.FromMilliseconds(500));
        session.Tick(Snapshot(1800), true, true);
        Assert.Single(_writes);
        Assert.True(session.IsActive);

        _clock.Advance(TimeSpan.FromMilliseconds(499));
        session.Tick(Snapshot(1800), true, true);
        Assert.Single(_writes);
        Assert.True(session.IsActive);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        session.Tick(Snapshot(1800), true, true);
        Assert.Equal(2, _writes.Count);
        Assert.Equal(FanRpmFeedbackState.Adjusting, session.State);
    }

    [Fact]
    public void LatchedGuardianStopHandsOffOnTheFirstSampleBeforeTheControlInterval()
    {
        var session = Create();
        session.Start(2500);
        Tick(session, Snapshot() with { CpuTemperatureC = 90 });
        Assert.True(session.IsEmergencyFullSpeed);

        _clock.Advance(TimeSpan.FromMilliseconds(300));
        session.Tick(Snapshot() with { CpuTemperatureC = 90 }, false, true);
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.False(session.IsActive);
        Assert.False(session.RecoveryRequired);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Equal(1, _restores);
        Assert.Equal(100, _writes[^1]);

        // Same latch failure re-detected at once: the EC hand-off is not repeated.
        session.Tick(Snapshot() with { CpuTemperatureC = 90 }, false, true);
        Assert.Equal(1, _restores);
        Assert.True(session.IsEmergencyFullSpeed);
    }

    [Fact]
    public void FailedRecoveryIsRetriedAtMostOncePerControlInterval()
    {
        var session = Create();
        session.Start(2500);
        _failRestore = true;

        _clock.Advance(TimeSpan.FromMilliseconds(950));
        session.Tick(Snapshot(), true, false);
        Assert.Equal(1, _restores);
        Assert.True(session.RecoveryRequired);

        for (var index = 0; index < 5; index++) session.Tick(Snapshot(), true, false);
        Assert.Equal(1, _restores);

        _clock.Advance(TimeSpan.FromMilliseconds(1000));
        session.Tick(Snapshot(), true, false);
        Assert.Equal(2, _restores);
        Assert.True(session.RecoveryRequired);

        _failRestore = false;
        _clock.Advance(TimeSpan.FromMilliseconds(1000));
        session.Tick(Snapshot(), true, false);
        Assert.Equal(3, _restores);
        Assert.False(session.RecoveryRequired);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void LatchedRecoveryFailureIsRetriedAtMostOncePerControlInterval()
    {
        var session = Create();
        session.Start(2500);
        Tick(session, Snapshot() with { CpuTemperatureC = 90 });
        _failRestore = true;

        _clock.Advance(TimeSpan.FromMilliseconds(400));
        session.Tick(Snapshot(), false, true);
        Assert.Equal(1, _restores);
        Assert.True(session.RecoveryRequired);
        Assert.True(session.IsEmergencyFullSpeed);

        for (var index = 0; index < 4; index++) session.Tick(Snapshot(), false, true);
        Assert.Equal(1, _restores);

        _clock.Advance(TimeSpan.FromSeconds(1));
        session.Tick(Snapshot(), false, true);
        Assert.Equal(2, _restores);
        Assert.True(session.IsEmergencyFullSpeed);
    }

    [Theory]
    [InlineData("sensor")]
    [InlineData("nan")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("guardian")]
    [InlineData("lease")]
    [InlineData("wmi")]
    public void InvalidControlInputsStopFeedbackAndRestore(string scenario)
    {
        var session = Create();
        session.Start(3000);
        var snapshot = Snapshot();
        snapshot = scenario switch
        {
            "sensor" => snapshot with { CpuTemperatureC = null },
            "nan" => snapshot with { CpuTemperatureC = double.NaN },
            "stale" => snapshot with { Timestamp = snapshot.Timestamp.AddSeconds(-10) },
            "future" => snapshot with { Timestamp = snapshot.Timestamp.AddMinutes(1) },
            "wmi" => snapshot with { Error = "WMI unavailable" },
            _ => snapshot
        };
        Tick(session, snapshot, guardian: scenario != "guardian", lease: scenario != "lease");
        Assert.False(session.IsActive);
        Assert.False(session.IsEmergencyFullSpeed); // Non-emergency faults never latch full speed.
        Assert.False(session.RecoveryRequired);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.NotNull(session.Fault);
        Assert.Equal(1, _restores);
        Assert.Single(_writes);
        Tick(session);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Equal(1, _restores);
    }

    [Fact]
    public void FailedFeedbackWriteStopsWritesRetriesRecoveryAndLatchesContext()
    {
        var session = Create();
        session.Start(3000);
        _failWrite = _failRestore = true;
        Tick(session);
        Assert.False(session.IsActive);
        Assert.True(session.RecoveryRequired);
        Assert.Equal(1200, session.ErrorRpm);
        Assert.Contains("恢复失败", session.Fault);
        Assert.Throws<InvalidOperationException>(() => session.Start(2500));
        Tick(session);
        Assert.Equal(2, _writes.Count);
        Assert.Equal(2, _restores);
        _failRestore = false;
        Tick(session);
        Assert.False(session.RecoveryRequired);
        Assert.Equal(3000, session.TargetRpm);
        Assert.Equal(3000, session.RequestedTargetRpm);
        Assert.Equal(1200, session.ErrorRpm);
        Assert.Contains("控制归属不可回读", session.Fault);
        Tick(session);
        Assert.Equal(3, _restores);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        session.RestoreAutomatic();
        Assert.Null(session.Fault);
        Assert.Null(session.TargetRpm);
        Assert.Equal(FanRpmFeedbackState.Disabled, session.State);
    }

    [Fact]
    public void HighTemperatureLatchesFullSpeedWithoutAutomaticHandoffOrDutyReduction()
    {
        var session = Create();
        session.Start(2500);
        Tick(session, Snapshot() with { CpuTemperatureC = 90 });
        Assert.Equal(100, _writes.Last());
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(0, _restores);
        Tick(session, Snapshot() with { CpuTemperatureC = 60 });
        Assert.Equal(2, _writes.Count);
        Assert.Throws<InvalidOperationException>(() => session.Start(2500));
        session.RestoreAutomatic();
        Assert.False(session.IsEmergencyFullSpeed);
    }

    [Fact]
    public void EmergencyLatchSurvivesGuardianStopAndRestoresFullSpeedOnTheNextTick()
    {
        var session = Create();
        session.Start(2500);
        Tick(session, Snapshot() with { CpuTemperatureC = 90 });
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(100, _writes.Last());

        // Guardian stopping is an ordinary fault: it may hand control back to EC automatic once,
        // but it must never clear the 90 °C latch or leave a later tick free to slow the fan.
        Tick(session, guardian: false);
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.False(session.IsActive);
        Assert.False(session.RecoveryRequired);
        Assert.Equal(1, _restores);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Contains("锁定", session.Fault);
        Assert.Throws<InvalidOperationException>(() => session.Start(2500));

        // Authority is back: the next ordinary tick re-asserts full duty instead of the old target.
        var writesBefore = _writes.Count;
        Tick(session, Snapshot() with { CpuTemperatureC = 60 });
        Assert.Equal(writesBefore + 1, _writes.Count);
        Assert.Equal(100, _writes.Last());
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.False(session.IsActive);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Null(session.ErrorRpm);

        session.RestoreAutomatic();
        Assert.False(session.IsEmergencyFullSpeed);
        Assert.Equal(FanRpmFeedbackState.Disabled, session.State);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("sensor")]
    [InlineData("wmi")]
    [InlineData("lease")]
    public void EmergencyLatchSurvivesBadOrStaleSamples(string scenario)
    {
        var session = Create();
        session.Start(2500);
        Tick(session, Snapshot() with { CpuTemperatureC = 90 });
        Assert.True(session.IsEmergencyFullSpeed);
        var snapshot = Snapshot();
        snapshot = scenario switch
        {
            "stale" => snapshot with { Timestamp = snapshot.Timestamp.AddSeconds(-30) },
            "sensor" => snapshot with { CpuTemperatureC = null },
            "wmi" => snapshot with { Error = "WMI unavailable" },
            _ => snapshot
        };
        Tick(session, snapshot, lease: scenario != "lease");
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(1, _restores);
        Assert.False(session.IsActive);
        Assert.Equal(100, _writes.Last());

        session.RestoreAutomatic();
        Assert.False(session.IsEmergencyFullSpeed);
        Assert.Equal(2, _restores);
    }

    [Fact]
    public void EmergencyLatchIsOnlyClearedByAnExplicitRestore()
    {
        var session = Create();
        session.Start(2500);
        Tick(session, Snapshot() with { CpuTemperatureC = 90 });
        Assert.True(session.IsEmergencyFullSpeed);

        // More ticks, a mode switch and a failed restore attempt all keep the latch engaged.
        Tick(session, Snapshot() with { CpuTemperatureC = 70 });
        Tick(session, Snapshot() with { CpuTemperatureC = 95 });
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(2, _writes.Count);
        Assert.Equal(100, _writes.Last());
        session.Stop();
        Assert.True(session.IsEmergencyFullSpeed);
        _failRestore = true;
        Assert.Throws<IOException>(session.RestoreAutomatic);
        Assert.False(session.IsEmergencyFullSpeed);
        Assert.True(session.RecoveryRequired);
        Assert.Throws<InvalidOperationException>(() => session.Start(2500));
        _failRestore = false;
        session.RestoreAutomatic();
        Assert.False(session.IsEmergencyFullSpeed);
        Assert.Null(session.Fault);
    }

    [Fact]
    public void EmergencyWriteFailureKeepsTheLatchAndRetriesFullSpeed()
    {
        var session = Create();
        session.Start(2500);
        _failWrite = true;
        Tick(session, Snapshot() with { CpuTemperatureC = 91 });
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(2, _writes.Count);
        Assert.Contains("紧急满速写入失败", session.Fault);
        Assert.Equal(0, _restores);
        Assert.Throws<InvalidOperationException>(() => session.Start(2500));

        _failWrite = false;
        Tick(session, Snapshot() with { CpuTemperatureC = 91 });
        Assert.Equal(100, _writes.Last());
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.False(session.IsActive);
        session.RestoreAutomatic();
        Assert.False(session.IsEmergencyFullSpeed);
    }

    [Fact]
    public void InitialWriteFailureRetainsPendingRecovery()
    {
        var session = Create();
        _failWrite = _failRestore = true;
        Assert.Throws<IOException>(() => session.Start(3000));
        Assert.True(session.RecoveryRequired);
        Assert.False(session.IsActive);
        Assert.Contains("初始写入失败", session.Fault);
        _failRestore = false;
        Tick(session);
        Assert.False(session.RecoveryRequired);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
    }

    [Fact]
    public void ExplicitRestoreFailureDisablesFeedbackUntilRecovery()
    {
        var session = Create();
        session.Start(3000);
        _failRestore = true;
        Assert.Throws<IOException>(session.RestoreAutomatic);
        Assert.False(session.IsActive);
        Assert.True(session.RecoveryRequired);
        Tick(session);
        Assert.Single(_writes);
        _failRestore = false;
        session.RestoreAutomatic();
        Assert.False(session.RecoveryRequired);
        Assert.Null(session.Fault);
    }

    [Fact]
    public void ClampingIsNotRecordedAsProtectionFaultAndNewTargetCanReplaceActiveTarget()
    {
        var session = Create();
        session.Start(9000);
        Assert.Equal(4083, session.TargetRpm);
        Assert.Null(session.Fault);
        session.Start(3000);
        Assert.Equal(3000, session.TargetRpm);
        Assert.True(session.IsActive);
        Assert.Equal(2, _writes.Count);
    }

    [Fact]
    public void StallFaultRetainsErrorUntilAcknowledged()
    {
        var session = Create();
        session.Start(2500);
        Tick(session, Snapshot(0));
        Tick(session, Snapshot(0));
        Tick(session, Snapshot(0));
        Assert.False(session.IsActive);
        Assert.Contains("RPM 不高于 100", session.Fault);
        Assert.Equal(2500, session.ErrorRpm);
        Tick(session);
        Assert.Equal(2500, session.ErrorRpm);
    }

    [Fact]
    public void MinimumDutyLimitHasTypedUnreachableState()
    {
        var feedback = new FanRpmFeedbackController();
        feedback.Start(2100, 40);
        Assert.Equal(FanRpmFeedbackState.TargetUnreachable, feedback.Evaluate(3000).State);
    }

    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        private readonly DateTimeOffset _epoch = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _epoch.AddTicks(_ticks);
        public void Advance(TimeSpan interval) => _ticks += interval.Ticks;
    }
}
