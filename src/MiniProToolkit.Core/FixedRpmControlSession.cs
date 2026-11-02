namespace MiniProToolkit.Core;

// Owns fixed-RPM policy and recovery, independently of WMI and presentation refresh.
public sealed class FixedRpmControlSession
{
    public static readonly TimeSpan ControlInterval = TimeSpan.FromSeconds(1);

    // 90 °C protection: write full duty and latch until an explicit EC automatic restore.
    public const int EmergencyTemperatureC = 90;
    private const int EmergencyDutyPercent = 100;
    // A single missing or out-of-range Fan1Rpm reading is not a control fault on its own.
    public const int DefaultRpmBadSampleTolerance = 3;
    private const int MaximumRpmBadSampleTolerance = 30;
    private const int MaximumValidRpm = 10000;
    private const string EmergencyLatchText =
        "CPU 温度达到 90°C，已锁定紧急满速；请处理温度后显式恢复 EC 自动。";
    private const string EmergencyLatchRetainedText =
        "紧急满速锁定保持；请处理温度后显式恢复 EC 自动。";

    private readonly FanRpmTargetController _target;
    private readonly FanRpmFeedbackController _feedback;
    private readonly Action<int> _writeDuty;
    private readonly Action _restoreAutomatic;
    private readonly TimeProvider _clock;
    private readonly int _rpmBadSampleTolerance;
    private int _rpmBadSamples;
    private long _lastTick;
    // Timestamp of the last EC-automatic restore attempt made by the latch/recovery retry paths.
    // Bounds automatic retries to one per ControlInterval; null means "no attempt yet".
    private long? _lastRecoveryAttempt;
    private string? _faultReason;
    private bool _emergencyLatched;
    private bool _emergencyDutyHeld;

    public FixedRpmControlSession(FanCalibration calibration, Action<int> writeDuty,
        Action restoreAutomatic, TimeProvider? clock = null,
        int rpmBadSampleTolerance = DefaultRpmBadSampleTolerance)
    {
        calibration.ValidateForFixedRpm();
        if (rpmBadSampleTolerance is < 0 or > MaximumRpmBadSampleTolerance)
            throw new ArgumentOutOfRangeException(nameof(rpmBadSampleTolerance));
        _target = new(calibration);
        _feedback = new(calibration);
        _writeDuty = writeDuty;
        _restoreAutomatic = restoreAutomatic;
        _clock = clock ?? TimeProvider.System;
        _rpmBadSampleTolerance = rpmBadSampleTolerance;
    }

    public bool IsActive => _feedback.IsActive;

    // The 90 °C latch is a separate protection state from "recovery required". Ordinary faults
    // keep it engaged; only RestoreAutomatic (the explicit restore path) acknowledges it.
    public bool IsEmergencyFullSpeed => _emergencyLatched;
    public bool RecoveryRequired { get; private set; }
    public int? RequestedTargetRpm { get; private set; }
    public int? TargetRpm { get; private set; }
    public int? ErrorRpm { get; private set; }
    // Consecutive tolerated invalid Fan1Rpm samples; resets on the first valid reading.
    public int BadRpmSamples => _rpmBadSamples;
    public FanRpmFeedbackState State { get; private set; }
    public string? Fault { get; private set; }

    public FanRpmTargetResult Start(int requestedRpm)
    {
        if (IsEmergencyFullSpeed || (RecoveryRequired && !IsActive))
            throw new InvalidOperationException("请先完成 EC 自动恢复，再设置固定 RPM。");
        var target = _target.Resolve(requestedRpm);
        Stop();
        RequestedTargetRpm = requestedRpm;
        TargetRpm = target.ClampedRpm;
        RecoveryRequired = true; // Before a write: an exception may still mean hardware was changed.
        try
        {
            _writeDuty(target.DutyPercent);
        }
        catch (Exception ex)
        {
            Fail($"固定 RPM 初始写入失败：{ex.GetBaseException().Message}");
            throw;
        }
        _feedback.Start(target.ClampedRpm, target.DutyPercent);
        State = target.WasClamped ? FanRpmFeedbackState.TargetUnreachable : FanRpmFeedbackState.WaitingForRpm;
        _lastTick = _clock.GetTimestamp();
        return target;
    }

