using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

// Pure Core coverage for the curve status classification and the curve-emergency dedup. The
// safety gate (Guardian stopped / invalid lease) must never be rendered as a sensor failure, and
// the curve 90 °C emergency is a separate signal from the fixed-RPM emergency latch.
public sealed class FanCurveStatusTests
{
    private static FanCurveDecision Decision(
        FanCurveAction action, int? duty, FanTemperatureLevel level, string reason) =>
        new(action, duty, reason) { Level = level };

    [Fact]
    public void LeaseGateRefusalsAreNeverReportedAsSensorFailures()
    {
        var cases = new (string Reason, FanCurveStatusKind Kind, string Text)[]
        {
            (FanCurveLeasePolicy.GuardianStoppedReason, FanCurveStatusKind.GuardianUnavailable, "Guardian 已停止"),
            (FanCurveLeasePolicy.LeaseInvalidReason, FanCurveStatusKind.LeaseInvalid, "租约无效"),
            (FanCurveLeasePolicy.RecoveryPendingReason, FanCurveStatusKind.RecoveryPending, "恢复未完成")
        };

        foreach (var (reason, kind, text) in cases)
        {
            var lease = FanCurveLeaseDecision.Restore(reason);
            var status = FanCurveStatusFormatter.Describe(
                FanCurveDecision.Restore(reason), lease, curveEnabled: false, applied: true);

            Assert.Equal(kind, status.Kind);
            Assert.False(status.Applied);
            Assert.Contains(text, status.Text);
            // The whole point of the change: a safety-gate refusal is not a sensor problem.
            Assert.DoesNotContain("传感器", status.Text);
            Assert.Contains("曲线: ", status.Text);
        }
    }

    [Fact]
    public void CurveOwnRestoreAutomaticIsTheSensorState()
    {
        var status = FanCurveStatusFormatter.Describe(
            FanCurveDecision.Restore(FanCurveStatusFormatter.InvalidSensorReason),
            curveEnabled: false,
            applied: true);

        Assert.Equal(FanCurveStatusKind.SensorInvalid, status.Kind);
        Assert.Contains(FanCurveStatusFormatter.SensorInvalidText, status.Text);
        Assert.Contains("已恢复 EC 自动", status.Text);

        // Same decision on the preview path: still the sensor state, but never claims a restore.
        var preview = FanCurveStatusFormatter.Describe(
            FanCurveDecision.Restore(FanCurveStatusFormatter.InvalidSensorReason));
        Assert.Equal(FanCurveStatusKind.SensorInvalid, preview.Kind);
        Assert.DoesNotContain("已恢复", preview.Text);
        Assert.StartsWith("曲线预览", preview.Text);
    }

