using System.Text;
using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed record GuardianLease(
    int ProcessId,
    string ControlMode,
    DateTimeOffset HeartbeatUtc,
    DateTimeOffset? ProcessStartUtc = null)
{
    public bool IsStale(DateTimeOffset now, TimeSpan timeout) =>
        now - HeartbeatUtc > timeout || now < HeartbeatUtc - TimeSpan.FromSeconds(2);
}

/// <summary>
/// Outcome of one lease-file read. A read that fails for any reason other than "there is no file"
/// is <see cref="GuardianLeaseReadStatus.Unreadable"/> and never collapses into
/// <see cref="GuardianLeaseReadStatus.Missing"/>: "no lease" is a decision the recovery
/// coordinator may act on, while "the lease could not be read" is not - the owner is unknown, so a
/// caller must defer the decision instead of writing hardware.
/// </summary>
public enum GuardianLeaseReadStatus
{
    Missing,
    Success,
    Unreadable
}

/// <summary>
/// Typed, diagnostics-friendly lease read result. <see cref="Reason"/> is a fixed, bounded message
/// from a closed set: it never contains the lease path, the file content or a raw exception
/// message, so it is safe to put in a log or a diagnostic event.
/// </summary>
public sealed record GuardianLeaseReadResult
{
    public const string MissingReason = "lease file is absent";
    public const string EmptyReason = "lease file is empty";
    public const string AccessDeniedReason = "lease file access was denied";
    public const string IoReason = "lease file could not be opened";
    public const string InvalidJsonReason = "lease file is not valid JSON";
    public const string NotAnObjectReason = "lease file root is not a JSON object";
    public const string NotALeaseReason = "lease file is not a usable lease record";
    public const string SuccessReason = "lease file is valid";

    private GuardianLeaseReadResult(GuardianLeaseReadStatus status, GuardianLease? lease, string reason)
    {
        Status = status;
        Lease = lease;
        Reason = reason;
    }

    public GuardianLeaseReadStatus Status { get; }

    /// <summary>Non-null only for <see cref="GuardianLeaseReadStatus.Success"/>.</summary>
    public GuardianLease? Lease { get; }

    /// <summary>Fixed reason from the constants above; never a path, JSON fragment or exception text.</summary>
    public string Reason { get; }

    public bool IsSuccess => Status == GuardianLeaseReadStatus.Success;

    public bool IsUnreadable => Status == GuardianLeaseReadStatus.Unreadable;

    public static GuardianLeaseReadResult Missing { get; } =
        new(GuardianLeaseReadStatus.Missing, null, MissingReason);

    public static GuardianLeaseReadResult Success(GuardianLease lease) => new(
        GuardianLeaseReadStatus.Success,
        lease ?? throw new ArgumentNullException(nameof(lease)),
        SuccessReason);

    public static GuardianLeaseReadResult Unreadable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(GuardianLeaseReadStatus.Unreadable, null, reason);
    }

    public override string ToString() => $"{Status}: {Reason}";
}

/// <summary>
/// Bounded repeater for "the lease is unreadable" warnings. A Guardian poll runs every 2 s, so a
/// persistent sharing violation or ACL problem would otherwise emit a log line (plus a bounded-log
/// and diagnostic entry) 30 times a minute. The throttle reports the first failure, every change of
/// reason, and then at most once per <see cref="RepeatInterval"/>; a successful (or absent) read
/// clears the state so the next failure is reported immediately. It has no clock of its own: the
/// caller passes the timestamp, and a clock that jumps backwards only suppresses entries.
/// </summary>
public sealed class GuardianLeaseReadWarningThrottle
{
    public static readonly TimeSpan DefaultRepeatInterval = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private string? _reason;
    private DateTimeOffset _reportedUtc;
    private bool _faulted;

    public GuardianLeaseReadWarningThrottle(TimeSpan? repeatInterval = null)
    {
        var interval = repeatInterval ?? DefaultRepeatInterval;
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(repeatInterval));
        RepeatInterval = interval;
    }

    public TimeSpan RepeatInterval { get; }

    /// <summary>True while an unreadable-lease warning is outstanding.</summary>
    public bool IsFaulted
    {
        get { lock (_sync) return _faulted; }
    }

    /// <summary>True when the caller must emit the warning now instead of suppressing it.</summary>
    public bool ShouldReport(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_sync)
        {
            if (!_faulted)
            {
                _faulted = true;
                _reason = reason;
                _reportedUtc = now;
                return true;
            }
            if (!string.Equals(_reason, reason, StringComparison.Ordinal))
            {
                _reason = reason;
                _reportedUtc = now;
                return true;
            }
            if (now - _reportedUtc < RepeatInterval)
                return false;
            _reportedUtc = now;
            return true;
        }
    }

    /// <summary>
    /// Clears the warning state after a readable (or absent) lease. Returns true exactly once per
    /// fault, so the caller can report the recovery without repeating it on every healthy poll.
    /// </summary>
    public bool Clear()
    {
        lock (_sync)
        {
            if (!_faulted)
                return false;
            _faulted = false;
            _reason = null;
            return true;
        }
    }
}

