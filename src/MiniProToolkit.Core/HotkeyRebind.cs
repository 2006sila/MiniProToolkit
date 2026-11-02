namespace MiniProToolkit.Core;

/// <summary>Why a hotkey rebind was not committed.</summary>
public enum HotkeyRebindFailureKind
{
    /// <summary>Every new gesture is live and the settings file was written.</summary>
    None = 0,

    /// <summary>At least one new gesture could not be registered (or registration was deferred).</summary>
    Registration = 1,

    /// <summary>The new gestures were live but the settings file could not be written.</summary>
    Persistence = 2
}

/// <summary>
/// Result of one transactional hotkey rebind. It only describes what happened; the side effects
/// (registration, file write, restore) are injected by the caller, so the whole ordering rule is
/// unit-testable without touching Win32, the settings file or a window.
/// </summary>
public sealed class HotkeyRebindOutcome
{
    private HotkeyRebindOutcome(
        HotkeyRebindFailureKind failure,
        bool saveCompleted,
        bool rollbackAttempted,
        IReadOnlyList<HotkeyRegistrationFailure> activationFailures,
        IReadOnlyList<HotkeyRegistrationFailure> rollbackFailures,
        Exception? activationError,
        Exception? persistenceError,
        Exception? rollbackError)
    {
        Failure = failure;
        SaveCompleted = saveCompleted;
        RollbackAttempted = rollbackAttempted;
        ActivationFailures = activationFailures;
        RollbackFailures = rollbackFailures;
        ActivationError = activationError;
        PersistenceError = persistenceError;
        RollbackError = rollbackError;
    }

    public HotkeyRebindFailureKind Failure { get; }

    /// <summary>True only when the new gestures are live and the settings file was written.</summary>
    public bool Committed => Failure == HotkeyRebindFailureKind.None;

    /// <summary>The save call returned normally. False means the file must be treated as unchanged.</summary>
    public bool SaveCompleted { get; }

    /// <summary>The previous settings were pushed back (memory and registrations) at least once.</summary>
    public bool RollbackAttempted { get; }

    /// <summary>Actions the new settings could not register, with their gesture and fixed reason.</summary>
    public IReadOnlyList<HotkeyRegistrationFailure> ActivationFailures { get; }

    /// <summary>Actions the previous settings could not register again while rolling back.</summary>
    public IReadOnlyList<HotkeyRegistrationFailure> RollbackFailures { get; }

    public Exception? ActivationError { get; }

    public Exception? PersistenceError { get; }

    public Exception? RollbackError { get; }

    /// <summary>True when the restore ran and every previous gesture is live again.</summary>
    public bool RestoredPreviousHotkeys =>
        RollbackAttempted && RollbackError is null && RollbackFailures.Count == 0;

    internal static HotkeyRebindOutcome Commit() =>
        new(HotkeyRebindFailureKind.None, saveCompleted: true, rollbackAttempted: false, [], [], null, null, null);

    internal static HotkeyRebindOutcome RegistrationFailed(
        IReadOnlyList<HotkeyRegistrationFailure> activationFailures,
        Exception? activationError,
        IReadOnlyList<HotkeyRegistrationFailure> rollbackFailures,
        Exception? rollbackError) =>
        new(HotkeyRebindFailureKind.Registration, saveCompleted: false, rollbackAttempted: true,
            activationFailures, rollbackFailures, activationError, null, rollbackError);

    internal static HotkeyRebindOutcome PersistenceFailed(
        Exception persistenceError,
        IReadOnlyList<HotkeyRegistrationFailure> rollbackFailures,
        Exception? rollbackError) =>
        new(HotkeyRebindFailureKind.Persistence, saveCompleted: false, rollbackAttempted: true,
            [], rollbackFailures, null, persistenceError, rollbackError);

    /// <summary>
    /// User-facing failure text. It never claims success: when the previous gestures could not be
    /// registered again, the actions and combinations that are still missing are listed together
    /// with the explicit next step. Empty for a committed rebind.
    /// </summary>
    public string DescribeProblem()
    {
        var restore = DescribeRestore();
        switch (Failure)
        {
            case HotkeyRebindFailureKind.Registration:
                var reason = ActivationError is { } activationError
                    ? $"应用新快捷键时出错：{activationError.GetBaseException().Message}"
                    : ActivationFailures.Count > 0
                        ? $"新快捷键无法生效：{DescribeFailures(ActivationFailures)}"
                        : "新快捷键未能全部注册生效（窗口尚未就绪，注册被推迟）";
                return $"{reason}。{restore}";
            case HotkeyRebindFailureKind.Persistence:
                var saveError = PersistenceError?.GetBaseException().Message ?? "未知错误";
                return $"保存快捷键设置失败：{saveError}。{restore}";
            default:
                return string.Empty;
        }
    }

