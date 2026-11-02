using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCurveTests
{
    [Fact]
    public void InterpolatesAndLimitsDutyRamp()
    {
        var settings = new FanCurveSettings(
            [new(40, 40), new(80, 80)],
            MaximumDutyChangePerTick: 10);
        var engine = new FanCurveEngine(settings);

        Assert.Equal(40, engine.Evaluate(Snapshot(40)).DutyPercent);
        Assert.Equal(50, engine.Evaluate(Snapshot(80)).DutyPercent);
        Assert.Equal(60, engine.Evaluate(Snapshot(80)).DutyPercent);
    }

    [Fact]
    public void HysteresisSuppressesSmallTemperatureChanges()
    {
        var engine = new FanCurveEngine(new FanCurveSettings(
            [new(40, 40), new(80, 80)], HysteresisC: 2));

        Assert.Equal(FanCurveAction.SetDuty, engine.Evaluate(Snapshot(50)).Action);
        var result = engine.Evaluate(Snapshot(51));

        Assert.Equal(FanCurveAction.NoChange, result.Action);
    }

    [Fact]
    public void EmergencyTemperatureBypassesRampAndRequestsFullDuty()
    {
        var engine = new FanCurveEngine(new FanCurveSettings(
            [new(40, 40), new(90, 90)],
            EmergencyDutyPercent: 100,
            MaximumDutyChangePerTick: 5,
            EmergencyTemperatureC: 90));

        engine.Evaluate(Snapshot(50));
        var result = engine.Evaluate(Snapshot(90));

        Assert.Equal(FanCurveAction.SetDuty, result.Action);
        Assert.Equal(100, result.DutyPercent);
    }

    [Fact]
    public void ReadFailureRequestsAutomaticControl()
    {
        var engine = new FanCurveEngine(FanCurveSettings.Default);
        engine.Evaluate(Snapshot(60));

        var result = engine.Evaluate(HardwareSnapshot.ErrorSnapshot("sensor unavailable"));

        Assert.Equal(FanCurveAction.RestoreAutomatic, result.Action);
    }

    [Fact]
    public void StaleTemperatureRequestsAutomaticControl()
    {
        var engine = new FanCurveEngine(FanCurveSettings.Default);
        var stale = Snapshot(60) with { Timestamp = DateTimeOffset.UtcNow.AddSeconds(-6) };

        var result = engine.Evaluate(stale);

        Assert.Equal(FanCurveAction.RestoreAutomatic, result.Action);
    }

    [Fact]
    public void RejectsCurveBelowMinimumDuty()
    {
        Assert.Throws<ArgumentException>(() => new FanCurveSettings(
            [new(40, 20)]).Validate());
    }

    private static HardwareSnapshot Snapshot(double temperature) => new(
        DateTimeOffset.UtcNow,
        temperature,
        2000,
        0,
        PowerMode.Balance,
        20,
        40,
        FanControlMode.FirmwareAutomatic,
        null,
        null);
}
