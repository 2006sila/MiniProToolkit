using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// Pure Core coverage for the optional RPM-per-second ramp limit. No hardware is touched;
// every sample is a synthetic HardwareSnapshot with an explicit timestamp. Each test uses
// a base time slightly in the past so all samples stay inside the 5 s freshness window.
public sealed class FanCurveRpmRateLimitTests
{
    private static FanRpmRateLimit Limit => new(
        MaximumRiseRpmPerSecond: 600,
        MaximumFallRpmPerSecond: 1200,
        MinimumSampleSeconds: 1);

    private static FanCurveSettings Settings(int maximumDutyChangePerTick = 100, double hysteresisC = 1) => new(
        [new(40, 40), new(90, 100)],
        HysteresisC: hysteresisC,
        MaximumDutyChangePerTick: maximumDutyChangePerTick,
        RpmRateLimit: Limit);

    private static DateTimeOffset Base => DateTimeOffset.UtcNow.AddSeconds(-4);

    [Fact]
    public void LimitationsAreExpressedInRpmPerSecondNotDutyPerTick()
    {
        var rise = FanRpmRateLimiter.Evaluate(Limit, 1, 2000, 3200, TimeSpan.FromSeconds(1));
        Assert.Equal(FanRpmRateLimitOutcome.RiseLimited, rise.Outcome);
        Assert.False(rise.AllowDutyChange);
        Assert.Equal(1200, rise.MeasuredRpmPerSecond!.Value, 3);

        var withinRise = FanRpmRateLimiter.Evaluate(Limit, 1, 2000, 2500, TimeSpan.FromSeconds(1));
        Assert.Equal(FanRpmRateLimitOutcome.WithinLimit, withinRise.Outcome);
        Assert.True(withinRise.AllowDutyChange);

        var fall = FanRpmRateLimiter.Evaluate(Limit, -1, 3000, 1500, TimeSpan.FromSeconds(1));
        Assert.Equal(FanRpmRateLimitOutcome.FallLimited, fall.Outcome);
        Assert.False(fall.AllowDutyChange);

        var withinFall = FanRpmRateLimiter.Evaluate(Limit, -1, 3000, 2000, TimeSpan.FromSeconds(1));
        Assert.Equal(FanRpmRateLimitOutcome.WithinLimit, withinFall.Outcome);
    }

    [Fact]
    public void RateUsesTheActualSampleInterval()
    {
        // The same 2400 RPM delta spread over two seconds is only 1200 RPM/s.
        var decision = FanRpmRateLimiter.Evaluate(Limit, 1, 2000, 4400, TimeSpan.FromSeconds(2));

        Assert.Equal(FanRpmRateLimitOutcome.RiseLimited, decision.Outcome);
        Assert.Equal(1200, decision.MeasuredRpmPerSecond!.Value, 3);
    }

    [Theory]
    [InlineData(null, 2000)]
    [InlineData(2000, null)]
    [InlineData(-5, 2000)]
    [InlineData(2000, 30000)]
    public void MissingOrImplausibleRpmAllowsTheDutyPolicyToStayInCharge(int? previousRpm, int? currentRpm)
    {
        var decision = FanRpmRateLimiter.Evaluate(Limit, 1, previousRpm, currentRpm, TimeSpan.FromSeconds(1));

        Assert.Equal(FanRpmRateLimitOutcome.RpmUnavailable, decision.Outcome);
        Assert.True(decision.AllowDutyChange);
    }

    [Fact]
    public void ShortSampleIntervalIsNotExtrapolated()
    {
        var decision = FanRpmRateLimiter.Evaluate(Limit, 1, 1000, 4000, TimeSpan.FromSeconds(0.5));

        Assert.Equal(FanRpmRateLimitOutcome.SampleIntervalTooShort, decision.Outcome);
        Assert.True(decision.AllowDutyChange);
        Assert.Null(decision.MeasuredRpmPerSecond);
    }

