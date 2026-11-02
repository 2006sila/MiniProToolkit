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
        // The pass-start verdict is not enough: this pass may have spent seconds in an LHM/WMI
        // rebuild, so the lease it accepted can be stale or already recovered by Guardian. The
        // lease is therefore validated *and* renewed atomically under the cross-process lock
        // immediately before the hardware write: a separate check-then-write would leave a window
        // in which Guardian could clear the record after the read. Renewal only updates an existing
        // record owned by this process instance with a fresh heartbeat; a missing, stale or foreign
        // record returns false and nothing is written, so this path can never create or revive
        // ownership. The exception keeps the caller's existing fail path (FanCurveController
        // restores EC automatic, which closes the gate).
        // The store invokes the clock itself, after it holds the cross-process lock, so the staleness
        // verdict and the heartbeat it publishes use one time taken *after* a possibly long lock wait.
        if (!_leaseStore.TryRenewOwnedFresh(Environment.ProcessId, GuardianProcessIdentity.CurrentStartUtc,
                "fan-curve", static () => DateTimeOffset.UtcNow, GuardianCurveWriteLeasePolicy.ControlLeaseTimeout))
            throw new InvalidOperationException("控制租约在写入前已失效，曲线写入已被拒绝。");
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
