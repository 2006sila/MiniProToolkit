using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// The optional RPM feedback loop turns a curve duty target into an expected RPM. Production must
// feed it the application's *real* FanCalibration instead of the shipped default table, the old
// constructor shape must stay on the default table, and a feedback correction must stay a pure
// upward duty request that still goes through the existing lease gate.
public sealed class FanCurveCalibrationWiringTests
{
    // A clearly different table: 40 % -> 400 RPM, 100 % -> 1000 RPM, linear in between.
    private static readonly FanCalibration LowCalibration = new([new(40, 400), new(100, 1000)]);

    private static FanCurveSettings Curve(FanCurveRpmFeedbackSettings? feedback = null) => new(
        [new(40, 40), new(90, 100)],
        HysteresisC: 1,
        MaximumDutyChangePerTick: 100,
        RpmFeedback: feedback);

    private static FanCurveRpmFeedbackSettings Feedback(int stallSampleCount = 2) =>
        new(StallSampleCount: stallSampleCount);

    private static HardwareSnapshot Snapshot(double temperature, int? rpm) => new(
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
    public void EngineUsesTheProvidedCalibrationForTheExpectedRpm()
    {
        // 42 % duty expects 420 RPM on the low table, i.e. below MinimumExpectedRpm, so a silent
        // tachometer must not be judged at all.
        var low = new FanCurveEngine(Curve(Feedback()), LowCalibration);
        Assert.Same(LowCalibration, low.Calibration);
        for (var pass = 0; pass < 5; pass++)
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, low.Evaluate(Snapshot(42, 0)).Action);

        // The same curve on the shipped default table expects ~2180 RPM at 42 % duty, so 0 RPM is
        // a stall and becomes a fault.
        var shipped = new FanCurveEngine(Curve(Feedback()));
        Assert.Same(FanCalibration.Default, shipped.Calibration);
        Assert.NotEqual(FanCurveAction.RestoreAutomatic, shipped.Evaluate(Snapshot(42, 0)).Action);
        var fault = shipped.Evaluate(Snapshot(42, 0));
        Assert.Equal(FanCurveAction.RestoreAutomatic, fault.Action);
        Assert.True(fault.RpmFeedbackFault);
        Assert.StartsWith(FanCurveRpmFeedbackController.FaultReasonPrefix, fault.Reason);
    }

    [Fact]
    public void ExplicitDefaultCalibrationMatchesTheLegacyConstructor()
    {
        foreach (var temperature in new[] { 42d, 60, 75, 90 })
        {
            var legacy = new FanCurveEngine(Curve(Feedback())).Evaluate(Snapshot(temperature, 1200));
            var explicitDefault = new FanCurveEngine(Curve(Feedback()), FanCalibration.Default)
                .Evaluate(Snapshot(temperature, 1200));

            Assert.Equal(legacy.Action, explicitDefault.Action);
            Assert.Equal(legacy.DutyPercent, explicitDefault.DutyPercent);
            Assert.Equal(legacy.Level, explicitDefault.Level);
        }
    }

    [Fact]
    public void ControllerPassesTheCalibrationThroughToTheFeedbackLoop()
    {
        var lowTarget = new FakeTarget();
        using var low = new FanCurveController(lowTarget, Curve(Feedback()), LowCalibration);
        low.Enable();
        for (var pass = 0; pass < 5; pass++)
            Assert.True(low.Apply(Snapshot(42, 0)).Success);
        Assert.True(low.IsEnabled);
        Assert.Equal(0, lowTarget.RestoreCount);

        var shippedTarget = new FakeTarget();
        using var shipped = new FanCurveController(shippedTarget, Curve(Feedback()));
        shipped.Enable();
        Assert.True(shipped.Apply(Snapshot(42, 0)).Success);
        var fault = shipped.Apply(Snapshot(42, 0));

        Assert.Equal(FanCurveAction.RestoreAutomatic, fault.Decision.Action);
        Assert.Equal(1, shippedTarget.RestoreCount);
        Assert.False(shipped.IsEnabled);
    }

    [Fact]
    public void FeedbackOnlyRaisesDutyAndNeverBelowTheCurveTarget()
    {
        var raising = new FanCurveRpmFeedbackController(
            new FanCurveRpmFeedbackSettings(), FanCalibration.Default);
        var raise = raising.Evaluate(60, 100);
        Assert.Equal(FanCurveRpmFeedbackAction.RaiseDuty, raise.Action);
        Assert.True(raise.CommandDuty > 60);

        // Far too many RPM for the requested duty: the loop has no way to command less.
        var overspeed = new FanCurveRpmFeedbackController(
            new FanCurveRpmFeedbackSettings(), FanCalibration.Default).Evaluate(60, 20000);
        Assert.NotEqual(FanCurveRpmFeedbackAction.RestoreAutomatic, overspeed.Action);
        Assert.Equal(60, overspeed.CommandDuty);
        Assert.False(overspeed.RequestsRaise);
    }

    [Fact]
    public void FeedbackCorrectionStillGoesThroughTheLeaseGate()
    {
        var gate = new FanCurveLeaseGate();
        gate.SetPass(false);
        var target = new GatedTarget(gate);
        using var controller = new FanCurveController(
            target, Curve(Feedback(stallSampleCount: 30)), FanCalibration.Default);
        controller.Enable();

        // The curve asks for a raise at 70 °C and the feedback loop adds more, but the gate is
        // closed, so no duty is written and the controller takes the ordinary failure path.
        var result = controller.Apply(Snapshot(70, 500));

        Assert.False(result.Success);
        Assert.Equal(0, target.DutyWrites);
        Assert.Equal(0, target.LeaseWrites);
        Assert.Equal(1, target.RestoreCount);
        Assert.False(controller.IsEnabled);
    }

    [Fact]
    public void LatchKeepsWinningOverFeedbackEvenWithARealCalibration()
    {
        var settings = Curve(Feedback()) with { LatchCurveEmergency = true };
        var engine = new FanCurveEngine(settings, LowCalibration);

        engine.Evaluate(Snapshot(90, 0));
        for (var pass = 0; pass < 5; pass++)
        {
            var decision = engine.Evaluate(Snapshot(45, 0));
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, decision.Action);
            Assert.True(decision.EmergencyLatched);
            Assert.Equal(100, engine.CurrentDuty);
        }
    }

    private sealed class FakeTarget : IFanControlTarget
    {
        public int RestoreCount { get; private set; }
        public int DutyCount { get; private set; }

        public void SetCurveDuty(int dutyPercent) => DutyCount++;

        public void RestoreAutomatic() => RestoreCount++;
    }

    // Same shape as the App target: the duty write and the lease publication are both behind the gate.
    private sealed class GatedTarget(FanCurveLeaseGate gate) : IFanControlTarget
    {
        public int DutyWrites { get; private set; }
        public int LeaseWrites { get; private set; }
        public int RestoreCount { get; private set; }

        public void SetCurveDuty(int dutyPercent)
        {
            if (!gate.TryWrite(() => { DutyWrites++; LeaseWrites++; }))
                throw new InvalidOperationException("control lease is invalid; curve write refused");
        }

        public void RestoreAutomatic() => RestoreCount++;
    }
}
