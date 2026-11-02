using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCurveControllerTests
{
    [Fact]
    public void FailedDutyWriteRestoresAutomaticAndDisablesCurve()
    {
        var target = new FakeTarget { ThrowOnDuty = true };
        using var controller = new FanCurveController(target, FanCurveSettings.Default);
        controller.Enable();

        var result = controller.Apply(Snapshot(60));

        Assert.False(result.Success);
        Assert.False(controller.IsEnabled);
        Assert.Equal(1, target.RestoreCount);
    }

    [Fact]
    public void DisableRestoresAutomaticAfterDutyWasApplied()
    {
        var target = new FakeTarget();
        using var controller = new FanCurveController(target, FanCurveSettings.Default);
        controller.Enable();
        controller.Apply(Snapshot(60));

        controller.Disable();

        Assert.Equal(1, target.RestoreCount);
        Assert.False(controller.IsEnabled);
    }

    [Fact]
    public void FailedDisableStopsCurveAndKeepsRecoveryPendingUntilRetry()
    {
        var target = new FakeTarget();
        using var controller = new FanCurveController(target, FanCurveSettings.Default);
        controller.Enable();
        controller.Apply(Snapshot(60));
        target.ThrowOnRestore = true;
        Assert.Throws<IOException>(controller.Disable);
        Assert.False(controller.IsEnabled);
        Assert.True(controller.RecoveryRequired);
        controller.Apply(Snapshot(75));
        Assert.Equal(1, target.DutyCount);
        target.ThrowOnRestore = false;
        controller.Disable();
        Assert.False(controller.RecoveryRequired);
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

    private sealed class FakeTarget : IFanControlTarget
    {
        public bool ThrowOnDuty { get; init; }
        public int RestoreCount { get; private set; }
        public int DutyCount { get; private set; }
        public bool ThrowOnRestore { get; set; }

        public void SetCurveDuty(int dutyPercent)
        {
            DutyCount++;
            if (ThrowOnDuty)
                throw new InvalidOperationException("simulated duty failure");
        }

        public void RestoreAutomatic()
        {
            RestoreCount++;
            if (ThrowOnRestore) throw new IOException("restore failed");
        }
    }
}