    [Fact]
    public void GradedLevelsMapToDistinctKindsAndTexts()
    {
        var normal = FanCurveStatusFormatter.Describe(
            Decision(FanCurveAction.SetDuty, 45, FanTemperatureLevel.Normal, "Curve target changed"),
            curveEnabled: true, applied: true);
        var warning = FanCurveStatusFormatter.Describe(
            Decision(FanCurveAction.SetDuty, 80, FanTemperatureLevel.Warning, "Graded temperature level reached"),
            curveEnabled: true, applied: true);
        var preEmergency = FanCurveStatusFormatter.Describe(
            Decision(FanCurveAction.SetDuty, 100, FanTemperatureLevel.PreEmergency, "Graded temperature level reached"),
            curveEnabled: true, applied: true);
        var emergency = FanCurveStatusFormatter.Describe(
            Decision(FanCurveAction.SetDuty, 100, FanTemperatureLevel.Emergency, "Emergency temperature limit reached"),
            curveEnabled: true, applied: true);

        Assert.Equal(FanCurveStatusKind.Normal, normal.Kind);
        Assert.Equal(FanCurveStatusKind.Warning, warning.Kind);
        Assert.Equal(FanCurveStatusKind.PreEmergency, preEmergency.Kind);
        Assert.Equal(FanCurveStatusKind.CurveEmergency, emergency.Kind);

        Assert.Contains("正常", normal.Text);
        Assert.Contains("45%", normal.Text);
        Assert.Contains("预警级", warning.Text);
        Assert.Contains("紧急前级", preEmergency.Text);
        Assert.Contains("紧急满速", emergency.Text);
        Assert.Contains("≥90°C", emergency.Text);
        Assert.Contains("100%", emergency.Text);
        Assert.All(new[] { normal, warning, preEmergency, emergency },
            status => Assert.True(status.Applied));

        // The levels must stay distinguishable from each other, not just from the gate states.
        var texts = new[] { normal.Text, warning.Text, preEmergency.Text, emergency.Text };
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EmergencyLabelUsesTheConfiguredThreshold()
    {
        var status = FanCurveStatusFormatter.Describe(
            Decision(FanCurveAction.SetDuty, 100, FanTemperatureLevel.Emergency, "Emergency temperature limit reached"),
            curveEnabled: true, applied: true, emergencyTemperatureC: 88);

        Assert.Contains("≥88°C", status.Text);
    }

    [Fact]
    public void PreviewNeverReportsAnAppliedEmergency()
    {
        var preview = FanCurveStatusFormatter.Describe(
            Decision(FanCurveAction.SetDuty, 100, FanTemperatureLevel.Emergency, "Emergency temperature limit reached"));

        Assert.Equal(FanCurveStatusKind.CurveEmergency, preview.Kind);
        Assert.False(preview.Applied);
        Assert.StartsWith("曲线预览", preview.Text);
        Assert.Contains("未应用", preview.Text);
    }

    [Fact]
    public void UnchangedDutyFallsBackToTheCurrentDuty()
    {
        var status = FanCurveStatusFormatter.Describe(
            Decision(FanCurveAction.NoChange, null, FanTemperatureLevel.Warning, "Target unchanged or within hysteresis band"),
            curveEnabled: true, applied: true, currentDuty: 55);

        Assert.Contains("55%", status.Text);
        Assert.Equal(FanTemperatureLevel.Warning, status.Level);
    }

    [Fact]
    public void FailedApplyIsItsOwnKind()
    {
        var status = FanCurveStatusFormatter.Describe(
            FanCurveDecision.Restore("curve failure; firmware control requested"),
            curveEnabled: true, applied: false, applyError: "EC 写入失败");

        Assert.Equal(FanCurveStatusKind.ApplyFailed, status.Kind);
        Assert.Contains("曲线已停止", status.Text);
        Assert.Equal("curve failure; firmware control requested", status.Reason);
    }

    [Fact]
    public void StatusTextCarriesNoPathCommandLineOrAdapterDetail()
    {
        var statuses = new[]
        {
            FanCurveStatusFormatter.Describe(
                Decision(FanCurveAction.SetDuty, 100, FanTemperatureLevel.Emergency, "Emergency temperature limit reached")),
            FanCurveStatusFormatter.Describe(
                FanCurveDecision.Restore(FanCurveLeasePolicy.LeaseInvalidReason),
                FanCurveLeaseDecision.Restore(FanCurveLeasePolicy.LeaseInvalidReason)),
            FanCurveStatusFormatter.Describe(
                Decision(FanCurveAction.NoChange, null, FanTemperatureLevel.Normal, "Curve target changed"))
        };

        foreach (var status in statuses)
        {
            Assert.DoesNotContain(":\\", status.Text);
            Assert.DoesNotContain("网卡", status.Text);
            Assert.DoesNotContain(".exe", status.Text);
        }
    }

    [Fact]
    public void CurveEmergencyIsAnnouncedOncePerEntry()
    {
        var tracker = new FanCurveEmergencyTracker();
        var emergency = Status(FanCurveStatusKind.CurveEmergency, applied: true);
        var normal = Status(FanCurveStatusKind.Normal, applied: true);

        Assert.False(tracker.Observe(normal));
        Assert.True(tracker.Observe(emergency));
        Assert.True(tracker.InCurveEmergency);
        // A steady emergency must not repeat the popup.
        Assert.False(tracker.Observe(emergency));
        Assert.False(tracker.Observe(normal));
        Assert.False(tracker.InCurveEmergency);
        // Re-entry is a new event.
        Assert.True(tracker.Observe(emergency));
    }

    [Fact]
    public void PreviewOrRefusedEmergencyDoesNotStartTheEvent()
    {
        var tracker = new FanCurveEmergencyTracker();

        Assert.False(tracker.Observe(Status(FanCurveStatusKind.CurveEmergency, applied: false)));
        Assert.False(tracker.InCurveEmergency);
        Assert.False(tracker.Observe(Status(FanCurveStatusKind.GuardianUnavailable, applied: false)));
        Assert.False(tracker.InCurveEmergency);
        Assert.False(tracker.Observe(Status(FanCurveStatusKind.ApplyFailed, applied: false)));
    }

    [Fact]
    public void CurveStatusIsTelemetryJsonOnlyAndKeepsTheCsvSchema()
    {
        var snapshot = new HardwareSnapshot(DateTimeOffset.UtcNow, 91, 3000, 0, PowerMode.Balance,
            20, 40, FanControlMode.Curve, 100, null);
        var record = HardwareTelemetryRecord.FromSnapshot(
            snapshot, "curve-emergency", nameof(FanCurveStatusKind.CurveEmergency), "Emergency temperature limit reached");

        Assert.Equal(nameof(FanCurveStatusKind.CurveEmergency), record.CurveStatus);
        Assert.Equal("Emergency temperature limit reached", record.CurveReason);

        var json = System.Text.Json.JsonSerializer.Serialize(record,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("curveStatus", json);
        Assert.Contains("curveReason", json);

        // The CSV surface stays exactly as before, so no existing file is archived.
        Assert.DoesNotContain("CurveStatus", TelemetryRecorder.CsvHeader);
        Assert.Equal(TelemetryRecorder.CsvHeader.Split(',').Length, TelemetryRecorder.ToCsv(record).Split(',').Length);
    }

    [Fact]
    public void LatchedEmergencyIsItsOwnStatusAndNotTheFixedRpmEmergency()
    {
        var decision = new FanCurveDecision(FanCurveAction.SetDuty, 100, "Curve target changed")
        { Level = FanTemperatureLevel.Emergency, EmergencyLatched = true };

        var status = FanCurveStatusFormatter.Describe(
            decision, curveEnabled: true, applied: true, emergencyTemperatureC: 90);

        Assert.Equal(FanCurveStatusKind.EmergencyLatched, status.Kind);
        Assert.True(status.IsCurveEmergencyLatched);
        Assert.True(status.Applied);
        Assert.Contains("锁存", status.Text);
        Assert.Contains("100%", status.Text);
        Assert.DoesNotContain("正常", status.Text);
        // A latch-only pass is not the curve emergency entry, and it must never drive the
        // fixed-RPM emergency event key.
        Assert.False(status.IsCurveEmergency);
        Assert.False(new FanCurveEmergencyTracker().Observe(status));
    }

    [Fact]
    public void LatchOutranksTheFeedbackFaultAndTheLevelLabels()
    {
        var latchedFault = new FanCurveDecision(
                FanCurveAction.RestoreAutomatic, null,
                FanCurveRpmFeedbackController.FaultReasonPrefix + "：连续 3 次无有效 RPM")
        { Level = FanTemperatureLevel.Emergency, EmergencyLatched = true, RpmFeedbackFault = true };
        var latchedNormalLevel = new FanCurveDecision(FanCurveAction.NoChange, null, "Curve target changed")
        { Level = FanTemperatureLevel.Normal, EmergencyLatched = true };

        Assert.Equal(FanCurveStatusKind.EmergencyLatched,
            FanCurveStatusFormatter.Describe(latchedFault, applied: true).Kind);
        Assert.Equal(FanCurveStatusKind.EmergencyLatched,
            FanCurveStatusFormatter.Describe(latchedNormalLevel, curveEnabled: true, applied: true).Kind);
    }

    [Fact]
    public void RpmFeedbackFaultIsAHandoverNotASensorProblem()
    {
        var reason = FanCurveRpmFeedbackController.FaultReasonPrefix + "：实际 RPM 500 连续 4 次低于期望";
        var flagged = new FanCurveDecision(FanCurveAction.RestoreAutomatic, null, reason)
        { Level = FanTemperatureLevel.Warning, RpmFeedbackFault = true };

        var status = FanCurveStatusFormatter.Describe(flagged, curveEnabled: false, applied: true);
        Assert.Equal(FanCurveStatusKind.RpmFeedbackFault, status.Kind);
        Assert.False(status.Applied);
        Assert.Contains("RPM 反馈故障", status.Text);
        Assert.Contains("已交还 EC 自动", status.Text);
        Assert.DoesNotContain("传感器", status.Text);
        Assert.Equal(reason, status.Reason);

        // A decision rebuilt from the reason text alone is still classified the same way.
        var fromReasonOnly = FanCurveStatusFormatter.Describe(
            new FanCurveDecision(FanCurveAction.RestoreAutomatic, null,
                FanCurveRpmFeedbackController.FaultReasonPrefix + "：连续 3 次无有效 RPM"),
            curveEnabled: false, applied: true);
        Assert.Equal(FanCurveStatusKind.RpmFeedbackFault, fromReasonOnly.Kind);

        // Same decision on the preview path: still its own kind, but never claims a handover.
        var preview = FanCurveStatusFormatter.Describe(flagged);
        Assert.Equal(FanCurveStatusKind.RpmFeedbackFault, preview.Kind);
        Assert.StartsWith("曲线预览", preview.Text);
        Assert.DoesNotContain("已交还", preview.Text);
    }

    [Fact]
    public void LeaseRefusalStillShowsTheLeaseReason()
    {
        // The pass read an invalid lease and already handed control back: the gate verdict wins
        // over any curve-internal label of the same pass.
        var status = FanCurveStatusFormatter.Describe(
            new FanCurveDecision(FanCurveAction.RestoreAutomatic, null, "reason")
            { Level = FanTemperatureLevel.Emergency, EmergencyLatched = true },
            FanCurveLeaseDecision.Restore(FanCurveLeasePolicy.LeaseInvalidReason),
            curveEnabled: false, applied: true);

        Assert.Equal(FanCurveStatusKind.LeaseInvalid, status.Kind);
        Assert.Contains("租约", status.Text);
        Assert.Contains("租约", status.Reason);
        Assert.DoesNotContain("锁存", status.Text);
    }

    [Fact]
    public void RealEngineFeedbackFaultReachesTheStatus()
    {
        var settings = new FanCurveSettings(
            [new(40, 40), new(90, 100)],
            HysteresisC: 1,
            MaximumDutyChangePerTick: 100,
            RpmFeedback: new FanCurveRpmFeedbackSettings(StallSampleCount: 2));
        var engine = new FanCurveEngine(settings);
        var snapshot = new HardwareSnapshot(DateTimeOffset.UtcNow, 42, 0, 0, PowerMode.Balance,
            20, 40, FanControlMode.FirmwareAutomatic, null, null);

        engine.Evaluate(snapshot);
        var fault = engine.Evaluate(snapshot);

        Assert.Equal(FanCurveAction.RestoreAutomatic, fault.Action);
        Assert.True(fault.RpmFeedbackFault);
        var status = FanCurveStatusFormatter.Describe(fault, curveEnabled: false, applied: true);
        Assert.Equal(FanCurveStatusKind.RpmFeedbackFault, status.Kind);
        Assert.DoesNotContain("传感器", status.Text);
    }

    private static FanCurveStatus Status(FanCurveStatusKind kind, bool applied) =>
        new(kind, null, applied, kind.ToString(), "reason");
}
