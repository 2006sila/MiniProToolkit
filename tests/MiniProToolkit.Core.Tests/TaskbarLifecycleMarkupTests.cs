using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Source guards for the taskbar overlay lifecycle: an overlay created between two sampling passes
/// (enable preview, Explorer restart, 30 s discovery, display change) must take the open dialog's
/// full preview buffer and be painted from the newest kept sample in the same turn, and the taskbar
/// settings dialog must stay a single editor. These checks read source text only: they never
/// construct a WPF window, open a log file, touch hardware or start a GUI.
/// </summary>
public sealed class TaskbarLifecycleMarkupTests
{
    private static string FindAppSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests", "MiniProToolkit.Core.Tests.csproj");
            var candidate = Path.Combine(directory.FullName, "src", "MiniProToolkit.App", fileName);
            if (File.Exists(project) && File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"Could not locate the App source {fileName} above the test directory.");
    }

    private static string ReadMainWindow() => File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));

    private static string SourceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker {endMarker} after {startMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string EnsureTaskbars(string source) =>
        SourceBetween(source, "private void EnsureTaskbars()", "private void OpenTaskbarSettingsFromMenu()");

    private static string TaskbarSettingsClick(string source) =>
        SourceBetween(source, "private void TaskbarSettingsClick(", "private void DisplaySourceSettingsClick(");

    private static string PreviewTaskbarSettings(string source) =>
        SourceBetween(source, "private void PreviewTaskbarSettings(TaskbarDisplaySettings settings)",
            "private void TaskbarSettingsClick(");

    [Fact]
    public void NewTaskbarWindowsTakeTheSharedPreviewBufferAndTheNewestKeptSample()
    {
        var source = ReadMainWindow();
        var ensure = EnsureTaskbars(source);

        // Exactly one creation site, and it prefers the preview buffer over the persisted settings:
        // an overlay mounted while the dialog is open must not revert to the saved colours/layout.
        Assert.Equal(1, CountOccurrences(source, "new TaskbarStatusWindow("));
        Assert.Contains(
            "var window = new TaskbarStatusWindow(_taskbarPreviewSettings ?? _taskbarSettings, handle);",
            ensure);

        // The constructor applies the settings before the window is shown, and the kept sample is
        // painted after Show() (UpdateSnapshot only renders a visible window) but still inside the
        // same EnsureTaskbars turn.
        var created = ensure.IndexOf("new TaskbarStatusWindow(", StringComparison.Ordinal);
        var shown = ensure.IndexOf("window.Show();", StringComparison.Ordinal);
        var painted = ensure.IndexOf("window.UpdateSnapshot(", StringComparison.Ordinal);
        var marked = ensure.IndexOf("_taskbarDiscovery.MarkRefreshed(", StringComparison.Ordinal);
        Assert.True(created >= 0 && shown > created && painted > shown,
            "The preview settings must be applied at construction and the snapshot painted right after Show().");
        Assert.True(marked > painted, "The immediate sample must be painted before EnsureTaskbars returns.");

        // Only the kept snapshot is replayed, never a second synchronous sample, and it is guarded.
        Assert.Contains("if (_latestSnapshot is { } snapshot)", ensure);
        Assert.Contains("window.UpdateSnapshot(snapshot, _demo || _lastGuardianRunning, safeManual, safeRpmOrCurve,", ensure);
        Assert.Contains("!_demo && snapshot.Error is null);", ensure);

        // The gate tuple comes from the same helper the sampling pass uses, so creation cannot drift
        // from the pass; the creation path itself carries no sampling/hardware read.
        Assert.Contains("var (safeManual, safeRpmOrCurve) = ComputeTaskbarFanGates(snapshot);", ensure);
        Assert.DoesNotContain("snapshot.RpmFeedbackState != FanRpmFeedbackState.Fault", ensure);
        Assert.DoesNotContain("FanFault is null", ensure);
        Assert.DoesNotContain("RefreshSnapshotAsync", ensure);
        Assert.DoesNotContain("GuardianAvailability", ensure);
        Assert.DoesNotContain("Task.Run(", ensure);
        Assert.Equal(1, CountOccurrences(ensure, "NativeMethods.EnumerateTaskbars()"));

        // A late preview/refresh callback may not resurrect a strip during teardown.
        Assert.Contains("if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)", ensure);
    }

    [Fact]
    public void SamplingPassAndBetweenPassCreationShareOneGateFormula()
    {
        var source = ReadMainWindow();
        var pass = SourceBetween(source, "private async Task<bool> RefreshSnapshotAsync(", "private void TelemetryChartSizeChanged(");
        var ensure = EnsureTaskbars(source);
        var helper = SourceBetween(source,
            "private (bool SafeManual, bool SafeRpmOrCurve) ComputeTaskbarFanGates(HardwareSnapshot snapshot)",
            "private void EnsureTaskbars()");

        Assert.Contains("var (safeManual, safeRpmOrCurve) = ComputeTaskbarFanGates(state.snapshot);", pass);
        Assert.Contains("ComputeTaskbarFanGates(snapshot)", ensure);
        // The safety terms live in the helper only; the two call sites no longer duplicate them.
        Assert.Contains("var safeManual = !_demo && snapshot.Error is null && snapshot.FanFault is null &&", helper);
        Assert.Contains("snapshot.RpmFeedbackState != FanRpmFeedbackState.Fault && !_restoreRequired &&", helper);
        Assert.Contains("_curveController?.RecoveryRequired != true && _hardware?.IsEmergencyFullSpeed != true &&", helper);
        Assert.Contains("_curveController?.IsEnabled != true;", helper);
        Assert.Contains("var safeRpmOrCurve = safeManual && snapshot.CpuTemperatureC is < 90 &&", helper);
        // The duplicated gate blocks are gone: one tuple helper is the only definition, the pass and
        // the between-pass creation are its only call sites (the tray menu keeps its own separate
        // guardian-parameterized copy).
        Assert.Equal(1, CountOccurrences(source, "ComputeTaskbarFanGates(HardwareSnapshot snapshot)"));
        Assert.Equal(1, CountOccurrences(pass, "ComputeTaskbarFanGates("));
        Assert.Equal(1, CountOccurrences(ensure, "ComputeTaskbarFanGates("));
        Assert.DoesNotContain("var safeManual =", pass);
        Assert.DoesNotContain("var safeRpmOrCurve =", pass);
        // The pass still hands the taskbar the same variables and the same refresh interval.
        Assert.Contains("taskbar.UpdateSnapshot(state.snapshot, state.guardianRunning, safeManual, safeRpmOrCurve,", pass);
        Assert.Contains("TimeSpan.FromSeconds(_taskbarSettings.RefreshIntervalSeconds)", pass);
    }

    [Fact]
    public void PreviewBufferIsPublishedBeforeCreationAndClearedOnEveryExitPath()
    {
        var source = ReadMainWindow();
        Assert.Contains("private TaskbarDisplaySettings? _taskbarPreviewSettings;", source);

        var preview = PreviewTaskbarSettings(source);
        var published = preview.IndexOf("_taskbarPreviewSettings = settings;", StringComparison.Ordinal);
        var creation = preview.IndexOf("EnsureTaskbars();", StringComparison.Ordinal);
        Assert.True(published >= 0, "The preview must publish the full edit buffer.");
        Assert.True(published < preview.IndexOf("_taskbarPreviewEnabled = settings.Enabled;", StringComparison.Ordinal),
            "The full buffer must be published before the on/off override it belongs to.");
        Assert.True(published < creation,
            "A strip created by the enabled preview must already see the full buffer.");
        // Disabled preview still publishes the buffer (the switch is part of it) and only closes.
        Assert.Contains("if (!settings.Enabled)", preview);
        Assert.DoesNotContain("_taskbarSettingsStore", preview);
        Assert.DoesNotContain("_taskbarSettings =", preview);

        var handler = TaskbarSettingsClick(source);
        var closed = handler.IndexOf("editor.ShowDialog();", StringComparison.Ordinal);
        var save = handler.IndexOf("_taskbarSettingsStore.Save(settings);", StringComparison.Ordinal);
        var clearedAfterClose = handler.IndexOf("_taskbarPreviewSettings = null;", closed, StringComparison.Ordinal);
        Assert.True(closed >= 0 && save > closed, "The save path must follow the closed dialog.");
        Assert.True(clearedAfterClose > closed && clearedAfterClose < save,
            "Cancel/close (and a save) drops the buffer; a save must not recreate from the preview.");

        // A failed save pushes the on-disk settings back through the same preview, then drops both
        // preview fields before reporting the failure.
        var catchIndex = handler.IndexOf("catch (Exception ex)", save, StringComparison.Ordinal);
        var revert = handler.IndexOf("PreviewTaskbarSettings(_taskbarSettings);", catchIndex, StringComparison.Ordinal);
        var messageBox = handler.IndexOf("MessageBox.Show(", catchIndex, StringComparison.Ordinal);
        var clearedAfterRevert = handler.IndexOf("_taskbarPreviewSettings = null;", revert, StringComparison.Ordinal);
        Assert.True(revert > catchIndex && messageBox > revert, "The revert must run before the failure is reported.");
        Assert.True(clearedAfterRevert > revert && clearedAfterRevert < messageBox,
            "The rolled-back preview buffer must be cleared after it was applied.");
        Assert.Equal(2, CountOccurrences(handler, "_taskbarPreviewSettings = null;"));
    }

    [Fact]
    public void TaskbarSettingsDialogIsASingleEditorClosedWithTheApp()
    {
        var source = ReadMainWindow();
        Assert.Contains("private TaskbarSettingsWindow? _taskbarSettingsDialog;", source);

        var handler = TaskbarSettingsClick(source);
        // One dialog type, one ShowDialog call and a guard before construction: the taskbar overlay
        // menu is clickable inside the modal dispatcher loop, so a second click must activate the
        // existing editor instead of building a second one.
        Assert.Equal(1, CountOccurrences(source, "new TaskbarSettingsWindow(_taskbarSettings, _hotkeySettings, PreviewTaskbarSettings)"));
        Assert.Equal(1, CountOccurrences(handler, "ShowDialog()"));
        var create = handler.IndexOf("new TaskbarSettingsWindow(_taskbarSettings, _hotkeySettings, PreviewTaskbarSettings)", StringComparison.Ordinal);
        var guard = handler.IndexOf("if (_taskbarSettingsDialog is { } openEditor)", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < create, "The single-editor guard must run before a dialog is created.");
        Assert.Contains("openEditor.Activate();", handler);

        // The field is published before ShowDialog and only the same instance may clear it.
        var publish = handler.IndexOf("_taskbarSettingsDialog = editor;", StringComparison.Ordinal);
        var dialog = handler.IndexOf("editor.ShowDialog();", StringComparison.Ordinal);
        Assert.True(publish > create && publish < dialog, "A menu click inside ShowDialog must find the editor.");
        var clear = handler.IndexOf("if (ReferenceEquals(_taskbarSettingsDialog, editor))", StringComparison.Ordinal);
        Assert.True(clear > dialog, "The field is cleared in a finally after the dialog closed.");
        Assert.Contains("_taskbarSettingsDialog = null;", handler);

        // Entry guard for a shutdown that already started, and the exit sequence closes the open
        // editor (clearing the field first so no late click can build another one).
        Assert.Contains("if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)", handler);
        var teardown = SourceBetween(source, "// Phase 3, irreversible UI teardown.",
            "// Close the card's settings dialog first");
        Assert.Contains("if (_taskbarSettingsDialog is { } taskbarDialog)", teardown);
        Assert.Contains("_taskbarSettingsDialog = null;", teardown);
        Assert.Contains("taskbarDialog.Close();", teardown);

        // The dialog's own cancel path still restores the opened settings through the preview; the
        // single-editor guard did not replace that rollback.
        var dialogSource = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        Assert.Contains("_preview(_initialSettings);", dialogSource);
        Assert.Contains("if (DialogResult != true && _preview is not null)", dialogSource);
    }
}
