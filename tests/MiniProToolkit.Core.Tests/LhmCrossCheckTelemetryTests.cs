using System.Text;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class LhmCrossCheckTelemetryTests
{
    private static HardwareSnapshot SnapshotWithCrossCheck() => new(
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
        CpuPackagePowerWatts = 12.5,
        LhmCpuPackageTemperatureC = 58.3,
        LhmCpuPackagePowerWatts = 15.4,
        LhmStatus = "LHM 正常（温度 CPU Package 58.3°C，功耗 Package 15.4 W）"
    };

    [Fact]
    public void ExistingFieldsKeepTheirOwnValuesWhenCrossCheckIsAttached()
    {
        var snapshot = SnapshotWithCrossCheck();

        Assert.Equal(52, snapshot.CpuTemperatureC);
        Assert.Equal(12.5, snapshot.CpuPackagePowerWatts);
        Assert.Equal(58.3, snapshot.LhmCpuPackageTemperatureC);
        Assert.Equal(15.4, snapshot.LhmCpuPackagePowerWatts);
    }

    [Fact]
    public void TelemetryRecordCarriesBothValueSets()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(SnapshotWithCrossCheck(), "sample");

        Assert.Equal(52, record.CpuTemperatureC);
        Assert.Equal(12.5, record.CpuPackagePowerWatts);
        Assert.Equal(58.3, record.LhmCpuPackageTemperatureC);
        Assert.Equal(15.4, record.LhmCpuPackagePowerWatts);
        Assert.Equal("LHM 正常（温度 CPU Package 58.3°C，功耗 Package 15.4 W）", record.LhmStatus);
        Assert.Equal("sample", record.Event);
    }

    [Fact]
    public void CsvKeepsOriginalColumnsAndAppendsCrossCheckColumns()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(SnapshotWithCrossCheck());

        var header = TelemetryRecorder.CsvHeader.Split(',');
        var values = TelemetryRecorder.ToCsv(record).Split(',');

        // Original schema prefix is unchanged, new columns are additive.
        Assert.Equal("Timestamp,CpuTemperatureC,Fan1Rpm,Fan2Rpm,RequestedTargetRpm,TargetRpm,TargetDuty,RpmError,RpmFeedbackState,FanFault,CpuUsagePercent,MemoryUsagePercent,CpuPackagePowerWatts,PowerMode,FanControlMode,Error,Event",
            string.Join(",", header.Take(17)));
        Assert.Contains("CpuTemperatureC", header);
        Assert.Equal(header.Length, values.Length);
        Assert.Equal("12.5", values[12]);
        Assert.Equal("58.3", values[17]);
        Assert.Equal("15.4", values[18]);
    }

    [Fact]
    public void MissingCrossCheckWritesEmptyCsvCellsAndNullsInJsonl()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(HardwareTelemetryRecord.FromSnapshot(HardwareSnapshot.ErrorSnapshot("offline")));

            var csvLines = File.ReadAllLines(recorder.CsvPath);
            Assert.Equal(2, csvLines.Length);
            var cells = csvLines[1].Split(',');
            Assert.Equal(string.Empty, cells[17]);
            Assert.Equal(string.Empty, cells[18]);
            Assert.Equal(string.Empty, cells[19]);

            var jsonl = File.ReadAllText(recorder.JsonlPath, Encoding.UTF8);
            Assert.Contains("\"lhmCpuPackageTemperatureC\":null", jsonl);
            Assert.Contains("\"lhmCpuPackagePowerWatts\":null", jsonl);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
