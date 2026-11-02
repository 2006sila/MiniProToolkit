using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class AuxTemperatureTests
{
    private static AuxTemperatureCandidate Sensor(
        string name, double? value = 42, string id = "/board/0/temperature/0",
        AuxTemperatureKind kind = AuxTemperatureKind.Motherboard,
        LhmSensorCategory category = LhmSensorCategory.Temperature) =>
        new(kind, kind == AuxTemperatureKind.Storage ? id[..id.LastIndexOf('/')] : "/board/0",
            id, name, value, category, kind == AuxTemperatureKind.Storage && id.StartsWith("/nvme/", StringComparison.Ordinal));

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(125.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidTemperaturesRemainUnknown(double? value)
    {
        var result = AuxTemperatureSelector.Select([Sensor("System", value)], AuxTemperatureKind.Motherboard);
        Assert.False(result.IsKnown);
        Assert.Equal(AuxTemperatureKind.Unknown, result.Kind);
        Assert.Null(result.TemperatureC);
        Assert.Null(result.SensorId);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(42.35)]
    [InlineData(125)]
    public void PlausibleTemperaturesAreNormalized(double value)
    {
        var result = AuxTemperatureSelector.Select([Sensor("System", value)], AuxTemperatureKind.Motherboard);
        Assert.True(result.IsKnown);
        Assert.Equal(Math.Round(value, 1, MidpointRounding.AwayFromZero), result.TemperatureC);
    }

    [Fact]
    public void HardwareFamilyAndUnitAreMandatory()
    {
        var candidates = new[]
        {
            Sensor("System", kind: AuxTemperatureKind.Unknown),
            Sensor("System", kind: AuxTemperatureKind.Storage),
            Sensor("System", category: LhmSensorCategory.Other),
            Sensor("System", id: "")
        };
        Assert.False(AuxTemperatureSelector.Select(candidates, AuxTemperatureKind.Motherboard).IsKnown);
        Assert.False(AuxTemperatureSelector.Select(candidates, AuxTemperatureKind.Unknown).IsKnown);
        Assert.False(AuxTemperatureSelector.Select(null, AuxTemperatureKind.Storage).IsKnown);
    }

    [Theory]
    [InlineData("CPU")]
    [InlineData("CPU Package")]
    [InlineData("CPU (Tctl/Tdie)")]
    [InlineData("VRM")]
    [InlineData("Temperature #1")]
    [InlineData("GPU Core")]
    public void BoardSelectionNeverPretendsCpuOrVrmIsBoardTemperature(string name)
    {
        Assert.False(AuxTemperatureSelector.Select([Sensor(name)], AuxTemperatureKind.Motherboard).IsKnown);
    }

    [Fact]
    public void BoardAndStorageAreSeparateEvenWhenNamesOverlap()
    {
        var candidates = new[]
        {
            Sensor("System", 41),
            Sensor("Temperature", 55, "/nvme/0/temperature/0", AuxTemperatureKind.Storage),
            Sensor("CPU Package", 90, "/board/cpu"),
            Sensor("VRM", 80, "/board/vrm")
        };
        Assert.Equal(41, AuxTemperatureSelector.Select(candidates, AuxTemperatureKind.Motherboard).TemperatureC);
        Assert.Equal(55, AuxTemperatureSelector.Select(candidates, AuxTemperatureKind.Storage).TemperatureC);
    }

    [Fact]
    public void NvmeCompositeWinsOverNumberedAndSataTemperatures()
    {
        var candidates = new[]
        {
            Sensor("Temperature #1", 70, "/nvme/0/temperature/1", AuxTemperatureKind.Storage),
            Sensor("Temperature", 40, "/hdd/0/temperature/0", AuxTemperatureKind.Storage),
            Sensor("Composite Temperature", 50, "/nvme/0/temperature/0", AuxTemperatureKind.Storage)
        };
        var result = AuxTemperatureSelector.Select(candidates, AuxTemperatureKind.Storage, "/hdd/0/temperature/0");
        Assert.Equal("/nvme/0/temperature/0", result.SensorId);
        Assert.Equal(50, result.TemperatureC);
        Assert.Equal(result, AuxTemperatureSelector.Select(candidates.Reverse(), AuxTemperatureKind.Storage));
    }

    [Fact]
    public void DefaultLhmNvmeTemperatureWinsOverNumberedSensor()
    {
        var result = AuxTemperatureSelector.Select([
            Sensor("Temperature #1", 75, "/nvme/0/temperature/1", AuxTemperatureKind.Storage),
            Sensor("Temperature", 50, "/nvme/0/temperature/0", AuxTemperatureKind.Storage),
            Sensor("Temperature", 35, "/hdd/0/temperature/0", AuxTemperatureKind.Storage)
        ], AuxTemperatureKind.Storage);
        Assert.Equal(50, result.TemperatureC);
    }

    [Fact]
    public void StableIdentifierAndDeterministicSortIgnoreInputOrder()
    {
        var candidates = new[] { Sensor("System", 43, "/board/b"), Sensor("System", 41, "/board/a") };
        var first = AuxTemperatureSelector.Select(candidates, AuxTemperatureKind.Motherboard);
        Assert.Equal("/board/a", first.SensorId);
        Assert.Equal(first, AuxTemperatureSelector.Select(candidates.Reverse(), AuxTemperatureKind.Motherboard));
        Assert.Equal("/board/b", AuxTemperatureSelector.Select(candidates.Reverse(), AuxTemperatureKind.Motherboard, "/board/b").SensorId);
        var invalidPrevious = new[] { candidates[0] with { Value = double.NaN }, candidates[1] };
        Assert.Equal("/board/a", AuxTemperatureSelector.Select(invalidPrevious, AuxTemperatureKind.Motherboard, "/board/b").SensorId);
    }

    [Fact]
    public void SataAndInvalidCompositeFallbackAreSupportedWithoutCpuFallback()
    {
        var result = AuxTemperatureSelector.Select([
            Sensor("Composite Temperature", null, "/nvme/0/t0", AuxTemperatureKind.Storage),
            Sensor("CPU Temperature", 60, "/nvme/0/t1", AuxTemperatureKind.Storage),
            Sensor("Drive Temperature", 38, "/hdd/0/t0", AuxTemperatureKind.Storage)
        ], AuxTemperatureKind.Storage);
        Assert.Equal(38, result.TemperatureC);
        Assert.Equal("/hdd/0/t0", result.SensorId);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetryPolicyKeepsAnyHealthyGroupFreshAndResetsFailures(bool boardKnown, bool storageKnown)
    {
        var retry = AuxTemperatureRetryPolicy.AfterSample(boardKnown, storageKnown, 7);
        Assert.Equal(TimeSpan.FromSeconds(5), retry.Delay);
        Assert.Equal(0, retry.Failures);
    }

    [Fact]
    public void RetryPolicyBacksOffOnlyWhenBothGroupsAreMissing()
    {
        var first = AuxTemperatureRetryPolicy.AfterSample(false, false, 0);
        Assert.Equal(1, first.Failures);
        Assert.Equal(TimeSpan.FromSeconds(5), first.Delay);
        var second = AuxTemperatureRetryPolicy.AfterSample(false, false, first.Failures);
        Assert.Equal(2, second.Failures);
        Assert.Equal(TimeSpan.FromSeconds(10), second.Delay);
        var capped = AuxTemperatureRetryPolicy.AfterSample(false, false, 7);
        Assert.Equal(7, capped.Failures);
        Assert.Equal(TimeSpan.FromSeconds(320), capped.Delay);
        var recovered = AuxTemperatureRetryPolicy.AfterSample(false, true, capped.Failures);
        var missingAgain = AuxTemperatureRetryPolicy.AfterSample(false, false, recovered.Failures);
        Assert.Equal(first, missingAgain);
    }

    [Fact]
    public void RetryPolicyCachesFailuresForAtLeastFiveSecondsAndCapsBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), AuxTemperatureRetryPolicy.MinimumSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), AuxTemperatureRetryPolicy.FailureDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), AuxTemperatureRetryPolicy.FailureDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(320), AuxTemperatureRetryPolicy.FailureDelay(100));
    }

    [Fact]
    public void TelemetryIsJsonOnlyAndLeavesAllOriginalControlFieldsUntouched()
    {
        var original = new HardwareSnapshot(DateTimeOffset.UtcNow, 55, 2100, 0, PowerMode.Balance,
            20, 40, FanControlMode.FirmwareAutomatic, null, null) { CpuPackagePowerWatts = 15 };
        var snapshot = original with
        {
            MotherboardTemperatureC = 43.2, MotherboardTemperatureSensorId = "/board/0/t0",
            MotherboardTemperatureSensorName = "System", StorageTemperatureC = 49.7,
            StorageTemperatureSensorId = "/nvme/0/t0", StorageTemperatureSensorName = "Composite Temperature",
            AuxTemperatureStatus = "LHM 只读正常"
        };
        var record = HardwareTelemetryRecord.FromSnapshot(snapshot);
        Assert.Equal(original.CpuTemperatureC, record.CpuTemperatureC);
        Assert.Equal(original.CpuPackagePowerWatts, record.CpuPackagePowerWatts);
        Assert.Equal(original.Fan1Rpm, record.Fan1Rpm);
        Assert.Equal(original.TargetDuty, record.TargetDuty);
        Assert.Equal(43.2, record.MotherboardTemperatureC);
        Assert.Equal("/board/0/t0", record.MotherboardTemperatureSensorId);
        Assert.Equal("System", record.MotherboardTemperatureSensorName);
        Assert.Equal(49.7, record.StorageTemperatureC);
        Assert.Equal("/nvme/0/t0", record.StorageTemperatureSensorId);
        Assert.Equal("Composite Temperature", record.StorageTemperatureSensorName);
        Assert.Equal(snapshot.AuxTemperatureStatus, record.AuxTemperatureStatus);
        Assert.Equal(TelemetryRecorder.ToCsv(HardwareTelemetryRecord.FromSnapshot(original)), TelemetryRecorder.ToCsv(record));
        Assert.DoesNotContain("Motherboard", TelemetryRecorder.CsvHeader);
        Assert.DoesNotContain("Storage", TelemetryRecorder.CsvHeader);
        Assert.DoesNotContain("AuxTemperature", TelemetryRecorder.CsvHeader);
    }

    [Fact]
    public void JsonlRoundTripAndLegacyUnknownRemainCompatible()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            var snapshot = HardwareSnapshot.ErrorSnapshot("offline") with
            {
                MotherboardTemperatureC = 39, StorageTemperatureC = 48,
                StorageTemperatureSensorName = "Temperature", AuxTemperatureStatus = "Unknown：部分无读数"
            };
            recorder.Append(HardwareTelemetryRecord.FromSnapshot(snapshot));
            var json = File.ReadAllText(recorder.JsonlPath);
            Assert.Contains("\"motherboardTemperatureC\":39", json);
            Assert.Contains("\"storageTemperatureC\":48", json);
            var loaded = TelemetryHistory.Load([recorder.JsonlPath]);
            Assert.Empty(loaded.Warnings);
            Assert.Equal(48, Assert.Single(loaded.Records).StorageTemperatureC);
            var legacy = JsonSerializer.Deserialize<HardwareTelemetryRecord>(
                "{\"timestamp\":\"2000-01-01T00:00:00Z\",\"cpuTemperatureC\":50}",
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Null(legacy.MotherboardTemperatureC);
            Assert.Null(legacy.StorageTemperatureC);
            Assert.Null(legacy.AuxTemperatureStatus);
            var unknown = HardwareTelemetryRecord.FromSnapshot(HardwareSnapshot.ErrorSnapshot("offline"));
            Assert.Null(unknown.MotherboardTemperatureC);
            Assert.Null(unknown.StorageTemperatureC);
            Assert.Null(unknown.MotherboardTemperatureSensorId);
            Assert.Null(unknown.StorageTemperatureSensorName);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
