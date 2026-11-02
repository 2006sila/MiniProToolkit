namespace MiniProToolkit.Core;

/// <summary>
/// UI/diagnostic classification of one fan-curve control pass. It keeps apart the states that a
/// single "restore automatic" text used to collapse: the curve's own unusable-temperature path
/// (sensor), the safety gate that handed control back (Guardian stopped / invalid lease), a
/// still pending recovery and a failed curve write. Emergency here means the <em>curve</em>
/// threshold (90 °C / 100 % duty), which is not the fixed-RPM emergency latch.
/// </summary>
public enum FanCurveStatusKind
{
    Normal = 0,
    Warning = 1,
    PreEmergency = 2,
    CurveEmergency = 3,
    SensorInvalid = 4,
    GuardianUnavailable = 5,
    LeaseInvalid = 6,
    RecoveryPending = 7,
    ApplyFailed = 8,

    /// <summary>
    /// The optional 90 °C curve latch still holds full speed after the CPU cooled down. It is its
    /// own state because the temperature is no longer at the threshold, and it is not the
    /// fixed-RPM emergency (that path keeps its own event key).
    /// </summary>
    EmergencyLatched = 9,

    /// <summary>The optional RPM feedback loop faulted and control was handed back to EC automatic.</summary>
    RpmFeedbackFault = 10
}

/// <summary>
/// One rendered curve status. <see cref="Applied"/> is true only when this pass actually held
/// curve control, so a preview or a refused gate can never be reported as an active emergency.
/// </summary>
public sealed record FanCurveStatus(
    FanCurveStatusKind Kind,
    FanTemperatureLevel? Level,
    bool Applied,
    string Text,
    string Reason)
{
    public bool IsCurveEmergency => Kind == FanCurveStatusKind.CurveEmergency;

    /// <summary>True while the optional 90 °C curve latch holds full speed.</summary>
    public bool IsCurveEmergencyLatched => Kind == FanCurveStatusKind.EmergencyLatched;
}

/// <summary>
/// Pure, non-throwing formatter for the curve status line. It consumes the existing
/// <see cref="FanCurveDecision.Level"/>/<see cref="FanCurveDecision.Reason"/> and the
/// <see cref="FanCurveLeaseDecision"/> verdict of the same pass, so the safety gate (Guardian
/// stopped / invalid lease / pending recovery) is never displayed as a sensor failure. It
/// changes no control algorithm and reads no hardware; the reason strings it copies come from
/// Core and never contain paths, command lines or adapter details.
/// </summary>
public static class FanCurveStatusFormatter
{
    /// <summary>Reason the curve engine reports for an unusable or stale temperature sample.</summary>
    public const string InvalidSensorReason = "Invalid or stale temperature; automatic control required";

    public const string SensorInvalidText = "传感器无效或数据过期";
    public const string GuardianUnavailableText = "Guardian 已停止（已交还 EC 自动）";
    public const string LeaseInvalidText = "控制租约无效或已过期（已交还 EC 自动）";
    public const string RecoveryPendingText = "恢复未完成（正在重试 EC 自动）";
    public const string WarningText = "预警级";
    public const string PreEmergencyText = "紧急前级";
    public const string NormalText = "正常";

    /// <summary>
    /// Optional 90 °C curve latch: full speed is still held although the CPU cooled down again.
    /// Deliberately distinct from the fixed-RPM emergency so the two never share an event key.
    /// </summary>
    public const string EmergencyLatchedText = "紧急锁存（≥90°C 已触发，仅恢复 EC 自动可释放）";

    public const string RpmFeedbackFaultText = "RPM 反馈故障，控制已交还 EC 自动";
    public const string RpmFeedbackFaultPreviewText = "RPM 反馈故障（未接管硬件）";

    /// <summary>Curve emergency threshold used by the label when the caller passes none.</summary>
    public const double DefaultEmergencyTemperatureC = 90;

