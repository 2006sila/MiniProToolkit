using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// profiles.json must carry the optional curve RPM-feedback and 90 °C latch fields end to end:
// a saved profile keeps them, a profile file written before they existed still loads with
// feedback/latch off, unknown members never reset the file and invalid values are still rejected.
public sealed class FanProfileSafetyFieldTests
{
    private const string LegacyProfileJson = """
        {
          "version": 2,
          "profiles": [
            {
              "name": "旧方案",
              "powerMode": 0,
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
              "warningTier": { "temperatureC": 70, "dutyPercent": 80 },
              "preEmergencyTier": { "temperatureC": 82, "dutyPercent": 100 },
              "fanMode": 2,
              "fixedRpm": 2500
            }
          ]
        }
        """;

    [Fact]
    public void RpmFeedbackAndLatchRoundTripThroughTheProfilesFile()
    {
        var (directory, path) = CreateFile(null);
        var store = new FanProfileStore(path);
        var feedback = new FanCurveRpmFeedbackSettings(
            DeadbandRpm: 200,
            MinimumExpectedRpm: 700,
            StallRatio: 0.6,
            StallSampleCount: 5,
            MaximumCorrectionDuty: 8,
            MaximumCorrectionStep: 1,
            BadSampleTolerance: 3);
        var expected = new FanProfile("反馈方案", PowerMode.Balance,
            FanCurveSettings.Default with { RpmFeedback = feedback, LatchCurveEmergency = true });
        try
        {
            store.Save([expected]);
            var actual = Assert.Single(store.LoadOrDefault(out var warning));

            Assert.Null(warning);
            Assert.Equal(feedback, actual.FanCurve.RpmFeedback);
            Assert.True(actual.FanCurve.LatchCurveEmergency);
            Assert.True(actual.FanCurve.HasRpmFeedback);
            Assert.Equal(FanCurveSettings.Default.Points, actual.FanCurve.Points);
            Assert.Equal(FanCurveSettings.Default.WarningTier, actual.FanCurve.WarningTier);
            Assert.Equal(FanCurveSettings.Default.PreEmergencyTier, actual.FanCurve.PreEmergencyTier);
            actual.FanCurve.Validate();

            var json = File.ReadAllText(path);
            Assert.Contains("\"rpmFeedback\"", json);
            Assert.Contains("\"latchCurveEmergency\": true", json);
        }
        finally { Cleanup(directory); }
    }

    [Fact]
    public void DisabledSafetyFieldsSaveAsNullAndFalse()
    {
        var (directory, path) = CreateFile(null);
        var store = new FanProfileStore(path);
        try
        {
            store.Save([new("默认方案", PowerMode.Balance, FanCurveSettings.Default)]);
            var json = File.ReadAllText(path);
            var reloaded = Assert.Single(store.Load());

            Assert.Contains("\"rpmFeedback\": null", json);
            Assert.Contains("\"latchCurveEmergency\": false", json);
            Assert.Null(reloaded.FanCurve.RpmFeedback);
            Assert.False(reloaded.FanCurve.LatchCurveEmergency);
        }
        finally { Cleanup(directory); }
    }

    [Fact]
    public void LegacyProfilesFileWithoutSafetyFieldsKeepsFeedbackAndLatchOff()
    {
        var (directory, path) = CreateFile(LegacyProfileJson);
        try
        {
            var profile = Assert.Single(new FanProfileStore(path).Load());

            Assert.Null(profile.FanCurve.RpmFeedback);
            Assert.False(profile.FanCurve.LatchCurveEmergency);
            Assert.Equal([new FanCurvePoint(45, 40, 43), new FanCurvePoint(90, 100, 88)], profile.FanCurve.Points);
            Assert.Equal(ProfileFanMode.Curve, profile.FanMode);
            Assert.Equal(new FanTemperatureTier(70, 80), profile.FanCurve.WarningTier);
            Assert.Equal(new FanTemperatureTier(82, 100), profile.FanCurve.PreEmergencyTier);
        }
        finally { Cleanup(directory); }
    }

    [Fact]
    public void UnknownProfileMembersDoNotResetTheWholeFile()
    {
        var json = LegacyProfileJson.Replace(
            "\"fixedRpm\": 2500",
            "\"fixedRpm\": 2500, \"futureField\": { \"a\": 1 }, \"rpmFeedback\": { \"deadbandRpm\": 120 }");
        var (directory, path) = CreateFile(json);
        try
        {
            var profile = Assert.Single(new FanProfileStore(path).Load());

            Assert.Equal("旧方案", profile.Name);
            Assert.Equal(120, profile.FanCurve.RpmFeedback?.DeadbandRpm);
            Assert.False(profile.FanCurve.LatchCurveEmergency);
        }
        finally { Cleanup(directory); }
    }

