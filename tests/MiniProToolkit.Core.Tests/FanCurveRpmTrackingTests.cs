using System.Text.Json.Nodes;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCurveRpmTrackingTests
{
    private static readonly FanCalibration Calibration = new([new(40, 2000), new(100, 5000)]);
    private static FanCurveSettings Curve(bool latch = false) => new(
        [new(40, 40, 38, 2500), new(80, 80, 76, 4500)],
        MaximumDutyChangePerTick: 100, LatchCurveEmergency: latch);
    private static HardwareSnapshot Snapshot(double temperature, int? rpm, DateTimeOffset? timestamp = null) => new(
        timestamp ?? DateTimeOffset.UtcNow, temperature, rpm, 0, PowerMode.Balance, 20, 40,
        FanControlMode.FirmwareAutomatic, null, null);

    [Fact]
    public void LegacyConstructionDeconstructionAndDefaultRemainCompatible()
    {
        var point = new FanCurvePoint(45, 40, 43);
        var (temperature, duty, down) = point;
        Assert.Equal(45, temperature);
        Assert.Equal(40, duty);
        Assert.Equal(43, down);
        Assert.Null(point.TargetRpm);
        Assert.False(FanCurveSettings.Default.HasExplicitRpmTargets);
        Assert.Null(FanCurveSettings.Default.RpmFeedback);
        var (action, output, reason) = FanCurveDecision.SetDuty(40, "legacy");
        Assert.Equal(FanCurveAction.SetDuty, action);
        Assert.Equal(40, output);
        Assert.Equal("legacy", reason);
    }

    [Fact]
    public void TargetsMustBeCompleteLegalNondecreasingAndExclusiveOfLegacyFeedback()
    {
        Assert.Throws<ArgumentException>(() => (Curve() with { Points = [new(40, 40, 38, 2500), new(80, 80, 76)] }).Validate());
        Assert.Throws<ArgumentException>(() => (Curve() with { Points = [new(40, 40, 38), new(80, 80, 76, 4500)] }).Validate());
        Assert.Throws<ArgumentException>(() => (Curve() with { Points = [new(40, 40, 38, 4500), new(80, 80, 76, 2500)] }).Validate());
        foreach (var invalid in new[] { 0, -1, 10001 })
            Assert.Throws<ArgumentException>(() => (Curve() with { Points = [new(40, 40, 38, invalid)] }).Validate());
        var conflict = Assert.Throws<ArgumentException>(() => (Curve() with { RpmFeedback = new() }).Validate());
        Assert.Contains("cannot both", conflict.Message);
        (Curve() with { Points = [new(40, 40, 38, 2500), new(80, 80, 76, 2500)] }).Validate();
    }

    [Fact]
    public void ActualCalibrationRejectsUnreachableTargetsAndIncompleteCoverage()
    {
        Curve().Validate();
        Curve().ValidateCalibration(Calibration);
        Assert.Throws<ArgumentException>(() => new FanCurveEngine(Curve(), FanCalibration.Default));
        Assert.Throws<ArgumentException>(() => new FanCurveEngine(Curve(), new([new(50, 2000), new(100, 5000)])));
        Assert.Throws<ArgumentException>(() => new FanCurveEngine(Curve(), new([new(40, 2000), new(90, 5000)])));
        Assert.Throws<ArgumentException>(() => new FanCurveEngine(Curve(), new([new(40, 3000), new(100, 5000)])));
        Assert.Throws<ArgumentException>(() => new FanCurveEngine(Curve(), new([new(40, 2000), new(100, 4000)])));
    }

    [Fact]
    public void ExplicitTargetUsesTheSameRisingFallingAndHysteresisThresholds()
    {
        var engine = new FanCurveEngine(Curve(), Calibration);
        var up = engine.Evaluate(Snapshot(60, 3500));
        Assert.Equal(3500, up.TargetRpm);
        Assert.Equal(3500, up.ActualRpm);
        Assert.Equal(0, up.RpmError);
        Assert.Equal(70, up.DutyPercent); // RPM feed-forward, not temperature duty 60.
        Assert.Equal(FanCurveRpmTrackingState.Stable, up.TrackingState);
        var held = engine.Evaluate(Snapshot(61, 3500));
        Assert.Equal(3500, held.TargetRpm);
        engine.Evaluate(Snapshot(70, 4000));
        var down = engine.Evaluate(Snapshot(60, 3658));
        Assert.Equal(3658, down.TargetRpm); // thresholds 38..76, not ramp-up 40..80.
        Assert.Equal(FanCurveRpmTrackingState.Stable, down.TrackingState);
    }

    [Fact]
    public void TrackingAddsAndReclaimsOnlyBoundedCompensation()
    {
        var tracker = new FanCurveRpmTrackingController(Calibration);
        Assert.Equal(62, tracker.Evaluate(3000, 2400, 40).CommandDuty);
        Assert.Equal(64, tracker.Evaluate(3000, 2400, 40).CommandDuty);
        Assert.Equal(62, tracker.Evaluate(3000, 3600, 40).CommandDuty);
        Assert.Equal(60, tracker.Evaluate(3000, 3600, 40).CommandDuty);
        Assert.Equal(0, tracker.CorrectionDuty);
        var safety = tracker.Evaluate(3000, 4500, 90);
        Assert.Equal(90, safety.CommandDuty);
        Assert.Equal(FanCurveRpmTrackingState.SafetyFloorLimited, safety.State);
        for (var i = 0; i < 20; i++)
            Assert.False(tracker.Evaluate(3000, 4500, 90).IsFault);
        tracker.Reset();
        Assert.Equal(0, tracker.CorrectionDuty);
        Assert.Equal(60, tracker.Evaluate(3000, 3000, 40).CommandDuty);
    }

    [Fact]
    public void TemperatureTierAndBothRampsNeverFallBelowIndependentLegacyOutput()
    {
        var settings = Curve() with
        {
            MaximumDutyIncreasePerTick = 3, MaximumDutyDecreasePerTick = 2,
            WarningTier = new(65, 85), PreEmergencyTier = new(75, 100),
            RpmRateLimit = new(600, 1200)
        };
        var plain = new FanCurveEngine(settings with
        {
            Points = settings.Points.Select(p => p with { TargetRpm = null }).ToArray()
        }, Calibration);
        var tracking = new FanCurveEngine(settings, Calibration);
        var temperatures = new[] { 40d, 60, 65, 70, 75, 80, 90, 80, 70, 60, 40, 40, 40 };
        var time = DateTimeOffset.UtcNow;
        for (var i = 0; i < temperatures.Length; i++)
        {
            var sample = Snapshot(temperatures[i], i % 2 == 0 ? 4900 : 3500, time.AddSeconds(i));
            plain.Evaluate(sample, sample.Timestamp);
            var decision = tracking.Evaluate(sample, sample.Timestamp);
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, decision.Action);
            Assert.True(tracking.CurrentDuty >= plain.CurrentDuty, $"sample {i}: {tracking.CurrentDuty} < {plain.CurrentDuty}");
            if (temperatures[i] >= 90) Assert.Equal(100, tracking.CurrentDuty);
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(80)]
    public void EmergencyAndLatchSkipTrackingIncludingMissingRpm(double threshold)
    {
        var settings = Curve(latch: true) with { EmergencyTemperatureC = threshold, MaximumDutyChangePerTick = 1 };
        var engine = new FanCurveEngine(settings, Calibration);
        for (var i = 0; i < 8; i++)
        {
            var decision = engine.Evaluate(Snapshot(i == 0 ? threshold : 40, null));
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, decision.Action);
            Assert.Equal(100, engine.CurrentDuty);
            Assert.True(decision.EmergencyLatched);
            Assert.Equal(FanCurveRpmTrackingState.EmergencyBypass, decision.TrackingState);
        }
        engine.Reset(clearEmergencyLatch: true);
        Assert.False(engine.EmergencyLatched);
        Assert.NotEqual(FanCurveAction.RestoreAutomatic, engine.Evaluate(Snapshot(40, 2500)).Action);
    }

    [Fact]
    public void HigherMinimumDutyIsASafetyConstraintNotAnUnreachableTarget()
    {
        var settings = Curve() with
        {
            MinimumDutyPercent = 70,
            Points = [new(40, 70, 38, 2500), new(80, 80, 76, 4500)]
        };
        var engine = new FanCurveEngine(settings, Calibration);
        for (var i = 0; i < 12; i++)
        {
            var decision = engine.Evaluate(Snapshot(40, 3500));
            Assert.Equal(FanCurveRpmTrackingState.SafetyFloorLimited, decision.TrackingState);
            Assert.Equal(70, engine.CurrentDuty);
            Assert.Equal(2500, decision.TargetRpm);
            Assert.Equal(-1000, decision.RpmError);
        }
    }

    [Fact]
    public void SinglePointAndEmergencyCooldownRecalculateTargetsWithoutStaleState()
    {
        var settings = Curve() with { Points = [new(40, 40, 38, 2500)] };
        var engine = new FanCurveEngine(settings, Calibration);
        Assert.Equal(2500, engine.Evaluate(Snapshot(80, 2500)).TargetRpm);
        Assert.Equal(FanCurveRpmTrackingState.EmergencyBypass, engine.Evaluate(Snapshot(90, null)).TrackingState);
        var cooled = engine.Evaluate(Snapshot(89, 2500));
        Assert.Equal(2500, cooled.TargetRpm);
        Assert.Equal(FanCurveRpmTrackingState.Stable, cooled.TrackingState);
        Assert.Equal(50, engine.CurrentDuty);
        engine.Reset();
        Assert.Null(engine.CurrentDuty);
        Assert.Equal(2500, engine.Evaluate(Snapshot(40, 2500)).TargetRpm);
    }

    [Fact]
    public void MissingRpmHasABoundedToleranceEvenWhenTargetsKeepChanging()
    {
        var engine = new FanCurveEngine(Curve(), Calibration);
        Assert.Equal(FanCurveRpmTrackingState.RpmUnavailable, engine.Evaluate(Snapshot(40, null)).TrackingState);
        Assert.Equal(FanCurveRpmTrackingState.RpmUnavailable, engine.Evaluate(Snapshot(50, -1)).TrackingState);
        var fault = engine.Evaluate(Snapshot(60, 21000));
        Assert.Equal(FanCurveAction.RestoreAutomatic, fault.Action);
        Assert.True(fault.RpmFeedbackFault);
        Assert.Equal(FanCurveRpmTrackingState.FeedbackLost, fault.TrackingState);
        Assert.Null(fault.ActualRpm);
        Assert.Null(fault.RpmError);
    }

    [Fact]
    public void ConsecutiveStallAndUnreachableSamplesEventuallyRestore()
    {
        var stalled = new FanCurveRpmTrackingController(Calibration);
        for (var i = 1; i < FanCurveRpmTrackingController.FaultSampleCount; i++)
            Assert.False(stalled.Evaluate(3000, 0, 40).IsFault);
        Assert.Equal(FanCurveRpmTrackingState.StallFault, stalled.Evaluate(3000, 0, 40).State);
        Assert.True(stalled.Evaluate(3000, 3000, 40).IsFault);
        var unreachable = new FanCurveRpmTrackingController(Calibration);
        for (var i = 0; i < 6; i++)
        {
            Assert.False(unreachable.Evaluate(3000, 2400, 40).IsFault);
            Assert.InRange(unreachable.CorrectionDuty, 0, 12);
        }
        for (var i = 1; i < FanCurveRpmTrackingController.FaultSampleCount; i++)
            Assert.False(unreachable.Evaluate(3000, 2400, 40).IsFault);
        Assert.Equal(FanCurveRpmTrackingState.UnreachableFault, unreachable.Evaluate(3000, 2400, 40).State);
    }

    [Fact]
    public void FaultUsesExistingRestoreAndLeaseFailurePaths()
    {
        var target = new FakeTarget();
        using var controller = new FanCurveController(target, Curve(), Calibration);
        controller.Enable();
        controller.Apply(Snapshot(40, null));
        controller.Apply(Snapshot(40, null));
        var lost = controller.Apply(Snapshot(40, null));
        Assert.True(lost.Success);
        Assert.Equal(FanCurveAction.RestoreAutomatic, lost.Decision.Action);
        Assert.False(controller.IsEnabled);
        Assert.Equal(1, target.Restores);

        var gated = new FakeTarget { DenyWrites = true };
        using var denied = new FanCurveController(gated, Curve(), Calibration);
        denied.Enable();
        Assert.False(denied.Apply(Snapshot(60, 3500)).Success);
        Assert.Equal(0, gated.Writes);
        Assert.Equal(1, gated.Restores);
        Assert.False(denied.IsEnabled);
    }

    [Fact]
    public void InvalidOrStaleTemperatureStillRestoresWithoutTracking()
    {
        foreach (var sample in new[] { Snapshot(double.NaN, 3000), Snapshot(0, 3000), Snapshot(60, 3000, DateTimeOffset.UtcNow.AddSeconds(-10)) })
        {
            var engine = new FanCurveEngine(Curve(), Calibration);
            var decision = engine.Evaluate(sample);
            Assert.Equal(FanCurveAction.RestoreAutomatic, decision.Action);
            Assert.False(decision.RpmFeedbackFault);
            Assert.Equal(FanCurveRpmTrackingState.Disabled, decision.TrackingState);
        }
    }

    [Fact]
    public void ConfigurationAndProfileRoundTripPreserveTargetsAndAllSafetyFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var settings = Curve(latch: true) with { WarningTier = new(65, 85), RpmRateLimit = new(700, 1300, 2) };
            var config = new FanCurveConfigurationStore(Path.Combine(directory, "curve.json"));
            config.Save(settings);
            var reloaded = config.Load();
            Assert.Equal(settings.Points, reloaded.Points);
            Assert.Equal(settings.WarningTier, reloaded.WarningTier);
            Assert.Equal(settings.RpmRateLimit, reloaded.RpmRateLimit);
            Assert.True(reloaded.LatchCurveEmergency);
            var profileStore = new FanProfileStore(Path.Combine(directory, "profiles.json"));
            profileStore.Save([new("RPM", PowerMode.Balance, settings, ProfileFanMode.Curve)]);
            var profile = Assert.Single(profileStore.Load());
            Assert.Equal(settings.Points, profile.FanCurve.Points);
            Assert.True(profile.FanCurve.LatchCurveEmergency);
            // Record copies preserve targets without editing the independent duty line.
            var copy = profile with { Name = "copy", FanCurve = profile.FanCurve with { Points = profile.FanCurve.Points.ToArray() } };
            Assert.Equal(settings.Points, copy.FanCurve.Points);
            foreach (var path in new[] { config.Path, profileStore.Path })
            {
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                var curve = path == config.Path ? json : json["profiles"]![0]!;
                curve["futureField"] = new JsonObject { ["ignored"] = true };
                File.WriteAllText(path, json.ToJsonString());
            }
            Assert.Equal(settings.Points, config.Load().Points);
            Assert.Equal(settings.Points, Assert.Single(profileStore.Load()).FanCurve.Points);
            foreach (var path in new[] { config.Path, profileStore.Path })
            {
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                var curve = path == config.Path ? json : json["profiles"]![0]!;
                foreach (var point in curve["points"]!.AsArray())
                {
                    point!.AsObject().Remove("targetRpm");
                    point["futurePoint"] = 1;
                }
                File.WriteAllText(path, json.ToJsonString());
            }
            Assert.False(config.Load().HasExplicitRpmTargets);
            Assert.False(Assert.Single(profileStore.Load()).FanCurve.HasExplicitRpmTargets);
            Assert.All(FanCurveSettings.Default.Points, point => Assert.Null(point.TargetRpm));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FakeTarget : IFanControlTarget
    {
        public bool DenyWrites { get; init; }
        public int Writes { get; private set; }
        public int Restores { get; private set; }
        public void SetCurveDuty(int dutyPercent)
        {
            if (DenyWrites) throw new InvalidOperationException("lease gate refused");
            Writes++;
        }
        public void RestoreAutomatic() => Restores++;
    }
}
