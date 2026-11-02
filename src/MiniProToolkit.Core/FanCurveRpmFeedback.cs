namespace MiniProToolkit.Core;

/// <summary>
/// Optional closed-loop RPM feedback for the fan curve. It is purely additive: the evaluator may
/// only ask for <em>more</em> duty than the curve already commanded, never less, so a missing or
/// lying tachometer can never weaken the curve or the fixed 90 °C full-speed protection.
/// <see cref="MinimumExpectedRpm"/> disables stall judgement where the expected RPM is too low to
/// be meaningful, and <see cref="BadSampleTolerance"/> absorbs transient read failures before a
/// permanently missing tachometer turns into a fault.
/// </summary>
public sealed record FanCurveRpmFeedbackSettings(
    int DeadbandRpm = 150,
    int MinimumExpectedRpm = 600,
    double StallRatio = 0.5,
    int StallSampleCount = 4,
    int MaximumCorrectionDuty = 12,
    int MaximumCorrectionStep = 2,
    int BadSampleTolerance = 2)
{
    public void Validate()
    {
        if (DeadbandRpm is < 0 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(DeadbandRpm));
        if (MinimumExpectedRpm is < 0 or > 6000)
            throw new ArgumentOutOfRangeException(nameof(MinimumExpectedRpm));
        if (!double.IsFinite(StallRatio) || StallRatio is < 0.05 or > 0.95)
            throw new ArgumentOutOfRangeException(nameof(StallRatio));
        if (StallSampleCount is < 2 or > 30)
            throw new ArgumentOutOfRangeException(nameof(StallSampleCount));
        if (MaximumCorrectionDuty is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(MaximumCorrectionDuty));
        if (MaximumCorrectionStep is < 1 or > 20 || MaximumCorrectionStep > MaximumCorrectionDuty)
            throw new ArgumentOutOfRangeException(nameof(MaximumCorrectionStep));
        if (BadSampleTolerance is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(BadSampleTolerance));
    }
}

public enum FanCurveRpmFeedbackState
{
    Disabled,
    Stable,
    Correcting,
    RpmUnavailable,
    TargetUnreachable,
    StallFault,
    UnreachableFault,
    FeedbackLost
}

public enum FanCurveRpmFeedbackAction
{
    NoChange,
    RaiseDuty,
    RestoreAutomatic
}

/// <summary>
/// One feedback verdict. <see cref="CommandDuty"/> is the duty the curve should hold after the
/// correction and is never below the curve duty it was evaluated with.
/// </summary>
public sealed record FanCurveRpmFeedbackDecision(
    FanCurveRpmFeedbackAction Action,
    FanCurveRpmFeedbackState State,
    int CommandDuty,
    int? ExpectedRpm,
    int? ActualRpm,
    string Reason)
{
    public bool IsFault => Action == FanCurveRpmFeedbackAction.RestoreAutomatic;
    public bool RequestsRaise => Action == FanCurveRpmFeedbackAction.RaiseDuty;
}

/// <summary>
/// Stateful RPM feedback for one curve target. It never touches hardware: it only returns
/// "keep", "raise by at most N" or "fault", and the curve controller decides what to do with a
/// fault (the existing <see cref="FanCurveController"/> restore path). Expected RPM comes from
/// the explicit <see cref="FanCalibration.RpmForDuty"/> mapping of the curve-target duty.
/// </summary>
public sealed class FanCurveRpmFeedbackController
{
    private const int MaximumPlausibleRpm = 20000;

    /// <summary>
    /// Prefix every fault reason carries. The curve status formatter classifies a
    /// <see cref="FanCurveAction.RestoreAutomatic" /> verdict by it, so an RPM-feedback fault is
    /// never displayed as an unusable temperature sample.
    /// </summary>
    public const string FaultReasonPrefix = "RPM 反馈故障";

    private readonly FanCurveRpmFeedbackSettings _settings;
    private readonly FanCalibration _calibration;
    private int _correctionDuty;
    private int _stallSamples;
    private int _unreachableSamples;
    private int _badSamples;
    private int? _lastCommandDuty;
    private bool _faulted;

    public FanCurveRpmFeedbackController(
        FanCurveRpmFeedbackSettings settings,
        FanCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings;
        _calibration = calibration ?? FanCalibration.Default;
        State = FanCurveRpmFeedbackState.Disabled;
    }

    public FanCurveRpmFeedbackState State { get; private set; }
    public int CorrectionDuty => _correctionDuty;
    public int? LastExpectedRpm { get; private set; }
    public int? LastActualRpm { get; private set; }

    /// <summary>Explicit, testable target mapping used for the deadband/stall comparison.</summary>
    public int TargetRpmForDuty(int commandDuty) =>
        _calibration.RpmForDuty(Math.Clamp(commandDuty, 0, 100));

    public void Reset()
    {
        ResetSampleState();
        _faulted = false;
        LastExpectedRpm = null;
        LastActualRpm = null;
        State = FanCurveRpmFeedbackState.Disabled;
    }

