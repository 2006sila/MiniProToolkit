using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// Pure Core coverage for the optional warning / pre-emergency tiers. The 90 °C full-speed
// protection, the 40 % duty floor, hysteresis and the failure latch must not regress.
public sealed class FanCurveTemperatureTierTests
{
    // Curve duty in this profile is 40 % at 40 °C rising linearly to 100 % at 90 °C.
    private static FanCurveSettings Tiers(
        FanTemperatureTier? warning = null,
        FanTemperatureTier? preEmergency = null,
        int maximumDutyChangePerTick = 100,
        double hysteresisC = 0) => new(
        [new(40, 40), new(90, 100)],
        HysteresisC: hysteresisC,
        MaximumDutyChangePerTick: maximumDutyChangePerTick,
        WarningTier: warning,
        PreEmergencyTier: preEmergency);

    [Fact]
    public void ShippedDefaultEnablesTheConservativeGradedTiers()
    {
        var settings = FanCurveSettings.Default;

        Assert.Equal(new FanTemperatureTier(70, 80), settings.WarningTier);
        Assert.Equal(new FanTemperatureTier(82, 100), settings.PreEmergencyTier);
        Assert.Null(settings.RpmRateLimit);
        Assert.True(settings.HasGradedTemperatureTiers);
        Assert.Equal(40, settings.MinimumDutyPercent);
        Assert.Equal(90, settings.EmergencyTemperatureC);
        Assert.Equal(100, settings.EmergencyDutyPercent);

        var engine = new FanCurveEngine(settings);
        Assert.Equal(40, engine.Evaluate(Snapshot(45)).DutyPercent);
        Assert.Equal(FanTemperatureLevel.Normal, engine.ActiveLevel);

        var emergency = engine.Evaluate(Snapshot(90));
        Assert.Equal(100, emergency.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Emergency, emergency.Level);
    }

    [Fact]
    public void ClearingTheTiersKeepsTheLegacySingleEmergencyThreshold()
    {
        // Exactly what an older JSON file without the optional tier fields deserialises to.
        var settings = FanCurveSettings.Default with { WarningTier = null, PreEmergencyTier = null };

        Assert.False(settings.HasGradedTemperatureTiers);
        var engine = new FanCurveEngine(settings);
        Assert.Equal(40, engine.Evaluate(Snapshot(45)).DutyPercent);
        Assert.Equal(FanTemperatureLevel.Normal, engine.ActiveLevel);

        var emergency = engine.Evaluate(Snapshot(90));
        Assert.Equal(100, emergency.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Emergency, emergency.Level);
    }

    [Fact]
    public void WarningAndPreEmergencyTiersRaiseTheDutyTarget()
    {
        var engine = new FanCurveEngine(Tiers(
            warning: new FanTemperatureTier(65, 75),
            preEmergency: new FanTemperatureTier(80, 100)));

        var normal = engine.Evaluate(Snapshot(40));
        Assert.Equal(40, normal.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Normal, normal.Level);

        var warning = engine.Evaluate(Snapshot(66));
        Assert.Equal(75, warning.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Warning, warning.Level);

        var preEmergency = engine.Evaluate(Snapshot(81));
        Assert.Equal(100, preEmergency.DutyPercent);
        Assert.Equal(FanTemperatureLevel.PreEmergency, preEmergency.Level);

        // Duty is already at 100 %, so only the level changes.
        var emergency = engine.Evaluate(Snapshot(90));
        Assert.Equal(FanTemperatureLevel.Emergency, emergency.Level);
        Assert.Equal(100, engine.CurrentDuty);
    }

    [Fact]
    public void TierBoostStillObeysTheDutyRampLimit()
    {
        var engine = new FanCurveEngine(Tiers(
            warning: new FanTemperatureTier(65, 75),
            maximumDutyChangePerTick: 5));

        Assert.Equal(40, engine.Evaluate(Snapshot(40)).DutyPercent);
        Assert.Equal(45, engine.Evaluate(Snapshot(66)).DutyPercent);
        Assert.Equal(50, engine.Evaluate(Snapshot(66)).DutyPercent);
    }

    [Fact]
    public void TierDowngradeWaitsForHysteresis()
    {
        var engine = new FanCurveEngine(Tiers(
            warning: new FanTemperatureTier(70, 90),
            hysteresisC: 2));

        Assert.Equal(40, engine.Evaluate(Snapshot(40)).DutyPercent);
        Assert.Equal(90, engine.Evaluate(Snapshot(72)).DutyPercent);
        Assert.Equal(FanTemperatureLevel.Warning, engine.ActiveLevel);

        // 69 °C is inside the 2 °C band below the 70 °C tier, so the level holds.
        Assert.Equal(FanCurveAction.NoChange, engine.Evaluate(Snapshot(69)).Action);
        Assert.Equal(FanTemperatureLevel.Warning, engine.ActiveLevel);

        // 67 °C is outside the band: back to the plain curve target (75 % on the fall path).
        var downgraded = engine.Evaluate(Snapshot(67));
        Assert.Equal(FanTemperatureLevel.Normal, downgraded.Level);
        Assert.Equal(75, downgraded.DutyPercent);
    }

