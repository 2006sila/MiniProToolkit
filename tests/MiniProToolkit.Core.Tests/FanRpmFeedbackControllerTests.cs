using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanRpmFeedbackControllerTests
{
    [Fact]
    public void DeadbandDoesNotAdjustDuty()
    {
        var controller = new FanRpmFeedbackController(deadbandRpm: 100);
        controller.Start(2500, 50);

        var decision = controller.Evaluate(2420);

        Assert.Equal(FanRpmFeedbackAction.NoChange, decision.Action);
        Assert.Equal(50, decision.DutyPercent);
        Assert.Equal("已稳定", decision.Status);
    }

    [Fact]
    public void LargeErrorAdjustsDutyByAtMostConfiguredStep()
    {
        var controller = new FanRpmFeedbackController(maximumAdjustmentDuty: 3);
        controller.Start(3500, 50);

        var decision = controller.Evaluate(1800);

        Assert.Equal(FanRpmFeedbackAction.AdjustDuty, decision.Action);
        Assert.Equal(53, decision.DutyPercent);
        Assert.Equal(1700, decision.ErrorRpm);
    }

    [Fact]
    public void RepeatedLimitErrorBecomesFault()
    {
        var controller = new FanRpmFeedbackController(
            maximumDuty: 60,
            faultSampleCount: 3,
            deadbandRpm: 50);
        controller.Start(3500, 60);

        Assert.Equal(FanRpmFeedbackAction.NoChange, controller.Evaluate(2000).Action);
        Assert.Equal(FanRpmFeedbackAction.NoChange, controller.Evaluate(2000).Action);
        var decision = controller.Evaluate(2000);

        Assert.Equal(FanRpmFeedbackAction.Fault, decision.Action);
        Assert.Contains("连续 3 次", decision.FaultReason);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public void VeryLowRpmRepeatedlyTriggersStallFault()
    {
        var controller = new FanRpmFeedbackController(faultSampleCount: 10);
        controller.Start(2500, 50);

        controller.Evaluate(0);
        controller.Evaluate(0);
        var decision = controller.Evaluate(0);

        Assert.Equal(FanRpmFeedbackAction.Fault, decision.Action);
        Assert.Contains("RPM 不高于 100", decision.FaultReason);
    }

    [Fact]
    public void StartClampsTargetButKeepsRequestedTargetVisibleInResultOnly()
    {
        var controller = new FanRpmFeedbackController();

        var decision = controller.Start(5000, 100);

        Assert.Equal(FanRpmFeedbackAction.NoChange, decision.Action);
        Assert.Equal(FanCalibration.Default.RpmForDuty(100), controller.TargetRpm);
        Assert.Equal(100, controller.CurrentDuty);
        Assert.Contains("钳制", decision.Status);
    }
}
