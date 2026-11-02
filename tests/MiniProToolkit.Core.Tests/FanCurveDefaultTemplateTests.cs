using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// Guards the shipped default fan curve template: graded temperature tiers are on by default,
// the RPM/s limit stays opt-in (null), the fixed 90 °C / 100 % emergency path is untouched and
// older configuration files without the optional fields keep their previous behaviour.
public sealed class FanCurveDefaultTemplateTests
{
    [Fact]
    public void ShippedDefaultCarriesTheConservativeGradedTemplate()
    {
        var settings = FanCurveSettings.Default;

        Assert.Equal(new FanTemperatureTier(70, 80), settings.WarningTier);
        Assert.Equal(new FanTemperatureTier(82, 100), settings.PreEmergencyTier);
        Assert.Equal(70d, FanCurveSettings.DefaultWarningTierTemperatureC);
        Assert.Equal(80, FanCurveSettings.DefaultWarningTierDutyPercent);
        Assert.Equal(82d, FanCurveSettings.DefaultPreEmergencyTierTemperatureC);
        Assert.Equal(100, FanCurveSettings.DefaultPreEmergencyTierDutyPercent);

        // RPM/s limiting depends on real fan feedback and stays opt-in.
        Assert.Null(settings.RpmRateLimit);
        Assert.True(settings.HasGradedTemperatureTiers);

        // The 90 °C full-speed protection is unchanged by the template.
        Assert.Equal(90, settings.EmergencyTemperatureC);
        Assert.Equal(100, settings.EmergencyDutyPercent);
        Assert.Equal(40, settings.MinimumDutyPercent);
        Assert.Equal(10, settings.MaximumDutyChangePerTick);

        // The curve nodes themselves are unchanged.
        Assert.Equal(
            new[]
            {
                new FanCurvePoint(45, 40, 43), new FanCurvePoint(55, 45, 53),
                new FanCurvePoint(65, 55, 63), new FanCurvePoint(75, 70, 73),
                new FanCurvePoint(85, 85, 83), new FanCurvePoint(90, 100, 88)
            },
            settings.Points);

        settings.Validate();
    }

    [Fact]
    public void ShippedDefaultReachesWarningThenPreEmergencyThenEmergency()
    {
        var engine = new FanCurveEngine(FanCurveSettings.Default with { MaximumDutyChangePerTick = 100 });

        var normal = engine.Evaluate(Snapshot(45));
        Assert.Equal(40, normal.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Normal, normal.Level);

        // 70 °C: curve alone asks 62 %, the tier floor lifts it to 80 %.
        var warning = engine.Evaluate(Snapshot(70));
        Assert.Equal(80, warning.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Warning, warning.Level);

        // 82 °C: curve alone asks 80 %, the pre-emergency tier lifts it to 100 %.
        var preEmergency = engine.Evaluate(Snapshot(82));
        Assert.Equal(100, preEmergency.DutyPercent);
        Assert.Equal(FanTemperatureLevel.PreEmergency, preEmergency.Level);

        // Emergency keeps 100 % even when the pre-emergency tier already reached it (the
        // decision is then a no-op write, so assert the tracked duty).
        var emergency = engine.Evaluate(Snapshot(90));
        Assert.Equal(FanTemperatureLevel.Emergency, emergency.Level);
        Assert.Equal(100, engine.CurrentDuty);
    }

    [Fact]
    public void EmergencyFullSpeedStillBypassesTheDutyRampWithTheDefaultTemplate()
    {
        var engine = new FanCurveEngine(FanCurveSettings.Default with { MaximumDutyIncreasePerTick = 1 });

        Assert.Equal(50, engine.Evaluate(Snapshot(60)).DutyPercent);
        var emergency = engine.Evaluate(Snapshot(90));

        Assert.Equal(100, emergency.DutyPercent);
        Assert.Equal(FanTemperatureLevel.Emergency, emergency.Level);
        Assert.Equal(100, engine.CurrentDuty);
    }

