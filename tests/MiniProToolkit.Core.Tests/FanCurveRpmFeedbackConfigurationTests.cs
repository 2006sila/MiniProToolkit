using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// Configuration compatibility for the optional curve RPM-feedback fields: schema stays version 2,
// the fields are nullable, and a file written before they existed loads with feedback off.
public sealed class FanCurveRpmFeedbackConfigurationTests
{
    [Fact]
    public void LegacyJsonWithoutFeedbackFieldsKeepsFeedbackAndLatchOff()
    {
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
              "emergencyTemperatureC": 90
            }
            """;
        var (directory, path) = CreateFile(legacyJson);
        try
        {
            var settings = new FanCurveConfigurationStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Null(settings.RpmFeedback);
            Assert.False(settings.LatchCurveEmergency);
            Assert.False(settings.HasRpmFeedback);

            // Curve-only behaviour: low RPM never faults the legacy file.
            var engine = new FanCurveEngine(settings);
            Assert.NotEqual(FanCurveAction.RestoreAutomatic, engine.Evaluate(Snapshot(70, 0)).Action);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void FeedbackAndLatchRoundTripThroughTheCurveFile()
    {
        var (directory, path) = CreateFile(null);
        var store = new FanCurveConfigurationStore(path);
        var expected = new FanCurveSettings(
            [new(40, 40), new(90, 100)],
            RpmFeedback: new FanCurveRpmFeedbackSettings(
                DeadbandRpm: 200,
                MinimumExpectedRpm: 700,
                StallRatio: 0.6,
                StallSampleCount: 5,
                MaximumCorrectionDuty: 8,
                MaximumCorrectionStep: 1,
                BadSampleTolerance: 3),
            LatchCurveEmergency: true);
        try
        {
            store.Save(expected);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(expected.RpmFeedback, actual.RpmFeedback);
            Assert.True(actual.LatchCurveEmergency);
            Assert.True(actual.HasRpmFeedback);
            actual.Validate();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void InvalidFeedbackFieldsFallBackToTheShippedTemplate()
    {
        var (directory, path) = CreateFile("""
            { "version": 2, "points": [ { "temperatureC": 45, "dutyPercent": 40 }, { "temperatureC": 90, "dutyPercent": 100 } ],
              "minimumDutyPercent": 40, "maximumDutyPercent": 100, "emergencyDutyPercent": 100,
              "hysteresisC": 2, "maximumDutyChangePerTick": 10, "emergencyTemperatureC": 90,
              "rpmFeedback": { "deadbandRpm": -5 } }
            """);
        try
        {
            var settings = new FanCurveConfigurationStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(FanCurveSettings.Default.Points, settings.Points);
            Assert.Null(settings.RpmFeedback);
            Assert.False(settings.LatchCurveEmergency);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SavingWithoutTheOptionalFieldsKeepsTheLegacyShape()
    {
        var (directory, path) = CreateFile(null);
        var store = new FanCurveConfigurationStore(path);
        try
        {
            store.Save(new FanCurveSettings([new(45, 40), new(90, 100)]));
            var json = File.ReadAllText(path);
            var reloaded = store.Load();

            Assert.Contains("\"version\": 2", json);
            Assert.Contains("\"rpmFeedback\": null", json);
            Assert.Null(reloaded.RpmFeedback);
            Assert.False(reloaded.LatchCurveEmergency);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static (string Directory, string Path) CreateFile(string? content)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "fan-curve.json");
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
}
