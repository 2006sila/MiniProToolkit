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

        var temporary = Path + $".{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new GuardianLease(processId, controlMode, now, processStartUtc), JsonOptions));
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
    /// reader sees either the old or the new record, and a failed read reports "no lease" which the
    /// recovery coordinator treats as "nothing to recover". Taking the lock here would let a busy
    /// lock look like a missing lease.
    /// </summary>
    public GuardianLease? Read() => ReadCore();

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

    private GuardianLease? ReadCore()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            return JsonSerializer.Deserialize<GuardianLease>(File.ReadAllText(Path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
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
