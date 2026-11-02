namespace MiniProToolkit.Core;

/// <summary>
/// Pure "may I still write the curve now?" decision for the record read from the lease file.
/// It is the same rule the control pass uses (owner PID, owner process start, heartbeat age), but
/// it is evaluated against a *fresh* read taken immediately before a hardware write, so a lease
/// that expired, was recovered, or was replaced while a slow pass (LHM rebuild, WMI) was running
/// cannot be used to write the fan curve. Every input that cannot be proven valid fails closed:
/// a missing record, a foreign PID, a missing or different process start, and a stale heartbeat all
/// return false. The policy never touches I/O, so callers decide when the read happens.
/// </summary>
public static class GuardianCurveWriteLeasePolicy
{
    /// <summary>
    /// Lease timeout shared by the control pass and the pre-write renewal. The value is the one the
    /// Guardian recovery coordinator already uses; it changes no heartbeat or recovery timing.
    /// </summary>
    public static readonly TimeSpan ControlLeaseTimeout = TimeSpan.FromSeconds(8);

    public static bool CanWrite(
        GuardianLease? current,
        int expectedProcessId,
        DateTimeOffset? expectedProcessStartUtc,
        DateTimeOffset now,
        TimeSpan timeout)
    {
        if (current is null)
            return false;
        if (expectedProcessId <= 0 || timeout <= TimeSpan.Zero)
            return false;
        if (current.ProcessId != expectedProcessId)
            return false;
        // Ownership is bound to the process instance: a restarted owner with the same PID is a
        // different owner. An unknown start time cannot be proven equal, so it is refused.
        if (expectedProcessStartUtc is not { } expectedStart || current.ProcessStartUtc != expectedStart)
            return false;
        return !current.IsStale(now, timeout);
    }
}
