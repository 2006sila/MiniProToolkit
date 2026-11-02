using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// Pure Core coverage for the optional curve RPM feedback loop. No hardware is touched: every
// pass is a synthetic HardwareSnapshot and the expected RPM comes from an explicit, linear
// FanCalibration so every threshold below is readable from the test itself.
public sealed class FanCurveRpmFeedbackTests
{
    // 40 % duty = 2000 RPM, 100 % duty = 5000 RPM, linear in between.
    private static readonly FanCalibration Calibration = new([new(40, 2000), new(100, 5000)]);

    private static FanCurveRpmFeedbackSettings Feedback(
        int deadbandRpm = 150,
        int minimumExpectedRpm = 600,
        double stallRatio = 0.5,
        int stallSampleCount = 4,
        int maximumCorrectionDuty = 12,
        int maximumCorrectionStep = 2,
        int badSampleTolerance = 2) => new(
        deadbandRpm,
        minimumExpectedRpm,
        stallRatio,
        stallSampleCount,
        maximumCorrectionDuty,
        maximumCorrectionStep,
        badSampleTolerance);

    private static FanCurveSettings Curve(
        FanCurveRpmFeedbackSettings? feedback = null,
        bool latch = false,
        int maximumDutyChangePerTick = 100) => new(
        [new(40, 40), new(90, 100)],
        HysteresisC: 1,
        MaximumDutyChangePerTick: maximumDutyChangePerTick,
        RpmFeedback: feedback,
        LatchCurveEmergency: latch);

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
    public void DisabledFeedbackKeepsTheExactLegacyBehaviour()
    {
        // An absent tachometer must not change a single duty decision.
        var withTach = new FanCurveEngine(Curve());
        var withoutTach = new FanCurveEngine(Curve());

        foreach (var temperature in new[] { 45d, 55d, 65d, 75d, 85d, 90d, 80d, 60d, 45d })
        {
            var healthy = withTach.Evaluate(Snapshot(temperature, 3000));
            var stalling = withoutTach.Evaluate(Snapshot(temperature, 0));

            Assert.Equal(healthy.Action, stalling.Action);
            Assert.Equal(healthy.DutyPercent, stalling.DutyPercent);
            Assert.Equal(healthy.Level, stalling.Level);
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, stalling.Action);
        }

