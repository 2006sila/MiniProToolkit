using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCurveConfigurationTests
{
    [Fact]
    public void SaveAndLoadRoundTripPreservesSettings()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "fan-curve.json");
        Directory.CreateDirectory(directory);
        var store = new FanCurveConfigurationStore(path);
        var expected = new FanCurveSettings(
            [new(45, 40), new(70, 65), new(90, 100)],
            HysteresisC: 3,
            MaximumDutyChangePerTick: 7);
        try
        {
            store.Save(expected);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(expected.MinimumDutyPercent, actual.MinimumDutyPercent);
            Assert.Equal(expected.MaximumDutyPercent, actual.MaximumDutyPercent);
            Assert.Equal(expected.EmergencyDutyPercent, actual.EmergencyDutyPercent);
            Assert.Equal(expected.HysteresisC, actual.HysteresisC);
            Assert.Equal(expected.MaximumDutyChangePerTick, actual.MaximumDutyChangePerTick);
            Assert.Equal(expected.MaximumDutyIncreasePerTick, actual.MaximumDutyIncreasePerTick);
            Assert.Equal(expected.MaximumDutyDecreasePerTick, actual.MaximumDutyDecreasePerTick);
            Assert.Equal(expected.EmergencyTemperatureC, actual.EmergencyTemperatureC);
            Assert.Equal(expected.Points.Count, actual.Points.Count);
            Assert.Equal(expected.Points, actual.Points);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InvalidFileFallsBackToDefaultWithWarning()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "fan-curve.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{ invalid json");
        try
        {
            var store = new FanCurveConfigurationStore(path);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Equal(FanCurveSettings.Default.MinimumDutyPercent, actual.MinimumDutyPercent);
            Assert.Equal(FanCurveSettings.Default.MaximumDutyPercent, actual.MaximumDutyPercent);
            Assert.Equal(FanCurveSettings.Default.Points, actual.Points);
            Assert.NotNull(warning);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LegacyVersionTwoJsonWithoutOptionalFieldsKeepsLegacyBehaviour()
    {
        // Exact shape written by builds that know nothing about tiers or RPM/s limits.
        const string legacyJson = """
            {
              "version": 2,
              "points": [
                { "temperatureC": 45, "dutyPercent": 40, "rampDownTemperatureC": 43 },
                { "temperatureC": 90, "dutyPercent": 100, "rampDownTemperatureC": 88 }
              ],
              "minimumDutyPercent": 40,
              "maximumDutyPercent": 100,
              "emergencyDutyPercent": 100,
              "hysteresisC": 2,
              "maximumDutyChangePerTick": 10,
              "emergencyTemperatureC": 90,
              "maximumDutyIncreasePerTick": null,
              "maximumDutyDecreasePerTick": null
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
            Assert.Null(actual.RpmRateLimit);
            Assert.Null(actual.WarningTier);
            Assert.Null(actual.PreEmergencyTier);
            Assert.False(actual.HasGradedTemperatureTiers);
            Assert.Equal(40, actual.MinimumDutyPercent);
            Assert.Equal(90, actual.EmergencyTemperatureC);

            var engine = new FanCurveEngine(actual);
            Assert.Equal(40, engine.Evaluate(Snapshot(45)).DutyPercent);
            Assert.Equal(100, engine.Evaluate(Snapshot(90)).DutyPercent);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GradedTiersAndRpmLimitRoundTrip()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "fan-curve.json");
        Directory.CreateDirectory(directory);
        var store = new FanCurveConfigurationStore(path);
        var expected = new FanCurveSettings(
            [new(45, 40), new(70, 65), new(90, 100)],
            HysteresisC: 2,
            MaximumDutyChangePerTick: 10,
            RpmRateLimit: new FanRpmRateLimit(500, 900, 1.5),
            WarningTier: new FanTemperatureTier(70, 70),
            PreEmergencyTier: new FanTemperatureTier(85, 100));
        try
        {
            store.Save(expected);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(expected.RpmRateLimit, actual.RpmRateLimit);
            Assert.Equal(expected.WarningTier, actual.WarningTier);
            Assert.Equal(expected.PreEmergencyTier, actual.PreEmergencyTier);
            Assert.Equal(expected.Points, actual.Points);
            Assert.True(actual.HasGradedTemperatureTiers);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GradedTiersAndRpmLimitRoundTripThroughProfiles()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "profiles.json");
        Directory.CreateDirectory(directory);
        var store = new FanProfileStore(path);
        var curve = new FanCurveSettings(
            [new(45, 40), new(70, 65), new(90, 100)],
            RpmRateLimit: new FanRpmRateLimit(500, 900, 1.5),
            WarningTier: new FanTemperatureTier(70, 70),
            PreEmergencyTier: new FanTemperatureTier(85, 100));
        try
        {
            store.Save([new("分级", PowerMode.Balance, curve)]);
            var profile = Assert.Single(store.Load());

            Assert.Equal(curve.RpmRateLimit, profile.FanCurve.RpmRateLimit);
            Assert.Equal(curve.WarningTier, profile.FanCurve.WarningTier);
            Assert.Equal(curve.PreEmergencyTier, profile.FanCurve.PreEmergencyTier);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InvalidOptionalCurveFieldsAreRejectedOnLoad()    {
        // RPM/s rise limit far below the validation range.
        AssertFallsBackToDefaultWithWarning("""
            { "version": 2, "points": [ { "temperatureC": 45, "dutyPercent": 40 }, { "temperatureC": 90, "dutyPercent": 100 } ],
              "minimumDutyPercent": 40, "maximumDutyPercent": 100, "emergencyDutyPercent": 100,
              "hysteresisC": 2, "maximumDutyChangePerTick": 10, "emergencyTemperatureC": 90,
              "rpmRateLimit": { "maximumRiseRpmPerSecond": 5 } }
            """);

        // A pre-emergency tier that would lower duty below the curve at the same temperature.
        AssertFallsBackToDefaultWithWarning("""
            { "version": 2, "points": [ { "temperatureC": 45, "dutyPercent": 40 }, { "temperatureC": 90, "dutyPercent": 100 } ],
              "minimumDutyPercent": 40, "maximumDutyPercent": 100, "emergencyDutyPercent": 100,
              "hysteresisC": 2, "maximumDutyChangePerTick": 10, "emergencyTemperatureC": 90,
              "preEmergencyTier": { "temperatureC": 80, "dutyPercent": 41 } }
            """);

        // A tier sitting on top of the fixed 90 C emergency threshold.
        AssertFallsBackToDefaultWithWarning("""
            { "version": 2, "points": [ { "temperatureC": 45, "dutyPercent": 40 }, { "temperatureC": 90, "dutyPercent": 100 } ],
              "minimumDutyPercent": 40, "maximumDutyPercent": 100, "emergencyDutyPercent": 100,
              "hysteresisC": 2, "maximumDutyChangePerTick": 10, "emergencyTemperatureC": 90,
              "preEmergencyTier": { "temperatureC": 90, "dutyPercent": 100 } }
            """);
    }

    [Fact]
    public void UnknownCurveFieldsAreIgnoredInsteadOfBreakingTheLoad()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "fan-curve.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, """
                { "version": 2, "points": [ { "temperatureC": 45, "dutyPercent": 40 }, { "temperatureC": 90, "dutyPercent": 100 } ],
                  "minimumDutyPercent": 40, "maximumDutyPercent": 100, "emergencyDutyPercent": 100,
                  "hysteresisC": 2, "maximumDutyChangePerTick": 10, "emergencyTemperatureC": 90,
                  "futureField": { "anything": 1 } }
                """);

            var actual = new FanCurveConfigurationStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(2, actual.Points.Count);
            Assert.Null(actual.RpmRateLimit);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LegacyProfilesJsonWithoutOptionalCurveFieldsLoads()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "profiles.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, """
                { "version": 2, "profiles": [ {
                    "name": "旧方案", "powerMode": 0,
                    "points": [ { "temperatureC": 45, "dutyPercent": 40, "rampDownTemperatureC": 43 },
                                { "temperatureC": 90, "dutyPercent": 100, "rampDownTemperatureC": 88 } ],
                    "minimumDutyPercent": 40, "maximumDutyPercent": 100, "emergencyDutyPercent": 100,
                    "hysteresisC": 2, "maximumDutyChangePerTick": 10, "emergencyTemperatureC": 90,
                    "maximumDutyIncreasePerTick": null, "maximumDutyDecreasePerTick": null,
                    "fanMode": 0, "fixedRpm": 2500
                } ] }
                """);

            var profile = Assert.Single(new FanProfileStore(path).Load());

            Assert.Null(profile.FanCurve.RpmRateLimit);
            Assert.Null(profile.FanCurve.WarningTier);
            Assert.Null(profile.FanCurve.PreEmergencyTier);
            Assert.Equal(40, profile.FanCurve.MinimumDutyPercent);
            Assert.Equal(90, profile.FanCurve.EmergencyTemperatureC);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AssertFallsBackToDefaultWithWarning(string json)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "fan-curve.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, json);
            var actual = new FanCurveConfigurationStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(FanCurveSettings.Default.Points, actual.Points);
            Assert.Null(actual.RpmRateLimit);
            // A corrupt file falls back to the shipped template, which now carries the
            // conservative graded tiers instead of the legacy null fields.
            Assert.Equal(FanCurveSettings.Default.WarningTier, actual.WarningTier);
            Assert.Equal(FanCurveSettings.Default.PreEmergencyTier, actual.PreEmergencyTier);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
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