public enum GuardianRecoveryAction
{
    None,
    RecoverAutomatic
}

public sealed record GuardianRecoveryDecision(
    GuardianRecoveryAction Action,
    string Reason)
{
    public static GuardianRecoveryDecision None(string reason) =>
        new(GuardianRecoveryAction.None, reason);

    public static GuardianRecoveryDecision Recover(string reason) =>
        new(GuardianRecoveryAction.RecoverAutomatic, reason);
}

public sealed class GuardianRecoveryCoordinator
{
    public GuardianRecoveryCoordinator(TimeSpan heartbeatTimeout)
    {
        if (heartbeatTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(heartbeatTimeout));
        HeartbeatTimeout = heartbeatTimeout;
    }

    public TimeSpan HeartbeatTimeout { get; }

    public GuardianRecoveryDecision Evaluate(
        GuardianLease? lease,
        DateTimeOffset now,
        Func<int, bool> isProcessAlive,
        Func<int, DateTimeOffset?>? getProcessStartUtc = null)
    {
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        if (lease is null)
            return GuardianRecoveryDecision.None("no active control lease");
        if (lease.IsStale(now, HeartbeatTimeout))
            return GuardianRecoveryDecision.Recover("control heartbeat expired");
        if (!isProcessAlive(lease.ProcessId))
            return GuardianRecoveryDecision.Recover("owner process is no longer running");
        if (lease.ProcessStartUtc is { } expectedStart && getProcessStartUtc is not null)
        {
            var actualStart = getProcessStartUtc(lease.ProcessId);
            if (actualStart is null || Math.Abs((actualStart.Value - expectedStart).TotalSeconds) > 2)
                return GuardianRecoveryDecision.Recover("owner PID was reused by another process");
        }
        return GuardianRecoveryDecision.None("owner process and heartbeat are healthy");
    }
}

