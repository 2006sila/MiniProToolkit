using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void RecorderWritesJsonlAndCsvWithEscapedFields()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(new HardwareTelemetryRecord(
                DateTimeOffset.UtcNow,
                52.5,
                2180,
                0,
                2300,
                2400,
                42,
                100,
                FanRpmFeedbackState.Stable,
                null,
                18,
                40,
                12.5,
                PowerMode.Balance,
                FanControlMode.FixedRpm,
                "WMI, retry",
                "mode \"changed\""));

            var jsonl = File.ReadAllText(recorder.JsonlPath);
            var csv = File.ReadAllText(recorder.CsvPath);
            Assert.Contains("WMI, retry", jsonl);
            Assert.Contains("\"WMI, retry\"", csv);
            Assert.Contains("\"mode \"\"changed\"\"\"", csv);
            Assert.Contains("Timestamp,CpuTemperatureC", csv);
            Assert.Contains("RpmFeedbackState", csv);
            Assert.Contains("CpuPackagePowerWatts", csv);
            Assert.Contains(",12.5,Balance,", csv);
            Assert.Contains("\"cpuPackagePowerWatts\":12.5", jsonl);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CsvEscapesDisplayStatusContainingHalfWidthComma()
    {
        var record = new HardwareTelemetryRecord(
            DateTimeOffset.UtcNow, 52.5, 2180, 0, null, null, null, null, FanRpmFeedbackState.Stable,
            null, 18, 40, 12.5, PowerMode.Balance, FanControlMode.FirmwareAutomatic, null, null)
        {
            LhmCpuPackageTemperatureC = 58.3,
            DisplayCpuTemperatureC = 52.5,
            DisplayCpuSource = CpuDisplaySource.LibreHardwareMonitor,
            DisplaySourceStatus = "LHM 温度不可用, 已回退 EC"
        };

        var line = TelemetryRecorder.ToCsv(record);
        var header = TelemetryRecorder.CsvHeader.Split(',');
        var cells = SplitCsvLine(line);

        // The half-width comma inside the status must not shift the column count.
        Assert.Contains("\"LHM 温度不可用, 已回退 EC\"", line, StringComparison.Ordinal);
        Assert.Equal(header.Length, cells.Count);
        Assert.Equal("CpuFrequencyStatus", header[^1]);
        Assert.Equal("LHM 温度不可用, 已回退 EC", cells[Array.IndexOf(header, "DisplaySourceStatus")]);
        Assert.Equal("LibreHardwareMonitor", cells[Array.IndexOf(header, "DisplayCpuSource")]);
    }

    [Fact]
    public void IncompatibleCsvIsArchivedIntactBeforeNewColumnsAreWritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            const string oldCsv = "Timestamp,Fan1Rpm,Error\r\nold,2200,legacy\r\n";
            File.WriteAllText(recorder.CsvPath, oldCsv);
            var record = HardwareTelemetryRecord.FromSnapshot(HardwareSnapshot.ErrorSnapshot("offline"));
            recorder.Append(record);
            var archive = Assert.Single(Directory.GetFiles(directory, "telemetry.csv.schema-*.bak"));
            Assert.Equal(oldCsv, File.ReadAllText(archive));
            var lines = File.ReadAllLines(recorder.CsvPath);
            var expectedColumns = TelemetryRecorder.CsvHeader.Split(',').Length;
            Assert.Equal(2, lines.Length);
            Assert.Equal(expectedColumns, lines[0].Split(',').Length);
            Assert.Equal(expectedColumns, lines[1].Split(',').Length);
            recorder.Append(record);
            Assert.Equal(3, File.ReadAllLines(recorder.CsvPath).Length);
            Assert.Single(Directory.GetFiles(directory, "telemetry.csv.schema-*.bak"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TelemetryCanBeCreatedFromSnapshot()
    {
        var snapshot = new HardwareSnapshot(
            DateTimeOffset.UtcNow,
            60,
            2500,
            0,
            PowerMode.Performance,
            30,
            50,
            FanControlMode.FixedRpm,
            55,
            null)
        {
            RequestedTargetRpm = 3000,
            TargetRpm = 2800,
            CpuPackagePowerWatts = 22.4
        };

        var record = HardwareTelemetryRecord.FromSnapshot(snapshot, "fixed-rpm");

        Assert.Equal(60, record.CpuTemperatureC);
        Assert.Equal(2800, record.TargetRpm);
        Assert.Equal(22.4, record.CpuPackagePowerWatts);
        Assert.Equal("fixed-rpm", record.Event);
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quoted && character == '"' && index + 1 < line.Length && line[index + 1] == '"')
            {
                current.Append('"');
                index++;
            }
            else if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(character);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