    [Fact]
    public void TierFloorOnlyAddsCoolingOnTheDefaultCurve()
    {
        var withTiers = FanCurveSettings.Default with { MaximumDutyChangePerTick = 100 };
        var legacy = withTiers with { WarningTier = null, PreEmergencyTier = null };

        // Below the warning tier both templates must ask for the same duty.
        Assert.Equal(
            new FanCurveEngine(legacy).Evaluate(Snapshot(55)).DutyPercent,
            new FanCurveEngine(withTiers).Evaluate(Snapshot(55)).DutyPercent);

        // Inside the warning band the default template is never below the plain curve.
        foreach (var temperature in new[] { 70d, 76d, 82d, 88d })
            Assert.True(
                new FanCurveEngine(withTiers).Evaluate(Snapshot(temperature)).DutyPercent
                >= new FanCurveEngine(legacy).Evaluate(Snapshot(temperature)).DutyPercent);
    }

    [Fact]
    public void LegacyJsonWithoutOptionalFieldsKeepsCurveOnlyBehaviour()
    {
        // Exactly the shape an older build wrote: no tier and no RPM/s members at all.
        const string legacyJson = """
            {
              "version": 2,
              "points": [
                { "temperatureC": 45, "dutyPercent": 40, "rampDownTemperatureC": 43 },
                { "temperatureC": 55, "dutyPercent": 45, "rampDownTemperatureC": 53 },
                { "temperatureC": 65, "dutyPercent": 55, "rampDownTemperatureC": 63 },
                { "temperatureC": 75, "dutyPercent": 70, "rampDownTemperatureC": 73 },
                { "temperatureC": 85, "dutyPercent": 85, "rampDownTemperatureC": 83 },
                { "temperatureC": 90, "dutyPercent": 100, "rampDownTemperatureC": 88 }
              ],
              "minimumDutyPercent": 40,
              "maximumDutyPercent": 100,
              "emergencyDutyPercent": 100,
              "hysteresisC": 2,
              "maximumDutyChangePerTick": 10,
              "emergencyTemperatureC": 90
            }
            """;
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "fan-curve.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, legacyJson);
            var actual = new FanCurveConfigurationStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Null(actual.WarningTier);
            Assert.Null(actual.PreEmergencyTier);
            Assert.Null(actual.RpmRateLimit);
            Assert.False(actual.HasGradedTemperatureTiers);

            // 72 °C would be lifted to 80 % by the shipped template; the legacy file keeps the
            // curve-only target.
            var engine = new FanCurveEngine(actual);
            var decision = engine.Evaluate(Snapshot(72));
            Assert.Equal(FanTemperatureLevel.Normal, decision.Level);
            Assert.Equal(actual.CurveDutyAt(72), decision.DutyPercent);
            Assert.True(decision.DutyPercent < 80);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InvalidTierVariantsOfTheDefaultTemplateAreRejected()
    {
        // A tier on top of the fixed 90 °C emergency threshold.
        Assert.Throws<ArgumentException>(() =>
            (FanCurveSettings.Default with { PreEmergencyTier = new FanTemperatureTier(90, 100) }).Validate());

        // A tier below the curve duty at the same temperature would weaken cooling.
        Assert.Throws<ArgumentException>(() =>
            (FanCurveSettings.Default with { WarningTier = new FanTemperatureTier(70, 61) }).Validate());

        // Warning must stay cooler than pre-emergency, and never require more duty.
        Assert.Throws<ArgumentException>(() => (FanCurveSettings.Default with
        {
            WarningTier = new FanTemperatureTier(80, 100),
            PreEmergencyTier = new FanTemperatureTier(75, 100)
        }).Validate());
        Assert.Throws<ArgumentException>(() => (FanCurveSettings.Default with
        {
            WarningTier = new FanTemperatureTier(70, 90),
            PreEmergencyTier = new FanTemperatureTier(80, 85)
        }).Validate());

        // Out-of-range tier fields.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (FanCurveSettings.Default with { WarningTier = new FanTemperatureTier(70, 39) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (FanCurveSettings.Default with { WarningTier = new FanTemperatureTier(70, 101) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (FanCurveSettings.Default with { WarningTier = new FanTemperatureTier(0, 80) }).Validate());
    }

    [Fact]
    public void BuiltInProfilesUseTheShippedTemplate()
    {
        var profiles = FanProfile.Defaults;

        Assert.NotEmpty(profiles);
        foreach (var profile in profiles)
        {
            profile.Validate();
            Assert.Equal(FanCurveSettings.Default.WarningTier, profile.FanCurve.WarningTier);
            Assert.Equal(FanCurveSettings.Default.PreEmergencyTier, profile.FanCurve.PreEmergencyTier);
            Assert.Null(profile.FanCurve.RpmRateLimit);
            Assert.Equal(90, profile.FanCurve.EmergencyTemperatureC);
        }
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
