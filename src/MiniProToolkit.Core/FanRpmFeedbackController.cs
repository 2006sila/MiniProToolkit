namespace MiniProToolkit.Core;

public enum FanRpmFeedbackAction
{
    NoChange,
    AdjustDuty,
    Fault
}

public sealed record FanRpmFeedbackDecision(
    FanRpmFeedbackAction Action,
    int DutyPercent,
    int? ErrorRpm,
    string Status,
    string? FaultReason = null)
{
    public FanRpmFeedbackState State { get; init; }
}

public sealed class FanRpmFeedbackController
{
    private readonly FanCalibration _calibration;
    private readonly int _minimumDuty;
    private readonly int _maximumDuty;
    private readonly int _deadbandRpm;
    private readonly int _maximumAdjustmentDuty;
    private readonly int _faultSampleCount;
    private readonly int _stallSampleCount;
    private int _targetRpm;
    private int _currentDuty;
    private int _faultSamples;
    private int _stallSamples;
    private bool _started;

    public FanRpmFeedbackController(
        FanCalibration? calibration = null,
        int minimumDuty = 40,
        int maximumDuty = 100,
        int deadbandRpm = 100,
        int maximumAdjustmentDuty = 3,
        int faultSampleCount = 8,
        int stallSampleCount = 3)
    {
        _calibration = calibration ?? FanCalibration.Default;
        if (minimumDuty is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(minimumDuty));
        if (maximumDuty < minimumDuty || maximumDuty > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumDuty));
        if (deadbandRpm < 0)
            throw new ArgumentOutOfRangeException(nameof(deadbandRpm));
        if (maximumAdjustmentDuty is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maximumAdjustmentDuty));
        if (faultSampleCount is < 2 or > 30)
            throw new ArgumentOutOfRangeException(nameof(faultSampleCount));
        if (stallSampleCount is < 2 or > 30)
            throw new ArgumentOutOfRangeException(nameof(stallSampleCount));

        _minimumDuty = minimumDuty;
        _maximumDuty = maximumDuty;
        _deadbandRpm = deadbandRpm;
        _maximumAdjustmentDuty = maximumAdjustmentDuty;
        _faultSampleCount = faultSampleCount;
        _stallSampleCount = stallSampleCount;
    }

    public bool IsActive => _started;
    public int TargetRpm => _targetRpm;
    public int CurrentDuty => _currentDuty;
    public int? LastErrorRpm { get; private set; }
    public string Status { get; private set; } = "未启用";
    public FanRpmFeedbackState State { get; private set; }

    public FanRpmFeedbackDecision Start(int requestedRpm, int initialDuty)
    {
        var minRpm = _calibration.RpmForDuty(_minimumDuty);
        var maxRpm = _calibration.RpmForDuty(_maximumDuty);
        _targetRpm = Math.Clamp(requestedRpm, minRpm, maxRpm);
        _currentDuty = Math.Clamp(initialDuty, _minimumDuty, _maximumDuty);
        _faultSamples = 0;
        _stallSamples = 0;
        LastErrorRpm = null;
        _started = true;
        State = FanRpmFeedbackState.WaitingForRpm;
        Status = requestedRpm == _targetRpm ? "闭环等待反馈" : "目标已钳制，闭环等待反馈";
        return Decision(FanRpmFeedbackAction.NoChange, null, Status);
    }

    public FanRpmFeedbackDecision Evaluate(int? actualRpm)
    {
        if (!_started)
            return Decision(FanRpmFeedbackAction.NoChange, null, "未启用");
        if (actualRpm is null or < 0)
        {
            State = FanRpmFeedbackState.WaitingForRpm;
            Status = "等待有效 RPM";
            return Decision(FanRpmFeedbackAction.NoChange, null, Status);
        }

        var error = _targetRpm - actualRpm.Value;
        LastErrorRpm = error;
        if (actualRpm.Value <= 100 && _currentDuty >= _minimumDuty)
        {
            _stallSamples++;
            Status = "检测到风扇 RPM 过低";
            if (_stallSamples >= _stallSampleCount)
            {
                _started = false;
                State = FanRpmFeedbackState.Fault;
                return Decision(
                    FanRpmFeedbackAction.Fault,
                    error,
                    "已请求恢复 EC 自动",
                    $"固定 RPM 控制期间连续 {_stallSamples} 次读取到 RPM 不高于 100。 ");
            }
        }
        else
        {
            _stallSamples = 0;
        }

        var absoluteError = Math.Abs(error);
        if (absoluteError <= _deadbandRpm)
        {
            _faultSamples = 0;
            State = FanRpmFeedbackState.Stable;
            Status = "已稳定";
            return Decision(FanRpmFeedbackAction.NoChange, error, Status);
        }

        var atDutyLimit = error > 0
            ? _currentDuty >= _maximumDuty
            : _currentDuty <= _minimumDuty;
        if (atDutyLimit)
        {
            _faultSamples++;
            State = FanRpmFeedbackState.TargetUnreachable;
            Status = error > 0 ? "目标可能不可达" : "RPM 高于目标";
            if (_faultSamples >= _faultSampleCount)
            {
                _started = false;
                State = FanRpmFeedbackState.Fault;
                return Decision(
                    FanRpmFeedbackAction.Fault,
                    error,
                    "已请求恢复 EC 自动",
                    error > 0
                        ? $"实际 RPM 连续 {_faultSampleCount} 次低于目标且 duty 已达上限。"
                        : $"实际 RPM 连续 {_faultSampleCount} 次高于目标且 duty 已达下限。");
            }
            return Decision(FanRpmFeedbackAction.NoChange, error, Status);
        }

        _faultSamples = 0;
        var direction = error > 0 ? 1 : -1;
        var magnitude = Math.Clamp((int)Math.Ceiling(absoluteError / 500.0), 1, _maximumAdjustmentDuty);
        var nextDuty = Math.Clamp(_currentDuty + direction * magnitude, _minimumDuty, _maximumDuty);
        if (nextDuty == _currentDuty)
            return Decision(FanRpmFeedbackAction.NoChange, error, Status);

        State = FanRpmFeedbackState.Adjusting;
        _currentDuty = nextDuty;
        Status = error > 0 ? "正在提高 duty" : "正在降低 duty";
        return Decision(FanRpmFeedbackAction.AdjustDuty, error, Status);
    }

    public void Stop()
    {
        _started = false;
        _faultSamples = 0;
        _stallSamples = 0;
        LastErrorRpm = null;
        State = FanRpmFeedbackState.Disabled;
        Status = "未启用";
    }

    private FanRpmFeedbackDecision Decision(
        FanRpmFeedbackAction action,
        int? error,
        string status,
        string? faultReason = null) =>
        new(action, _currentDuty, error, status, faultReason) { State = State };
}