    [Fact]
    public void InvalidFeedbackValuesAreRejectedInsteadOfLoaded()
    {
        var json = LegacyProfileJson.Replace(
            "\"fixedRpm\": 2500",
            "\"fixedRpm\": 2500, \"rpmFeedback\": { \"deadbandRpm\": -5 }");
        var (directory, path) = CreateFile(json);
        var store = new FanProfileStore(path);
        try
        {
            Assert.ThrowsAny<ArgumentException>(() => store.Load());

            var fallback = store.LoadOrDefault(out var warning);
            Assert.NotNull(warning);
            Assert.Equal(FanProfile.Defaults.Select(p => p.Name), fallback.Select(p => p.Name));
            Assert.All(fallback, profile =>
            {
                Assert.Null(profile.FanCurve.RpmFeedback);
                Assert.False(profile.FanCurve.LatchCurveEmergency);
            });
        }
        finally { Cleanup(directory); }
    }

    [Fact]
    public void EditorSafeDefaultsAreAValidEnabledConfiguration()
    {
        // Mirrors what the curve editor pre-fills when RPM feedback is switched on; production
        // code cannot be constructed here, so the values are asserted against Core validation.
        var feedback = new FanCurveRpmFeedbackSettings(150, 600, 0.5, 4, 12, 2, 2);
        var settings = FanCurveSettings.Default with { RpmFeedback = feedback, LatchCurveEmergency = true };

        settings.Validate();
        Assert.True(settings.HasRpmFeedback);
        Assert.True(settings.LatchCurveEmergency);
        Assert.Equal(90, settings.EmergencyTemperatureC);
        Assert.Equal(100, settings.EmergencyDutyPercent);
    }

    [Fact]
    public void VersionOneProfilesFileLoadsWithLatchAndFeedbackOff()
    {
        // Version 1 had no fanMode/fixedRpm and, of course, no curve safety fields.
        const string versionOneJson = """
            {
              "version": 1,
              "profiles": [
                {
                  "name": "一版方案",
                  "powerMode": 1,
                  "points": [
                    { "temperatureC": 45, "dutyPercent": 40 },
                    { "temperatureC": 90, "dutyPercent": 100 }
                  ],
                  "minimumDutyPercent": 40,
                  "maximumDutyPercent": 100,
                  "emergencyDutyPercent": 100,
                  "hysteresisC": 2,
                  "maximumDutyChangePerTick": 10,
                  "emergencyTemperatureC": 90
                }
              ]
            }
            """;
        var (directory, path) = CreateFile(versionOneJson);
        try
        {
            var profile = Assert.Single(new FanProfileStore(path).Load());

            Assert.Null(profile.FanCurve.RpmFeedback);
            Assert.False(profile.FanCurve.LatchCurveEmergency);
            Assert.Equal(ProfileFanMode.Automatic, profile.FanMode);
            Assert.Equal(2500, profile.FixedRpm);
            Assert.True(profile.FanCurve.HasGradedTemperatureTiers is false);
        }
        finally { Cleanup(directory); }
    }

    [Fact]
    public void ReloadedProfileStillLatchesAndKeepsFeedbackDisabledByDefault()
    {
        var (directory, path) = CreateFile(null);
        var store = new FanProfileStore(path);
        var latched = new FanProfile("锁存方案", PowerMode.Performance,
            FanCurveSettings.Default with { LatchCurveEmergency = true });
        var plain = new FanProfile("旧方案", PowerMode.Balance, FanCurveSettings.Default);
        try
        {
            store.Save([latched, plain]);
            var reloaded = store.Load();

            var latchedSettings = reloaded[0].FanCurve;
            var plainSettings = reloaded[1].FanCurve;
            Assert.True(latchedSettings.LatchCurveEmergency);
            Assert.False(plainSettings.LatchCurveEmergency);
            Assert.Null(latchedSettings.RpmFeedback);

            var latchEngine = new FanCurveEngine(latchedSettings, FanCalibration.Default);
            latchEngine.Evaluate(Snapshot(90));
            Assert.True(latchEngine.EmergencyLatched);
            latchEngine.Evaluate(Snapshot(50));
            Assert.Equal(100, latchEngine.CurrentDuty);

            // A reloaded profile without the latch cools down again on its own.
            var plainEngine = new FanCurveEngine(plainSettings, FanCalibration.Default);
            plainEngine.Evaluate(Snapshot(90));
            Assert.False(plainEngine.EmergencyLatched);
            Assert.True(plainEngine.Evaluate(Snapshot(50)).DutyPercent < 100);
        }
        finally { Cleanup(directory); }
    }

    private static HardwareSnapshot Snapshot(double temperature) => new(
        DateTimeOffset.UtcNow, temperature, 3000, 0, PowerMode.Balance, 20, 40,
        FanControlMode.FirmwareAutomatic, null, null);

    private static (string Directory, string Path) CreateFile(string? content)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "profiles.json");
        if (content is not null)
            File.WriteAllText(path, content);
        return (directory, path);
    }

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // Best effort only; temporary test directory.
        }
    }
}