        // A curve with real feedback but a perfectly matching tachometer is identical as well.
        foreach (var temperature in new[] { 50d, 70d, 88d, 90d })
        {
            var plain = new FanCurveEngine(Curve()).Evaluate(Snapshot(temperature, 3000));
            var curveDuty = plain.DutyPercent!.Value;
            var followed = new FanCurveEngine(Curve(Feedback()))
                .Evaluate(Snapshot(temperature, Calibration.RpmForDuty(curveDuty)));

            Assert.Equal(plain.DutyPercent, followed.DutyPercent);
            Assert.Equal(plain.Level, followed.Level);
            Assert.Equal(plain.Action, followed.Action);
        }
    }

    [Fact]
    public void ErrorInsideTheDeadbandNeverChangesDuty()
    {
        var engine = new FanCurveEngine(Curve(Feedback(deadbandRpm: 150)));
        Assert.Equal(76, engine.Evaluate(Snapshot(70, 5000)).DutyPercent);
        var expected = Calibration.RpmForDuty(76);

        var decision = engine.Evaluate(Snapshot(70, expected - 100));

        Assert.Equal(FanCurveAction.NoChange, decision.Action);
        Assert.Equal(76, engine.CurrentDuty);
        Assert.Equal(76, engine.TargetDuty);
    }

    [Fact]
    public void CorrectionRespectsTheStepAndTheTotalCap()
    {
        var settings = Feedback(maximumCorrectionDuty: 12, maximumCorrectionStep: 2, stallSampleCount: 30);
        var engine = new FanCurveEngine(Curve(settings));
        Assert.Equal(76, engine.Evaluate(Snapshot(70, 5000)).DutyPercent);

        var duties = new List<int>();
        for (var pass = 0; pass < 10; pass++)
        {
            var decision = engine.Evaluate(Snapshot(70, 2500));
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, decision.Action);
            duties.Add(engine.CurrentDuty!.Value);
        }

        for (var index = 1; index < duties.Count; index++)
            Assert.InRange(duties[index] - duties[index - 1], 0, settings.MaximumCorrectionStep);

        // The curve target is 76, so the feedback budget can never leave 76 + 12.
        Assert.Equal(76 + settings.MaximumCorrectionDuty, duties.Max());
    }

    [Fact]
    public void ConsecutiveLowRpmRaisesAStallFaultOnlyAfterTheSampleCount()
    {
        var feedback = Feedback(stallSampleCount: 4);
        var engine = new FanCurveEngine(Curve(feedback));
        engine.Evaluate(Snapshot(70, 5000));

        for (var pass = 1; pass < feedback.StallSampleCount; pass++)
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, engine.Evaluate(Snapshot(70, 500)).Action);

        var fault = engine.Evaluate(Snapshot(70, 500));
        Assert.Equal(FanCurveAction.RestoreAutomatic, fault.Action);
        Assert.Contains("RPM", fault.Reason);

        // The evaluator stays faulted until the caller resets it.
        var controller = new FanCurveRpmFeedbackController(feedback, Calibration);
        controller.Evaluate(76, 5000);
        for (var pass = 1; pass < feedback.StallSampleCount; pass++)
            Assert.False(controller.Evaluate(76, 500).IsFault);
        var decision = controller.Evaluate(76, 500);
        Assert.True(decision.IsFault);
        Assert.Equal(FanCurveRpmFeedbackState.StallFault, decision.State);
        Assert.Equal(FanCurveRpmFeedbackAction.RestoreAutomatic, decision.Action);
        Assert.True(controller.Evaluate(76, 5000).IsFault);
    }

    [Fact]
    public void StallIsNotJudgedWhenTheExpectedRpmIsLow()
    {
        var feedback = Feedback(minimumExpectedRpm: 600, stallSampleCount: 2);

        // 40 % duty maps to 400 RPM here, far below the 600 RPM stall-judgement floor.
        var lowCalibration = new FanCalibration([new(40, 400), new(100, 4000)]);
        var low = new FanCurveRpmFeedbackController(feedback, lowCalibration);

        for (var pass = 0; pass < 5; pass++)
        {
            var lowDecision = low.Evaluate(40, 0);
            Assert.False(lowDecision.IsFault);
            Assert.Equal(FanCurveRpmFeedbackState.Stable, lowDecision.State);
            Assert.Equal(40, lowDecision.CommandDuty);
        }
    }

    [Fact]
    public void FaultIsRaisedOnlyForTheSampleCountAtDutyLimit()
    {
        var feedback = Feedback(maximumCorrectionDuty: 4, maximumCorrectionStep: 2, stallSampleCount: 3);
        var controller = new FanCurveRpmFeedbackController(feedback, Calibration);

        // 5000 RPM are expected at 76 % duty; 3400 RPM is far outside the deadband but above stall.
        var first = controller.Evaluate(76, 3400);
        Assert.Equal(FanCurveRpmFeedbackAction.RaiseDuty, first.Action);
        Assert.Equal(78, first.CommandDuty);
        Assert.Equal(80, controller.Evaluate(76, 3400).CommandDuty);

        // The correction cap is reached and the target stays unreachable.
        Assert.Equal(FanCurveRpmFeedbackAction.NoChange, controller.Evaluate(76, 3400).Action);
        Assert.Equal(FanCurveRpmFeedbackState.TargetUnreachable, controller.State);
        Assert.False(controller.Evaluate(76, 3400).IsFault);
        var fault = controller.Evaluate(76, 3400);
        Assert.True(fault.IsFault);
        Assert.Equal(FanCurveRpmFeedbackState.UnreachableFault, fault.State);
    }

    [Fact]
    public void BadSamplesAreToleratedBeforeTheLoopFaults()
    {
        var feedback = Feedback(badSampleTolerance: 2);
        var controller = new FanCurveRpmFeedbackController(feedback, Calibration);

        // Interleaved: a good sample resets the tolerance counter.
        Assert.False(controller.Evaluate(76, null).IsFault);
        Assert.False(controller.Evaluate(76, 5000).IsFault);
        Assert.False(controller.Evaluate(76, null).IsFault);
        Assert.False(controller.Evaluate(76, 5000).IsFault);

        // Consecutive bad samples beyond the tolerance become a fault.
        Assert.False(controller.Evaluate(76, -1).IsFault);
        Assert.False(controller.Evaluate(76, 25000).IsFault);
        var lost = controller.Evaluate(76, null);
        Assert.True(lost.IsFault);
        Assert.Equal(FanCurveRpmFeedbackState.FeedbackLost, lost.State);
    }

    [Fact]
    public void InvalidPlausibilityRangeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(deadbandRpm: -1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(minimumExpectedRpm: 6001).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(stallRatio: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(stallRatio: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(stallSampleCount: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(maximumCorrectionDuty: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Feedback(maximumCorrectionDuty: 2, maximumCorrectionStep: 5).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(badSampleTolerance: 11).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Curve(Feedback(deadbandRpm: -1)).Validate());
    }

    [Fact]
    public void NinetyDegreeEmergencyFullSpeedIsNeverReducedByFeedback()
    {
        var feedback = Feedback(stallSampleCount: 2);
        var engine = new FanCurveEngine(Curve(feedback));

        Assert.Equal(100, engine.Evaluate(Snapshot(90, 0)).DutyPercent);
        for (var pass = 0; pass < 6; pass++)
        {
            var decision = engine.Evaluate(Snapshot(90, 0));
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, decision.Action);
            Assert.Equal(100, engine.CurrentDuty);
            Assert.Equal(FanTemperatureLevel.Emergency, engine.ActiveLevel);
        }
    }

    [Fact]
    public void FaultFlowsThroughRestoreAutomaticOnly()
    {
        var target = new FakeTarget();
        using var controller = new FanCurveController(target, Curve(Feedback(stallSampleCount: 2)));
        controller.Enable();
        Assert.True(controller.Apply(Snapshot(70, 5000)).Success);
        Assert.True(controller.Apply(Snapshot(70, 500)).Success);
        var fault = controller.Apply(Snapshot(70, 500));

        Assert.True(fault.Success);
        Assert.Equal(FanCurveAction.RestoreAutomatic, fault.Decision.Action);
        Assert.Equal(1, target.RestoreCount);
        Assert.False(controller.IsEnabled);
        Assert.False(controller.RecoveryRequired);

        // Control was handed back, so no further duty writes happen.
        var dutiesAfterFault = target.DutyCount;
        controller.Apply(Snapshot(80, 500));
        Assert.Equal(dutiesAfterFault, target.DutyCount);
    }

    private sealed class FakeTarget : IFanControlTarget
    {
        public int RestoreCount { get; private set; }
        public int DutyCount { get; private set; }

        public void SetCurveDuty(int dutyPercent) => DutyCount++;

        public void RestoreAutomatic() => RestoreCount++;
    }
}
