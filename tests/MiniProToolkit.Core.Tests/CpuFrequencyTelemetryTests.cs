using System.Text;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Focused tests for the additive CPU-frequency fields: snapshot -> telemetry passthrough,
/// old CSV/JSONL compatibility and display re-projection.
/// </summary>
public sealed class CpuFrequencyTelemetryTests
{
    private static HardwareSnapshot SnapshotWithFrequency() => new(
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
        CpuFrequencyMhz = 4200,
        CpuFrequencyPercentOfBase = 150,
        CpuFrequencyBaseMhz = 2800,
        CpuFrequencySource = CpuFrequencySource.ProcessorInformationPercent,
        CpuFrequencyStatus = null
    };

    [Fact]
    public void SnapshotFieldsAreAdditiveAndControlFieldsStayUntouched()
    {
        var snapshot = SnapshotWithFrequency();

        // Positional control fields keep their own values.
        Assert.Equal(52, snapshot.CpuTemperatureC);
        Assert.Equal(2180, snapshot.Fan1Rpm);
        Assert.Equal(FanControlMode.FirmwareAutomatic, snapshot.FanControlMode);
        // The frequency fields are init-only additions.
        Assert.Equal(4200, snapshot.CpuFrequencyMhz);
        Assert.Equal(CpuFrequencySource.ProcessorInformationPercent, snapshot.CpuFrequencySource);
    }

    [Fact]
    public void SnapshotsWithoutMetricsLeaveTheFrequencyUnknown()
    {
        var snapshot = HardwareSnapshot.ErrorSnapshot("offline");

        Assert.Null(snapshot.CpuFrequencyMhz);
        Assert.Equal(CpuFrequencySource.Unavailable, snapshot.CpuFrequencySource);
    }

    [Fact]
    public void TelemetryRecordCarriesFrequencyFields()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(SnapshotWithFrequency(), "sample");

