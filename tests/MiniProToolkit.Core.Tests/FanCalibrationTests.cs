using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCalibrationTests
{
    [Fact]
    public void InterpolatesRpmFromDuty()
    {
        var calibration = FanCalibration.Default;

        Assert.Equal(1868, calibration.RpmForDuty(35));
        Assert.Equal(3028, calibration.RpmForDuty(65));
    }

    [Fact]
    public void InterpolatesDutyFromRpm()
    {
        var calibration = FanCalibration.Default;

        Assert.Equal(35, calibration.DutyForRpm(1868));
        Assert.Equal(65, calibration.DutyForRpm(3028));
    }

    [Fact]
    public void TargetControllerClampsBelowMinimumDutyRpm()
    {
        var controller = new FanRpmTargetController(FanCalibration.Default, minimumDuty: 40);

        var result = controller.Resolve(1000);

        Assert.True(result.WasClamped);
        Assert.Equal(40, result.DutyPercent);
        Assert.Equal(FanCalibration.Default.RpmForDuty(40), result.ClampedRpm);
    }

    [Fact]
    public void TargetControllerClampsAboveMaximumRpm()
    {
        var controller = new FanRpmTargetController();

        var result = controller.Resolve(5000);

        Assert.True(result.WasClamped);
        Assert.Equal(100, result.DutyPercent);
        Assert.Equal(4083, result.ClampedRpm);
    }

    [Fact]
    public void RejectsNonMonotonicCalibration()
    {
        Assert.Throws<ArgumentException>(() => new FanCalibration([
            new(20, 1200),
            new(50, 1100)
        ]));
    }
}