    [Theory]
    [InlineData(10, 1200, 1.0)]
    [InlineData(600, 10, 1.0)]
    [InlineData(600, 1200, 0.1)]
    [InlineData(600, 1200, 20.0)]
    public void OutOfRangeLimitsAreRejected(int rise, int fall, double minimumSampleSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FanRpmRateLimit(rise, fall, minimumSampleSeconds).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FanCurveSettings([new(40, 40)], RpmRateLimit: new FanRpmRateLimit(rise, fall, minimumSampleSeconds))
                .Validate());
    }

    [Fact]
    public void NonFiniteSampleIntervalIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FanRpmRateLimit(600, 1200, double.NaN).Validate());
    }

    [Fact]
    public void EngineHoldsDutyWhileRpmRisesFasterThanTheLimit()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 1200, start)).DutyPercent);
        Assert.Equal(64, engine.Evaluate(Snapshot(60, 1200, start.AddSeconds(1))).DutyPercent);

        // 800 RPM/s measured over one second exceeds the 600 RPM/s rise limit.
        var held = engine.Evaluate(Snapshot(70, 2000, start.AddSeconds(2)));
        Assert.Equal(FanCurveAction.NoChange, held.Action);
        Assert.Equal(64, engine.CurrentDuty);
        Assert.Contains("RPM", held.Reason);

        // Once the fan has settled, the pending target is applied.
        Assert.Equal(76, engine.Evaluate(Snapshot(70, 2050, start.AddSeconds(3))).DutyPercent);
    }

    [Fact]
    public void EngineHoldsDutyWhileRpmFallsFasterThanTheLimit()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        Assert.Equal(88, engine.Evaluate(Snapshot(80, 3000, start)).DutyPercent);
        // 60 °C on the way down uses the ramp-down thresholds (hysteresis 1), so 65 %.
        Assert.Equal(65, engine.Evaluate(Snapshot(60, 3000, start.AddSeconds(1))).DutyPercent);

        var held = engine.Evaluate(Snapshot(40, 1500, start.AddSeconds(2)));
        Assert.Equal(FanCurveAction.NoChange, held.Action);
        Assert.Equal(65, engine.CurrentDuty);

        // Within the fall limit again: the ramp-down curve target at 40 °C is 41 %.
        Assert.Equal(41, engine.Evaluate(Snapshot(40, 1450, start.AddSeconds(3))).DutyPercent);
    }

    [Fact]
    public void MissingRpmKeepsTheExistingDutyPerTickPolicy()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings(maximumDutyChangePerTick: 5));

        Assert.Equal(40, engine.Evaluate(Snapshot(40, null, start)).DutyPercent);
        Assert.Equal(45, engine.Evaluate(Snapshot(60, null, start.AddSeconds(1))).DutyPercent);
        Assert.Equal(50, engine.Evaluate(Snapshot(70, null, start.AddSeconds(2))).DutyPercent);
        // Only the duty limit acts, so the pending target of 76 is reached step by step.
        Assert.Equal(55, engine.Evaluate(Snapshot(70, null, start.AddSeconds(3))).DutyPercent);
        Assert.Equal(FanCurveAction.SetDuty, engine.Evaluate(Snapshot(70, null, start.AddSeconds(4))).Action);
    }

    [Fact]
    public void RpmGapDoesNotFabricateARateLimit()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 1200, start)).DutyPercent);
        Assert.Equal(64, engine.Evaluate(Snapshot(60, null, start.AddSeconds(1))).DutyPercent);
        // 4000 RPM looks like a huge jump, but the previous sample was unusable.
        Assert.Equal(76, engine.Evaluate(Snapshot(70, 4000, start.AddSeconds(2))).DutyPercent);
    }

    [Fact]
    public void EmergencyFullDutyBypassesTheRpmRateLimit()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings(maximumDutyChangePerTick: 5));

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 3000, start)).DutyPercent);
        var emergency = engine.Evaluate(Snapshot(90, 3500, start.AddSeconds(1)));

        Assert.Equal(100, emergency.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Emergency, emergency.Level);
    }

    [Fact]
    public void InvalidOrStaleReadingsStillRequestAutomaticControl()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());
        Assert.Equal(40, engine.Evaluate(Snapshot(40, 1200, start)).DutyPercent);

        var failed = engine.Evaluate(HardwareSnapshot.ErrorSnapshot("sensor unavailable"));
        Assert.Equal(FanCurveAction.RestoreAutomatic, failed.Action);

        var stale = Snapshot(60, 1200, start) with { Timestamp = DateTimeOffset.UtcNow.AddSeconds(-6) };
        Assert.Equal(FanCurveAction.RestoreAutomatic, new FanCurveEngine(Settings()).Evaluate(stale).Action);
    }

    [Fact]
    public void ShortSamplesBetweenFullWindowsCannotBypassTheRateLimit()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        // t = 0: the first valid sample establishes the window anchor (1200 RPM at 40 C).
        Assert.Equal(40, engine.Evaluate(Snapshot(40, 1200, start)).DutyPercent);

        // t = 0.95: the window is still incomplete, so only the duty policy applies.
        Assert.Equal(64, engine.Evaluate(Snapshot(60, 1800, start.AddMilliseconds(950))).DutyPercent);

        // t = 1.5: the anchor window is now complete and (2400 - 1200) / 1.5 s = 800 RPM/s
        // exceeds the 600 RPM/s rise limit. With a baseline that followed every short sample
        // only 0.55 s would have been measured, the interval would count as "too short" and
        // the 76 % target would slip through.
        var held = engine.Evaluate(Snapshot(70, 2400, start.AddSeconds(1.5)));
        Assert.Equal(FanCurveAction.NoChange, held.Action);
        Assert.Equal(64, engine.CurrentDuty);
        Assert.Contains("RPM", held.Reason);

        // t = 1.9: only 0.4 s after the new anchor, so the duty policy decides again.
        Assert.Equal(88, engine.Evaluate(Snapshot(80, 3000, start.AddSeconds(1.9))).DutyPercent);

        // t = 2.5: a complete window again; 1200 RPM/s keeps the 94 % target held.
        var heldAgain = engine.Evaluate(Snapshot(85, 3600, start.AddSeconds(2.5)));
        Assert.Equal(FanCurveAction.NoChange, heldAgain.Action);
        Assert.Equal(88, engine.CurrentDuty);
        Assert.Contains("RPM", heldAgain.Reason);
    }

    [Fact]
    public void WindowAnchorDoesNotAdvanceOnShortSamples()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 1200, start)).DutyPercent);
        // 0.5 s later nothing is requested, so this only must not move the anchor.
        Assert.Equal(FanCurveAction.NoChange,
            engine.Evaluate(Snapshot(40, 1800, start.AddSeconds(0.5))).Action);
        Assert.Equal(40, engine.CurrentDuty);

        // Measured from t = 0 over the full second this is 1200 RPM/s and has to hold. Had the
        // 0.5 s sample moved the anchor, only 0.5 s would be measured and 64 % would pass.
        var held = engine.Evaluate(Snapshot(60, 2400, start.AddSeconds(1)));
        Assert.Equal(FanCurveAction.NoChange, held.Action);
        Assert.Equal(40, engine.CurrentDuty);
        Assert.Contains("RPM", held.Reason);
    }

    [Fact]
    public void CompleteWindowAdvancesTheAnchor()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 1200, start)).DutyPercent);
        Assert.Equal(FanCurveAction.NoChange,
            engine.Evaluate(Snapshot(40, 1800, start.AddSeconds(0.5))).Action);
        Assert.Equal(40, engine.CurrentDuty);
        // Held, and the measured sample becomes the next anchor at 2400 RPM / t = 1 s.
        Assert.Equal(FanCurveAction.NoChange,
            engine.Evaluate(Snapshot(60, 2400, start.AddSeconds(1))).Action);

        // 0.5 s after the anchor the window is incomplete, so the duty policy advances.
        Assert.Equal(64, engine.Evaluate(Snapshot(60, 2450, start.AddSeconds(1.5))).DutyPercent);

        // A full second after the 1 s anchor: only 100 RPM/s, so the 76 % target is applied.
        // Without the advance the anchor would still be t = 0 and 650 RPM/s would hold.
        Assert.Equal(76, engine.Evaluate(Snapshot(70, 2500, start.AddSeconds(2))).DutyPercent);
    }

    [Fact]
    public void LongGapResetsInsteadOfAveragingAcrossThePause()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 900, start)).DutyPercent);

        // (20000 - 900) / 12 s = 1592 RPM/s would hold if the paused interval were averaged;
        // a gap beyond the window cap is treated as a fresh baseline instead.
        var afterPause = start.AddSeconds(12);
        Assert.Equal(64, engine.Evaluate(Snapshot(60, 20000, afterPause), afterPause).DutyPercent);

        // The post-pause sample is the new anchor, so normal 1 s measurement resumes.
        var next = afterPause.AddSeconds(1);
        Assert.Equal(76, engine.Evaluate(Snapshot(70, 20100, next), next).DutyPercent);
    }

    [Fact]
    public void GapBelowTheWindowCapIsStillMeasured()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 900, start)).DutyPercent);
        // (4000 - 900) / 4 s = 775 RPM/s is still an honest rate inside the window cap.
        var later = engine.Evaluate(Snapshot(60, 4000, start.AddSeconds(4)));
        Assert.Equal(FanCurveAction.NoChange, later.Action);
        Assert.Equal(40, engine.CurrentDuty);
        Assert.Contains("RPM", later.Reason);
    }

    [Fact]
    public void RpmGapInsideTheWindowClearsTheAnchor()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings());

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 1200, start)).DutyPercent);
        // An unusable sample drops the anchor instead of being remembered.
        Assert.Equal(64, engine.Evaluate(Snapshot(60, null, start.AddSeconds(0.9))).DutyPercent);
        // The next valid sample is a fresh anchor, so the 2800 RPM jump is never a rate.
        Assert.Equal(76, engine.Evaluate(Snapshot(70, 4000, start.AddSeconds(1.9))).DutyPercent);
    }

    [Fact]
    public void EmergencyFullDutyBypassesTheRateLimitAndKeepsTheWindowFresh()
    {
        var start = Base;
        var engine = new FanCurveEngine(Settings(maximumDutyChangePerTick: 5));

        Assert.Equal(40, engine.Evaluate(Snapshot(40, 3000, start)).DutyPercent);
        var emergency = engine.Evaluate(Snapshot(90, 9000, start.AddSeconds(1)));

        Assert.Equal(100, emergency.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Emergency, emergency.Level);

        // The emergency sample is tracked, so the 100 RPM/s afterwards is measured from it and
        // the regular duty policy resumes (76 % target, 5 % per tick).
        var cooled = engine.Evaluate(Snapshot(70, 9100, start.AddSeconds(2)));
        Assert.Equal(FanCurveAction.SetDuty, cooled.Action);
        Assert.Equal(95, cooled.DutyPercent);
    }

    [Fact]
    public void RpmRateLimitStaysOffByDefault()
    {
        var start = Base;
        var defaultSettings = new FanCurveSettings(
            [new(40, 40), new(90, 100)],
            HysteresisC: 1,
            MaximumDutyChangePerTick: 100);

        Assert.Null(defaultSettings.RpmRateLimit);
        Assert.Null(FanCurveSettings.Default.RpmRateLimit);

        var engine = new FanCurveEngine(defaultSettings);
        Assert.Equal(40, engine.Evaluate(Snapshot(40, 1200, start)).DutyPercent);
        // Wild RPM jumps cannot hold anything while the limit is opt-in and switched off.
        Assert.Equal(64, engine.Evaluate(Snapshot(60, 9000, start.AddSeconds(0.2))).DutyPercent);
        Assert.Equal(76, engine.Evaluate(Snapshot(70, 12000, start.AddSeconds(0.4))).DutyPercent);
    }

    private static HardwareSnapshot Snapshot(double temperature, int? fan1Rpm, DateTimeOffset timestamp) => new(
        timestamp,
        temperature,
        fan1Rpm,
        null,
        PowerMode.Balance,
        20,
        40,
        FanControlMode.FirmwareAutomatic,
        null,
        null);
}