public sealed class GuardianLeaseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    // Cross-process mutual exclusion for the lease file. A named mutex would need an explicit ACL
    // that works for both the interactive user and the LocalSystem Guardian; a sibling lock file
    // inherits exactly the ACL of the lease directory, so whoever may publish a lease may also take
    // the lock. It is opened for *read* only: a lock file first created by the SYSTEM Guardian gets
    // a CREATOR OWNER ACE for SYSTEM, and the user is then left with the directory's inherited read
    // access - FileShare.None needs no write access, so mutual exclusion still works. The handle is
    // released by Dispose *or* by the kernel when the holder crashes, so a stale lock file never
    // blocks anybody: the open of the existing file simply succeeds once the dead handle is gone.
    private const string LockFileSuffix = ".lock";
    private const int LockRetryDelayMilliseconds = 10;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaximumLockTimeout = TimeSpan.FromSeconds(5);

    public GuardianLeaseStore(string? path = null, TimeSpan? lockTimeout = null)
    {
        Path = path ?? DefaultPath;
        LockPath = Path + LockFileSuffix;
        var timeout = lockTimeout ?? DefaultLockTimeout;
        if (timeout <= TimeSpan.Zero || timeout > MaximumLockTimeout)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        LockTimeout = timeout;
    }

    public string Path { get; }

    /// <summary>Sibling lock file used for the bounded cross-process lease lock.</summary>
    public string LockPath { get; }

    /// <summary>Upper bound on how long a lease operation waits for the cross-process lock.</summary>
    public TimeSpan LockTimeout { get; }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MiniProToolkit",
        "guardian-lease.json");

    /// <summary>
    /// Publishes (or renews) the control lease. Runs under the cross-process lease lock, so a
    /// concurrent clear in Guardian can never slip between "replace the record" and "the record is
    /// the old one". If the lock cannot be taken within <see cref="LockTimeout"/> the lease is left
    /// untouched and an <see cref="IOException"/> is thrown: the caller's existing failure path
    /// aborts the control step instead of writing hardware it cannot publish.
    /// </summary>
    public void WriteActive(
        int processId,
        string controlMode,
        DateTimeOffset now,
        DateTimeOffset? processStartUtc = null)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (string.IsNullOrWhiteSpace(controlMode))
            throw new ArgumentException("Control mode is required.", nameof(controlMode));

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Guardian lease path has no directory.");
        Directory.CreateDirectory(directory);
        using var guard = TryAcquireLock();
        if (guard is null)
            throw new IOException("Guardian lease is locked by another process; the control step was not published.");

        WriteRecord(new GuardianLease(processId, controlMode, now, processStartUtc));
    }

    /// <summary>
    /// Atomically renews the lease only when the record on disk still belongs to this process
    /// instance (same PID and process start) and its heartbeat is fresh. The read, the verdict and
    /// the replacement all run under the cross-process lease lock, so Guardian's
    /// compare-and-clear is serialised with this renewal: either the renewal lands first and
    /// Guardian's delete sees a changed record (safe no-op), or the clear lands first and the
    /// renewal finds no record and returns false.
    /// <para>
    /// A missing record, a foreign PID, a different process start and a stale heartbeat all return
    /// false and never write anything, so a curve write that renews here cannot create or revive
    /// ownership. A busy lock also returns false (fail closed): ownership cannot be proven, so the
    /// caller must not touch hardware. No directory is created: no lease directory means no lease
    /// was ever published.
    /// </para>
    /// <para>
    /// <paramref name="utcNow"/> is invoked by this method, never by the caller: the clock is read
    /// only after the cross-process lock is held, and that single timestamp is used both for the
    /// staleness verdict and for the record it publishes. A caller-computed timestamp would be
    /// taken before a bounded lock wait (up to <see cref="LockTimeout"/>), so a call that queued
    /// behind another holder could judge a heartbeat fresh with a timestamp that is already stale by
    /// the time the record lands - publishing an already expired heartbeat. Reading the clock under
    /// the lock removes that window.
    /// </para>
    /// </summary>
    public bool TryRenewOwnedFresh(
        int expectedProcessId,
        DateTimeOffset? expectedProcessStartUtc,
        string controlMode,
        Func<DateTimeOffset> utcNow,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(controlMode))
            throw new ArgumentException("Control mode is required.", nameof(controlMode));
        ArgumentNullException.ThrowIfNull(utcNow);

        using var guard = TryAcquireLock();
        if (guard is null)
            return false;
        // Only now, with the lock in hand, is the time meaningful for the verdict below.
        var now = utcNow();
        if (!GuardianCurveWriteLeasePolicy.CanWrite(ReadCore(), expectedProcessId,
                expectedProcessStartUtc, now, timeout))
            return false;

        WriteRecord(new GuardianLease(expectedProcessId, controlMode, now, expectedProcessStartUtc));
        return true;
    }

    /// <summary>Replaces the lease file atomically. Caller must hold the lease lock.</summary>
    private void WriteRecord(GuardianLease record)
    {
        var temporary = Path + $".{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temporary, Path, true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Reads the lease. Deliberately lock-free: replacing the record is an atomic rename, so a
    /// reader sees either the old or the new record. Kept for callers that only need the record:
    /// anything but a successful read is reported as null, so the App stays fail-closed. Callers
    /// that must tell "no lease" from "could not read the lease" use <see cref="ReadResult"/>.
    /// </summary>
    public GuardianLease? Read() => ReadCore();

    /// <summary>
    /// Reads the lease and reports <i>why</i> it could not be read. "Absent" is decided by opening
    /// the file and catching FileNotFound/DirectoryNotFound - not by a pre-check such as
    /// <c>File.Exists</c>, which also returns false for a permission problem and would silently turn
    /// an unknown owner into "no owner". Every other failure (sharing violation, denied access,
    /// zero bytes, malformed JSON, a JSON root that is not an object, or an object without a usable
    /// owner identity) is <see cref="GuardianLeaseReadStatus.Unreadable"/> with a fixed safe reason.
    /// </summary>
    public GuardianLeaseReadResult ReadResult()
    {
        string text;
        try
        {
            // Opened for read with write+delete sharing, so the atomic replace (and a
            // compare-and-clear) is never blocked by an in-flight read. A concurrent share-none
            // holder of the lease file still surfaces as IOException -> Unreadable, never Missing.
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        // No file (or no directory) is the one failure that really means "nothing to recover".
        catch (FileNotFoundException)
        {
            return GuardianLeaseReadResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return GuardianLeaseReadResult.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return GuardianLeaseReadResult.Unreadable(GuardianLeaseReadResult.AccessDeniedReason);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Security.SecurityException)
        {
            return GuardianLeaseReadResult.Unreadable(GuardianLeaseReadResult.IoReason);
        }

        if (string.IsNullOrWhiteSpace(text))
            return GuardianLeaseReadResult.Unreadable(GuardianLeaseReadResult.EmptyReason);

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return GuardianLeaseReadResult.Unreadable(GuardianLeaseReadResult.NotAnObjectReason);

            var lease = document.RootElement.Deserialize<GuardianLease>(JsonOptions);
            // An object without a usable owner identity is not a lease: treating it as a record
            // would let Guardian evaluate - and recover - an owner it cannot identify.
            if (lease is null || lease.ProcessId <= 0
                || string.IsNullOrWhiteSpace(lease.ControlMode)
                || lease.HeartbeatUtc == default)
                return GuardianLeaseReadResult.Unreadable(GuardianLeaseReadResult.NotALeaseReason);

            return GuardianLeaseReadResult.Success(lease);
        }
        catch (JsonException)
        {
            return GuardianLeaseReadResult.Unreadable(GuardianLeaseReadResult.InvalidJsonReason);
        }
        catch (NotSupportedException)
        {
            return GuardianLeaseReadResult.Unreadable(GuardianLeaseReadResult.InvalidJsonReason);
        }
    }

    public void Clear()
    {
        using var guard = TryAcquireLock();
        if (guard is null)
            return; // Bounded wait expired: leave the lease for the next attempt instead of racing.
        TryDelete(Path);
    }

    // Legacy PID-only clear, kept for callers that only track their own ownership and for the
    // compatibility tests. Guardian recovery must use the compare-and-clear overload below: a
    // restarted owner can publish a new lease while an old lease is still being recovered.
    public bool ClearOwned(int processId)
    {
        using var guard = TryAcquireLock();
        if (guard is null)
            return false;
        try
        {
            var lease = ReadCore();
            return lease is not null && lease.ProcessId == processId && DeleteIfUnchanged(lease);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // Compare-and-clear. Removes the lease only when the record on disk is still exactly the
    // record the caller evaluated (PID + process start + mode + heartbeat). Anything else - a
    // missing file, a newer lease from a restarted owner, a different heartbeat - is a safe
    // no-op so Guardian can never delete ownership it did not decide about. The read, the compare
    // and the delete run under the cross-process lease lock, so no owner can publish a new record
    // between the compare and the delete. A lock timeout is a safe no-op as well.
    public bool ClearOwned(GuardianLease expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var guard = TryAcquireLock();
        if (guard is null)
            return false;
        try
        {
            return DeleteIfUnchanged(expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Caller must hold the lease lock.</summary>
    private bool DeleteIfUnchanged(GuardianLease expected)
    {
        if (!File.Exists(Path))
            return false;
        var identity = new FileInfo(Path);
        identity.Refresh();
        var stamp = identity.LastWriteTimeUtc;
        var length = identity.Length;
        var current = ReadCore();
        if (current is null || current != expected)
            return false;
        // Belt and braces on top of the lock: a swap that bypassed the lock (an older build still
        // running, an external tool) changes the file identity, and the new record is kept.
        identity.Refresh();
        if (identity.LastWriteTimeUtc != stamp || identity.Length != length)
            return false;
        File.Delete(Path);
        return true;
    }

    /// <summary>
    /// Fail-closed read used under the lease lock (renewal/clear already treat "no record" as "not
    /// ours"): only a fully successful read yields a record, everything else yields null.
    /// </summary>
    private GuardianLease? ReadCore()
    {
        var result = ReadResult();
        return result.Status == GuardianLeaseReadStatus.Success ? result.Lease : null;
    }

    /// <summary>
    /// Takes the cross-process lease lock with a bounded wait. Returns null when the lock stayed
    /// busy, is inaccessible, or the lease directory does not exist (no lease was ever published),
    /// for <see cref="LockTimeout"/> at most - never blocks forever, no abandoned mutex. The helper
    /// never creates the lease directory: a SYSTEM Guardian must not pre-create it with an ACL the
    /// interactive user cannot write to. A crashed holder cannot block anybody: Windows closes its
    /// exclusive handle, so the next caller opens the same lock file successfully. The lock file
    /// itself is never deleted, so there is no delete-pending race either.
    /// </summary>
    private FileStream? TryAcquireLock()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.Read,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (elapsed.Elapsed >= LockTimeout)
                    return null;
                Thread.Sleep(LockRetryDelayMilliseconds);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