    /// <param name="commandDuty">Curve duty the curve itself asks for (before any correction).</param>
    /// <param name="actualRpm">Fan 1 tachometer reading from the snapshot.</param>
    public FanCurveRpmFeedbackDecision Evaluate(int commandDuty, int? actualRpm)
    {
        var baseDuty = Math.Clamp(commandDuty, 0, 100);
        var expected = TargetRpmForDuty(baseDuty);
        LastExpectedRpm = expected;
        LastActualRpm = actualRpm is >= 0 and <= MaximumPlausibleRpm ? actualRpm : null;

        if (_faulted)
            return Fault(FanCurveRpmFeedbackState.StallFault, baseDuty, expected, actualRpm,
                $"{FaultReasonPrefix}：已判定故障，等待控制器交还 EC 自动");

        // A new curve target starts a fresh correction budget: feedback may only add duty on top
        // of the target it was evaluated against, never carry a stale boost onto a new one.
        if (_lastCommandDuty is { } previous && previous != baseDuty)
            ResetSampleState();
        _lastCommandDuty = baseDuty;

        if (actualRpm is not { } rpm || rpm is < 0 or > MaximumPlausibleRpm)
        {
            _badSamples++;
            if (_badSamples > _settings.BadSampleTolerance)
                return Fault(FanCurveRpmFeedbackState.FeedbackLost, baseDuty, expected, null,
                    $"{FaultReasonPrefix}：连续 {_badSamples} 次无有效 RPM，无法确认风扇反馈");
            State = FanCurveRpmFeedbackState.RpmUnavailable;
            return new FanCurveRpmFeedbackDecision(FanCurveRpmFeedbackAction.NoChange, State,
                EffectiveDuty(baseDuty), expected, null,
                $"RPM 反馈无效（第 {_badSamples} 次），在容差内暂不修正");
        }

        _badSamples = 0;

        // Low duty / low expected RPM: a tachometer cannot be judged on a barely turning fan.
        if (expected <= _settings.MinimumExpectedRpm)
        {
            _stallSamples = 0;
            _unreachableSamples = 0;
            State = FanCurveRpmFeedbackState.Stable;
            return new FanCurveRpmFeedbackDecision(FanCurveRpmFeedbackAction.NoChange, State,
                EffectiveDuty(baseDuty), expected, rpm,
                $"期望 RPM {expected} 过低，不判定失速");
        }

        var error = expected - rpm;
        var stallFloor = (int)Math.Round(expected * _settings.StallRatio);
        if (rpm < stallFloor)
        {
            _stallSamples++;
            if (_stallSamples >= _settings.StallSampleCount)
                return Fault(FanCurveRpmFeedbackState.StallFault, baseDuty, expected, rpm,
                    $"{FaultReasonPrefix}：实际 RPM {rpm} 连续 {_stallSamples} 次低于期望 {expected} RPM 的失速阈值 {stallFloor} RPM");
        }
        else
        {
            _stallSamples = 0;
        }

        if (error <= _settings.DeadbandRpm)
        {
            _unreachableSamples = 0;
            State = FanCurveRpmFeedbackState.Stable;
            return new FanCurveRpmFeedbackDecision(FanCurveRpmFeedbackAction.NoChange, State,
                EffectiveDuty(baseDuty), expected, rpm,
                $"RPM 误差 {error} 在死区 {_settings.DeadbandRpm} 内");
        }

        var room = Math.Min(
            _settings.MaximumCorrectionDuty - _correctionDuty,
            100 - baseDuty - _correctionDuty);
        if (room <= 0)
        {
            _unreachableSamples++;
            State = FanCurveRpmFeedbackState.TargetUnreachable;
            if (_unreachableSamples >= _settings.StallSampleCount)
                return Fault(FanCurveRpmFeedbackState.UnreachableFault, baseDuty, expected, rpm,
                    $"{FaultReasonPrefix}：实际 RPM {rpm} 连续 {_unreachableSamples} 次低于期望 {expected}，且补 duty 已达上限");
            return new FanCurveRpmFeedbackDecision(FanCurveRpmFeedbackAction.NoChange, State,
                EffectiveDuty(baseDuty), expected, rpm,
                $"补 duty 已达上限（{_correctionDuty}%），目标暂不可达");
        }

        _unreachableSamples = 0;
        var step = Math.Min(room, _settings.MaximumCorrectionStep);
        _correctionDuty += step;
        State = FanCurveRpmFeedbackState.Correcting;
        return new FanCurveRpmFeedbackDecision(FanCurveRpmFeedbackAction.RaiseDuty, State,
            EffectiveDuty(baseDuty), expected, rpm,
            $"实际 RPM {rpm} 低于期望 {expected}（误差 {error}），补 {step}% duty");
    }

    private int EffectiveDuty(int baseDuty) =>
        Math.Clamp(baseDuty + _correctionDuty, 0, 100);

    private void ResetSampleState()
    {
        _correctionDuty = 0;
        _stallSamples = 0;
        _unreachableSamples = 0;
        _badSamples = 0;
    }

    private FanCurveRpmFeedbackDecision Fault(
        FanCurveRpmFeedbackState state,
        int baseDuty,
        int expected,
        int? actualRpm,
        string reason)
    {
        _faulted = true;
        _correctionDuty = 0;
        _stallSamples = 0;
        _unreachableSamples = 0;
        State = state;
        return new FanCurveRpmFeedbackDecision(FanCurveRpmFeedbackAction.RestoreAutomatic, state,
            baseDuty, expected, actualRpm, reason);
    }
}
