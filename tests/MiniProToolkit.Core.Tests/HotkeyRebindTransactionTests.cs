using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// The hotkey rebind is a transaction: register the new gestures first, write the file only when all
/// of them are live, and put the previous settings/registrations back on any failure. These tests
/// inject all four side effects, so the ordering, the "never save after a failed registration" rule
/// and the honest restore-failure report are verified without Win32, a settings file or a window.
/// </summary>
public sealed class HotkeyRebindTransactionTests
{
    private static HotkeyRegistrationFailure Occupied(HotkeyAction action, string gesture, string reason = "已被其他程序占用") =>
        new(action, gesture, reason);

    [Fact]
    public void CommittedRebindRegistersVerifiesThenSavesAndNeverRestores()
    {
        var steps = new List<string>();
        var outcome = HotkeyRebindTransaction.Run(
            () =>
            {
                steps.Add("activate");
                return [];
            },
            () =>
            {
                steps.Add("verify");
                return true;
            },
            () => steps.Add("save"),
            () =>
            {
                steps.Add("restore");
                return [];
            });

        Assert.True(outcome.Committed);
        Assert.Equal(HotkeyRebindFailureKind.None, outcome.Failure);
        Assert.True(outcome.SaveCompleted);
        Assert.False(outcome.RollbackAttempted);
        // The file is written only after the new gestures are live: activate, verify, save.
        Assert.Equal(["activate", "verify", "save"], steps);
        Assert.Empty(outcome.DescribeProblem());
    }

    [Fact]
    public void FailedNewRegistrationNeverSavesAndRestoresThePreviousRegistrations()
    {
        var steps = new List<string>();
        var saves = 0;
        var outcome = HotkeyRebindTransaction.Run(
            () =>
            {
                steps.Add("activate");
                return [Occupied(HotkeyAction.SilenceMode, "Ctrl+Alt+1")];
            },
            () =>
            {
                steps.Add("verify");
                return true;
            },
            () =>
            {
                steps.Add("save");
                saves++;
            },
            () =>
            {
                steps.Add("restore");
                return [];
            });

        Assert.Equal(HotkeyRebindFailureKind.Registration, outcome.Failure);
        Assert.False(outcome.Committed);
        Assert.False(outcome.SaveCompleted);
        Assert.Equal(0, saves);
        // No verify needed (the failure list is already conclusive) and certainly no save.
        Assert.Equal(["activate", "restore"], steps);
        Assert.True(outcome.RestoredPreviousHotkeys);

        var message = outcome.DescribeProblem();
        Assert.Contains("静音模式（Ctrl+Alt+1）", message);
        Assert.Contains("已被其他程序占用", message);
        Assert.Contains("已恢复原有快捷键", message);
        Assert.Contains("设置未保存", message);
    }

    [Fact]
    public void DeferredRegistrationIsTreatedAsIncompleteAndNeverSaves()
    {
        var steps = new List<string>();
        var saves = 0;
        var outcome = HotkeyRebindTransaction.Run(
            () =>
            {
                steps.Add("activate");
                return [];
            },
            () =>
            {
                steps.Add("verify");
                return false;
            },
            () =>
            {
                steps.Add("save");
                saves++;
            },
            () =>
            {
                steps.Add("restore");
                return [];
            });

        Assert.Equal(HotkeyRebindFailureKind.Registration, outcome.Failure);
        Assert.Equal(0, saves);
        // A missing window handle reports no failing action but still must not reach the file.
        Assert.Equal(["activate", "verify", "restore"], steps);
        Assert.Contains("注册被推迟", outcome.DescribeProblem());
    }

    [Fact]
    public void PersistenceFailureRestoresThePreviousRegistrationsAndReportsTheSaveError()
    {
        var steps = new List<string>();
        var saveError = new IOException("磁盘空间不足");
        var outcome = HotkeyRebindTransaction.Run(
            () =>
            {
                steps.Add("activate");
                return [];
            },
            () =>
            {
                steps.Add("verify");
                return true;
            },
            () =>
            {
                steps.Add("save");
                throw saveError;
            },
            () =>
            {
                steps.Add("restore");
                return [];
            });

        Assert.Equal(HotkeyRebindFailureKind.Persistence, outcome.Failure);
        Assert.False(outcome.SaveCompleted);
        Assert.Same(saveError, outcome.PersistenceError);
        Assert.True(outcome.RestoredPreviousHotkeys);
        Assert.Equal(["activate", "verify", "save", "restore"], steps);

        var message = outcome.DescribeProblem();
        Assert.Contains("保存快捷键设置失败", message);
        Assert.Contains("磁盘空间不足", message);
        Assert.Contains("已恢复原有快捷键", message);
    }

    [Fact]
    public void FailedRestoreListsTheActionsAndGesturesThatAreStillMissing()
    {
        var outcome = HotkeyRebindTransaction.Run(
            () => [Occupied(HotkeyAction.BalanceMode, "Ctrl+Alt+2")],
            () => true,
            () => { },
            () => [Occupied(HotkeyAction.FullSpeed, "Ctrl+Alt+F")]);

        Assert.Equal(HotkeyRebindFailureKind.Registration, outcome.Failure);
        Assert.False(outcome.RestoredPreviousHotkeys);
        Assert.Single(outcome.RollbackFailures);

        var message = outcome.DescribeProblem();
        Assert.Contains("均衡模式（Ctrl+Alt+2）", message);
        Assert.Contains("满速（Ctrl+Alt+F）", message);
        Assert.Contains("尝试恢复原有快捷键失败", message);
        Assert.Contains("重启程序", message);
        // The restore was blocked by another process: the app must not pretend it succeeded.
        Assert.DoesNotContain("已恢复原有快捷键", message);
    }

    [Fact]
    public void ThrowingRestoreIsReportedAsAnIncompleteRestore()
    {
        var restoreError = new InvalidOperationException("窗口句柄不可用");
        var outcome = HotkeyRebindTransaction.Run(
            () => [Occupied(HotkeyAction.PerformanceMode, "Ctrl+Alt+3")],
            () => true,
            () => { },
            () => throw restoreError);

        Assert.Same(restoreError, outcome.RollbackError);
        Assert.False(outcome.RestoredPreviousHotkeys);
        var message = outcome.DescribeProblem();
        Assert.Contains("尝试恢复原有快捷键时出错", message);
        Assert.Contains("窗口句柄不可用", message);
        Assert.Contains("没有全部恢复", message);
    }

    [Fact]
    public void ThrowingActivationStillRestoresAndReportsTheActivationError()
    {
        var restoreCount = 0;
        var outcome = HotkeyRebindTransaction.Run(
            () => throw new InvalidOperationException("注册调用失败"),
            () => true,
            () => { },
            () =>
            {
                restoreCount++;
                return [];
            });

        Assert.Equal(HotkeyRebindFailureKind.Registration, outcome.Failure);
        Assert.Equal(1, restoreCount);
        Assert.True(outcome.RestoredPreviousHotkeys);
        Assert.Contains("注册调用失败", outcome.DescribeProblem());
        Assert.Contains("已恢复原有快捷键", outcome.DescribeProblem());
    }
}
