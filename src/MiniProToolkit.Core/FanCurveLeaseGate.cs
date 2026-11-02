namespace MiniProToolkit.Core;

/// <summary>
/// One decision for one control pass: may the curve write to hardware now, and must the caller
/// restore EC automatic instead? Both flags are false while the curve is simply not controlling.
/// </summary>
public readonly record struct FanCurveLeaseDecision(bool MayApply, bool RestoreRequired, string Reason)
{
    public static FanCurveLeaseDecision None(string reason) => new(false, false, reason);

    public static FanCurveLeaseDecision Apply(string reason) => new(true, false, reason);

    public static FanCurveLeaseDecision Restore(string reason) => new(false, true, reason);
}

/// <summary>
/// Pure lease policy for the fan curve. It applies the same rule as the fixed-RPM path (Guardian
/// running *and* a valid control lease) and is evaluated from a single lease read per pass, so the
/// curve can never keep or regain control after Guardian stopped or the lease expired. Reasons are
/// shown in the UI and in the diagnostic log, so they never carry paths or command lines.
/// </summary>
public static class FanCurveLeasePolicy
{
    public const string GuardianStoppedReason = "Guardian 已停止，曲线控制已停止并请求 EC 自动。";
    public const string LeaseInvalidReason = "控制租约无效或已过期，曲线控制已停止并请求 EC 自动。";
    public const string RecoveryPendingReason = "曲线恢复未完成，重试请求 EC 自动。";
    public const string LeaseValidReason = "曲线租约有效。";
    public const string CurveDisabledReason = "曲线未启用。";

    public static FanCurveLeaseDecision Evaluate(
        bool curveEnabled,
        bool recoveryRequired,
        bool guardianRunning,
        bool leaseValid)
    {
        if (curveEnabled && !guardianRunning)
            return FanCurveLeaseDecision.Restore(GuardianStoppedReason);
        if (curveEnabled && !leaseValid)
            return FanCurveLeaseDecision.Restore(LeaseInvalidReason);
        if (curveEnabled)
            return FanCurveLeaseDecision.Apply(LeaseValidReason);
        // A previous recovery did not complete: keep retrying it, never resume control.
        if (recoveryRequired)
            return FanCurveLeaseDecision.Restore(RecoveryPendingReason);
        return FanCurveLeaseDecision.None(CurveDisabledReason);
    }
}

/// <summary>
/// Write gate for the curve path. It is opened only by the pass (or the explicit takeover entry
/// point) that just read and accepted the control lease; every other path leaves it closed.
/// Closing it is what stops a curve write from publishing — and therefore reviving — a lease that
/// Guardian already recovered. It holds no hardware handle: a closed gate refuses the write and the
/// caller keeps its existing restore-automatic path.
/// </summary>
public sealed class FanCurveLeaseGate
{
    private int _mayWrite;

    /// <summary>True only while the current control pass (or takeover) holds a valid lease.</summary>
    public bool MayWrite => Volatile.Read(ref _mayWrite) == 1;

    /// <summary>
    /// Publishes the verdict of one control pass. Callers pass the *same* lease validity they used
    /// for the fixed-RPM control step; a closed gate is the default for every other state.
    /// </summary>
    public void SetPass(bool mayWrite) => Volatile.Write(ref _mayWrite, mayWrite ? 1 : 0);

    /// <summary>Closes the gate; the next validated pass has to reopen it explicitly.</summary>
    public void Close() => Volatile.Write(ref _mayWrite, 0);

    /// <summary>
    /// Runs <paramref name="write"/> only while the gate is open. Returns false without touching
    /// hardware or the lease store when it is closed.
    /// </summary>
    public bool TryWrite(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (!MayWrite)
            return false;
        write();
        return true;
    }
}
