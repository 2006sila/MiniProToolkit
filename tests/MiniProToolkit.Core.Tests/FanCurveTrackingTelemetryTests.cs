using System.Text.Json;
using System.Text.Json.Nodes;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCurveTrackingTelemetryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static HardwareSnapshot Snapshot() => new(DateTimeOffset.UtcNow, 60, 3400, 0,
        PowerMode.Balance, 20, 40, FanControlMode.Curve, 70, null)
    {
        RequestedTargetRpm = 2900, TargetRpm = 2800, RpmError = -600
    };

    private static FanCurveDecision TrackingDecision() => new(FanCurveAction.NoChange, null, "安全底线优先")
    {
        TargetRpm = 3000, ActualRpm = 3400, RpmError = -400,
        TrackingState = FanCurveRpmTrackingState.SafetyFloorLimited
    };

    [Fact]
    public void MissingDecisionLegacyDecisionAndOldJsonKeepTrackingNull()
    {
        var record = HardwareTelemetryRecord.FromSnapshot(Snapshot());
        var legacyDecision = HardwareTelemetryRecord.FromSnapshot(Snapshot(), curveDecision: FanCurveDecision.SetDuty(40, "legacy"));
        var oldJson = JsonNode.Parse(JsonSerializer.Serialize(record, JsonOptions))!.AsObject();
        foreach (var name in new[] { "curveTargetRpm", "curveActualRpm", "curveRpmError", "curveTrackingState" })
            oldJson.Remove(name);
        var old = JsonSerializer.Deserialize<HardwareTelemetryRecord>(oldJson.ToJsonString(), JsonOptions)!;
        foreach (var item in new[] { record, legacyDecision, old })
        {
            Assert.Null(item.CurveTargetRpm);
            Assert.Null(item.CurveActualRpm);
            Assert.Null(item.CurveRpmError);
            Assert.Null(item.CurveTrackingState);
        }
    }

    [Fact]
    public void ExplicitDecisionRoundTripsWithoutReplacingFixedRpmFields()
    {
        var decision = TrackingDecision();
        var record = HardwareTelemetryRecord.FromSnapshot(Snapshot(), "curve", "Normal", "reason", decision);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        Assert.Contains("\"curveTargetRpm\":3000", json);
        Assert.Contains("\"curveActualRpm\":3400", json);
        Assert.Contains("\"curveRpmError\":-400", json);
        Assert.Contains("\"curveTrackingState\":\"SafetyFloorLimited\"", json);
        var roundtrip = JsonSerializer.Deserialize<HardwareTelemetryRecord>(json, JsonOptions)!;
        Assert.Equal(record, roundtrip);
        Assert.Equal(3000, roundtrip.CurveTargetRpm);
        Assert.Equal(3400, roundtrip.CurveActualRpm);
        Assert.Equal(-400, roundtrip.CurveRpmError);
        Assert.Equal("SafetyFloorLimited", roundtrip.CurveTrackingState);
        Assert.Equal(2800, roundtrip.TargetRpm);
        Assert.Equal(2900, roundtrip.RequestedTargetRpm);
        Assert.Equal(-600, roundtrip.RpmError);
    }

    [Fact]
    public void MissingTachAndEmergencyBypassKeepAnObservableStateWithoutInventingRpm()
    {
        foreach (var state in new[] { FanCurveRpmTrackingState.RpmUnavailable, FanCurveRpmTrackingState.EmergencyBypass })
        {
            var decision = TrackingDecision() with { TargetRpm = null, ActualRpm = null, RpmError = null, TrackingState = state };
            var record = HardwareTelemetryRecord.FromSnapshot(Snapshot(), curveDecision: decision);
            Assert.Equal(state.ToString(), record.CurveTrackingState);
            Assert.Null(record.CurveActualRpm);
            Assert.Null(record.CurveRpmError);
            var status = FanCurveStatusFormatter.Describe(decision);
            Assert.Contains("实际 -- RPM", status.Text);
            Assert.Contains($"state={state}", status.Text);
        }
    }

    [Fact]
    public void TrackingDoesNotChangeThe31ColumnCsvOrTriggerASchemaArchive()
    {
        const string expectedHeader = "Timestamp,CpuTemperatureC,Fan1Rpm,Fan2Rpm,RequestedTargetRpm,TargetRpm,TargetDuty,RpmError,RpmFeedbackState,FanFault,CpuUsagePercent,MemoryUsagePercent,CpuPackagePowerWatts,PowerMode,FanControlMode,Error,Event,LhmCpuPackageTemperatureC,LhmCpuPackagePowerWatts,LhmStatus,DisplayCpuTemperatureC,DisplayCpuPackagePowerWatts,DisplayCpuSource,DisplayTemperatureFromLhm,DisplayPowerFromLhm,DisplaySourceStatus,CpuFrequencyMhz,CpuFrequencyPercentOfBase,CpuFrequencyBaseMhz,CpuFrequencySource,CpuFrequencyStatus";
        Assert.Equal(expectedHeader, TelemetryRecorder.CsvHeader);
        Assert.Equal(31, TelemetryRecorder.CsvHeader.Split(',').Length);
        var plain = HardwareTelemetryRecord.FromSnapshot(Snapshot());
        var tracking = plain with { CurveTargetRpm = 3000, CurveActualRpm = 3400, CurveRpmError = -400, CurveTrackingState = "SafetyFloorLimited" };
        Assert.Equal(TelemetryRecorder.ToCsv(plain), TelemetryRecorder.ToCsv(tracking));
        Assert.Equal(31, TelemetryRecorder.ToCsv(tracking).Split(',').Length);
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        try
        {
            using var recorder = new TelemetryRecorder(directory);
            recorder.Append(plain);
            recorder.Append(tracking);
            Assert.Equal(expectedHeader, File.ReadAllLines(recorder.CsvPath)[0]);
            Assert.Empty(Directory.GetFiles(directory, "*.schema-*.bak"));
            var rows = File.ReadAllLines(recorder.JsonlPath);
            Assert.Equal(3000, JsonSerializer.Deserialize<HardwareTelemetryRecord>(rows[1], JsonOptions)!.CurveTargetRpm);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void StatusAppendsTrackingToTextAndReasonWithoutChangingExistingClassification()
    {
        var decision = TrackingDecision();
        var status = FanCurveStatusFormatter.Describe(decision, curveEnabled: true, applied: true, currentDuty: 70);
        Assert.Equal(FanCurveStatusKind.Normal, status.Kind);
        Assert.True(status.Applied);
        foreach (var text in new[] { status.Text, status.Reason })
        {
            Assert.Contains("显式目标 3000 RPM", text);
            Assert.Contains("实际 3400 RPM", text);
            Assert.Contains("误差(目标-实际) -400 RPM", text);
            Assert.Contains("state=SafetyFloorLimited", text);
        }
        Assert.StartsWith("安全底线优先", status.Reason);
        var legacy = FanCurveStatusFormatter.Describe(FanCurveDecision.SetDuty(40, "legacy"));
        Assert.Equal("legacy", legacy.Reason);
        Assert.DoesNotContain("state=", legacy.Text);
    }

    [Fact]
    public void ApplyFailureLeaseLatchAndFeedbackFaultRetainTheirPriority()
    {
        var conflict = TrackingDecision() with
        {
            Action = FanCurveAction.RestoreAutomatic,
            EmergencyLatched = true, RpmFeedbackFault = true, Level = FanTemperatureLevel.Emergency
        };
        var failure = FanCurveStatusFormatter.Describe(conflict,
            FanCurveLeaseDecision.Restore(FanCurveLeasePolicy.LeaseInvalidReason), applyError: "write failed");
        Assert.Equal(FanCurveStatusKind.ApplyFailed, failure.Kind);
        Assert.StartsWith("曲线已停止", failure.Text);
        foreach (var (reason, kind) in new[]
        {
            (FanCurveLeasePolicy.GuardianStoppedReason, FanCurveStatusKind.GuardianUnavailable),
            (FanCurveLeasePolicy.LeaseInvalidReason, FanCurveStatusKind.LeaseInvalid),
            (FanCurveLeasePolicy.RecoveryPendingReason, FanCurveStatusKind.RecoveryPending)
        })
        {
            var lease = FanCurveStatusFormatter.Describe(conflict, FanCurveLeaseDecision.Restore(reason));
            Assert.Equal(kind, lease.Kind);
            Assert.StartsWith(reason, lease.Reason);
            Assert.Contains("state=SafetyFloorLimited", lease.Reason);
            Assert.False(lease.Applied);
        }
        Assert.Equal(FanCurveStatusKind.EmergencyLatched, FanCurveStatusFormatter.Describe(conflict).Kind);
        var fault = FanCurveStatusFormatter.Describe(conflict with { EmergencyLatched = false }, applied: true);
        Assert.Equal(FanCurveStatusKind.RpmFeedbackFault, fault.Kind);
        Assert.Contains("已交还 EC 自动", fault.Text);
        Assert.Contains("显式目标 3000 RPM", fault.Text);
        Assert.False(fault.Applied);
    }
}
