using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Lease gate for the fan curve: a curve may only write while Guardian runs *and* the lease read by
/// that pass is valid. These tests cover the pure policy, the write gate and the controller wiring
/// that keeps a refused write on the existing restore-automatic path.
/// </summary>
public sealed class FanCurveLeaseGateTests
{
    [Fact]
    public void ValidLeaseWithGuardianAllowsCurveWrite()
    {
        var decision = FanCurveLeasePolicy.Evaluate(
            curveEnabled: true, recoveryRequired: false, guardianRunning: true, leaseValid: true);

        Assert.True(decision.MayApply);
        Assert.False(decision.RestoreRequired);
    }

    [Fact]
    public void GuardianStoppedStopsCurveInsteadOfWriting()
    {
        var decision = FanCurveLeasePolicy.Evaluate(
            curveEnabled: true, recoveryRequired: false, guardianRunning: false, leaseValid: true);

        Assert.False(decision.MayApply);
        Assert.True(decision.RestoreRequired);
        Assert.Contains("Guardian", decision.Reason);
    }

    [Fact]
    public void InvalidLeaseStopsCurveInsteadOfWriting()
    {
        // Guardian is still detected, but its lease expired or was recovered: no curve write.
        var decision = FanCurveLeasePolicy.Evaluate(
            curveEnabled: true, recoveryRequired: false, guardianRunning: true, leaseValid: false);

        Assert.False(decision.MayApply);
        Assert.True(decision.RestoreRequired);
        Assert.Contains("租约", decision.Reason);
    }

    [Fact]
    public void ExpiredLeaseStopsCurveEvenWhileRecoveryIsNotPending()
    {
        // Regression for the Guardian-recovery window: an expired lease used to fall through to
        // Apply(), which republished the lease from the duty write and took control back.
        var gate = new FanCurveLeaseGate();
        var decision = FanCurveLeasePolicy.Evaluate(
            curveEnabled: true, recoveryRequired: false, guardianRunning: true, leaseValid: false);
        gate.SetPass(decision.MayApply);

        var wrote = gate.TryWrite(() => Assert.Fail("the gate must stay closed for an invalid lease"));

        Assert.False(wrote);
        Assert.False(gate.MayWrite);
    }

    [Fact]
    public void PendingCurveRecoveryKeepsRetryingRestore()
    {
        var decision = FanCurveLeasePolicy.Evaluate(
            curveEnabled: false, recoveryRequired: true, guardianRunning: true, leaseValid: false);

        Assert.False(decision.MayApply);
        Assert.True(decision.RestoreRequired);
    }

    [Fact]
    public void DisabledCurveWithoutRecoveryKeepsDefaultPreviewBehaviour()
    {
        var decision = FanCurveLeasePolicy.Evaluate(
            curveEnabled: false, recoveryRequired: false, guardianRunning: true, leaseValid: true);

        Assert.False(decision.MayApply);
        Assert.False(decision.RestoreRequired);
    }

    [Fact]
    public void LeaseReasonsCarryNoPathOrCommandLine()
    {
        string[] reasons =
        [
            FanCurveLeasePolicy.GuardianStoppedReason,
            FanCurveLeasePolicy.LeaseInvalidReason,
            FanCurveLeasePolicy.RecoveryPendingReason
        ];

        foreach (var reason in reasons)
        {
            Assert.DoesNotContain('\\', reason);
            Assert.DoesNotContain('/', reason);
            Assert.DoesNotContain(".exe", reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ClosedGateRefusesTheWrite()
    {
        var gate = new FanCurveLeaseGate();
        var writes = 0;

        Assert.False(gate.TryWrite(() => writes++));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void ClosedGateStaysClosedUntilANewPassOpensIt()
    {
        // "Wake after Guardian recovered": the gate was opened by an earlier pass, then the lease
        // lapses. Closing it must stay effective until a pass re-validates the lease.
        var gate = new FanCurveLeaseGate();
        gate.SetPass(true);
        Assert.True(gate.MayWrite);

        gate.Close();
        var writes = 0;
        Assert.False(gate.TryWrite(() => writes++));
        Assert.Equal(0, writes);

        gate.SetPass(true);
        Assert.True(gate.TryWrite(() => writes++));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void RefusedWriteNeverPublishesOrRevivesTheLease()
    {
        var gate = new FanCurveLeaseGate();
        var target = new GatedTarget(gate);
        using var controller = new FanCurveController(target, FanCurveSettings.Default);
        controller.Enable();

        // First pass holds the lease: one duty write, one lease publication.
        gate.SetPass(true);
        Assert.True(controller.Apply(Snapshot(60)).Success);
        Assert.Equal(1, target.DutyWrites);
        Assert.Equal(1, target.LeaseWrites);

        // Guardian recovers EC automatic and the lease lapses: a further pass must not renew it.
        target.ExpireLease();
        gate.SetPass(false);
        var result = controller.Apply(Snapshot(70));

        Assert.False(result.Success);
        Assert.Equal(1, target.DutyWrites);
        Assert.Equal(1, target.LeaseWrites);
        Assert.Equal(1, target.RestoreCount);
        Assert.False(controller.IsEnabled);
        Assert.False(controller.RecoveryRequired);
    }

    [Fact]
    public void FailedCurveWriteRestoreKeepsRecoveryPendingWithoutRevivingTheLease()
    {
        var gate = new FanCurveLeaseGate();
        var target = new GatedTarget(gate) { ThrowOnRestore = true };
        using var controller = new FanCurveController(target, FanCurveSettings.Default);
        controller.Enable();
        gate.SetPass(false);

        var result = controller.Apply(Snapshot(60));

        Assert.False(result.Success);
        Assert.True(controller.RecoveryRequired);
        Assert.Equal(0, target.DutyWrites);
        Assert.Equal(0, target.LeaseWrites);
        Assert.Equal(1, target.RestoreCount);
        // Let Dispose() run its own restore retry without masking the assertions above.
        target.ThrowOnRestore = false;
    }

    private static HardwareSnapshot Snapshot(double temperature) => new(
        DateTimeOffset.UtcNow,
        temperature,
        2000,
        0,
        PowerMode.Balance,
        20,
        40,
        FanControlMode.FirmwareAutomatic,
        null,
        null);

    /// <summary>
    /// Same shape as the App target: the duty write is wrapped by the lease gate, so a closed gate
    /// touches neither the hardware nor the lease store.
    /// </summary>
    private sealed class GatedTarget(FanCurveLeaseGate gate) : IFanControlTarget
    {
        private bool _leaseAlive = true;

        public int DutyWrites { get; private set; }
        public int LeaseWrites { get; private set; }
        public int RestoreCount { get; private set; }
        public bool ThrowOnRestore { get; set; }

        public void ExpireLease() => _leaseAlive = false;

        public void SetCurveDuty(int dutyPercent)
        {
            var wrote = gate.TryWrite(() =>
            {
                if (!_leaseAlive)
                    throw new InvalidOperationException("control lease expired");
                DutyWrites++;
                LeaseWrites++;
            });
            if (!wrote)
                throw new InvalidOperationException("control lease is invalid; curve write refused");
        }

        public void RestoreAutomatic()
        {
            RestoreCount++;
            _leaseAlive = true;
            if (ThrowOnRestore) throw new IOException("restore failed");
        }
    }
}
