using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class TelemetryHistoryTests
{
    private static HardwareTelemetryRecord Record(DateTimeOffset time, PowerMode mode = PowerMode.Balance,
        FanControlMode fanMode = FanControlMode.FirmwareAutomatic, string? error = null,
        string? fault = null, string? eventName = null) => new(
        time, 55, 2200, 0, null, null, null, null, FanRpmFeedbackState.Disabled,
        fault, 20, 40, 12.5, mode, fanMode, error, eventName);

    [Fact]
    public void LoadsJsonlRotationsSkipsMalformedLinesAndOrdersRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Record(DateTimeOffset.UtcNow.AddMinutes(-2));
            var second = Record(DateTimeOffset.UtcNow.AddMinutes(-1), PowerMode.Performance, FanControlMode.FixedRpm);
            File.WriteAllText(Path.Combine(directory, "telemetry.jsonl.1"),
                System.Text.Json.JsonSerializer.Serialize(second) + "\n{broken\n");
            File.WriteAllText(Path.Combine(directory, "telemetry.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(first) + "\n");
            var result = TelemetryHistory.Load(Directory.GetFiles(directory, "telemetry.jsonl*"));
            Assert.Equal(2, result.Records.Count);
            Assert.True(result.Records[0].Timestamp < result.Records[1].Timestamp);
            Assert.Contains(result.Warnings, warning => warning.Contains("无效"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LoadsLegacyRecordWithoutPowerAsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "telemetry.jsonl");
            File.WriteAllText(path, "{\"timestamp\":\"2026-01-01T00:00:00+00:00\",\"cpuTemperatureC\":52,\"fan1Rpm\":2000,\"fan2Rpm\":0,\"requestedTargetRpm\":null,\"targetRpm\":null,\"targetDuty\":null,\"rpmError\":null,\"rpmFeedbackState\":\"Disabled\",\"fanFault\":null,\"cpuUsagePercent\":20,\"memoryUsagePercent\":40,\"powerMode\":\"Balance\",\"fanControlMode\":\"FirmwareAutomatic\",\"error\":null,\"event\":null}");
            var loaded = TelemetryHistory.Load([path]);
            Assert.Null(Assert.Single(loaded.Records).CpuPackagePowerWatts);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LoadsCpuPackagePowerFromJsonlHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var record = Record(DateTimeOffset.UtcNow) with { CpuPackagePowerWatts = 18.7 };
            var path = Path.Combine(directory, "telemetry.jsonl");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(record) + Environment.NewLine);
            var loaded = TelemetryHistory.Load([path]);
            Assert.Equal(18.7, Assert.Single(loaded.Records).CpuPackagePowerWatts);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DetectsModeControlRecoveryAndErrorEventsWithoutDuplicates()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            Record(now),
            Record(now.AddSeconds(1), PowerMode.Performance, FanControlMode.FixedRpm),
            Record(now.AddSeconds(2), PowerMode.Performance, FanControlMode.FirmwareAutomatic, "WMI unavailable", "fan stopped", "restore"),
            Record(now.AddSeconds(3), PowerMode.Performance, FanControlMode.FirmwareAutomatic, "WMI unavailable", "fan stopped", "restore")
        };
        var events = TelemetryHistory.Events(records);
        Assert.Contains(events, e => e.Kind == HistoryEventKind.Mode);
        Assert.Contains(events, e => e.Kind == HistoryEventKind.Control);
        Assert.Contains(events, e => e.Kind == HistoryEventKind.Recovery);
        Assert.Equal(1, events.Count(e => e.Message.Contains("WMI unavailable")));
        Assert.Equal(1, events.Count(e => e.Message.Contains("fan stopped")));
    }

    [Fact]
    public void DisplaySourceChangeEventUsesChineseSourceNames()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            Record(now),
            Record(now.AddSeconds(1)) with { DisplayCpuSource = CpuDisplaySource.LibreHardwareMonitor },
            Record(now.AddSeconds(2)) with { DisplayCpuSource = CpuDisplaySource.LibreHardwareMonitor },
            Record(now.AddSeconds(3)) with { DisplayCpuSource = CpuDisplaySource.EcEnergyMeter }
        };

        var events = TelemetryHistory.Events(records);

        var displayEvents = events.Where(e => e.Message.StartsWith("主显示源：", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, displayEvents.Length);
        Assert.All(displayEvents, e => Assert.Equal(HistoryEventKind.Control, e.Kind));
        Assert.Equal("主显示源：EC 温度 + Energy Meter 功耗 → LHM（LibreHardwareMonitor）", displayEvents[0].Message);
        Assert.Equal("主显示源：LHM（LibreHardwareMonitor） → EC 温度 + Energy Meter 功耗", displayEvents[1].Message);
        Assert.All(displayEvents, e =>
        {
            Assert.DoesNotContain("EcEnergyMeter", e.Message);
            Assert.DoesNotContain("→ LibreHardwareMonitor", e.Message);
            Assert.DoesNotContain("LibreHardwareMonitor →", e.Message);
        });
    }

    [Fact]
    public void CurveStatusTransitionsRecordTheReasonOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            Record(now) with { CurveStatus = nameof(FanCurveStatusKind.Normal), CurveReason = "Curve target changed" },
            Record(now.AddSeconds(1)) with { CurveStatus = nameof(FanCurveStatusKind.Warning), CurveReason = "Graded temperature level reached (Warning)" },
            // Repeat of the same status: no duplicate history row.
            Record(now.AddSeconds(2)) with { CurveStatus = nameof(FanCurveStatusKind.Warning), CurveReason = "Graded temperature level reached (Warning)" },
            Record(now.AddSeconds(3)) with { CurveStatus = nameof(FanCurveStatusKind.LeaseInvalid), CurveReason = FanCurveLeasePolicy.LeaseInvalidReason }
        };

        var events = TelemetryHistory.Events(records);

        var curveEvents = events.Where(e => e.Message.StartsWith("曲线状态：", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, curveEvents.Length);
        Assert.Equal(HistoryEventKind.Control, curveEvents[0].Kind);
        Assert.Contains(nameof(FanCurveStatusKind.Warning), curveEvents[1].Message);
        Assert.Equal(HistoryEventKind.Recovery, curveEvents[2].Kind);
        Assert.Contains("租约无效", curveEvents[2].Message);
    }

    [Fact]
    public void RecordsWithoutCurveStatusKeepsTheLegacyEventList()
    {
        var now = DateTimeOffset.UtcNow;
        var events = TelemetryHistory.Events([Record(now), Record(now.AddSeconds(1))]);

        Assert.DoesNotContain(events, e => e.Message.StartsWith("曲线状态：", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportsSelectedRecordsAndEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var record = Record(DateTimeOffset.UtcNow, eventName: "profile: Quiet, Office", error: "token=private-value at C:\\synthetic\\Alice\\private.log");
            var events = new[] { new HistoryEvent(record.Timestamp, HistoryEventKind.Control, "方案,切换 token=event-secret", "应用") };
            var csv = Path.Combine(directory, "history.csv");
            var jsonl = Path.Combine(directory, "history.jsonl");
            var eventCsv = Path.Combine(directory, "events.csv");
            TelemetryHistory.ExportRecords(csv, [record], true);
            TelemetryHistory.ExportRecords(jsonl, [record], false);
            TelemetryHistory.ExportEvents(eventCsv, events);
            Assert.Contains("Timestamp,CpuTemperatureC", File.ReadAllText(csv));
            Assert.Contains("profile: Quiet, Office", File.ReadAllText(jsonl));
            Assert.DoesNotContain("private-value", File.ReadAllText(jsonl));
            Assert.DoesNotContain("Alice", File.ReadAllText(jsonl));
            Assert.Contains("\"方案,切换 token=[REDACTED]\"", File.ReadAllText(eventCsv));
            Assert.DoesNotContain("event-secret", File.ReadAllText(eventCsv));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DiagnosticRedactorRemovesSecretsAndLocalPaths()
    {
        var input = "token=abc123 Authorization: Bearer secret-value file=C:\\synthetic\\Alice\\private.log \\\\server\\share\\x";
        var output = DiagnosticRedactor.Redact(input);
        Assert.DoesNotContain("abc123", output);
        Assert.DoesNotContain("secret-value", output);
        Assert.DoesNotContain("Alice", output);
        Assert.DoesNotContain("server\\share", output);
        Assert.Contains("[REDACTED]", output);
    }
}