    /// <summary>Log-safe one-line summary: fixed status words plus action labels and gestures only.</summary>
    public string DescribeForLog()
    {
        if (Committed)
            return "global hotkey rebind committed (register, save; failures 0)";
        var status = Failure == HotkeyRebindFailureKind.Registration ? "registration" : "persistence";
        var activation = ActivationFailures.Count > 0
            ? $" activation=[{DescribeFailures(ActivationFailures)}]"
            : string.Empty;
        var rollback = RollbackError is not null
            ? " rollback=error"
            : RollbackAttempted
                ? RollbackFailures.Count > 0
                    ? $" rollback=[{DescribeFailures(RollbackFailures)}]"
                    : " rollback=restored"
                : " rollback=skipped";
        return $"global hotkey rebind rejected ({status}); save={SaveCompleted}{activation}{rollback}";
    }

    private string DescribeRestore()
    {
        if (!RollbackAttempted)
            return "原有快捷键保持不变，设置未保存。";
        if (RollbackError is { } rollbackError)
            return $"尝试恢复原有快捷键时出错：{rollbackError.GetBaseException().Message}。" +
                   "原有快捷键可能没有全部恢复，请重新设置快捷键或重启程序。";
        if (RollbackFailures.Count > 0)
            return $"尝试恢复原有快捷键失败：{DescribeFailures(RollbackFailures)}。" +
                   "这些组合当前不可用，请重新设置快捷键或重启程序后再试。";
        return "已恢复原有快捷键，设置未保存。";
    }

    private static string DescribeFailures(IReadOnlyList<HotkeyRegistrationFailure> failures) =>
        string.Join("；", failures.Select(failure =>
            $"{HotkeySettings.LabelFor(failure.Action)}（{failure.Gesture}）→{failure.Reason}"));
}

/// <summary>
/// Transaction around a hotkey rebind. The old flow saved the new file first and then released every
/// live registration before trying the new gestures, so one combination owned by another process
/// dropped all working hotkeys while the user was still told the save succeeded.
/// <para>
/// The rule here is fail closed: register the new gestures first, write the settings file only when
/// every one of them is live, and push the previous settings/registrations back on any failure. A
/// deferred registration (no window handle yet) counts as unfinished, so nothing is written. The
/// restore can itself fail when another process grabbed a combination in between; that is reported
/// as a failure with the exact actions/gestures, never as a success.
/// </para>
/// All four side effects are injected, so the ordering is verified in unit tests without Win32, a
/// settings file, a window or a real global hotkey.
/// </summary>
public static class HotkeyRebindTransaction
{
    /// <summary>
    /// Runs one rebind.
    /// </summary>
    /// <param name="activate">Replaces the live registrations with the new settings and returns the actions that failed.</param>
    /// <param name="isFullyRegistered">Reports whether every configured action is actually registered right now.</param>
    /// <param name="save">Persists the new settings. Only called after the new registrations are fully live.</param>
    /// <param name="restore">Puts the previous settings and registrations back; returns the actions that could not be restored.</param>
    public static HotkeyRebindOutcome Run(
        Func<IReadOnlyList<HotkeyRegistrationFailure>> activate,
        Func<bool> isFullyRegistered,
        Action save,
        Func<IReadOnlyList<HotkeyRegistrationFailure>> restore)
    {
        ArgumentNullException.ThrowIfNull(activate);
        ArgumentNullException.ThrowIfNull(isFullyRegistered);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(restore);

        IReadOnlyList<HotkeyRegistrationFailure> activationFailures;
        try
        {
            activationFailures = activate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FailRegistration([], ex, restore);
        }
        // A deferred registration reports no failing action (the handle is simply gone), so the
        // independent fully-registered check is what keeps it from being saved.
        if (activationFailures.Count == 0 && isFullyRegistered())
        {
            try
            {
                save();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return FailPersistence(ex, restore);
            }
            return HotkeyRebindOutcome.Commit();
        }
        return FailRegistration(activationFailures, null, restore);
    }

    private static HotkeyRebindOutcome FailRegistration(
        IReadOnlyList<HotkeyRegistrationFailure> activationFailures,
        Exception? activationError,
        Func<IReadOnlyList<HotkeyRegistrationFailure>> restore)
    {
        var (rollbackFailures, rollbackError) = TryRestore(restore);
        return HotkeyRebindOutcome.RegistrationFailed(activationFailures, activationError, rollbackFailures, rollbackError);
    }

    private static HotkeyRebindOutcome FailPersistence(
        Exception persistenceError,
        Func<IReadOnlyList<HotkeyRegistrationFailure>> restore)
    {
        var (rollbackFailures, rollbackError) = TryRestore(restore);
        return HotkeyRebindOutcome.PersistenceFailed(persistenceError, rollbackFailures, rollbackError);
    }

    private static (IReadOnlyList<HotkeyRegistrationFailure> Failures, Exception? Error) TryRestore(
        Func<IReadOnlyList<HotkeyRegistrationFailure>> restore)
    {
        try
        {
            return (restore(), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ([], ex);
        }
    }
}
