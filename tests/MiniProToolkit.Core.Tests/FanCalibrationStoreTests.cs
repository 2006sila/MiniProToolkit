using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCalibrationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-calibration-tests", Guid.NewGuid().ToString("N"));
    private FanCalibrationStore Store => new(Path.Combine(_directory, "fan-calibration.json"));
    private static FanCalibration Custom => new([new(40, 1800), new(70, 3000), new(100, 4500)]);

    [Fact]
    public void MissingConfigurationUsesDefaultWithoutWritingFile()
    {
        var calibration = Store.LoadOrDefault(out var warning);
        Assert.Null(warning);
        Assert.Same(FanCalibration.Default, calibration);
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public void RoundTripFeedsTargetAndFeedbackControllers()
    {
        Store.Save(Custom);
        var loaded = Store.LoadOrDefault(out var warning);
        Assert.Null(warning);
        Assert.Equal(Custom.Points, loaded.Points);
        var result = new FanRpmTargetController(loaded).Resolve(2500);
        Assert.Equal(58, result.DutyPercent);
        Assert.Equal(4500, new FanRpmTargetController(loaded).Resolve(5000).ClampedRpm);
        var feedback = new FanRpmFeedbackController(loaded);
        feedback.Start(5000, 100);
        Assert.Equal(4500, feedback.TargetRpm);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"points\":[]}")]
    [InlineData("{\"version\":1,\"points\":null}")]
    [InlineData("{\"version\":1,\"points\":[null]}")]
    [InlineData("{\"version\":1,\"points\":[{\"rpm\":1200},{\"dutyPercent\":100,\"rpm\":4000}]}")]
    [InlineData("{\"version\":1,\"points\":[{\"dutyPercent\":0},{\"dutyPercent\":100,\"rpm\":4000}]}")]
    [InlineData("{\"version\":1,\"points\":[{\"dutyPercent\":40,\"rpm\":1800},{\"dutyPercent\":100,\"rpm\":1700}]}")]
    [InlineData("{\"version\":1,\"points\":[{\"dutyPercent\":50,\"rpm\":1800},{\"dutyPercent\":100,\"rpm\":4000}]}")]
    public void InvalidFileFallsBackWithoutOverwritingEvidence(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Store.Path, json);
        Assert.Same(FanCalibration.Default, Store.LoadOrDefault(out var warning));
        Assert.NotNull(warning);
        Assert.Equal(json, File.ReadAllText(Store.Path));
        Assert.ThrowsAny<Exception>(() => Store.Load());
    }

    [Fact]
    public void StrictImportRejectsMissingFile()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Store.Load());
    }

    [Fact]
    public void ReplacingConfigurationKeepsPreviousVersionAndCleansTemporaryFiles()
    {
        Store.Save(FanCalibration.Default);
        var previous = File.ReadAllText(Store.Path);
        Store.Save(Custom);
        Assert.Equal(previous, File.ReadAllText(Store.Path + ".bak"));
        Assert.Equal(Custom.Points, Store.Load().Points);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void RejectingInvalidCoverageLeavesSavedFileUntouched()
    {
        Store.Save(Custom);
        var previous = File.ReadAllText(Store.Path);
        var partial = new FanCalibration([new(50, 2500), new(80, 3500)]);
        Assert.Throws<ArgumentException>(() => Store.Save(partial));
        Assert.Equal(previous, File.ReadAllText(Store.Path));
    }

    [Theory]
    [InlineData(50, 1800, 100, 4000)]
    [InlineData(40, 1800, 90, 4000)]
    [InlineData(40, 100, 100, 4000)]
    [InlineData(40, 1800, 100, 1800)]
    public void FixedRpmRequiresUsableRange(int lowDuty, int lowRpm, int highDuty, int highRpm)
    {
        var calibration = new FanCalibration([new(lowDuty, lowRpm), new(highDuty, highRpm)]);
        Assert.Throws<ArgumentException>(calibration.ValidateForFixedRpm);
    }

    [Fact]
    public void CalibrationPointsCannotBeChangedThroughArrayCast()
    {
        var source = new[] { new FanCalibrationPoint(40, 1800), new FanCalibrationPoint(100, 4000) };
        var calibration = new FanCalibration(source);
        source[0] = new(40, 3000);
        Assert.Equal(1800, calibration.RpmForDuty(40));
        Assert.False(calibration.Points is FanCalibrationPoint[]);
        Assert.Throws<NotSupportedException>(() => ((IList<FanCalibrationPoint>)calibration.Points)[0] = new(40, 3000));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