    public void Tick(HardwareSnapshot snapshot, bool guardianRunning, bool leaseValid = true)
    {
        if (!IsActive && !RecoveryRequired && !_emergencyLatched) return;
        // ControlInterval throttles closed-loop work only (feedback adjustment, bad-RPM
        // tolerance counting, full-duty re-assert after a failed emergency write and the bounded
        // recovery retry). It must never defer a fail-safe gate: an expired lease, a stopped
        // Guardian, an invalid/stale/future sample or a 90 °C reading are evaluated on every
        // completed sample, so the worst-case reaction is one sample instead of up to two
        // control intervals. The gate below is deliberately checked after those guards.
        var controlDue = _clock.GetElapsedTime(_lastTick) >= ControlInterval;
        if (controlDue) _lastTick = _clock.GetTimestamp();
        if (_emergencyLatched)
        {
            TickEmergencyLatched(snapshot, guardianRunning, leaseValid, controlDue);
            return;
        }
        if (!IsActive)
        {
            // Fail-closed after a fault: EC automatic owns the fan, only the bounded retry runs.
            // The retry cadence is its own 1 Hz bound (not the control cadence), so a fault that
            // was detected mid-interval is still retried no more than once per second.
            if (RecoveryRetryDue) RetryRecovery(); // A failed restore never re-enables feedback.
            return;
        }
        if (!guardianRunning || !leaseValid)
        {
            Fail(!guardianRunning ? "Guardian 已停止，固定 RPM 控制已停止。" : "控制租约丢失或过期，固定 RPM 控制已停止。");
            return;
        }
        // Temperature, WMI errors and stale/future timestamps are never tolerated.
        if (DescribeInvalidSample(snapshot) is { } invalidSample)
        {
            Fail(invalidSample);
            return;
        }
        // Checked before the RPM tolerance window: 90 °C must never be delayed or masked by a
        // missing fan reading.
        if (snapshot.CpuTemperatureC >= EmergencyTemperatureC)
        {
            EngageEmergencyFullSpeed();
            return;
        }
        // Only the duty-adjusting feedback path below is throttled by the control cadence.
        if (!controlDue) return;
        if (!IsValidRpm(snapshot.Fan1Rpm))
        {
            TolerateBadRpmSample();
            return;
        }
        _rpmBadSamples = 0;
        var decision = _feedback.Evaluate(snapshot.Fan1Rpm);
        ErrorRpm = decision.ErrorRpm;
        State = decision.State;
        if (decision.Action == FanRpmFeedbackAction.Fault)
        {
            Fail(decision.FaultReason ?? "固定 RPM 反馈异常。");
        }
        else if (decision.Action == FanRpmFeedbackAction.AdjustDuty)
        {
            try { _writeDuty(decision.DutyPercent); }
            catch (Exception ex) { Fail($"RPM 闭环写入失败：{ex.GetBaseException().Message}"); }
        }
    }

    public void RestoreAutomatic()
    {
        _feedback.Stop();
        // The only path that clears the 90 °C latch. A failed restore keeps RecoveryRequired
        // engaged, so control stays fail-closed without the emergency flag.
        _emergencyLatched = false;
        _emergencyDutyHeld = false;
        RecoveryRequired = true;
        try
        {
            _restoreAutomatic();
            Stop(); // Explicit restore acknowledges the latched fault after success.
        }
        catch (Exception ex)
        {
            _faultReason ??= "EC 自动恢复失败。";
            State = FanRpmFeedbackState.Fault;
            Fault = $"{_faultReason} 恢复仍待重试：{ex.GetBaseException().Message}";
            throw;
        }
    }

    // Called only after another control mode has been written successfully. The 90 °C latch is
    // deliberately preserved: switching modes is not an explicit acknowledgement of the
    // protection event, so only RestoreAutomatic clears it.
    public void Stop()
    {
        _feedback.Stop();
        _rpmBadSamples = 0;
        _emergencyDutyHeld = false;
        RecoveryRequired = false;
        RequestedTargetRpm = TargetRpm = ErrorRpm = null;
        State = FanRpmFeedbackState.Disabled;
        _faultReason = null;
        Fault = _emergencyLatched ? EmergencyLatchText : null;
    }

    // 90 °C latch. Guardian/lease loss and bad or stale samples may still hand control back to
    // EC automatic once (the documented recovery), but they never clear the latch and they never
    // resume closed-loop control: a later ordinary tick can only re-assert full duty.
    private void TickEmergencyLatched(HardwareSnapshot snapshot, bool guardianRunning, bool leaseValid,
        bool controlDue)
    {
        _feedback.Stop();
        State = FanRpmFeedbackState.Fault;
        if (!guardianRunning || !leaseValid)
        {
            FailLatched(!guardianRunning
                ? "Guardian 已停止，固定 RPM 控制已停止。"
                : "控制租约丢失或过期，固定 RPM 控制已停止。");
            return;
        }
        if (DescribeInvalidSample(snapshot) is { } invalidSample)
        {
            FailLatched(invalidSample);
            return;
        }
        // Detections above are never throttled; only the full-duty re-assert (after a failed
        // write or an EC-automatic hand-off) stays at the control cadence. Never a lower duty,
        // never feedback writes.
        if (!_emergencyDutyHeld && controlDue)
            HoldEmergencyFullSpeed();
    }

