using MiniProToolkit.Core;
using MiniProToolkit.Hardware;

namespace MiniProToolkit.App;

internal sealed class GuardianLeasedFanTarget : IFanControlTarget
{
    private readonly PowerSwitchHardware _hardware;
    private readonly GuardianLeaseStore _leaseStore;
    private readonly FanCurveLeaseGate _leaseGate;

    public GuardianLeasedFanTarget(
        PowerSwitchHardware hardware,
        GuardianLeaseStore leaseStore,
        FanCurveLeaseGate leaseGate)
    {
        _hardware = hardware;
        _leaseStore = leaseStore;
        _leaseGate = leaseGate ?? throw new ArgumentNullException(nameof(leaseGate));
    }

    public void SetCurveDuty(int dutyPercent)
    {
        // The pass that produced this decision already read and accepted the lease; the gate only
        // repeats that verdict. A closed gate means Guardian stopped or the lease lapsed, so the
        // write is refused here and FanCurveController takes its existing restore-automatic path.
        // The lease store is never touched on that path: a curve write must not revive ownership.
        if (!_leaseGate.TryWrite(() => WriteDuty(dutyPercent)))
            throw new InvalidOperationException("控制租约无效或已过期，曲线写入已被拒绝。");
    }

    private void WriteDuty(int dutyPercent)
    {
        // Publish before the write so a crash in the WMI call is still recoverable.
        _leaseStore.WriteActive(Environment.ProcessId, "fan-curve", DateTimeOffset.UtcNow,
            GuardianProcessIdentity.CurrentStartUtc);
        _hardware.SetCurveDuty(dutyPercent);
    }

    public void RestoreAutomatic()
    {
        // Control is handed back: no curve write may follow until a later pass re-validates the lease.
        _leaseGate.Close();
        _hardware.RestoreAutomatic();
        _leaseStore.ClearOwned(Environment.ProcessId);
    }
}
