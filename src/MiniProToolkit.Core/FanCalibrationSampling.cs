namespace MiniProToolkit.Core;

public enum FanCalibrationSamplingState { AwaitingDuty, Sampling, Completed, Aborted, Cancelled }

public sealed record FanCalibrationSamplingProgress(
    int DutyPercent, int CompletedPoints, int StableSamples, FanCalibrationSamplingState State, string Message);

/// <summary>Pure single-fan sampling state machine. The caller owns hardware, lease and restoration.</summary>
public sealed class FanCalibrationSampling
{
    private static readonly int[] Duties = [40, 50, 60, 70, 80, 90, 100];
    public static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PointTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(4);
    private readonly DateTimeOffset _started;
    private DateTimeOffset _lastNow;
    private DateTimeOffset _pointStarted;
    private DateTimeOffset? _appliedAt;
    private DateTimeOffset? _lastTimestamp;
    private readonly List<FanCalibrationPoint> _points = [];
    private readonly Queue<int> _window = new();

    public FanCalibrationSampling(DateTimeOffset now)
    {
        _started = _lastNow = _pointStarted = now;
    }

    public FanCalibrationSamplingState State { get; private set; } = FanCalibrationSamplingState.AwaitingDuty;
    public int DutyPercent => Duties[Math.Min(_points.Count, Duties.Length - 1)];
    public FanCalibration? Result { get; private set; }
    public string Message { get; private set; } = "等待设置 40% duty。";
    public FanCalibrationSamplingProgress Progress => new(DutyPercent, _points.Count, _window.Count, State, Message);
    public bool LastObservationWasFresh { get; private set; }
    public bool IsTerminal => State is FanCalibrationSamplingState.Completed or FanCalibrationSamplingState.Aborted or FanCalibrationSamplingState.Cancelled;

    public void DutyApplied(DateTimeOffset now)
    {
        if (IsTerminal || !CheckTime(now)) return;
        if (State != FanCalibrationSamplingState.AwaitingDuty)
            throw new InvalidOperationException("当前不需要写入 duty。");
        _appliedAt = now;
        State = FanCalibrationSamplingState.Sampling;
        Message = $"{DutyPercent}%：等待至少 3 秒并收集 5 个稳定新样本。";
    }

    /// <summary>Reject unsafe raw EC data. Does not use display-source temperatures.</summary>
    public static string? SampleError(HardwareSnapshot? sample, DateTimeOffset now, bool starting = false)
    {
        if (sample is null) return "采样缺失。";
        if (sample.Error is not null || sample.FanFault is not null || sample.RpmFeedbackState == FanRpmFeedbackState.Fault)
            return "硬件读取或风扇保护故障。";
        if (sample.Timestamp > now || now - sample.Timestamp > MaximumSampleAge)
            return "采样已过期或时间倒退。";
        if (sample.CpuTemperatureC is null || !double.IsFinite(sample.CpuTemperatureC.Value) || sample.CpuTemperatureC <= 0)
            return "原始 EC 温度缺失或无效（必须大于 0°C）。";
        if (sample.CpuTemperatureC >= 90) return "原始 EC 温度达到 90°C，硬拒绝标定。";
        if (sample.CpuTemperatureC >= (starting ? 65 : 70))
            return starting ? "起始 EC 温度必须低于 65°C，请先降低负载并冷却。" : "EC 温度达到 70°C，立即停止标定并恢复自动。";
        if (sample.Fan1Rpm is null or < 101 or > 10000) return "单风扇 RPM 无效（需要 101–10000）。";
        return null;
    }

    public void Observe(HardwareSnapshot? sample, DateTimeOffset now, bool guardianRunning, bool leaseValid)
    {
        LastObservationWasFresh = false;
        if (IsTerminal || !CheckTime(now)) return;
        if (!guardianRunning || !leaseValid) { Abort("Guardian 或控制租约失效。"); return; }
        if (SampleError(sample, now) is { } error) { Abort(error); return; }
        var timestamp = sample!.Timestamp;
        if (_lastTimestamp is { } previous && timestamp < previous) { Abort("采样时间倒退。"); return; }
        if (_lastTimestamp == timestamp)
        {
            _window.Clear(); // Replayed data cannot contribute to a consecutive stable window.
            Message = "重复时间戳：等待新鲜唯一采样。";
            return;
        }
        _lastTimestamp = timestamp;
        LastObservationWasFresh = true;
        Message = $"{DutyPercent}%：等待至少 3 秒并收集 5 个稳定新样本。";
        if (State != FanCalibrationSamplingState.Sampling || now - _appliedAt!.Value < SettlingTime || timestamp < _appliedAt.Value + SettlingTime)
            return;
        _window.Enqueue(sample.Fan1Rpm!.Value);
        if (_window.Count > 5) _window.Dequeue();
        if (_window.Count != 5 || _window.Max() - _window.Min() > 150) return;
        var median = _window.OrderBy(rpm => rpm).ElementAt(2);
        _points.Add(new(DutyPercent, median));
        _window.Clear();
        if (_points.Count == Duties.Length)
        {
            try
            {
                var calibration = new FanCalibration(_points);
                calibration.ValidateForFixedRpm();
                Result = calibration;
                State = FanCalibrationSamplingState.Completed;
                Message = "实测完成；必须恢复 EC 自动成功后才可使用草稿。";
            }
            catch (ArgumentException ex) { Abort("实测标定被拒绝：" + ex.Message); }
            return;
        }
        _pointStarted = now;
        _appliedAt = null;
        State = FanCalibrationSamplingState.AwaitingDuty;
        Message = $"等待设置 {DutyPercent}% duty。";
    }

    public void CheckDeadline(DateTimeOffset now)
    {
        if (!IsTerminal) CheckTime(now);
    }

    public void Cancel()
    {
        if (IsTerminal) return;
        State = FanCalibrationSamplingState.Cancelled;
        Message = "采样已取消，必须恢复 EC 自动。";
    }

    private bool CheckTime(DateTimeOffset now)
    {
        if (now < _lastNow) { Abort("时钟倒退。"); return false; }
        _lastNow = now;
        if (now - _started >= TotalTimeout || now - _pointStarted >= PointTimeout)
        { Abort("标定采样超时。"); return false; }
        return true;
    }

    private void Abort(string reason)
    {
        State = FanCalibrationSamplingState.Aborted;
        Result = null;
        Message = reason;
    }
}