    [Fact]
    public void EmergencyFullSpeedStillBypassesRampWithTiersConfigured()
    {
        var engine = new FanCurveEngine(Tiers(
            warning: new FanTemperatureTier(60, 70),
            preEmergency: new FanTemperatureTier(85, 100),
            maximumDutyChangePerTick: 5));

        Assert.Equal(40, engine.Evaluate(Snapshot(40)).DutyPercent);
        var emergency = engine.Evaluate(Snapshot(90));

        Assert.Equal(100, emergency.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Emergency, emergency.Level);
    }

    [Fact]
    public void TiersCannotSitAboveTheFixedEmergencyThreshold()
    {
        Assert.Throws<ArgumentException>(() => Tiers(preEmergency: new FanTemperatureTier(90, 100)).Validate());
        Assert.Throws<ArgumentException>(() =>
            (FanCurveSettings.Default with { PreEmergencyTier = new FanTemperatureTier(90, 100) }).Validate());
    }

    [Fact]
    public void TiersCannotWeakenTheCurveDuty()
    {
        // The curve already commands 88 % at 80 °C, so an 50 % tier would reduce cooling.
        Assert.Throws<ArgumentException>(() => Tiers(preEmergency: new FanTemperatureTier(80, 50)).Validate());
    }

    [Fact]
    public void TierOrderAndDutyOrderAreEnforced()
    {
        Assert.Throws<ArgumentException>(() => Tiers(
            warning: new FanTemperatureTier(80, 100),
            preEmergency: new FanTemperatureTier(70, 100)).Validate());

        Assert.Throws<ArgumentException>(() => Tiers(
            warning: new FanTemperatureTier(60, 80),
            preEmergency: new FanTemperatureTier(70, 78)).Validate());
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(double.NaN, 80)]
    [InlineData(70, 39)]
    [InlineData(70, 101)]
    public void TierFieldRangesAreValidated(double temperatureC, int dutyPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FanTemperatureTier(temperatureC, dutyPercent).Validate());
    }

    [Fact]
    public void FailureLatchStillRestoresAutomaticWithTiersConfigured()
    {
        var target = new FakeTarget();
        using var controller = new FanCurveController(target, Tiers(
            warning: new FanTemperatureTier(60, 70),
            preEmergency: new FanTemperatureTier(80, 100)));
        controller.Enable();
        controller.Apply(Snapshot(66));
        Assert.Equal(1, target.DutyCount);

        var failed = controller.Apply(HardwareSnapshot.ErrorSnapshot("sensor unavailable"));

        Assert.True(failed.Success);
        Assert.False(controller.IsEnabled);
        Assert.Equal(1, target.RestoreCount);
    }

    [Fact]
    public void DutyWriteFailureAfterTierBoostRestoresAutomatic()
    {
        var target = new FakeTarget { ThrowOnDuty = true };
        using var controller = new FanCurveController(target, Tiers(
            preEmergency: new FanTemperatureTier(80, 100)));
        controller.Enable();

        var result = controller.Apply(Snapshot(85));

        Assert.False(result.Success);
        Assert.False(controller.IsEnabled);
        Assert.Equal(1, target.RestoreCount);
    }

    // The curve editor pre-fills exactly these values when a safety option is switched on, so
    // they must validate against the shipped default curve; otherwise the checkbox would put
    // the editor into a state that can only fail on save.
    [Fact]
    public void EditorSafetyPresetValuesAreValidOnTheDefaultCurve()
    {
        var settings = FanCurveSettings.Default with
        {
            RpmRateLimit = new FanRpmRateLimit(600, 1200, 1),
            WarningTier = new FanTemperatureTier(
                FanCurveSettings.DefaultWarningTierTemperatureC,
                FanCurveSettings.DefaultWarningTierDutyPercent),
            PreEmergencyTier = new FanTemperatureTier(
                FanCurveSettings.DefaultPreEmergencyTierTemperatureC,
                FanCurveSettings.DefaultPreEmergencyTierDutyPercent)
        };

        settings.Validate();

        // The fixed 90 °C / 100 % full-speed protection stays untouched by the presets.
        Assert.Equal(90, settings.EmergencyTemperatureC);
        Assert.Equal(100, settings.EmergencyDutyPercent);
        Assert.True(settings.HasGradedTemperatureTiers);
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

        public void SetCurveDuty(int dutyPercent)
        {
            DutyCount++;
            if (ThrowOnDuty)
                throw new InvalidOperationException("simulated duty failure");
        }

        public void RestoreAutomatic() => RestoreCount++;
    }
}
