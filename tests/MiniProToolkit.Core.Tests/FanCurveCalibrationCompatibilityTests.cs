using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCurveCalibrationCompatibilityTests
{
    private static FanCurveSettings TrackingCurve() => FanCurveSettings.Default with
    {
        Points = Array.AsReadOnly<FanCurvePoint>([new(45, 40, 43, 2500), new(90, 100, 88, 4500)]),
        RpmRateLimit = new(700, 1300, 2),
        MaximumDutyIncreasePerTick = 4,
        MaximumDutyDecreasePerTick = 2,
        LatchCurveEmergency = true
    };

    [Fact]
    public void CompatibleCalibrationReturnsOriginalSettingsWithoutWarning()
    {
        var settings = TrackingCurve();
        var calibration = new FanCalibration([new(40, 2000), new(100, 5000)]);
        var resolved = FanCurveCalibrationCompatibility.Resolve(settings, calibration, out var warning);
        Assert.Same(settings, resolved);
        Assert.Null(warning);
        _ = new FanCurveEngine(resolved, calibration);
    }

    [Theory]
    [InlineData(40, 2000, 100, 4000)] // upper target is outside a replaced table
    [InlineData(40, 3000, 100, 5000)] // lower target is outside a replaced table
    [InlineData(50, 2000, 100, 5000)] // table does not cover the safe lower duty
    [InlineData(40, 2000, 90, 5000)] // table does not cover full speed
    [InlineData(40, 2000, 100, 2000)] // no usable tracking range
    public void IncompatibleCandidateOnlyRemovesTargetsAndDoesNotMutateOriginal(
        int lowDuty, int lowRpm, int highDuty, int highRpm)
    {
        AssertSafeFallback(TrackingCurve(), new([new(lowDuty, lowRpm), new(highDuty, highRpm)]));
    }

    [Fact]
    public void DefaultCalibrationFallbackDoesNotCrashWithPreviouslyReachableTargets()
    {
        var settings = TrackingCurve();
        // This curve was valid with a 5000 RPM table, but the shipped fallback ends at 4083.
        AssertSafeFallback(settings, FanCalibration.Default);
    }

    [Fact]
    public void DefaultsAndLegacyFeedbackRemainUntouchedWithNullTargets()
    {
        foreach (var settings in new[]
        {
            FanCurveSettings.Default,
            FanCurveSettings.Default with { RpmFeedback = new(), LatchCurveEmergency = true }
        })
        {
            // Legacy duty/feedback mode does not require fixed-RPM calibration coverage.
            var calibration = new FanCalibration([new(50, 2000), new(90, 4000)]);
            var resolved = FanCurveCalibrationCompatibility.Resolve(settings, calibration, out var warning);
            Assert.Same(settings, resolved);
            Assert.Same(settings.RpmFeedback, resolved.RpmFeedback);
            Assert.All(resolved.Points, point => Assert.Null(point.TargetRpm));
            Assert.Null(warning);
            _ = new FanCurveEngine(resolved, calibration);
        }
    }

    [Fact]
    public void MalformedSafetySettingsAndConflictingFeedbackAreNotSilentlyRewritten()
    {
        var invalidDuty = TrackingCurve() with { MinimumDutyPercent = 20 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FanCurveCalibrationCompatibility.Resolve(invalidDuty, FanCalibration.Default, out _));
        var conflict = TrackingCurve() with { RpmFeedback = new() };
        Assert.Throws<ArgumentException>(() =>
            FanCurveCalibrationCompatibility.Resolve(conflict, FanCalibration.Default, out _));
        Assert.NotNull(conflict.RpmFeedback);
        Assert.All(conflict.Points, point => Assert.NotNull(point.TargetRpm));
    }

    private static void AssertSafeFallback(FanCurveSettings settings, FanCalibration calibration)
    {
        var originalPoints = settings.Points.ToArray();
        var resolved = FanCurveCalibrationCompatibility.Resolve(settings, calibration, out var warning);
        Assert.NotSame(settings, resolved);
        Assert.NotSame(settings.Points, resolved.Points);
        Assert.NotNull(warning);
        Assert.Contains("禁用显式 RPM 跟踪", warning);
        Assert.Equal(originalPoints, settings.Points);
        Assert.All(settings.Points, point => Assert.NotNull(point.TargetRpm));
        Assert.All(resolved.Points, point => Assert.Null(point.TargetRpm));
        // Whole-record equality after restoring the original points proves every other setting
        // (including feedback, tiers, ramp limits, minimum/full-speed duty and latch) survives.
        Assert.Equal(settings, resolved with { Points = settings.Points });
        Assert.Equal(originalPoints.Select(p => p with { TargetRpm = null }), resolved.Points);
        Assert.Same(settings.WarningTier, resolved.WarningTier);
        Assert.Same(settings.PreEmergencyTier, resolved.PreEmergencyTier);
        Assert.Same(settings.RpmRateLimit, resolved.RpmRateLimit);
        resolved.ValidateCalibration(calibration);
        var engine = new FanCurveEngine(resolved, calibration);
        var emergency = new HardwareSnapshot(DateTimeOffset.UtcNow, 90, null, 0, PowerMode.Balance,
            20, 40, FanControlMode.FirmwareAutomatic, null, null);
        Assert.Equal(100, engine.Evaluate(emergency).DutyPercent);
        engine.Evaluate(emergency with { CpuTemperatureC = 40 });
        Assert.Equal(100, engine.CurrentDuty);
        Assert.True(engine.EmergencyLatched);
        Assert.Same(resolved, FanCurveCalibrationCompatibility.Resolve(resolved, calibration, out var nextWarning));
        Assert.Null(nextWarning);
    }
}