        Assert.Equal(4200, record.CpuFrequencyMhz);
        Assert.Equal(150, record.CpuFrequencyPercentOfBase);
        Assert.Equal(2800, record.CpuFrequencyBaseMhz);
        Assert.Equal(CpuFrequencySource.ProcessorInformationPercent, record.CpuFrequencySource);
    }

    [Fact]
    public void DisplaySourceReprojectionKeepsTheOriginalFrequencyFields()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(SnapshotWithFrequency());

        var reprojected = CpuDisplaySourceSelector.Reproject(record, CpuDisplaySource.LibreHardwareMonitor);

        // Only the display block changes; the frequency fields must survive the `with` copy.
        Assert.Equal(CpuDisplaySource.LibreHardwareMonitor, reprojected.DisplayCpuSource);
        Assert.Equal(record.CpuFrequencyMhz, reprojected.CpuFrequencyMhz);
        Assert.Equal(record.CpuFrequencyBaseMhz, reprojected.CpuFrequencyBaseMhz);
        Assert.Equal(record.CpuFrequencySource, reprojected.CpuFrequencySource);
        Assert.Equal(52, reprojected.CpuTemperatureC);
    }

    [Fact]
    public void SnapshotSourceApplyKeepsTheOriginalFrequencyFields()
    {
        var snapshot = SnapshotWithFrequency();

        var applied = CpuDisplaySourceSelector.Apply(snapshot, CpuDisplaySource.LibreHardwareMonitor);

        Assert.Equal(snapshot.CpuFrequencyMhz, applied.CpuFrequencyMhz);
        Assert.Equal(snapshot.CpuFrequencySource, applied.CpuFrequencySource);
        Assert.Equal(52, applied.CpuTemperatureC);
    }

    [Fact]
    public void CsvAppendsFrequencyColumnsAfterTheDisplayBlock()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(SnapshotWithFrequency());
        var header = TelemetryRecorder.CsvHeader.Split(',');
        var cells = SplitCsvLine(TelemetryRecorder.ToCsv(record));

        Assert.Equal(header.Length, cells.Length);
        Assert.True(Array.IndexOf(header, "CpuFrequencyMhz") > Array.IndexOf(header, "DisplaySourceStatus"));
        Assert.Equal("CpuFrequencyStatus", header[^1]);
        Assert.Equal("4200", cells[Array.IndexOf(header, "CpuFrequencyMhz")]);
        Assert.Equal("150.0", cells[Array.IndexOf(header, "CpuFrequencyPercentOfBase")]);
        Assert.Equal("ProcessorInformationPercent", cells[Array.IndexOf(header, "CpuFrequencySource")]);
    }

    [Fact]
    public void MissingMetricsWriteEmptyCellsAndStayInTheSameColumnCount()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(HardwareSnapshot.ErrorSnapshot("offline"));
        var header = TelemetryRecorder.CsvHeader.Split(',');
        var cells = SplitCsvLine(TelemetryRecorder.ToCsv(record));

        Assert.Equal(header.Length, cells.Length);
        Assert.Equal(string.Empty, cells[Array.IndexOf(header, "CpuFrequencyMhz")]);
        Assert.Equal("Unavailable", cells[Array.IndexOf(header, "CpuFrequencySource")]);
    }

    [Fact]
    public void CsvWithOnlyThePreFrequencyColumnsIsArchivedInsteadOfOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            // A schema written before the display-source and frequency columns existed.
            const string oldCsv = "Timestamp,CpuTemperatureC,Fan1Rpm,Error\r\nold,52,2180,legacy\r\n";
            File.WriteAllText(recorder.CsvPath, oldCsv);

            recorder.Append(HardwareTelemetryRecord.FromSnapshot(SnapshotWithFrequency()));

            var archive = Assert.Single(Directory.GetFiles(directory, "telemetry.csv.schema-*.bak"));
            Assert.Equal(oldCsv, File.ReadAllText(archive));
            var lines = File.ReadAllLines(recorder.CsvPath);
            Assert.Equal(TelemetryRecorder.CsvHeader, lines[0]);
            Assert.Equal(TelemetryRecorder.CsvHeader.Split(',').Length, SplitCsvLine(lines[1]).Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void OldJsonlWithoutFrequencyFieldsLoadsWithEmptyValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "telemetry.jsonl");
            // Exactly the shape written before this change: no frequency keys at all.
            var legacy = "{\"timestamp\":\"2026-02-01T10:00:00+00:00\",\"cpuTemperatureC\":52," +
                "\"fan1Rpm\":2180,\"powerMode\":\"Balance\",\"cpuUsagePercent\":25,\"memoryUsagePercent\":40," +
                "\"fanControlMode\":\"FirmwareAutomatic\",\"rpmFeedbackState\":\"Disabled\"}";
            File.WriteAllText(path, legacy + Environment.NewLine);

            var result = TelemetryHistory.Load([path]);

            var record = Assert.Single(result.Records);
            Assert.Empty(result.Warnings);
            Assert.Null(record.CpuFrequencyMhz);
            Assert.Null(record.CpuFrequencyStatus);
            Assert.Equal(CpuFrequencySource.Unavailable, record.CpuFrequencySource);
            // Pre-existing fields keep their meaning.
            Assert.Equal(52, record.CpuTemperatureC);
            Assert.Equal(PowerMode.Balance, record.PowerMode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void JsonlWithUnknownExtraFieldsStillLoads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "telemetry.jsonl");
            // A row written by a build that also recorded fields this build no longer has.
            var legacy = "{\"timestamp\":\"2026-02-01T10:00:00+00:00\",\"cpuTemperatureC\":52," +
                "\"fan1Rpm\":2180,\"powerMode\":\"Balance\",\"retiredMetricBytesPerSecond\":1024," +
                "\"cpuFrequencyMhz\":4200,\"cpuFrequencySource\":\"ProcessorInformationPercent\"}";
            File.WriteAllText(path, legacy + Environment.NewLine);

            var result = TelemetryHistory.Load([path]);

            var record = Assert.Single(result.Records);
            Assert.Empty(result.Warnings);
            Assert.Equal(52, record.CpuTemperatureC);
            Assert.Equal(4200, record.CpuFrequencyMhz);
            Assert.Equal(CpuFrequencySource.ProcessorInformationPercent, record.CpuFrequencySource);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NewJsonlRoundTripsTheFrequencyFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(HardwareTelemetryRecord.FromSnapshot(SnapshotWithFrequency()));

            var result = TelemetryHistory.Load([recorder.JsonlPath]);

            var record = Assert.Single(result.Records);
            Assert.Equal(4200, record.CpuFrequencyMhz);
            Assert.Equal(CpuFrequencySource.ProcessorInformationPercent, record.CpuFrequencySource);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
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
        return fields.ToArray();
    }
}
