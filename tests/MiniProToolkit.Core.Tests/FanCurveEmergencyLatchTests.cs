using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// Optional 90 °C emergency latch plus configuration compatibility for the optional RPM-feedback
// fields. The latch is off by default, holds full speed after a real 90 °C event and is released
// only by the curve controller's Disable/Restore path.
public sealed class FanCurveEmergencyLatchTests
{
    private static FanCurveSettings Curve(bool latch, FanCurveRpmFeedbackSettings? feedback = null) => new(
        [new(40, 40), new(90, 100)],
        HysteresisC: 1,
        MaximumDutyChangePerTick: 100,
        RpmFeedback: feedback,
        LatchCurveEmergency: latch);

    private static HardwareSnapshot Snapshot(double temperature, int? rpm = 3000) => new(
        DateTimeOffset.UtcNow,
        temperature,
        rpm,
        0,
        PowerMode.Balance,
        20,
        40,
        FanControlMode.FirmwareAutomatic,
        null,
        null);

    [Fact]
    public void LatchIsOffByDefault()
    {
        Assert.False(FanCurveSettings.Default.LatchCurveEmergency);
        Assert.Null(FanCurveSettings.Default.RpmFeedback);
        Assert.False(FanCurveSettings.Default.HasRpmFeedback);

        var engine = new FanCurveEngine(Curve(latch: false));
        Assert.Equal(100, engine.Evaluate(Snapshot(90)).DutyPercent);
        Assert.False(engine.EmergencyLatched);

        // Without the latch the curve cools back down on its own.
        var cool = new FanCurveEngine(Curve(latch: false));
        cool.Evaluate(Snapshot(90));
        cool.Evaluate(Snapshot(60));
        Assert.True(cool.CurrentDuty < 100);
        Assert.False(cool.EmergencyLatched);
    }

    [Fact]
    public void LatchedEmergencyHoldsFullSpeedAfterTheCpuCoolsDown()
    {
        var engine = new FanCurveEngine(Curve(latch: true));

        var hot = engine.Evaluate(Snapshot(90));
        Assert.Equal(100, hot.DutyPercent);
        Assert.True(hot.EmergencyLatched);
        Assert.True(engine.EmergencyLatched);

        var cool = engine.Evaluate(Snapshot(60));
        Assert.NotEqual(FanCurveAction.RestoreAutomatic, cool.Action);
        Assert.Equal(100, engine.CurrentDuty);
        Assert.Equal(FanTemperatureLevel.Emergency, engine.ActiveLevel);
        Assert.True(cool.EmergencyLatched);

        var cold = engine.Evaluate(Snapshot(45));
        Assert.Equal(100, engine.CurrentDuty);
        Assert.True(engine.EmergencyLatched);
    }

    [Fact]
    public void LatchedEmergencyCannotBeReleasedByABadSensorSample()
    {
        var engine = new FanCurveEngine(Curve(latch: true));
        engine.Evaluate(Snapshot(90));

        var stale = engine.Evaluate(new HardwareSnapshot(
            DateTimeOffset.UtcNow, null, 3000, 0, PowerMode.Balance, 20, 40,
            FanControlMode.FirmwareAutomatic, null, null));

        Assert.Equal(FanCurveAction.RestoreAutomatic, stale.Action);
        // The engine alone never drops the latch; only the controller restore path may.
        Assert.True(engine.EmergencyLatched);
    }

    [Fact]
    public void LatchedEmergencyStillWinsOverRpmFeedback()
    {
        var feedback = new FanCurveRpmFeedbackSettings(StallSampleCount: 2);
        var engine = new FanCurveEngine(Curve(latch: true, feedback));

        engine.Evaluate(Snapshot(90, 0));
        for (var pass = 0; pass < 5; pass++)
        {
            var decision = engine.Evaluate(Snapshot(60, 0));
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, decision.Action);
            Assert.Equal(100, engine.CurrentDuty);
        }
    }

    [Fact]
    public void DisableClearsTheLatchAndRestoresAutomatic()
    {
        var target = new FakeTarget();
        var controller = new FanCurveController(target, Curve(latch: true));
        controller.Enable();
        controller.Apply(Snapshot(90, 0));
        controller.Apply(Snapshot(60, 0));
        Assert.True(controller.EmergencyLatched);
        Assert.Equal(100, target.LastDuty);

        controller.Disable();

        Assert.False(controller.EmergencyLatched);
        Assert.False(controller.IsEnabled);
        Assert.Equal(1, target.RestoreCount);
    }

    [Fact]
    public void RestoreAutomaticPathClearsTheLatch()
    {
        var target = new FakeTarget();
        var controller = new FanCurveController(target, Curve(latch: true));
        controller.Enable();
        controller.Apply(Snapshot(90));
        Assert.True(controller.EmergencyLatched);

        var restored = controller.Apply(new HardwareSnapshot(
            DateTimeOffset.UtcNow, null, 3000, 0, PowerMode.Balance, 20, 40,
            FanControlMode.FirmwareAutomatic, null, null));

        Assert.Equal(FanCurveAction.RestoreAutomatic, restored.Decision.Action);
        Assert.Equal(1, target.RestoreCount);
        Assert.False(controller.IsEnabled);
        Assert.False(controller.EmergencyLatched);

        // Re-enabling the curve after a restore starts unlatched.
        controller.Enable();
        controller.Apply(Snapshot(60));
        Assert.True(target.LastDuty < 100);
        Assert.False(controller.EmergencyLatched);
        controller.Dispose();
    }

    private sealed class FakeTarget : IFanControlTarget
    {
        public int RestoreCount { get; private set; }
        public int LastDuty { get; private set; }

        public void SetCurveDuty(int dutyPercent) => LastDuty = dutyPercent;

        public void RestoreAutomatic() => RestoreCount++;
    }
}
