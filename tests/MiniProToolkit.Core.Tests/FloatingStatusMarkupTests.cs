using System.Xml.Linq;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Static markup and source guards for the desktop floating window. Deliberately no WPF loading,
/// window construction, screen access or hardware call: these checks only prove that the markup
/// parses, every interactive control has an accessible name and the wiring the acceptance criteria
/// depend on is still present in the source. Real keyboard/screen-reader/drag behaviour stays a
/// target-machine smoke check.
/// </summary>
public sealed class FloatingStatusMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> InteractiveControls =
        ["Button", "CheckBox", "Slider", "TextBox", "ComboBox", "ListBox", "RadioButton"];

    private static XDocument LoadMarkup(string fileName) =>
        XDocument.Load(FindAppSource(fileName), LoadOptions.SetLineInfo);

    private static XElement FindNamed(XDocument document, string name) =>
        Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == name);

    private static void AssertHasName(XElement element) =>
        Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.Name")),
            $"{element.Name.LocalName} {(string?)element.Attribute(Xaml + "Name")} needs an explicit accessible name.");

    private static string SourceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker {endMarker} after {startMarker}");
        return source[start..end];
    }

    [Fact]
    public void FloatingSettingsMarkupParsesAndEveryInteractiveControlIsNamed()
    {
        var document = LoadMarkup("FloatingStatusSettingsWindow.xaml");
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Equal(Presentation + "Window", root.Name);
        Assert.Equal("MiniProToolkit.App.FloatingStatusSettingsWindow", (string?)root.Attribute(Xaml + "Class"));

        var controls = root.Descendants()
            .Where(element => element.Name.Namespace == Presentation &&
                              InteractiveControls.Contains(element.Name.LocalName)).ToArray();
        Assert.NotEmpty(controls);
        foreach (var control in controls)
            AssertHasName(control);
    }

    [Fact]
    public void FloatingSettingsWindowExposesTheIndependentSettingsEntryPoints()
    {
        var document = LoadMarkup("FloatingStatusSettingsWindow.xaml");
        // Enable is its own opt-in checkbox (default off is set in code from the saved settings).
        var enabled = FindNamed(document, "EnabledCheck");
        Assert.Equal(Presentation + "CheckBox", enabled.Name);
        AssertHasName(enabled);

        foreach (var name in new[]
                 {
                     "EnabledCheck", "CompactCheck", "TopmostCheck", "LockedCheck", "ItemsList",
                     "LayoutModeCombo", "LayoutModeHint", "FontFamilyText", "FontSizeText",
                     "TextColorTargetRadio", "BackgroundColorTargetRadio", "ColorSwatches",
                     "AdvancedColorText", "BackgroundOpacitySlider", "PositionStatusText", "ValidationText"
                 })
            AssertHasName(FindNamed(document, name));

        // Four layouts (A/C/D/E) are offered by tag, exactly like the taskbar dialog.
        var combo = FindNamed(document, "LayoutModeCombo");
        var tags = combo.Descendants(Presentation + "ComboBoxItem")
            .Select(item => (string?)item.Attribute("Tag") ?? string.Empty).ToArray();
        Assert.Equal<string>(["StackedPairs", "Inline", "AdaptiveColumns", "LabelOverValue"], tags);

        // Ordering has exactly one interaction pair: a per-row drag thumb plus Ctrl+Up / Ctrl+Down.
        // The old up/down buttons are gone (they duplicated the taskbar's physical direction buttons).
        Assert.DoesNotContain(document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Click") is "MoveUpClick" or "MoveDownClick");
        var thumb = Assert.Single(document.Descendants(Presentation + "Thumb"));
        Assert.Equal("ItemDragStarted", (string?)thumb.Attribute("DragStarted"));
        Assert.Equal("ItemDragDelta", (string?)thumb.Attribute("DragDelta"));
        Assert.Equal("ItemDragCompleted", (string?)thumb.Attribute("DragCompleted"));
        // The Enabled checkbox is a sibling of the handle, so toggling never reorders.
        var rowCheckbox = Assert.Single(thumb.Parent!.Elements(Presentation + "CheckBox"));
        Assert.Equal("{Binding Enabled, UpdateSourceTrigger=PropertyChanged}",
            (string?)rowCheckbox.Attribute("IsChecked"));
        Assert.Equal("ItemsListPreviewKeyDown", (string?)FindNamed(document, "ItemsList").Attribute("PreviewKeyDown"));

        // The single colour card keeps the shared target/palette/advanced-hex semantics.
        Assert.Equal(2, document.Descendants(Presentation + "RadioButton")
            .Count(element => (string?)element.Attribute("GroupName") == "FloatingColorTarget"));
        Assert.True(document.Descendants(Presentation + "Button")
            .Count(element => (string?)element.Attribute("Click") == "ColorSwatchClick") >= 12);
        Assert.Single(document.Descendants(Presentation + "Expander"),
            element => (string?)element.Attribute(Xaml + "Name") == "AdvancedColorExpander");
        // No numeric position editor: the card is dragged, not configured by coordinates.
        Assert.DoesNotContain(document.Descendants(Presentation + "TextBox"), element =>
            (string?)element.Attribute(Xaml + "Name") is { } name && name.Contains("Position", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Descendants(Presentation + "ComboBox"), element =>
            (string?)element.Attribute(Xaml + "Name") is { } name && name.Contains("Position", StringComparison.Ordinal));
    }

    [Fact]
    public void CardMarkupIsBorderlessNonTaskbarAndHasAnExplicitDragHandle()
    {
        var document = LoadMarkup("FloatingStatusWindow.xaml");
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Equal("None", (string?)root.Attribute("WindowStyle"));
        Assert.Equal("NoResize", (string?)root.Attribute("ResizeMode"));
        Assert.Equal("False", (string?)root.Attribute("ShowInTaskbar"));
        Assert.Equal("False", (string?)root.Attribute("ShowActivated"));
        Assert.Equal("True", (string?)root.Attribute("AllowsTransparency"));

        var handle = FindNamed(document, "DragHandle");
        Assert.Equal("DragHandleMouseLeftButtonDown", (string?)handle.Attribute("MouseLeftButtonDown"));
        AssertHasName(handle);
        Assert.False(string.IsNullOrWhiteSpace((string?)handle.Attribute("AutomationProperties.HelpText")));

        var layout = FindNamed(document, "StatusLayoutRoot");
        Assert.Equal("StatusMouseLeftButtonUp", (string?)layout.Attribute("MouseLeftButtonUp"));
        AssertHasName(layout);
        Assert.False(string.IsNullOrWhiteSpace((string?)layout.Attribute("AutomationProperties.HelpText")));

        // The content lives in a scroll viewer: when the work area is smaller than the card the
        // window is capped at the work area and the remaining items stay reachable by scrolling
        // instead of the window growing off screen.
        var scroller = FindNamed(document, "StatusContentScroller");
        Assert.Equal(Presentation + "ScrollViewer", scroller.Name);
        Assert.Equal("Auto", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        AssertHasName(scroller);
        Assert.NotEmpty(layout.Ancestors(Presentation + "ScrollViewer"));

        // The card itself exposes no hardware/fan control: the only interactive surfaces are the
        // drag handle and the status cells (the menu is built in code and routed to MainWindow).
        Assert.Empty(document.Descendants(Presentation + "Button"));
        Assert.Empty(document.Descendants(Presentation + "CheckBox"));
    }

    [Fact]
    public void CardDragIsLeftButtonOnlyAndRespectsTheLock()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        var drag = SourceBetween(source, "private void DragHandleMouseLeftButtonDown(", "/// A double click opens the main window exactly once.");
        Assert.Contains("if (e.ChangedButton != MouseButton.Left) return;", drag);
        Assert.Contains("if (_settings.IsLocked)", drag);
        Assert.Contains("DragMove();", drag);
        // The position is recorded once, after the drag loop ends (never per mouse move / disk write).
        Assert.Contains("RecordDraggedPosition();", drag);
        Assert.Contains("PositionChanged?.Invoke(_settings);", source);
        // The end of the drag is the only place that clamps/snaps, using the real physical rectangle.
        Assert.Contains("FloatingStatusLayout.TryComputeSnappedPlacement(", source);
        Assert.Contains("scale, _settings.SnapToEdges, out var placement, out _", source);
    }

    [Fact]
    public void CardDoubleClickOpensOnceAndSingleClickStaysInert()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        var handler = SourceBetween(source, "private void StatusMouseLeftButtonUp(", "private void BuildContextMenu()");
        Assert.Contains("e.ClickCount != 2", handler);
        Assert.Contains("OpenRequested?.Invoke();", handler);
        // A single click must not run any action: the handler returns before raising anything.
        Assert.DoesNotContain("ClickCount == 1", handler);
    }

    [Fact]
    public void CardContextMenuOffersSettingsLockTopmostHideAndExit()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        foreach (var label in new[] { "打开主窗口", "桌面悬浮窗设置…", "锁定位置", "总在最前", "隐藏悬浮窗", "退出" })
            Assert.Contains(label, source);
        Assert.Contains("SettingsRequested?.Invoke()", source);
        Assert.Contains("LockRequested?.Invoke(!_settings.IsLocked)", source);
        Assert.Contains("TopmostRequested?.Invoke(!_settings.Topmost)", source);
        Assert.Contains("HideRequested?.Invoke()", source);
        Assert.Contains("ExitRequested?.Invoke()", source);
    }

    [Fact]
    public void CardReusesTheCoreFormatterAndLayoutAndNeverTouchesHardware()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        Assert.Contains("TaskbarMetricFormatter.Format(item, snapshot, _guardianRunning)", source);
        Assert.Contains("TaskbarItemLayout.Build(_settings.LayoutMode, metrics, MaximumContentWidthDip, _fontSize, vertical: false)", source);
        Assert.Contains("TaskbarGridLayout.Project(plan, vertical: false)", source);
        Assert.Contains("cell.Style == TaskbarCellStyle.Colon ? metric.ColonText : metric.Text", source);
        Assert.Contains("Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular)", source);
        Assert.Contains("AutomationProperties.SetName(block,", source);
        // Read-only display: no hardware/EC/fan/Guardian/lease access from the card.
        foreach (var forbidden in new[]
                 {
                     "PowerSwitchHardware", "LibreHardwareMonitor", "SetFanDuty", "SetFixedRpm",
                     "SetPowerMode", "GuardianLease", "FanCurveController", "Wmi"
                 })
            Assert.DoesNotContain(forbidden, source);
        Assert.DoesNotContain("DispatcherTimer", source);
        Assert.DoesNotContain("new Thread(", source);
        Assert.DoesNotContain("Task.Run(", source);
    }

    [Fact]
    public void MainWindowKeepsTheCardIndependentFromTheTaskbarStrip()
    {
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        // Separate field, separate store, never part of the taskbar overlay collection.
        Assert.Contains("private FloatingStatusWindow? _floatingStatusWindow;", main);
        Assert.Contains("private readonly FloatingStatusSettingsStore _floatingSettingsStore = new();", main);
        Assert.DoesNotContain("_taskbarWindows.Add(_floatingStatusWindow", main);
        Assert.DoesNotContain("_taskbarWindows.Add(window);\n        _floatingStatusWindow", main);

        // The taskbar settings path (save + recreate) must not mention the floating card.
        var taskbarSettings = SourceBetween(main, "private void TaskbarSettingsClick(",
            "private void DisplaySourceSettingsClick(");
        Assert.DoesNotContain("Floating", taskbarSettings);

        // The floating window helper block never enumerates/mounts appbars or taskbar windows.
        var floatingBlock = SourceBetween(main, "private FloatingStatusWindow CreateFloatingWindow()",
            "private void ScheduleTaskbarRefresh(");
        Assert.DoesNotContain("EnsureTaskbars", floatingBlock);
        Assert.DoesNotContain("_taskbarWindows", floatingBlock);
        Assert.DoesNotContain("EnumerateTaskbars", floatingBlock);

        // The card is fed from the same sample pass but outside the taskbar strip block/interval,
        // with the same gate tuple the taskbar strip receives. The strip block now follows the
        // persisted taskbar switch instead of a main-window checkbox.
        var taskbarUpdate = main.IndexOf("if (TaskbarStripEnabled &&", StringComparison.Ordinal);
        var floatingUpdate = main.IndexOf("if (_floatingStatusWindow is { } floatingCard)", StringComparison.Ordinal);
        Assert.True(taskbarUpdate >= 0, "The taskbar update block must still be present.");
        Assert.Contains("private bool TaskbarStripEnabled => _taskbarPreviewEnabled ?? _taskbarSettings.Enabled;", main);
        Assert.True(floatingUpdate > taskbarUpdate,
            "The floating update must stay outside (after) the taskbar strip block.");
        Assert.Contains("floatingCard.UpdateSnapshot(state.snapshot, state.guardianRunning, cardSafeManual, cardSafeRpmOrCurve,",
            main);

        // Enabled is durable in the floating store (settings dialog and context menu), not runtime-only.
        Assert.Contains("_floatingSettingsStore.Save(updated);", main);
        Assert.Contains("_floatingSettingsStore.Save(settings);", main);
        // The overview checkbox is gone for good: no leftover code-behind field/guard references.
        Assert.DoesNotContain("FloatingToggle", main);
        Assert.DoesNotContain("SyncFloatingToggle", main);
        Assert.DoesNotContain("_synchronizingFloatingToggle", main);

        // Display/DPI changes re-clamp the card but a shell restart leaves it alone.
        Assert.Contains("if (trigger != TaskbarRefreshTrigger.ShellRecreated)", main);
        Assert.Contains("_floatingStatusWindow?.EnsureOnVisibleMonitor();", main);

        // Main window close closes the card explicitly: a plain Close would be turned into a hide
        // request by the card's own OnClosing. The OSD/taskbar teardown order is unchanged otherwise.
        Assert.Contains("floatingCard.CloseForShutdown();", main);
        Assert.DoesNotContain("_floatingStatusWindow?.Close();", main);
    }

    [Fact]
    public void MainWindowMarkupKeepsTheFloatingEnableSwitchInTheSidebarSettingsOnly()
    {
        var document = LoadMarkup("MainWindow.xaml");
        // The overview card no longer carries an enable switch: the sidebar settings dialog is the
        // only place that turns the card on and off, so no second, conflicting toggle can exist.
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "FloatingToggle");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("Checked") == "FloatingToggleChanged");

        // That single switch is the dialog's 启用 checkbox, and it only previews until 保存.
        var settings = LoadMarkup("FloatingStatusSettingsWindow.xaml");
        var enabled = FindNamed(settings, "EnabledCheck");
        Assert.Equal(Presentation + "CheckBox", enabled.Name);
        Assert.Equal("PreviewChanged", (string?)enabled.Attribute("Checked"));
        AssertHasName(enabled);

        // The full settings entry stays in the sidebar, directly next to the taskbar entry.
        var button = FindNamed(document, "NavFloatingButton");
        Assert.Equal(Presentation + "Button", button.Name);
        Assert.Equal("FloatingSettingsClick", (string?)button.Attribute("Click"));
        AssertHasName(button);
        Assert.Equal(FindNamed(document, "NavTaskbarButton").Parent, button.Parent);
        Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute("Click") == "FloatingSettingsClick");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "FloatingSettingsButton");

        // The taskbar on/off switch is no longer a main-window checkbox at all: it is the taskbar
        // settings dialog's 显示任务栏状态 checkbox, which previews until 保存 persists it.
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "TaskbarToggle");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("Checked") == "TaskbarToggleChanged");
        var taskbarSettings = LoadMarkup("TaskbarSettingsWindow.xaml");
        var taskbarEnabled = FindNamed(taskbarSettings, "EnabledCheck");
        Assert.Equal(Presentation + "CheckBox", taskbarEnabled.Name);
        Assert.Equal("EnabledChanged", (string?)taskbarEnabled.Attribute("Checked"));
        Assert.Equal("EnabledChanged", (string?)taskbarEnabled.Attribute("Unchecked"));
        AssertHasName(taskbarEnabled);
    }

    [Fact]
    public void FloatingSettingsDialogPreviewsLiveAndCancelRestoresTheOpenedState()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusSettingsWindow.xaml.cs"));
        Assert.Contains("private void TryPreview()", source);
        Assert.Contains("_preview(preview);", source);
        Assert.Contains("_preview(_initialSettings);", source);
        Assert.Contains("protected override void OnClosed(EventArgs e)", source);
        // Non-modal on purpose (the card stays draggable while the dialog is open), so the dialog
        // must close itself and report through Result instead of DialogResult.
        Assert.Contains("if (Result is null && _preview is not null)", source);
        Assert.DoesNotContain("DialogResult =", source);
        // The dialog never persists: only MainWindow writes the floating file.
        Assert.DoesNotContain("Store", source);
        Assert.DoesNotContain("Save(", source);
        // A live drag of the card updates the position the dialog would save.
        Assert.Contains("public void UpdatePosition(FloatingStatusSettings settings)", source);
        Assert.Contains("return _position with", source);

        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        Assert.Contains("new FloatingStatusSettingsWindow(current, PreviewFloatingSettings, SaveFloatingSettingsFromDialog)", main);
        Assert.Contains("window.PositionChanged += OnFloatingPositionChanged;", main);
        Assert.Contains("dialog.UpdatePosition(settings);", main);
        // Non-modal show, and the write happens synchronously from 保存 - never from a closed event.
        Assert.Contains("editor.Show();", main);
        Assert.DoesNotContain("if (editor.Result is { } accepted)", main);
        Assert.DoesNotContain("SaveFloatingResult(", main);
    }

    [Fact]
    public void FloatingSettingsSaveIsSynchronousAndKeepsTheWindowOnFailure()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusSettingsWindow.xaml.cs"));
        // A synchronous receiver instead of "write on Closed": the dialog knows the result before it
        // closes, so a failed write cannot lose every edit.
        Assert.Contains("Func<FloatingStatusSettings, string?>? save = null", source);
        var save = SourceBetween(source, "private void SaveClick(object sender, RoutedEventArgs e)",
            "private void ShowValidation(string message)");
        var call = save.IndexOf("error = _save(settings);", StringComparison.Ordinal);
        var result = save.IndexOf("Result = settings;", StringComparison.Ordinal);
        Assert.True(call >= 0, "保存 must call the synchronous save receiver.");
        Assert.True(result > call, "Result must only be set after the save reported success.");
        Assert.Contains("if (error is not null)", save);
        Assert.Contains("ShowValidation(\"保存失败，未写入磁盘，改动仍未保存：\" + error);", save);
        // The failure branch returns before Result/Close, so every control and the dragged position
        // stay on screen and 保存 can be pressed again.
        Assert.True(save.IndexOf("return;", call, StringComparison.Ordinal) < result);
        // The close event never writes a second time.
        var closed = SourceBetween(source, "protected override void OnClosed(EventArgs e)", "private void CancelClick(");
        Assert.DoesNotContain("_save(", closed);

        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        var saveFromDialog = SourceBetween(main, "private string? SaveFloatingSettingsFromDialog(",
            "private void SaveFloatingFlags(");
        var write = saveFromDialog.IndexOf("_floatingSettingsStore.Save(settings);", StringComparison.Ordinal);
        var adopt = saveFromDialog.IndexOf("_floatingSettings = settings;", StringComparison.Ordinal);
        Assert.True(write >= 0, "The dialog save must write the floating store.");
        Assert.True(adopt > write, "The new settings may only be adopted after a successful write.");
        Assert.Contains("return ex.GetBaseException().Message;", saveFromDialog);
        // Cancel still restores the opened settings through the preview callback.
        Assert.Contains("_preview(_initialSettings);", source);
    }

    [Fact]
    public void FloatingSettingsDialogsAreSingleInstanceAndBroughtToFront()
    {
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        var click = SourceBetween(main, "private void FloatingSettingsClick(object sender, RoutedEventArgs e)",
            "private static void ActivateFloatingSettings(");
        // A second click on any entry point (main button/checkbox, card menu, card Alt+F4) restores and
        // activates the existing dialog instead of returning silently or opening a twin.
        Assert.Contains("if (_floatingSettingsDialog is { } open)", click);
        Assert.Contains("ActivateFloatingSettings(open);", click);
        Assert.DoesNotContain("editor.Owner = this;", click);
        var activate = SourceBetween(main, "private static void ActivateFloatingSettings(Window editor)",
            "private void SetFloatingEditingState(");
        Assert.Contains("WindowState.Minimized", activate);
        Assert.Contains("editor.Activate();", activate);
    }

    [Fact]
    public void FloatingCardCloseIsTranslatedToHideAndShutdownClosesExplicitly()
    {
        var card = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        var closing = SourceBetween(card, "protected override void OnClosing(CancelEventArgs e)",
            "WPF really closed the window");
        Assert.Contains("if (_allowClose || e.Cancel) return;", closing);
        Assert.Contains("e.Cancel = true;", closing);
        // The hide is deferred out of the close stack, so the event handler itself raises nothing.
        Assert.Contains("QueueHideRequest();", closing);
        Assert.DoesNotContain("HideRequested", closing);
        Assert.Contains("public void CloseForShutdown()", card);

        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        // After _closing is set, the settings dialog's cancel path must not rebuild/show the card again.
        var preview = SourceBetween(main, "private void PreviewFloatingSettings(FloatingStatusSettings settings)",
            "private void OpenFloatingSettingsFromMenu()");
        Assert.Contains("if (_closing) return;", preview);
        var apply = SourceBetween(main, "private void ApplyFloatingSettings(FloatingStatusSettings settings)",
            "private void OnFloatingPositionChanged(");
        Assert.Contains("if (_closing) return;", apply);
        // The hide request while the dialog is open is not dropped silently.
        var hide = SourceBetween(main, "private void HideFloatingWindow()", "private void ScheduleTaskbarRefresh(");
        Assert.Contains("ActivateFloatingSettings(dialog);", hide);
    }

    [Fact]
    public void CardCloseHidesThroughOneDeferredDispatcherRequestOnly()
    {
        var card = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        // A plain close is canceled and translated into a queued hide; a real shutdown close is not.
        var closing = SourceBetween(card, "protected override void OnClosing(CancelEventArgs e)",
            "WPF really closed the window");
        Assert.Contains("if (_allowClose || e.Cancel) return;", closing);
        Assert.Contains("e.Cancel = true;", closing);
        Assert.Contains("QueueHideRequest();", closing);
        // Never synchronously from inside Window.Closing: the owner's hide path ends in
        // UpdateDisplaySettings -> Hide(), and a Show/Hide inside the close stack can throw
        // InvalidOperationException. No visibility change may happen in the handler at all.
        Assert.DoesNotContain("HideRequested", closing);
        Assert.DoesNotContain("Hide();", closing);
        Assert.DoesNotContain("Show();", closing);
        Assert.DoesNotContain("UpdateDisplaySettings(", closing);

        // The deferred callback is coalesced, dispatcher-marshalled and state-guarded.
        var queue = SourceBetween(card, "private void QueueHideRequest()",
            "internal void SetSettingsEditing(bool editing)");
        Assert.Contains("if (_hideRequestQueued) return;", queue);
        Assert.Contains("_hideRequestQueued = true;", queue);
        Assert.Contains("dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished", queue);
        Assert.Contains("DispatcherPriority.Background", queue);
        Assert.Contains("if (_allowClose || _closed) return;", queue);
        Assert.Contains("HideRequested?.Invoke();", queue);
        // Nothing is cleared or raised before both guards ran.
        Assert.True(queue.IndexOf("_hideRequestQueued = false;", StringComparison.Ordinal) <
                    queue.IndexOf("if (_allowClose || _closed) return;", StringComparison.Ordinal),
            "The queued-flag reset must precede the shutdown/closed guards.");
        Assert.True(queue.IndexOf("if (_allowClose || _closed) return;", StringComparison.Ordinal) <
                    queue.IndexOf("HideRequested?.Invoke();", StringComparison.Ordinal),
            "HideRequested must only be raised after the guards.");

        // The two state flags exist and the closed flag is set by the real close only.
        Assert.Contains("private bool _hideRequestQueued;", card);
        Assert.Contains("private bool _closed;", card);
        var closed = SourceBetween(card, "protected override void OnClosed(EventArgs e)",
            "private void QueueHideRequest()");
        Assert.Contains("_closed = true;", closed);
        Assert.Contains("base.OnClosed(e);", closed);
        // CloseForShutdown still marks the one real close, so a queued hide is dropped for it.
        var shutdownClose = SourceBetween(card, "public void CloseForShutdown()", "/// A plain close (Alt+F4");
        Assert.Contains("_allowClose = true;", shutdownClose);
        Assert.Contains("Close();", shutdownClose);
    }

    [Fact]
    public void FloatingSettingsEditingDisablesTheMutatingEntryPointsOnly()
    {
        var card = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        Assert.Contains("public bool IsSettingsEditing { get; private set; }", card);
        Assert.Contains("internal void SetSettingsEditing(bool editing)", card);
        var menu = SourceBetween(card, "private void UpdateMenuState()", "internal const string SettingsEditingHint");
        Assert.Contains("_lockItem.IsEnabled = !editing;", menu);
        Assert.Contains("_topmostItem.IsEnabled = !editing;", menu);
        Assert.Contains("_hideItem.IsEnabled = !editing;", menu);
        Assert.Contains("_lockItem.ToolTip = hint;", menu);
        Assert.Contains("internal const string SettingsEditingHint", card);
        // Locked state still stops DragMove: the drag handle itself is not disabled by editing.
        Assert.Contains("if (_settings.IsLocked)", card);

        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        // A card created by a live preview already knows the dialog is the single editor.
        Assert.Contains("window.SetSettingsEditing(_floatingSettingsDialog is not null);", main);
        var editing = SourceBetween(main, "private void SetFloatingEditingState(bool editing)",
            "private string? SaveFloatingSettingsFromDialog(");
        Assert.Contains("_floatingStatusWindow?.SetSettingsEditing(editing);", editing);
        // Nothing in the main window disables a floating switch any more: the only enable checkbox
        // lives inside the editor, which is the single editor while it is open.
        Assert.DoesNotContain("FloatingToggle", editing);
        // The settings entry stays reachable and its window follows the previewed Topmost value.
        Assert.Contains("editor.Topmost = current.Topmost;", main);
        Assert.Contains("dialog.Topmost = settings.Topmost;", main);
    }

    [Fact]
    public void FloatingCardPositionsWithOneNativePhysicalRectangle()
    {
        var card = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        Assert.Contains("FloatingStatusLayout.TryComputePlacementPixels(", card);
        Assert.Contains("NativeMethods.GetWindowRect(handle, out var rect)", card);
        // SWP_NOSIZE + NOZORDER + NOACTIVATE: the card only moves; WPF owns its size and its topmost
        // state, and a normal window is never raised through HWND_NOTOPMOST on every placement.
        Assert.Contains("SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate", card);
        Assert.DoesNotContain("HwndNoTopmost", card);
        Assert.DoesNotContain("HwndTopmost", card);
        // No Left/Top = physical/scale double correction on a mixed-DPI desktop.
        Assert.DoesNotContain("Left = placement.X", card);
        Assert.DoesNotContain("Top = placement.Y", card);

        // The drag flag is set around the modal DragMove loop and always cleared again.
        var drag = SourceBetween(card, "private void DragHandleMouseLeftButtonDown(", "private void StatusMouseLeftButtonUp(");
        Assert.Contains("_isDragging = true;", drag);
        Assert.Contains("finally", drag);
        Assert.Contains("_isDragging = false;", drag);
        Assert.Contains("RecordDraggedPosition();", drag);

        // A queued DPI/work-area clamp bails out while dragging (and never uses the old settings).
        var clamp = SourceBetween(card, "private void QueuePlacementClamp()", "private void DragHandleMouseLeftButtonDown(");
        Assert.Contains("if (_isDragging || _placementQueued || _handle == IntPtr.Zero) return;", clamp);
        Assert.Contains("if (_isDragging) return;", clamp);
        Assert.Contains("Dispatcher.BeginInvoke(DispatcherPriority.Loaded", clamp);
        // OnDpiChanged only queues: resolving the stored DIPs inside the callback is what pulled a
        // cross-monitor drag back to the monitor it was leaving.
        var dpi = SourceBetween(card, "protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)",
            "private void DragHandleMouseLeftButtonDown(");
        Assert.Contains("QueuePlacementClamp();", dpi);
        Assert.DoesNotContain("ApplyPlacement();", dpi);

        // The drag end records the monitor the window really ended on, with the real rectangle.
        var record = SourceBetween(card, "private void RecordDraggedPosition()", "private static Forms.Screen? ResolveScreen(");
        Assert.Contains("Forms.Screen.FromPoint(", record);
        Assert.Contains("MonitorDeviceName = screen.DeviceName", record);
        Assert.Contains("rect.Right - rect.Left", record);
        // Physical pixels in, physical pixels out: the clamp and the optional edge snap happen in one
        // pure call, so nothing is multiplied by the monitor scale a second time.
        Assert.Contains("FloatingStatusLayout.TryComputeSnappedPlacement(", record);
        Assert.DoesNotContain("ToRelativeDip(rect.Left", record);
        Assert.Contains("LeftDip = placement.RelativeLeftDip", record);
    }

    [Fact]
    public void FloatingCardDoesNotRenderAnInvisibleTreeOnEverySample()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        var update = SourceBetween(source, "public void UpdateSnapshot(", "public void UpdateDisplaySettings(");
        // The newest sample is always kept, but the tree is only rebuilt while it is really visible.
        Assert.Contains("_snapshot = snapshot;", update);
        Assert.Contains("if (IsVisible && _settings.Enabled) Render();", update);
        // The constructor never paints a throw-away "waiting" tree.
        var constructor = SourceBetween(source, "public FloatingStatusWindow(FloatingStatusSettings settings)",
            "public void CloseForShutdown()");
        Assert.DoesNotContain("Render();", constructor);
        Assert.Contains("SizeChanged += (_, _) => QueuePlacementClamp();", constructor);
        // Enabling/re-showing the card shows it first, then renders the latest snapshot.
        var display = SourceBetween(source, "public void UpdateDisplaySettings(FloatingStatusSettings settings)",
            "public void EnsureOnVisibleMonitor()");
        var show = display.IndexOf("if (!IsVisible) Show();", StringComparison.Ordinal);
        var render = display.IndexOf("Render();", StringComparison.Ordinal);
        Assert.True(show >= 0 && render > show, "The card must be shown before the latest snapshot is rendered.");
    }

    [Fact]
    public void FloatingSettingsExposeClickThroughSnapAndPositionReset()
    {
        var document = LoadMarkup("FloatingStatusSettingsWindow.xaml");
        var clickThrough = FindNamed(document, "ClickThroughCheck");
        var snap = FindNamed(document, "SnapToEdgesCheck");
        var reset = FindNamed(document, "ResetPositionButton");
        foreach (var element in new[] { clickThrough, snap, reset })
            AssertHasName(element);
        Assert.Equal("PreviewChanged", (string?)clickThrough.Attribute("Checked"));
        Assert.Equal("PreviewChanged", (string?)snap.Attribute("Checked"));
        Assert.Equal("ResetPositionClick", (string?)reset.Attribute("Click"));
        // The safety sentence is visible text, not only a tooltip.
        Assert.Contains(document.Descendants(Presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("编辑期间暂停穿透", StringComparison.Ordinal) == true);
        Assert.Contains("恢复默认位置", (string?)reset.Attribute("Content") ?? string.Empty);
    }

    [Fact]
    public void FloatingSettingsCodeReadsTheNewSwitchesAndKeepsResetAsAPreview()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusSettingsWindow.xaml.cs"));
        var read = SourceBetween(source, "private FloatingStatusSettings ReadSettings()", "public void ResetPosition()");
        Assert.Contains("ClickThrough = ClickThroughCheck.IsChecked == true", read);
        Assert.Contains("SnapToEdges = SnapToEdgesCheck.IsChecked == true", read);
        Assert.Contains("return _position with", read);
        // Both switches are initialised from the opened settings (inside the _ready guard).
        Assert.Contains("ClickThroughCheck.IsChecked = settings.ClickThrough;", source);
        Assert.Contains("SnapToEdgesCheck.IsChecked = settings.SnapToEdges;", source);

        var reset = SourceBetween(source, "public void ResetPosition()", "private void ResetPositionClick(");
        Assert.Contains("FloatingStatusInteraction.WithoutPosition(_position);", reset);
        Assert.Contains("TryPreview();", reset);
        Assert.DoesNotContain("_save(", reset);

        var recovery = SourceBetween(source, "public void ApplyClickThroughRecovery()", "public bool TogglePreviewVisibility()");
        Assert.Contains("_initialSettings = FloatingStatusInteraction.WithoutClickThrough(_initialSettings);", recovery);
        Assert.Contains("_position = FloatingStatusInteraction.WithoutClickThrough(_position);", recovery);
        Assert.Contains("ClickThroughCheck.IsChecked = false;", recovery);
        Assert.Contains("_ready = false;", recovery);
        Assert.DoesNotContain("Store", recovery);
        Assert.DoesNotContain("_save(", recovery);

        var toggle = SourceBetween(source, "public bool TogglePreviewVisibility()", "private void TryPreview()");
        Assert.Contains("EnabledCheck.IsChecked = EnabledCheck.IsChecked != true;", toggle);
        Assert.DoesNotContain("_save(", toggle);
    }

    [Fact]
    public void FloatingCardClickThroughTogglesOnlyTheOwnedBitsThroughTheCorePolicy()
    {
        var card = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        Assert.Contains("FloatingStatusInteraction.IsClickThroughEffective(", card);
        Assert.Contains("FloatingStatusInteraction.ComputeExStyle(current, effective, _baseNoActivate)", card);
        // The baseline is read from the live style, never assumed: hard-coding either value is what
        // cleared the card's own ShowActivated=false WS_EX_NOACTIVATE and let it steal the foreground.
        Assert.DoesNotContain("baseNoActivate: false", card);
        Assert.DoesNotContain("baseNoActivate: true", card);
        // The live style is read and written through the same call: no cached base style overwrite.
        Assert.Contains("NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle)", card);
        Assert.Contains("NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(wanted))", card);
        Assert.DoesNotContain("WsExTransparent", card);
        Assert.DoesNotContain("_baseExStyle", card);

        // Applied when the handle exists, after every settings update and after an editing change.
        Assert.Contains("ApplyMouseInteraction();", card);
        var initialized = SourceBetween(card, "private void OnSourceInitialized(", "/// The single place that writes the card's extended window style.");
        Assert.Contains("ApplyMouseInteraction();", initialized);
        // Ordering on the handle's first frame: read the existing style, cache the NOACTIVATE baseline
        // this feature may later restore, and only then add the tool-window bit / apply the feature.
        var read = initialized.IndexOf("NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle)", StringComparison.Ordinal);
        var capture = initialized.IndexOf("_baseNoActivate = (exStyle & NativeMethods.WsExNoActivate) != 0;", StringComparison.Ordinal);
        var toolWindow = initialized.IndexOf("NativeMethods.WsExToolWindow", StringComparison.Ordinal);
        var apply = initialized.IndexOf("ApplyMouseInteraction();", StringComparison.Ordinal);
        Assert.True(read >= 0, "OnSourceInitialized must read the live extended style.");
        Assert.True(capture > read, "The NOACTIVATE baseline must be captured from the style read above.");
        Assert.True(toolWindow > capture, "Only the tool-window bit may be added after the baseline is captured.");
        Assert.True(apply > capture, "The first style write must happen after the baseline was captured.");
        var editing = SourceBetween(card, "internal void SetSettingsEditing(bool editing)", "internal void SetClickThroughSafetyOverride(");
        Assert.Contains("ApplyMouseInteraction();", editing);
        var display = SourceBetween(card, "public void UpdateDisplaySettings(FloatingStatusSettings settings)", "public void EnsureOnVisibleMonitor()");
        Assert.Contains("ApplyMouseInteraction();", display);
        var safety = SourceBetween(card, "internal void SetClickThroughSafetyOverride(bool active)", "private void OnSourceInitialized(");
        Assert.Contains("_clickThroughSafetyOverride = active;", safety);
        Assert.Contains("ApplyMouseInteraction();", safety);

        var native = File.ReadAllText(FindAppSource("NativeMethods.cs"));
        Assert.Contains("WsExTransparent = 0x00000020", native);
    }

    [Fact]
    public void MainWindowClickThroughEscapeHatchStopsTheInteractionBeforeWriting()
    {
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        var tray = SourceBetween(main, "private Forms.NotifyIcon CreateTrayIcon()", "private async Task ExecuteHotkeyFullSpeedAsync()");
        foreach (var label in new[] { "显示/隐藏桌面悬浮窗", "桌面悬浮窗设置…", "关闭悬浮窗鼠标穿透", "恢复悬浮窗默认位置" })
            Assert.Contains(label, tray);
        Assert.Contains("ToggleFloatingStatus()", tray);
        Assert.Contains("DisableFloatingClickThrough()", tray);
        Assert.Contains("ResetFloatingPosition()", tray);

        var disable = SourceBetween(main, "private void DisableFloatingClickThrough()", "private void ResetFloatingPosition()");
        var stop = disable.IndexOf("_floatingStatusWindow?.SetClickThroughSafetyOverride(true);", StringComparison.Ordinal);
        var write = disable.IndexOf("_floatingSettingsStore.Save(updated);", StringComparison.Ordinal);
        Assert.True(stop >= 0, "The actual click-through must be stopped first.");
        Assert.True(write > stop, "The runtime override must precede the disk write.");
        Assert.Contains("FloatingStatusInteraction.WithoutClickThrough(_floatingSettings)", disable);
        Assert.Contains("dialog?.ApplyClickThroughRecovery();", disable);
        Assert.Contains("本次已恢复交互，未能保存，下次启动可能恢复原设置", disable);
        // The failure path re-asserts the override instead of quietly leaving a locked card.
        Assert.Equal(2, disable.Split("SetClickThroughSafetyOverride(true)").Length - 1);

        var reset = SourceBetween(main, "private void ResetFloatingPosition()", "private void CloseFloatingClickThroughClick(");
        Assert.Contains("dialog.ResetPosition();", reset);
        // The live card is the base, so only the three position fields are cleared.
        Assert.Contains("FloatingStatusInteraction.WithoutPosition(live)", reset);
        Assert.DoesNotContain("Enabled = true", reset);

        var toggle = SourceBetween(main, "private void ToggleFloatingStatus()", "private void DisableFloatingClickThrough()");
        Assert.Contains("dialog.TogglePreviewVisibility()", toggle);
        Assert.Contains("AppLog.Write(\"floating status toggle save failed\", ex);", toggle);
    }

    [Fact]
    public void MissingCardClickThroughDisableKeepsARuntimeOverrideForEveryLaterCard()
    {
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        // A float-only owner flag (the taskbar strip has no equivalent state) remembers the override
        // even while no card exists at all.
        Assert.Contains("private bool _floatingClickThroughSafetyOverride;", main);

        var disable = SourceBetween(main, "private void DisableFloatingClickThrough()",
            "private void ResetFloatingPosition()");
        var ownerFlag = disable.IndexOf("_floatingClickThroughSafetyOverride = true;", StringComparison.Ordinal);
        var write = disable.IndexOf("_floatingSettingsStore.Save(updated);", StringComparison.Ordinal);
        Assert.True(ownerFlag >= 0, "The owner override must be set even when no card is on screen.");
        Assert.True(write > ownerFlag, "The safety override must be remembered before the disk write.");
        // A failed write keeps it (the file still holds ClickThrough=true); only the success branch,
        // after the write, may clear it.
        var clear = disable.IndexOf("_floatingClickThroughSafetyOverride = false;", StringComparison.Ordinal);
        Assert.True(clear > write, "Only a successful write may clear the owner override.");
        Assert.Contains("if (dialog is null)", disable);

        // A card created later receives the override right after construction, i.e. before any
        // Show()/UpdateDisplaySettings route can re-apply the old stored ClickThrough=true.
        var create = SourceBetween(main, "private FloatingStatusWindow CreateFloatingWindow()",
            "private void ApplyFloatingSettings(");
        var construct = create.IndexOf("new FloatingStatusWindow(_floatingSettings)", StringComparison.Ordinal);
        var enforce = create.IndexOf("window.SetClickThroughSafetyOverride(_floatingClickThroughSafetyOverride);", StringComparison.Ordinal);
        Assert.True(construct >= 0, "The card is still constructed from the saved settings.");
        Assert.True(enforce > construct, "The created card must inherit the owner override after construction.");

        // Every preview/apply re-asserts it *after* the display update, and never clears it there.
        var apply = SourceBetween(main, "private void ApplyFloatingSettings(FloatingStatusSettings settings)",
            "private void OnFloatingPositionChanged(");
        var update = apply.IndexOf("UpdateDisplaySettings(settings)", StringComparison.Ordinal);
        var reassert = apply.IndexOf("ReassertFloatingClickThroughSafetyOverride();", StringComparison.Ordinal);
        Assert.True(update >= 0 && reassert > update,
            "The override must be re-applied after the previewed ClickThrough value was applied.");
        // Both apply paths (enabled card and the hidden/disabled branch) re-assert it.
        Assert.Equal(2, apply.Split("ReassertFloatingClickThroughSafetyOverride();").Length - 1);
        var reassertMethod = apply[apply.IndexOf("private void ReassertFloatingClickThroughSafetyOverride()", StringComparison.Ordinal)..];
        Assert.Contains("if (_floatingClickThroughSafetyOverride)", reassertMethod);
        Assert.Contains("SetClickThroughSafetyOverride(true)", reassertMethod);
        Assert.DoesNotContain("SetClickThroughSafetyOverride(false)", reassertMethod);

        // A committed explicit save is the user's decision (including an explicit ClickThrough=true):
        // it ends the owner override before the apply, and also clears the card instance flag.
        var save = SourceBetween(main, "private string? SaveFloatingSettingsFromDialog(",
            "private void SaveFloatingFlags(");
        var savedClear = save.IndexOf("_floatingClickThroughSafetyOverride = false;", StringComparison.Ordinal);
        var savedApply = save.IndexOf("ApplyFloatingSettings(settings);", StringComparison.Ordinal);
        Assert.True(savedClear >= 0 && savedApply > savedClear,
            "The owner override must be cleared before ApplyFloatingSettings re-asserts it.");
        Assert.Contains("_floatingStatusWindow?.SetClickThroughSafetyOverride(false);", save);
    }

    [Fact]
    public void ExternalSafetyActionsMergeTheLiveCardPositionInsteadOfTheStaleFile()
    {
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        // The last dragged position lives on the live card; its own save can have failed, so an older
        // stored Left/Top must never be written back by an unrelated safety action.
        var disable = SourceBetween(main, "private void DisableFloatingClickThrough()",
            "private void ResetFloatingPosition()");
        Assert.Contains("var live = _floatingStatusWindow?.Settings ?? _floatingSettings;", disable);
        Assert.Contains("MonitorDeviceName = live.MonitorDeviceName,", disable);
        Assert.Contains("LeftDip = live.LeftDip,", disable);
        Assert.Contains("TopDip = live.TopDip", disable);
        // Only the safety flag is changed: the saved instance stays the base for every other value
        // (dialog edit buffers are still never persisted).
        Assert.Contains("FloatingStatusInteraction.WithoutClickThrough(_floatingSettings)", disable);
        Assert.DoesNotContain("WithoutClickThrough(live)", disable);

        var reset = SourceBetween(main, "private void ResetFloatingPosition()",
            "private void CloseFloatingClickThroughClick(");
        Assert.Contains("var live = _floatingStatusWindow?.Settings ?? _floatingSettings;", reset);
        Assert.Contains("FloatingStatusInteraction.WithoutPosition(live)", reset);
        Assert.DoesNotContain("WithoutPosition(_floatingSettings)", reset);
    }

    [Fact]
    public void FloatingSettingsCancelButtonAlsoAnswersEscape()
    {
        var document = LoadMarkup("FloatingStatusSettingsWindow.xaml");
        var cancel = Assert.Single(document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Click") == "CancelClick");
        // ESC must reach the very same Close() path the button uses, so the dialog's OnClosed restore
        // (the claimed 取消 semantics) really runs. No DialogResult is involved: the window is modeless.
        Assert.Equal("True", (string?)cancel.Attribute("IsCancel"));
        Assert.Equal("取消", (string?)cancel.Attribute("Content"));
        var source = File.ReadAllText(FindAppSource("FloatingStatusSettingsWindow.xaml.cs"));
        Assert.Contains("private void CancelClick(object sender, RoutedEventArgs e) => Close();", source);
        Assert.DoesNotContain("DialogResult =", source);
    }

    [Fact]
    public void ClickThroughRecoveryStaysInTheTrayMenuAndTheGlobalHotkey()
    {
        // The in-card 关闭穿透 button was removed together with the floating settings button: the
        // sidebar owns the floating settings entry and the overview keeps only the two quick toggles.
        var document = LoadMarkup("MainWindow.xaml");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "FloatingClickThroughOffButton");

        // The recovery logic itself stays: tray menu entry, global hotkey and the shared helper that
        // both use are untouched, so no wake-up path was lost with the button.
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        Assert.Contains("menu.Items.Add(\"关闭悬浮窗鼠标穿透\", null, (_, _) => DisableFloatingClickThrough());", main);
        Assert.Contains("HotkeyAction.DisableFloatingClickThrough", main);
        Assert.Contains("private void DisableFloatingClickThrough()", main);
        Assert.Contains("window.ClickThroughDisableRequested += DisableFloatingClickThrough;", main);
    }

    [Fact]
    public void FloatingHotkeysAreDispatchedBeforeTheControlActionGate()
    {
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        var proc = SourceBetween(main, "private IntPtr HotkeyWindowProc(", "private static System.Drawing.Icon CreateBrandIcon()");
        var branch = proc.IndexOf("action is HotkeyAction.ToggleFloatingStatus or HotkeyAction.DisableFloatingClickThrough", StringComparison.Ordinal);
        var switchStart = proc.IndexOf("HotkeyAction.SilenceMode => ExecuteAsync", StringComparison.Ordinal);
        var control = proc.IndexOf("HotkeyAction.FullSpeed => ExecuteHotkeyFullSpeedAsync()", StringComparison.Ordinal);
        Assert.True(branch >= 0, "The display-only floating actions need their own dispatch branch.");
        Assert.True(control > branch, "They must be handled before the control actions.");
        Assert.True(switchStart > branch, "The control-action switch must come after the display-only branch.");
        var early = proc[branch..switchStart];
        Assert.Contains("ToggleFloatingStatus();", early);
        Assert.Contains("DisableFloatingClickThrough();", early);
        Assert.Contains("return IntPtr.Zero;", early);
        Assert.DoesNotContain("ExecuteAsync", early);

        // Registration stays per action, and the ids are still enum value + 1.
        Assert.Contains("private static int HotkeyId(HotkeyAction action) => 1 + (int)action;", main);
        Assert.Contains("id <= Enum.GetValues<HotkeyAction>().Length", main);
    }

    [Fact]
    public void HotkeySettingsUiStillEnumeratesTheActionsDynamically()
    {
        var taskbar = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        Assert.Contains("foreach (var action in Enum.GetValues<HotkeyAction>())", taskbar);
        // The new rows are discovered from the enum; no hard-coded action list was added.
        Assert.DoesNotContain("HotkeyAction.ToggleFloatingStatus", taskbar);
    }

    [Fact]
    public void FloatingDefaultsStayOffAndStatic()
    {
        // Guard the documented product decision: adding the card changes nothing until it is enabled.
        Assert.False(FloatingStatusSettings.Default.Enabled);
        Assert.True(FloatingStatusSettings.Default.Topmost);
        Assert.False(FloatingStatusSettings.Default.IsLocked);
        Assert.Equal(TaskbarLayoutMode.StackedPairs, FloatingStatusSettings.Default.LayoutMode);
        // Same headline metrics as the taskbar default; every one of them is a read-only display item.
        Assert.Equal(TaskbarDisplaySettings.Default.Items, FloatingStatusSettings.Default.Items);
    }

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
}
