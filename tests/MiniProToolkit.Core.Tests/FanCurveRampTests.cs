using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCurveRampTests
{
    [Fact]
    public void UsesSeparateRampDownThresholds()
    {
        var settings = new FanCurveSettings([
            new(40, 40, 35),
            new(80, 80, 70)
        ], HysteresisC: 1, MaximumDutyChangePerTick: 100);
        var engine = new FanCurveEngine(settings);

        Assert.Equal(40, engine.Evaluate(Snapshot(40)).DutyPercent);
        Assert.Equal(80, engine.Evaluate(Snapshot(80)).DutyPercent);
        var result = engine.Evaluate(Snapshot(69));

        Assert.Equal(FanCurveAction.SetDuty, result.Action);
        Assert.Equal(79, result.DutyPercent);
    }

    [Fact]
    public void AppliesIndependentDecreaseRateLimit()
    {
        var settings = new FanCurveSettings([
            new(40, 40, 35),
            new(80, 80, 70)
        ], MaximumDutyChangePerTick: 100, MaximumDutyDecreasePerTick: 3);
        var engine = new FanCurveEngine(settings);

        engine.Evaluate(Snapshot(40));
        engine.Evaluate(Snapshot(80));
        var result = engine.Evaluate(Snapshot(40));

        Assert.Equal(FanCurveAction.SetDuty, result.Action);
        Assert.Equal(77, result.DutyPercent);
    }

    [Fact]
    public void EmergencyStillBypassesRampAndHysteresis()
    {
        var settings = FanCurveSettings.Default with { MaximumDutyIncreasePerTick = 1 };
        var engine = new FanCurveEngine(settings);

        engine.Evaluate(Snapshot(60));
        var result = engine.Evaluate(Snapshot(90));

        Assert.Equal(100, result.DutyPercent);
    }

    private static HardwareSnapshot Snapshot(double temperature) => new(
        DateTimeOffset.UtcNow,
        temperature,
        2200,
        0,
        PowerMode.Balance,
        20,
        40,
        FanControlMode.FirmwareAutomatic,
        null,
        null);
}