    private void EngageEmergencyFullSpeed()
    {
        _feedback.Stop();
        State = FanRpmFeedbackState.Fault;
        _emergencyLatched = true; // Latch first: nothing may resume a lower duty afterwards.
        _emergencyDutyHeld = false;
        HoldEmergencyFullSpeed();
    }

    private void HoldEmergencyFullSpeed()
    {
        try
        {
            _writeDuty(EmergencyDutyPercent);
            _emergencyDutyHeld = true;
            Fault = EmergencyLatchText;
        }
        catch (Exception ex)
        {
            _emergencyDutyHeld = false;
            _faultReason = $"紧急满速写入失败：{ex.GetBaseException().Message}";
            Fault = $"{_faultReason} {EmergencyLatchRetainedText}";
        }
    }

    private void FailLatched(string reason)
    {
        _feedback.Stop();
        State = FanRpmFeedbackState.Fault;
        if (!_emergencyDutyHeld && !RecoveryRequired)
        {
            // EC automatic was already requested for this latch: keep the latch without repeating
            // the same write on every tick.
            Fault = $"{reason} {EmergencyLatchRetainedText}";
            return;
        }
        _emergencyDutyHeld = false;
        _faultReason = reason;
        RecoveryRequired = true;
        if (!RecoveryRetryDue)
        {
            // An attempt already happened within the last ControlInterval: keep the latch and wait
            // for the bounded retry instead of issuing one WMI restore per sample/UI refresh.
            Fault = $"{reason} EC 自动恢复失败，等待重试。{EmergencyLatchRetainedText}";
            return;
        }
        _lastRecoveryAttempt = _clock.GetTimestamp();
        try
        {
            _restoreAutomatic();
            RecoveryRequired = false;
            Fault = $"{reason} 已请求 EC 自动并读回 RPM（控制归属不可回读）。{EmergencyLatchRetainedText}";
        }
        catch (Exception ex)
        {
            RecoveryRequired = true;
            Fault = $"{reason} EC 自动恢复失败，等待重试：{ex.GetBaseException().Message} {EmergencyLatchRetainedText}";
        }
    }

    // Tolerated bad-RPM window: the current (already written) duty is held, the closed loop is
    // not fed a bogus value and no EC automatic restore is requested. Reaching the threshold
    // falls back to the ordinary fail-closed Fail/RetryRecovery path.
    private void TolerateBadRpmSample()
    {
        _rpmBadSamples++;
        if (_rpmBadSamples >= _rpmBadSampleTolerance)
        {
            Fail($"固定 RPM 连续 {_rpmBadSamples} 次读取到无效 RPM（缺失或越界）。");
            return;
        }
        ErrorRpm = null;
        State = FanRpmFeedbackState.WaitingForRpm;
    }

    private static bool IsValidRpm(int? rpm) => rpm is not null and >= 0 and <= MaximumValidRpm;

    private string? DescribeInvalidSample(HardwareSnapshot snapshot)
    {
        if (snapshot.Error is not null || snapshot.CpuTemperatureC is null or <= 0 or > 125 ||
            !double.IsFinite(snapshot.CpuTemperatureC.Value) ||
            _clock.GetUtcNow() - snapshot.Timestamp > TimeSpan.FromSeconds(3) ||
            snapshot.Timestamp - _clock.GetUtcNow() > TimeSpan.FromSeconds(2))
        {
            return $"固定 RPM 采样无效：{snapshot.Error ?? "温度/RPM 无效或数据过期"}";
        }
        return null;
    }

    private void Fail(string reason)
    {
        _feedback.Stop();
        _emergencyDutyHeld = false;
        _faultReason = reason;
        State = FanRpmFeedbackState.Fault;
        RecoveryRequired = true;
        RetryRecovery();
    }

    // One automatic restore attempt per ControlInterval; a latch or recovery fault can be
    // re-detected on every sample, but never turned into repeated WMI writes.
    private bool RecoveryRetryDue =>
        _lastRecoveryAttempt is not { } attempt ||
        _clock.GetElapsedTime(attempt) >= ControlInterval;

    private void RetryRecovery()
    {
        _lastRecoveryAttempt = _clock.GetTimestamp();
        try
        {
            _restoreAutomatic();
            RecoveryRequired = false;
            Fault = $"{_faultReason} 已请求 EC 自动并读回 RPM（控制归属不可回读）。";
        }
        catch (Exception ex)
        {
            RecoveryRequired = true;
            Fault = $"{_faultReason} EC 自动恢复失败，等待重试：{ex.GetBaseException().Message}";
        }
    }
}
