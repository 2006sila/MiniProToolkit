namespace MiniProToolkit.Core;

public enum FanCurveRpmTrackingState
{
    Disabled,
    Stable,
    Correcting,
    RecoveringCorrection,
    SafetyFloorLimited,
    RpmUnavailable,
    TargetUnreachable,
    StallFault,
    UnreachableFault,
    FeedbackLost,
    EmergencyBypass
}

public sealed record FanCurveRpmTrackingDecision(
    int CommandDuty, int TargetRpm, int? ActualRpm, FanCurveRpmTrackingState State, string Reason)
{
    public int? RpmError => ActualRpm is { } rpm ? TargetRpm - rpm : null;
    public bool IsFault => State is FanCurveRpmTrackingState.StallFault or
        FanCurveRpmTrackingState.UnreachableFault or FanCurveRpmTrackingState.FeedbackLost;
}

/// <summary>
/// Pure, hardware-free explicit RPM tracking. Feed-forward comes from calibration; only the
/// bounded positive compensation is added/reclaimed. The independent temperature/ramp duty
/// floor always wins. Overspeed at that floor is a constraint, never a stall/unreachable fault.
/// </summary>
public sealed class FanCurveRpmTrackingController
{
    public const int DeadbandRpm = 150;
    public const int MaximumCorrectionDuty = 12;
    public const int MaximumCorrectionStep = 2;
    public const int BadSampleTolerance = 2;
    public const int FaultSampleCount = 4;
    private readonly FanCalibration _calibration;
    private int _correction, _badSamples, _stallSamples, _unreachableSamples;
    private FanCurveRpmTrackingState? _fault;

    public FanCurveRpmTrackingController(FanCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        calibration.ValidateForFixedRpm();
        _calibration = calibration;
    }

    public int CorrectionDuty => _correction;

    public void Reset()
    {
        _correction = _badSamples = _stallSamples = _unreachableSamples = 0;
        _fault = null;
    }

    public FanCurveRpmTrackingDecision Evaluate(int targetRpm, int? actualRpm, int safetyFloorDuty)
    {
        if (targetRpm < _calibration.RpmForDuty(40) || targetRpm > _calibration.RpmForDuty(100))
            throw new ArgumentOutOfRangeException(nameof(targetRpm), "Explicit target is outside calibration reach.");
        if (safetyFloorDuty is < 40 or > 100)
            throw new ArgumentOutOfRangeException(nameof(safetyFloorDuty));
        var feedForward = Math.Clamp(_calibration.DutyForRpm(targetRpm), 40, 100);
        int Command() => Math.Max(safetyFloorDuty, Math.Min(100, feedForward + _correction));
        var actual = actualRpm is >= 0 and <= 20000 ? actualRpm : null;
        FanCurveRpmTrackingDecision Verdict(FanCurveRpmTrackingState state, string reason) =>
            new(Command(), targetRpm, actual, state, reason);
        FanCurveRpmTrackingDecision Fault(FanCurveRpmTrackingState state, string reason)
        {
            _fault = state;
            return Verdict(state, $"{FanCurveRpmFeedbackController.FaultReasonPrefix}：{reason}");
        }

        if (_fault is { } fault)
            return Fault(fault, "显式 RPM 跟踪已故障，等待交还 EC 自动");
        if (actual is not { } rpm)
        {
            // Missing samples break consecutive low/unreachable runs, but do not erase the
            // bounded lost-feedback budget when a target changes.
            _stallSamples = _unreachableSamples = 0;
            if (++_badSamples > BadSampleTolerance)
                return Fault(FanCurveRpmTrackingState.FeedbackLost, "连续无有效 RPM");
            return Verdict(FanCurveRpmTrackingState.RpmUnavailable, "RPM 暂不可用，保持有界补偿和安全底线");
        }
        _badSamples = 0;
        var error = targetRpm - rpm;
        if (error > DeadbandRpm && rpm < targetRpm * 0.5)
        {
            if (++_stallSamples >= FaultSampleCount)
                return Fault(FanCurveRpmTrackingState.StallFault, "实际 RPM 连续低于目标失速阈值");
        }
        else _stallSamples = 0;

        if (error > DeadbandRpm)
        {
            var room = Math.Min(MaximumCorrectionDuty - _correction, 100 - feedForward - _correction);
            // A high temperature floor can mask all remaining correction room. Do not pretend
            // compensation below that floor can increase hardware output.
            if (room <= 0 || Command() >= Math.Min(100, feedForward + MaximumCorrectionDuty))
            {
                if (++_unreachableSamples >= FaultSampleCount)
                    return Fault(FanCurveRpmTrackingState.UnreachableFault, "补偿已达上限，显式目标持续不可达");
                return Verdict(FanCurveRpmTrackingState.TargetUnreachable, "显式目标暂不可达，补偿有界");
            }
            _unreachableSamples = 0;
            _correction += Math.Min(room, MaximumCorrectionStep);
            return Verdict(FanCurveRpmTrackingState.Correcting, "实际 RPM 低于目标，小步增加补偿");
        }

        _unreachableSamples = 0;
        if (error < -DeadbandRpm)
        {
            _correction = Math.Max(0, _correction - MaximumCorrectionStep);
            if (safetyFloorDuty >= feedForward + _correction)
                return Verdict(FanCurveRpmTrackingState.SafetyFloorLimited, "RPM 高于目标，受温度安全底线限制；不判失速或不可达");
            return Verdict(FanCurveRpmTrackingState.RecoveringCorrection, "RPM 高于目标，小步回收补偿");
        }
        return Verdict(FanCurveRpmTrackingState.Stable, "显式目标 RPM 误差在死区内");
    }
}