    public static FanCurveStatus Describe(
        FanCurveDecision decision,
        FanCurveLeaseDecision? lease = null,
        bool curveEnabled = false,
        bool applied = false,
        int? currentDuty = null,
        string? applyError = null,
        double? emergencyTemperatureC = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        // Classify first: visibility never changes the existing failure/lease/latch priority.
        var status = DescribeCore(decision, lease, curveEnabled, applied, currentDuty,
            applyError, emergencyTemperatureC);
        if (decision.TargetRpm is null && decision.TrackingState == FanCurveRpmTrackingState.Disabled)
            return status;
        static string Rpm(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "--";
        var tracking = $"显式目标 {Rpm(decision.TargetRpm)} RPM · 实际 {Rpm(decision.ActualRpm)} RPM · " +
            $"误差(目标-实际) {Rpm(decision.RpmError)} RPM · state={decision.TrackingState}";
        return status with { Text = $"{status.Text} · {tracking}", Reason = $"{status.Reason} · {tracking}" };
    }

    private static FanCurveStatus DescribeCore(
        FanCurveDecision decision,
        FanCurveLeaseDecision? lease,
        bool curveEnabled,
        bool applied,
        int? currentDuty,
        string? applyError,
        double? emergencyTemperatureC)
    {
        if (!string.IsNullOrWhiteSpace(applyError))
            return new(FanCurveStatusKind.ApplyFailed, null, false, $"曲线已停止: {applyError}", decision.Reason);

        // Safety gate: the pass read the lease and handed control back. This is never a sensor
        // problem, so it is classified before the curve engine's own restore decision.
        if (lease is { RestoreRequired: true } restore)
        {
            var kind = restore.Reason switch
            {
                FanCurveLeasePolicy.GuardianStoppedReason => FanCurveStatusKind.GuardianUnavailable,
                FanCurveLeasePolicy.RecoveryPendingReason => FanCurveStatusKind.RecoveryPending,
                _ => FanCurveStatusKind.LeaseInvalid
            };
            var text = kind switch
            {
                FanCurveStatusKind.GuardianUnavailable => GuardianUnavailableText,
                FanCurveStatusKind.RecoveryPending => RecoveryPendingText,
                _ => LeaseInvalidText
            };
            return new FanCurveStatus(kind, null, false, $"曲线: {text}", restore.Reason);
        }

        var live = curveEnabled && applied;
        var prefix = live ? "曲线: " : "曲线预览: ";

        // Optional 90 °C latch: the CPU cooled down but the curve keeps holding full speed, so this
        // pass must never render as a normal/warning curve or as the sensor state. It outranks the
        // feedback fault and the level labels below; a safety-gate refusal handled above still
        // shows its own reason, because that pass already handed control back.
        if (decision.EmergencyLatched)
        {
            var latchedDuty = decision.DutyPercent ?? currentDuty;
            var latchedSuffix = live ? "（已应用）" : "（未应用）";
            return new FanCurveStatus(FanCurveStatusKind.EmergencyLatched, decision.Level, live,
                $"{prefix}{EmergencyLatchedText} · {(latchedDuty is { } heldDuty ? $"{heldDuty}%" : "--")}{latchedSuffix}",
                decision.Reason);
        }

        // Optional RPM feedback loop faulted: the controller took the existing restore path, so this
        // is a handover, never an unusable temperature sample and never a curve level.
        if (decision.RpmFeedbackFault || IsRpmFeedbackFaultReason(decision.Reason))
        {
            var faultText = applied ? $"曲线: {RpmFeedbackFaultText}" : $"曲线预览: {RpmFeedbackFaultPreviewText}";
            return new FanCurveStatus(FanCurveStatusKind.RpmFeedbackFault, decision.Level, false,
                faultText, decision.Reason);
        }

        if (decision.Action == FanCurveAction.RestoreAutomatic || decision.Reason == InvalidSensorReason)
        {
            // Applied but no longer enabled: the curve engine already restored EC automatic for
            // this pass, so it is a restore, not a preview.
            var text = applied ? $"曲线: {SensorInvalidText}（已恢复 EC 自动）" : $"曲线预览: {SensorInvalidText}";
            return new FanCurveStatus(FanCurveStatusKind.SensorInvalid, null, false, text, decision.Reason);
        }

        var (kindLevel, label) = Classify(decision.Level, emergencyTemperatureC);
        var duty = decision.DutyPercent ?? currentDuty;
        var suffix = live ? "（已应用）" : "（未应用）";
        return new FanCurveStatus(kindLevel, decision.Level, live,
            $"{prefix}{label} · {(duty is { } value ? $"{value}%" : "--")}{suffix}", decision.Reason);
    }

    private static (FanCurveStatusKind Kind, string Label) Classify(
        FanTemperatureLevel level, double? emergencyTemperatureC) => level switch
    {
        FanTemperatureLevel.Emergency => (FanCurveStatusKind.CurveEmergency,
            $"紧急满速（≥{FormatTemperature(emergencyTemperatureC)}°C）"),
        FanTemperatureLevel.PreEmergency => (FanCurveStatusKind.PreEmergency, PreEmergencyText),
        FanTemperatureLevel.Warning => (FanCurveStatusKind.Warning, WarningText),
        _ => (FanCurveStatusKind.Normal, NormalText)
    };

    private static string FormatTemperature(double? temperatureC) =>
        temperatureC is { } value && double.IsFinite(value) ? $"{value:0.##}" : "90";

    // Every RPM-feedback fault reason carries FanCurveRpmFeedbackController.FaultReasonPrefix, so a
    // decision rebuilt from the reason text alone is still classified as a feedback fault.
    private static bool IsRpmFeedbackFaultReason(string reason) =>
        reason is not null &&
        reason.StartsWith(FanCurveRpmFeedbackController.FaultReasonPrefix, StringComparison.Ordinal);
}

/// <summary>
/// Deduplicates the curve emergency OSD event: <see cref="Observe"/> returns true exactly once
/// per entry into the curve's 90 °C emergency, so a steady emergency never repeats the popup and
/// a re-entry (cool down, heat up again) is announced again. A pass that did not hold curve
/// control (preview, refused gate, failed write) never counts as an emergency.
/// </summary>
public sealed class FanCurveEmergencyTracker
{
    private bool _inCurveEmergency;

    public bool InCurveEmergency => _inCurveEmergency;

    public bool Observe(FanCurveStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var emergency = status.IsCurveEmergency && status.Applied;
        if (!emergency)
        {
            _inCurveEmergency = false;
            return false;
        }
        if (_inCurveEmergency)
            return false;
        _inCurveEmergency = true;
        return true;
    }

    public void Reset() => _inCurveEmergency = false;
}
