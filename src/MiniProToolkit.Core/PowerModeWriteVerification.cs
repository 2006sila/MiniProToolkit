using System.Globalization;

namespace MiniProToolkit.Core;

/// <summary>One write followed by bounded, read-only confirmation; no retry of the write.</summary>
public static class PowerModeWriteVerification
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    // Preserve an unknown byte for diagnostics, but never coerce null, strings, or other
    // provider objects to Balance. Int32 is accepted only for the three defined modes.
    public static byte? DecodeCurrentPowerMode(object? value) => value switch
    {
        byte raw => raw,
        int raw when raw is >= 0 and <= 2 => (byte)raw,
        _ => null
    };

    /// <param name="elapsed">A monotonic clock reading; the budget begins before write.</param>
    /// <param name="wait">Synchronous wait, called only after a valid non-target read.</param>
    /// <remarks>
    /// The caller must retain its hardware lock for this entire call. Synchronous native
    /// calls cannot be cancelled here: an over-budget call is allowed to return, then
    /// fails verification. No thread is abandoned and no second write is issued.
    /// A mismatch alone does not establish why the reported state differs.
    /// </remarks>
    public static void Verify(
        PowerMode expected,
        Action write,
        Func<byte?> read,
        Func<TimeSpan> elapsed,
        Action<TimeSpan> wait,
        Func<long?>? getSetterStatus = null)
    {
        if (expected is not (PowerMode.Balance or PowerMode.Performance or PowerMode.Silence))
            throw new ArgumentOutOfRangeException(nameof(expected));
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(elapsed);
        ArgumentNullException.ThrowIfNull(wait);

        var beganAt = elapsed();
        write(); // Preserve the original setter exception; never read or retry after it.
        var setterStatus = getSetterStatus?.Invoke(); // Metadata only, not a success code.
        byte? lastActual = null;
        var readCount = 0;

        PowerModeWriteVerificationException Failure(PowerModeWriteVerificationFailure reason, TimeSpan duration) =>
            new(reason, (byte)expected, lastActual, duration, readCount, setterStatus);

        while (true)
        {
            var duration = elapsed() - beganAt;
            if (duration >= Budget)
                throw Failure(PowerModeWriteVerificationFailure.Timeout, duration);

            readCount++;
            try
            {
                lastActual = read();
            }
            catch (Exception)
            {
                // Do not retain the provider exception/message/object: it can contain
                // machine paths or other non-allowlisted data.
                throw Failure(PowerModeWriteVerificationFailure.ReadFailed, elapsed() - beganAt);
            }

            duration = elapsed() - beganAt;
            if (duration >= Budget)
                throw Failure(PowerModeWriteVerificationFailure.Timeout, duration);
            if (lastActual is not (0 or 1 or 2))
                throw Failure(PowerModeWriteVerificationFailure.InvalidRead, duration);
            if (lastActual == (byte)expected)
                return;

            // Recheck before waiting, then again before the next read. A slow native
            // read must not turn into a late success or an extra out-of-budget read.
            duration = elapsed() - beganAt;
            if (duration >= Budget)
                throw Failure(PowerModeWriteVerificationFailure.Timeout, duration);
            var remaining = Budget - duration;
            wait(remaining < PollInterval ? remaining : PollInterval);
        }
    }
}

public enum PowerModeWriteVerificationFailure
{
    Timeout,
    InvalidRead,
    ReadFailed
}

/// <summary>Closed, scalar-only verification diagnostics; no provider exception is retained.</summary>
public sealed class PowerModeWriteVerificationException : InvalidOperationException
{
    public PowerModeWriteVerificationFailure Failure { get; }
    public byte Expected { get; }
    public byte? LastActual { get; }
    public TimeSpan Elapsed { get; }
    public int ReadCount { get; }
    public long? SetterStatus { get; }

    internal PowerModeWriteVerificationException(
        PowerModeWriteVerificationFailure failure,
        byte expected,
        byte? lastActual,
        TimeSpan elapsed,
        int readCount,
        long? setterStatus)
        : base(FormattableString.Invariant(
            $"Power mode verification failed: reason={failure}; expected={expected}; lastActual={lastActual?.ToString(CultureInfo.InvariantCulture) ?? "null"}; elapsedMs={elapsed.TotalMilliseconds:F3}; readCount={readCount}; setterStatus={setterStatus?.ToString(CultureInfo.InvariantCulture) ?? "null"}."))
    {
        Failure = failure;
        Expected = expected;
        LastActual = lastActual;
        Elapsed = elapsed;
        ReadCount = readCount;
        SetterStatus = setterStatus;
    }
}
