using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// A single missing/out-of-range Fan1Rpm sample must not hand control back to EC automatic, but
// temperature, WMI error, stale/future timestamps and the 90 °C latch keep their fail-closed
// semantics and stay outside the tolerance window.
public sealed class FixedRpmBadSampleToleranceTests
{
    private readonly FakeClock _clock = new();
    private readonly List<int> _writes = [];
    private int _restores;

    private FixedRpmControlSession Create(int? rpmBadSampleTolerance = null) => rpmBadSampleTolerance is { } tolerance
        ? new FixedRpmControlSession(FanCalibration.Default, Write, Restore, _clock, tolerance)
        : new FixedRpmControlSession(FanCalibration.Default, Write, Restore, _clock);

    private void Write(int duty) => _writes.Add(duty);

    private void Restore() => _restores++;

    private HardwareSnapshot Snapshot(int? rpm = 3000, double? temperature = 60) => new(
        _clock.GetUtcNow(), temperature, rpm, 0, PowerMode.Balance, 20, 40, FanControlMode.FixedRpm, 64, null);

    private void Tick(FixedRpmControlSession session, HardwareSnapshot snapshot, bool guardian = true, bool lease = true)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        session.Tick(snapshot, guardian, lease);
    }

    [Fact]
    public void FirstMissingRpmSampleHoldsDutyWithoutRestoringEcAutomatic()
    {
        var session = Create();
        session.Start(3000);
        Assert.Single(_writes);

        Tick(session, Snapshot(rpm: null));

        Assert.True(session.IsActive);
        Assert.False(session.IsEmergencyFullSpeed);
        Assert.Equal(FanRpmFeedbackState.WaitingForRpm, session.State);
        Assert.Null(session.ErrorRpm);
        Assert.Equal(1, session.BadRpmSamples);
        Assert.Equal(0, _restores);
        Assert.Single(_writes); // No duty write, no error recovery.
    }

    [Fact]
    public void ThirdConsecutiveBadRpmSampleFailsClosed()
    {
        var session = Create();
        session.Start(3000);
        Tick(session, Snapshot(rpm: null));
        Tick(session, Snapshot(rpm: 65535));

        Assert.True(session.IsActive);
        Assert.Equal(2, session.BadRpmSamples);
        Assert.Equal(0, _restores);
        Assert.Single(_writes);

        Tick(session, Snapshot(rpm: null));

        Assert.False(session.IsActive);
        Assert.False(session.IsEmergencyFullSpeed);
        Assert.False(session.RecoveryRequired);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Contains("无效 RPM", session.Fault);
        Assert.Equal(1, _restores);
        Assert.Single(_writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(65535)]
    public void MissingAndOutOfRangeReadingsShareOneCounter(int? badRpm)
    {
        var session = Create();
        session.Start(3000);
        Tick(session, Snapshot(rpm: badRpm));
        Assert.True(session.IsActive);
        Assert.Equal(1, session.BadRpmSamples);
        Tick(session, Snapshot(rpm: null));
        Assert.True(session.IsActive);
        Assert.Equal(2, session.BadRpmSamples);
    }

    [Fact]
    public void ValidSampleResetsTheToleranceWindowAndResumesClosedLoop()
    {
        var session = Create();
        session.Start(3000);
        Tick(session, Snapshot(rpm: null));
        Tick(session, Snapshot(rpm: null));

        Tick(session, Snapshot()); // Valid reading inside the deadband.

        Assert.True(session.IsActive);
        Assert.Equal(0, session.BadRpmSamples);
        Assert.Equal(FanRpmFeedbackState.Stable, session.State);
        Assert.Equal(0, _restores);

        Tick(session, Snapshot(rpm: null));
        Tick(session, Snapshot(rpm: null));
        Assert.True(session.IsActive);
        Assert.Equal(2, session.BadRpmSamples);
        Assert.Equal(0, _restores);

        Tick(session, Snapshot(rpm: null));
        Assert.False(session.IsActive);
        Assert.Equal(1, _restores);
    }

    [Fact]
    public void ValidSampleAfterBadReadingsStillDrivesTheLoop()
    {
        var session = Create();
        session.Start(3000);
        Tick(session, Snapshot(rpm: null));
        var writesBefore = _writes.Count;

        Tick(session, Snapshot(rpm: 1800)); // Large error: closed loop adjusts again.

        Assert.True(session.IsActive);
        Assert.Equal(0, session.BadRpmSamples);
        Assert.Equal(writesBefore + 1, _writes.Count);
        Assert.Equal(FanRpmFeedbackState.Adjusting, session.State);
        Assert.Equal(1200, session.ErrorRpm);
    }

    [Theory]
    [InlineData("sensor")]
    [InlineData("nan")]
    [InlineData("wmi")]
    [InlineData("stale")]
    [InlineData("future")]
    public void NonRpmFaultsStillFailImmediatelyWithABadRpmPresent(string scenario)
    {
        var session = Create();
        session.Start(3000);
        var snapshot = Snapshot(rpm: null);
        snapshot = scenario switch
        {
            "sensor" => snapshot with { CpuTemperatureC = null },
            "nan" => snapshot with { CpuTemperatureC = double.NaN },
            "wmi" => snapshot with { Error = "WMI unavailable" },
            "stale" => snapshot with { Timestamp = snapshot.Timestamp.AddSeconds(-10) },
            _ => snapshot with { Timestamp = snapshot.Timestamp.AddMinutes(1) }
        };

        Tick(session, snapshot);

        Assert.False(session.IsActive);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Equal(0, session.BadRpmSamples); // The tolerance window never started.
        Assert.Equal(1, _restores);
        Assert.Single(_writes);
    }

    [Fact]
    public void NinetyDegreesLatchesFullSpeedEvenWhenRpmIsMissing()
    {
        var session = Create();
        session.Start(2500);

        Tick(session, Snapshot(rpm: null, temperature: 90));

        Assert.True(session.IsEmergencyFullSpeed);
        Assert.False(session.IsActive);
        Assert.Equal(FanRpmFeedbackState.Fault, session.State);
        Assert.Equal(100, _writes.Last());
        Assert.Equal(0, _restores); // The latch never hands control back on its own.
        Assert.Equal(0, session.BadRpmSamples);

        // Still latched: no duty reduction, no restore, even with the fan reading missing.
        var writesBefore = _writes.Count;
        Tick(session, Snapshot(rpm: null, temperature: 60));
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(writesBefore, _writes.Count);
        Assert.Equal(0, _restores);

        // A bad temperature while latched keeps the latch and requests EC automatic once.
        Tick(session, Snapshot(rpm: null, temperature: null));
        Assert.True(session.IsEmergencyFullSpeed);
        Assert.Equal(1, _restores);
        Assert.Equal(100, _writes.Last());

        session.RestoreAutomatic();
        Assert.False(session.IsEmergencyFullSpeed);
    }

    [Fact]
    public void ZeroToleranceKeepsTheLegacyImmediateFail()
    {
        var session = Create(rpmBadSampleTolerance: 0);
        session.Start(3000);

        Tick(session, Snapshot(rpm: null));

        Assert.False(session.IsActive);
        Assert.Equal(1, _restores);
        Assert.Single(_writes);
    }

    [Fact]
    public void ToleranceIsValidatedAndExposed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(rpmBadSampleTolerance: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(rpmBadSampleTolerance: 31));

        var session = Create();
        session.Start(3000);
        Assert.Equal(0, session.BadRpmSamples);
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
