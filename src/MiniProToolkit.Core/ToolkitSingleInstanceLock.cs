namespace MiniProToolkit.Core;

/// <summary>Result of one attempt to become the only hardware-controlling instance of the toolkit.</summary>
public enum ToolkitSingleInstanceState
{
    /// <summary>This process owns the lock: it is the first instance and may open the main window.</summary>
    Acquired,

    /// <summary>Another live process owns the lock: this process must not create a window, hardware or a timer.</summary>
    AlreadyHeld,

    /// <summary>
    /// The lock could not be created or opened, so the process cannot know whether an instance is already running.
    /// The caller has to fail closed: neither a second hardware instance nor a guessed exit code is acceptable.
    /// </summary>
    Unavailable
}

/// <summary>
/// Process-wide single-instance lock of the main application. It is a pure OS handle helper: it opens no window,
/// reads no file, touches no hardware and does not know how to talk to a running instance.
/// <para>
/// The name lives in the <c>Global\</c> namespace, so the lock is machine-wide: it is visible across logon sessions
/// and across users, which is the real safety boundary because only one process on the machine may own the
/// EC/Guardian control path. Creating an object in <c>Global\</c> requires <c>SeCreateGlobalPrivilege</c>; the app
/// manifest requests <c>requireAdministrator</c>, so every real launch holds it. A caller that cannot create or open
/// the object (for example a non-elevated process) gets <see cref="ToolkitSingleInstanceState.Unavailable"/> and has
/// to fail closed instead of starting a second hardware instance.
/// </para>
/// </summary>
public sealed class ToolkitSingleInstanceLock : IDisposable
{
    /// <summary>
    /// Fixed, path-free name of the main application instance lock. <c>Global\</c> (not <c>Local\</c>) also keeps the
    /// second instance of another user/session out; that launch then finds "already running", cannot reach the
    /// per-user command pipe of the first one and exits with the documented 8/7 codes without touching hardware.
    /// </summary>
    public const string MutexName = @"Global\MiniProToolkit.Instance.v1";

    private Mutex? _mutex;
    private bool _owned;

    private ToolkitSingleInstanceLock(ToolkitSingleInstanceState state, bool recoveredAbandoned, string? failureCode)
    {
        State = state;
        RecoveredAbandoned = recoveredAbandoned;
        FailureCode = failureCode;
    }

    public ToolkitSingleInstanceState State { get; }

    /// <summary>True only when this process owns the lock and must therefore continue as the first instance.</summary>
    public bool Acquired => State == ToolkitSingleInstanceState.Acquired;

    /// <summary>
    /// True when the previous owner died without releasing the lock. The mutex is transferred to this process, so it
    /// is still the only live instance and has to continue as the first instance instead of treating this as "already
    /// running"; the caller should record it in the log.
    /// </summary>
    public bool RecoveredAbandoned { get; }

    /// <summary>Exception type name of a failed attempt, or null. Never a path, a message or user data.</summary>
    public string? FailureCode { get; }

    /// <summary>
    /// Tries to become the first instance without blocking: the mutex is created or opened, then acquired with a zero
    /// timeout. Never throws; every failure mode is reported through <see cref="State"/> so the caller can fail closed.
    /// </summary>
    public static ToolkitSingleInstanceLock TryAcquire(string? mutexName = null)
    {
        var name = string.IsNullOrWhiteSpace(mutexName) ? MutexName : mutexName.Trim();
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            var abandoned = false;
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The owner died without releasing. The abandoned mutex is granted to the caller, so this process is
                // the single live instance; treating it as "already running" would leave the app unusable.
                acquired = true;
                abandoned = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return new ToolkitSingleInstanceLock(ToolkitSingleInstanceState.AlreadyHeld, false, null);
            }

            var result = new ToolkitSingleInstanceLock(ToolkitSingleInstanceState.Acquired, abandoned, null);
            result._mutex = mutex;
            result._owned = true;
            return result;
        }
        catch (Exception ex)
        {
            // Fail closed: an unknown lock state must never be turned into a second hardware-controlling instance.
            try
            {
                mutex?.Dispose();
            }
            catch
            {
                // Ignored: the attempt already failed, closing a handle cannot change that.
            }
            return new ToolkitSingleInstanceLock(ToolkitSingleInstanceState.Unavailable, false, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Releases the ownership (when this process became the first instance) and closes the handle. Idempotent, never
    /// throws: a lock that was never acquired, or a release from a foreign thread, must not break shutdown.
    /// </summary>
    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
            return;
        try
        {
            if (_owned)
                mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread (or already released): closing the handle is the only safe remainder, and the
            // process is exiting anyway.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _owned = false;
            mutex.Dispose();
        }
    }
}
