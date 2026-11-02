namespace MiniProToolkit.Core;

public interface IFanControlTarget
{
    void SetCurveDuty(int dutyPercent);
    void RestoreAutomatic();
}

public sealed record FanCurveApplyResult(
    bool Success,
    FanCurveDecision Decision,
    string? Error = null);

public sealed class FanCurveController : IDisposable
{
    private readonly IFanControlTarget _target;
    private readonly FanCurveEngine _engine;
    private bool _enabled;
    private bool _restoreRequired;
    private bool _disposed;

    public FanCurveController(IFanControlTarget target, FanCurveSettings settings, FanCalibration? calibration = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _engine = new FanCurveEngine(settings, calibration);
    }

    public bool IsEnabled => _enabled;
    public bool RecoveryRequired => _restoreRequired;
    public int? CurrentDuty => _engine.CurrentDuty;
    public FanTemperatureLevel ActiveLevel => _engine.ActiveLevel;

    /// <summary>Optional 90 °C latch is active and only Disable/Restore can release it.</summary>
    public bool EmergencyLatched => _engine.EmergencyLatched;

    public void Enable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.Reset();
        _enabled = true;
    }

    public void Disable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_enabled && !_restoreRequired)
            return;

        _enabled = false;
        _restoreRequired = true;
        _target.RestoreAutomatic();
        // Handing control back is the only place the optional emergency latch is released.
        _engine.Reset(clearEmergencyLatch: true);
        _restoreRequired = false;
    }

    public FanCurveApplyResult Apply(HardwareSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_enabled)
            return new(true, FanCurveDecision.NoChange("fan curve is disabled"));

        var decision = _engine.Evaluate(snapshot);
        try
        {
            switch (decision.Action)
            {
                case FanCurveAction.SetDuty when decision.DutyPercent is { } duty:
                    _restoreRequired = true;
                    _target.SetCurveDuty(duty);
                    break;
                case FanCurveAction.RestoreAutomatic:
                    _target.RestoreAutomatic();
                    _engine.Reset(clearEmergencyLatch: true);
                    _enabled = false;
                    _restoreRequired = false;
                    break;
            }
            return new(true, decision);
        }
        catch (Exception ex)
        {
            try
            {
                _target.RestoreAutomatic();
                _restoreRequired = false;
            }
            catch (Exception restoreException)
            {
                _restoreRequired = true;
                ex = new InvalidOperationException(
                    $"Fan curve failed and automatic restore failed: {restoreException.GetBaseException().Message}", ex);
            }

            _engine.Reset(clearEmergencyLatch: true);
            _enabled = false;
            return new(false, FanCurveDecision.Restore("curve failure; firmware control requested"),
                ex.GetBaseException().Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_enabled || _restoreRequired)
            _target.RestoreAutomatic();
        _engine.Reset(clearEmergencyLatch: true);
        _enabled = false;
        _restoreRequired = false;
        _disposed = true;
    }
}
