using System.Text;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// The GPU temperature is an additive, JSON-only display value: the CSV header and
/// column count must not change, old JSONL rows must still deserialize and the
/// value must never replace or influence a control field.
/// </summary>
public sealed class GpuTelemetryTests
{
    private static HardwareSnapshot SnapshotWithGpu() => new(
        DateTimeOffset.UtcNow,
        52,
        2180,
        0,
        PowerMode.Balance,
        25,
        40,
        FanControlMode.FirmwareAutomatic,
        null,
        null)
    {
        GpuTemperatureC = 58.3,
        GpuSensorName = "GPU Core",
        GpuSensorStatus = "LHM GPU 正常（NVIDIA GeForce RTX 4060 · GPU Core 58.3°C）"
    };

    [Fact]
    public void SnapshotGpuFieldsAreNullByDefault()
    {
        var snapshot = HardwareSnapshot.ErrorSnapshot("offline");

        Assert.Null(snapshot.GpuTemperatureC);
        Assert.Null(snapshot.GpuSensorName);
        Assert.Null(snapshot.GpuSensorStatus);
    }

    [Fact]
    public void GpuFieldsStayAdditiveAndControlFieldsAreUntouched()
    {
        var snapshot = SnapshotWithGpu();

        Assert.Equal(52, snapshot.CpuTemperatureC);
        Assert.Equal(58.3, snapshot.GpuTemperatureC);
        Assert.Equal("GPU Core", snapshot.GpuSensorName);
    }

    [Fact]
    public void TelemetryRecordCarriesGpuFields()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(SnapshotWithGpu(), "sample");

        Assert.Equal(58.3, record.GpuTemperatureC);
        Assert.Equal("GPU Core", record.GpuSensorName);
        Assert.Equal("LHM GPU 正常（NVIDIA GeForce RTX 4060 · GPU Core 58.3°C）", record.GpuSensorStatus);
        Assert.Equal(52, record.CpuTemperatureC);
    }

    [Fact]
    public void FromSnapshotWithoutGpuValuesKeepsThemNull()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(HardwareSnapshot.ErrorSnapshot("offline"));

        Assert.Null(record.GpuTemperatureC);
        Assert.Null(record.GpuSensorName);
        Assert.Null(record.GpuSensorStatus);
    }

    [Fact]
    public void CsvHeaderAndColumnsAreUnchanged()
    {
        const string expectedHeader =
            "Timestamp,CpuTemperatureC,Fan1Rpm,Fan2Rpm,RequestedTargetRpm,TargetRpm,TargetDuty,RpmError,RpmFeedbackState,FanFault,CpuUsagePercent,MemoryUsagePercent,CpuPackagePowerWatts,PowerMode,FanControlMode,Error,Event,LhmCpuPackageTemperatureC,LhmCpuPackagePowerWatts,LhmStatus,DisplayCpuTemperatureC,DisplayCpuPackagePowerWatts,DisplayCpuSource,DisplayTemperatureFromLhm,DisplayPowerFromLhm,DisplaySourceStatus,CpuFrequencyMhz,CpuFrequencyPercentOfBase,CpuFrequencyBaseMhz,CpuFrequencySource,CpuFrequencyStatus";

        Assert.Equal(expectedHeader, TelemetryRecorder.CsvHeader);
        var line = TelemetryRecorder.ToCsv(HardwareTelemetryRecord.FromSnapshot(SnapshotWithGpu()));
        // The GPU value is JSON-only: it must not appear in the CSV row at all.
        Assert.DoesNotContain("58.3", line, StringComparison.Ordinal);
        Assert.Equal(TelemetryRecorder.CsvHeader.Split(',').Length, line.Split(',').Length);
    }

    [Fact]
    public void JsonlCarriesGpuFieldsAndOldRowsStillLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(HardwareTelemetryRecord.FromSnapshot(SnapshotWithGpu()));

            var jsonl = File.ReadAllText(recorder.JsonlPath, Encoding.UTF8);
            Assert.Contains("\"gpuTemperatureC\":58.3", jsonl);
            Assert.Contains("\"gpuSensorName\":\"GPU Core\"", jsonl);

            // A row written before the GPU fields existed must still deserialize.
            var legacyPath = Path.Combine(directory, "legacy.jsonl");
            File.WriteAllText(legacyPath,
                "{\"timestamp\":\"2000-05-01T12:00:00+00:00\",\"cpuTemperatureC\":52,\"fan1Rpm\":2180,\"powerMode\":\"Balance\",\"fanControlMode\":\"FirmwareAutomatic\"}" +
                Environment.NewLine,
                Encoding.UTF8);

            var loaded = TelemetryHistory.Load([recorder.JsonlPath, legacyPath]);
            Assert.Empty(loaded.Warnings);
            Assert.Equal(2, loaded.Records.Count);

            var legacy = loaded.Records.First();
            Assert.Equal(52, legacy.CpuTemperatureC);
            Assert.Null(legacy.GpuTemperatureC);
            Assert.Null(legacy.GpuSensorName);
            Assert.Null(legacy.GpuSensorStatus);

            var current = loaded.Records.Last();
            Assert.Equal(58.3, current.GpuTemperatureC);
            Assert.Equal("GPU Core", current.GpuSensorName);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
