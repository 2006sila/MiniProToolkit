using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MiniProToolkit.Core;
// The app project enables WinForms implicit usings as well, so the WPF control types used by the
// hotkey rows, the colour samples and the direction buttons have to be aliased explicitly.
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Thumb = System.Windows.Controls.Primitives.Thumb;

namespace MiniProToolkit.App;

public partial class TaskbarSettingsWindow : Window
{
    private readonly ObservableCollection<EditableItem> _items;
    private readonly List<HotkeyRow> _hotkeyRows = [];
    // Drag reorder state: the row being dragged and its live index, so every DragDelta can move
    // that one item instead of re-deriving the source from the pointer.
    private EditableItem? _draggedItem;
    private int _draggedIndex = -1;
    private ScrollViewer? _itemsScrollViewer;
    // Live preview target supplied by MainWindow: it applies the edit buffer to the overlays
    // that already exist (no disk write, no window recreate). Null means "no preview", so a
    // caller that only wants the persisted result keeps working unchanged.
    private readonly Action<TaskbarDisplaySettings>? _preview;
    // State of the window at the moment it was opened. Cancelling (including ESC / the title-bar
    // close button) pushes it back through the same preview callback, so no previewed value stays
    // on screen. Saving persists normally and the caller recreates the overlays as before.
    private readonly TaskbarDisplaySettings _initialSettings;
    // Bounded physical nudge in DIPs, edited only by the four direction buttons.
    private int _offsetXDip;
    private int _offsetYDip;
    private static readonly System.Windows.Media.Brush SwatchBorderBrush = Frozen(System.Windows.Media.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly System.Windows.Media.Brush SelectedSwatchBrush = Frozen(System.Windows.Media.Color.FromRgb(0x66, 0xDE, 0xC4));
    // The single source of truth for both taskbar colours. The colour card (target switch, shared
    // samples, 更多颜色, background opacity, advanced hex) reads and writes only this state; no
    // hidden hex input remains as a second authority.
    private readonly TaskbarColorEditorState _colorState;
    // Guards the controls while the state is being pushed into them, so initialising the slider or
    // the target radio cannot fire a preview or touch the state.
    private bool _updatingColorEditor;
    // Enabled only after the whole window (preview callback, items, hotkeys) has been initialised:
    // the Checked/ValueChanged events fire during InitializeComponent, before the state exists.
    private bool _colorEditorReady;
    // Same guard for the single taskbar on/off switch: setting its initial value from the opened
    // settings must not fire a preview, only a user click does.
    private bool _enabledEditorReady;
    public TaskbarDisplaySettings? Result { get; private set; }

    /// <summary>
    /// Rebound global hotkeys, or <c>null</c> when the user cancelled. A separate result keeps the two
    /// persisted files (taskbar.json / hotkeys.json) independent: the caller saves and applies each
    /// one on its own, so a failure in one never silently drops the other.
    /// </summary>
    public HotkeySettings? HotkeyResult { get; private set; }

    public TaskbarSettingsWindow(TaskbarDisplaySettings settings) : this(settings, HotkeySettings.Default, null)
    {
    }

    public TaskbarSettingsWindow(TaskbarDisplaySettings settings, HotkeySettings hotkeys) : this(settings, hotkeys, null)
    {
    }

    /// <param name="preview">Optional live-preview receiver. It must only apply the settings to
    /// already existing overlays; the dialog calls it on every direction click, colour sample click and
    /// colour text-box leave, and calls it once with <paramref name="settings"/> when cancelled.</param>
    public TaskbarSettingsWindow(TaskbarDisplaySettings settings, HotkeySettings hotkeys, Action<TaskbarDisplaySettings>? preview)
    {
        InitializeComponent();
        _preview = preview;
        // 纯展示：与其他功能窗共用同一份深色原生标题栏实现（DWM 不可用时静默跳过）。
        ObsidianWindowChrome.Apply(this);
        settings.Validate();
        hotkeys.Validate();
        _colorState = new TaskbarColorEditorState(settings.TextColor, settings.BackgroundColor);
        _initialSettings = settings;
        _offsetXDip = settings.OffsetXDip;
        _offsetYDip = settings.OffsetYDip;
        _items = new ObservableCollection<EditableItem>(
            Enum.GetValues<TaskbarDisplayItem>().Select(item => new EditableItem(
                item,
                LabelFor(item),
                settings.Items.Contains(item))));
        // Keep configured order first, then append currently hidden items.
        var ordered = settings.Items
            .Concat(Enum.GetValues<TaskbarDisplayItem>().Except(settings.Items))
            .Select(item => _items.First(entry => entry.Item == item))
            .ToArray();
        _items.Clear();
        foreach (var item in ordered)
            _items.Add(item);
        UpdateDisplayIndexes();
        ItemsList.ItemsSource = _items;
        CompactCheck.IsChecked = settings.Compact;
        VerticalCheck.IsChecked = settings.Vertical;
        // The single persisted taskbar on/off switch; the initial value never previews.
        EnabledCheck.IsChecked = settings.Enabled;
        SelectLayoutMode(settings.LayoutMode);
        UpdateLayoutModeHint();
        FontFamilyText.Text = settings.FontFamily;
        FontSizeText.Text = settings.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RefreshCombo.SelectedIndex = Math.Max(0, settings.RefreshIntervalSeconds switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            5 => 3,
            10 => 4,
            _ => 0
        });
        BuildHotkeyRows(hotkeys);
        UpdateOffsetStatus();
        // Push the opened colours into the one card last, then allow the colour events to preview.
        ApplyColorStateToEditor();
        _colorEditorReady = true;
        _enabledEditorReady = true;
    }

    /// <summary>
    /// Builds one editor row per hotkey action. The key box is filled from
    /// <see cref="HotkeyKeyCatalog.Keys"/>, so the UI cannot offer (or produce) a key outside the
    /// whitelist that the Core validation and the registration path accept.
    /// </summary>
    private void BuildHotkeyRows(HotkeySettings settings)
    {
        _hotkeyRows.Clear();
        HotkeyPanel.Children.Clear();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var row = new HotkeyRow(action);
            row.Apply(settings.GestureFor(action));
            HotkeyPanel.Children.Add(row.Build());
            _hotkeyRows.Add(row);
        }
    }

    private void ResetHotkeysClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _hotkeyRows)
            row.Apply(HotkeySettings.Default.GestureFor(row.Action));
        HotkeyStatusText.Text = "已恢复默认组合，点击“保存”后生效。";
    }

    private HotkeySettings ReadHotkeySettings() => new(
        _hotkeyRows.Select(row => new HotkeyBinding(row.Action, row.Read())).ToArray());

    /// <summary>
    /// Moves the selected row one slot and keeps it selected, so repeated key presses keep working
    /// on the same item. The dialog has no up/down buttons any more (they collided with the physical
    /// direction buttons of the status strip), so Ctrl+Up / Ctrl+Down is the only programmatic path.
    /// </summary>
    private void MoveSelected(int delta)
    {
        if (ItemsList.SelectedItem is not EditableItem item)
            return;
        var index = _items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _items.Count)
            return;
        _items.Move(index, target);
        UpdateDisplayIndexes();
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
    }

    private void ItemsListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
            return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Up:
                MoveSelected(-1);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Down:
                MoveSelected(1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Selects the stored mode. A <see cref="TaskbarDisplaySettings"/> has already been validated,
    /// but a hand-built instance could still carry an undefined value; the combo then stays on the
    /// default A instead of saving an invalid mode.
    /// </summary>
    private void SelectLayoutMode(TaskbarLayoutMode mode)
    {
        LayoutModeCombo.SelectedIndex = 0;
        foreach (var item in LayoutModeCombo.Items.OfType<ComboBoxItem>())
        {
            if (Enum.TryParse<TaskbarLayoutMode>(item.Tag?.ToString(), out var candidate) && candidate == mode)
            {
                LayoutModeCombo.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>The selected mode; an unusable selection falls back to the documented default A.</summary>
    private TaskbarLayoutMode ReadLayoutMode()
    {
        if (LayoutModeCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<TaskbarLayoutMode>(item.Tag?.ToString(), out var mode) && Enum.IsDefined(mode))
            return mode;
        return TaskbarLayoutMode.StackedPairs;
    }

    private void LayoutModeChanged(object sender, SelectionChangedEventArgs e) => UpdateLayoutModeHint();

    /// <summary>
    /// The only taskbar on/off switch. It previews through the same callback as every other edit
    /// (MainWindow creates or closes the overlays for the previewed value without writing the
    /// file); 保存 persists it through the normal save path and 取消 restores the opened value.
    /// </summary>
    private void EnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_enabledEditorReady) return;
        TryPreview();
    }

    /// <summary>
    /// Explains the selected mode: its letter/name and the situation it suits. The text is static
    /// per mode (no hardware or settings read), so it can be announced by a screen reader.
    /// </summary>
    private void UpdateLayoutModeHint()
    {
        if (LayoutModeHint is null) return;
        LayoutModeHint.Text = ReadLayoutMode() switch
        {
            TaskbarLayoutMode.Inline =>
                "C · 单行分段：所有项目按保存顺序排成一行，前 2 项为第一组、其余项目为第二组，两组之间一条细竖线（组内约 6、分隔线两侧各 10）；放不下时优先保留 CPU 占用/内存/CPU 温度并显示 +N，悬停可看被省略的指标。",
            TaskbarLayoutMode.AdaptiveColumns =>
                "D · 自适应多列：固定两行、按保存顺序两两成列，列间 12 无分隔线，最多 4 列（8 项）；列数按稳定宽度估算（标签/单位/高位数字预留）随安全宽度和字号变化，不随实时数值抖动；放不下或超出时显示 +N。",
            TaskbarLayoutMode.LabelOverValue =>
                "E · 标签上、数值下：每项一张小卡片，标签在上、数值在下；竖排任务栏时前两项保持卡片、其余每项一行（无冒号）；放不下时显示 +N。",
            _ =>
                "A · 双列堆叠（默认）：前两项一组、后两项一组，每列上下两项，只在两组之间画一条细竖线；4 项以上按 D 的规则继续向右加列（列间 12、无额外分隔线，最多 4 列/8 项）；放不下或超出时显示 +N。"
        };
    }

    /// <summary>
    /// Starts a row drag. The handle (a <c>Thumb</c> inside the row) is the only hit target that
    /// reorders, so clicking the checkbox only toggles visibility.
    /// </summary>
    private void ItemDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _draggedItem = null;
        _draggedIndex = -1;
        if (sender is not Thumb thumb)
            return;
        var container = ItemsControl.ContainerFromElement(ItemsList, thumb) as ListBoxItem;
        if (container?.DataContext is not EditableItem item)
            return;
        _draggedItem = item;
        _draggedIndex = _items.IndexOf(item);
        ItemsList.SelectedItem = item;
        _itemsScrollViewer = FindScrollViewer(ItemsList);
    }

    /// <summary>
    /// Reorders live while the handle is dragged: the collection is moved as soon as the pointer
    /// passes another row's midpoint, so the numbering and the saved order follow the drag.
    /// </summary>
    private void ItemDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_draggedItem is null || _draggedIndex < 0)
            return;
        var pointerY = System.Windows.Input.Mouse.GetPosition(ItemsList).Y;
        AutoScrollWhileDragging(pointerY);
        var target = TargetIndexFor(pointerY);
        if (target < 0 || target == _draggedIndex)
            return;
        _items.Move(_draggedIndex, target);
        _draggedIndex = target;
        UpdateDisplayIndexes();
        // The highlight follows the dragged row: visible feedback that the move happened.
        ItemsList.SelectedItem = _draggedItem;
    }

    private void ItemDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        // A cancelled drag keeps whatever order was already applied; only the transient state ends.
        _draggedItem = null;
        _draggedIndex = -1;
        _itemsScrollViewer = null;
        UpdateDisplayIndexes();
    }

    /// <summary>Nearest row under the pointer: the first row whose midpoint is below it.</summary>
    private int TargetIndexFor(double pointerY)
    {
        var lastRealized = -1;
        for (var index = 0; index < _items.Count; index++)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
                continue;
            var origin = container.TranslatePoint(new System.Windows.Point(0, 0), ItemsList);
            if (pointerY < origin.Y + (container.ActualHeight / 2))
                return index;
            lastRealized = index;
        }
        return lastRealized;
    }

    /// <summary>Optional comfort: scrolls the list while the handle is dragged at its edge.</summary>
    private void AutoScrollWhileDragging(double pointerY)
    {
        const double edge = 16;
        if (_itemsScrollViewer is not { } viewer)
            return;
        if (pointerY < edge) viewer.LineUp();
        else if (pointerY > ItemsList.ActualHeight - edge) viewer.LineDown();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, index)) is { } found)
                return found;
        return null;
    }

    /// <summary>Refreshes the visible 1-based row numbers after any reorder.</summary>
    private void UpdateDisplayIndexes()
    {
        for (var index = 0; index < _items.Count; index++)
            _items[index].DisplayIndex = index + 1;
    }

    /// <summary>
    /// Reads the display settings currently visible in the dialog. The save path and the live preview
    /// share this reader, so what the overlay previews can never disagree with what saving writes.
    /// </summary>
    private TaskbarDisplaySettings ReadDisplaySettings()
    {
        var selected = _items.Where(item => item.Enabled).Select(item => item.Item).ToArray();
        var refresh = int.Parse(((System.Windows.Controls.ComboBoxItem)RefreshCombo.SelectedItem).Content.ToString()!);
        return new TaskbarDisplaySettings(
            selected,
            CompactCheck.IsChecked == true,
            VerticalCheck.IsChecked == true,
            refresh,
            FontFamilyText.Text.Trim(),
            double.Parse(FontSizeText.Text, System.Globalization.CultureInfo.InvariantCulture),
            _colorState.TextHex,
            _colorState.BackgroundHex,
            ReadLayoutMode(),
            _offsetXDip,
            _offsetYDip,
            EnabledCheck.IsChecked == true);
    }

    /// <summary>
    /// Pushes the current edit buffer to the existing overlays through the preview callback. A
    /// half-typed value (empty font box, unfinished colour) is simply not previewed yet; it is
    /// reported only when saving. A failed preview is logged and never closes the dialog.
    /// </summary>
    private void TryPreview()
    {
        if (_preview is null) return;
        TaskbarDisplaySettings preview;
        try
        {
            preview = ReadDisplaySettings();
            preview.Validate();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return;
        }
        try
        {
            _preview(preview);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("taskbar settings preview failed", ex);
        }
    }

    /// <summary>
    /// Any close that is not the save button (取消, ESC, title-bar close) restores the exact settings
    /// the window was opened with through the same preview callback. Saving keeps the saved value:
    /// MainWindow persists it and recreates the overlays through its existing path.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true && _preview is not null)
        {
            try
            {
                _preview(_initialSettings);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("taskbar settings preview revert failed", ex);
            }
        }
        base.OnClosed(e);
    }

    private void NudgeUpClick(object sender, RoutedEventArgs e) => NudgeTaskbar(0, -TaskbarDisplaySettings.OffsetStepDip);

    private void NudgeDownClick(object sender, RoutedEventArgs e) => NudgeTaskbar(0, TaskbarDisplaySettings.OffsetStepDip);

    private void NudgeLeftClick(object sender, RoutedEventArgs e) => NudgeTaskbar(-TaskbarDisplaySettings.OffsetStepDip, 0);

    private void NudgeRightClick(object sender, RoutedEventArgs e) => NudgeTaskbar(TaskbarDisplaySettings.OffsetStepDip, 0);

    /// <summary>
    /// One bounded nudge step in physical screen directions. Only the two offset fields change; the
    /// preview then repositions the existing overlays immediately, and the placement math keeps the
    /// window inside the appbar rectangle. The value is persisted only by the save button.
    /// </summary>
    private void NudgeTaskbar(int deltaX, int deltaY)
    {
        _offsetXDip = Math.Clamp(_offsetXDip + deltaX, TaskbarDisplaySettings.MinimumOffsetDip, TaskbarDisplaySettings.MaximumOffsetDip);
        _offsetYDip = Math.Clamp(_offsetYDip + deltaY, TaskbarDisplaySettings.MinimumOffsetDip, TaskbarDisplaySettings.MaximumOffsetDip);
        UpdateOffsetStatus();
        TryPreview();
    }

    /// <summary>The live nudge readout; there is no numeric offset editor.</summary>
    private void UpdateOffsetStatus()
    {
        if (OffsetStatusText is null) return;
        OffsetStatusText.Text =
            $"当前微调：水平 {FormatOffset(_offsetXDip)} DIP，垂直 {FormatOffset(_offsetYDip)} DIP" +
            $"（每步 {TaskbarDisplaySettings.OffsetStepDip} DIP，范围 ±200 DIP；始终保持在任务栏矩形内）";

        static string FormatOffset(int value) =>
            value > 0 ? "+" + value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Pushes the single colour state into every part of the one card: the two target buttons and
    /// their colour chips, the local preview, the shared sample selection, the background-only
    /// opacity controls and the advanced hex box for the active target. Guarded so the resulting
    /// Checked/ValueChanged events cannot re-enter the state or trigger a preview.
    /// </summary>
    private void ApplyColorStateToEditor()
    {
        if (TextColorTargetRadio is null || ColorSwatches is null) return;
        _updatingColorEditor = true;
        try
        {
            var isBackground = _colorState.ActiveTarget == TaskbarColorTarget.Background;
            TextColorTargetRadio.IsChecked = !isBackground;
            BackgroundColorTargetRadio.IsChecked = isBackground;
            var textColor = _colorState.TextColor;
            var backgroundColor = _colorState.BackgroundColor;
            TextColorChip.Background = BrushFrom(textColor);
            BackgroundColorChip.Background = BrushFrom(backgroundColor);
            ColorPreviewCard.Background = BrushFrom(backgroundColor);
            ColorPreviewText.Foreground = BrushFrom(textColor);
            ColorTargetStatusText.Text = isBackground
                ? $"当前编辑：背景 · {_colorState.BackgroundHex}（不透明度 {_colorState.BackgroundOpacityPercent}%）"
                : $"当前编辑：文字 · {_colorState.TextHex}";
            if (!string.Equals(AdvancedColorText.Text, _colorState.ActiveHex, StringComparison.Ordinal))
                AdvancedColorText.Text = _colorState.ActiveHex;
            AdvancedColorErrorText.Text = string.Empty;
            AdvancedColorErrorText.Visibility = Visibility.Collapsed;
            UpdateColorSwatchSelection();
            var isPreset = IsPresetRgb(_colorState.ActiveColor.Rgb);
            ColorCustomHint.Text = isPreset ? string.Empty : $"当前颜色不在预设中：自定义 {_colorState.ActiveHex}";
            ColorCustomHint.Visibility = isPreset ? Visibility.Collapsed : Visibility.Visible;
            BackgroundOpacityPanel.Visibility = isBackground ? Visibility.Visible : Visibility.Collapsed;
            BackgroundOpacitySlider.Value = _colorState.BackgroundOpacityPercent;
            BackgroundOpacityText.Text = $"{_colorState.BackgroundOpacityPercent}%";
            BackgroundOpacityHint.Visibility = _colorState.BackgroundIsFullyTransparent ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updatingColorEditor = false;
        }
    }

    /// <summary>Highlights the sample whose RGB matches the edited colour; RGB-only, so alpha is ignored.</summary>
    private void UpdateColorSwatchSelection()
    {
        foreach (var button in ColorSwatches.Children.OfType<Button>())
        {
            if (button.Tag is not string tag || !TaskbarArgbColor.TryParse(tag, out var color) ||
                button.Content is not System.Windows.Controls.Border border)
                continue;
            var selected = _colorState.IsActiveRgbSelected(color.Rgb);
            border.BorderThickness = new Thickness(selected ? 2 : 1);
            border.BorderBrush = selected ? SelectedSwatchBrush : SwatchBorderBrush;
        }
    }

    private bool IsPresetRgb(uint rgb) => ColorSwatches.Children.OfType<Button>()
        .Any(button => button.Tag is string tag && TaskbarArgbColor.TryParse(tag, out var color) && color.Rgb == rgb);

    /// <summary>
    /// One shared colour sample. It edits only the selected target and goes through the state, so
    /// alpha is preserved (a transparent background stays at alpha 0 and keeps its remembered RGB).
    /// </summary>
    private void ColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !TaskbarArgbColor.TryParse(tag, out var color)) return;
        if (!_colorState.TryApplyRgb(color.Rgb)) return;
        ApplyColorStateToEditor();
        TryPreview();
    }

    /// <summary>
    /// Switches which value the card edits. It never modifies a colour, never previews and never
    /// writes: it only re-points the shared samples, the hex box and the background-only controls.
    /// A rejected advanced hex keeps the current target instead of leaking into the other colour.
    /// </summary>
    private void ColorTargetChanged(object sender, RoutedEventArgs e)
    {
        if (!_colorEditorReady || _updatingColorEditor) return;
        var target = BackgroundColorTargetRadio.IsChecked == true
            ? TaskbarColorTarget.Background
            : TaskbarColorTarget.Text;
        if (!_colorState.TrySelectTarget(target))
        {
            _updatingColorEditor = true;
            try
            {
                var isBackground = _colorState.ActiveTarget == TaskbarColorTarget.Background;
                TextColorTargetRadio.IsChecked = !isBackground;
                BackgroundColorTargetRadio.IsChecked = isBackground;
            }
            finally
            {
                _updatingColorEditor = false;
            }
            // Keep the rejected text visible so the user can fix it (or press Esc to restore).
            AdvancedColorExpander.IsExpanded = true;
            ShowAdvancedError(_colorState.AdvancedError ?? TaskbarColorEditorState.InvalidHexMessage);
            return;
        }
        ApplyColorStateToEditor();
    }

    private void BackgroundOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_colorEditorReady || _updatingColorEditor) return;
        if (!_colorState.TrySetBackgroundOpacity((int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero))) return;
        ApplyColorStateToEditor();
        TryPreview();
    }

    private void BackgroundTransparentClick(object sender, RoutedEventArgs e)
    {
        _colorState.SetBackgroundFullyTransparent();
        ApplyColorStateToEditor();
        TryPreview();
    }

    private void AdvancedColorTextLostFocus(object sender, RoutedEventArgs e) => CommitAdvancedColorEditor();

    private void AdvancedColorTextKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitAdvancedColorEditor();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            _colorState.CancelAdvanced();
            ApplyColorStateToEditor();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Commits the advanced hex box for the current target. Invalid text stays in the box with an
    /// inline message and never reaches the state, the preview or the saved settings.
    /// </summary>
    private bool CommitAdvancedColorEditor()
    {
        if (AdvancedColorText is null || ColorSwatches is null) return true;
        if (!_colorState.TryCommitAdvanced(AdvancedColorText.Text))
        {
            ShowAdvancedError(_colorState.AdvancedError ?? TaskbarColorEditorState.InvalidHexMessage);
            return false;
        }
        ApplyColorStateToEditor();
        TryPreview();
        return true;
    }

    private void ShowAdvancedError(string message)
    {
        AdvancedColorErrorText.Text = message;
        AdvancedColorErrorText.Visibility = Visibility.Visible;
    }

    private void MoreColorsClick(object sender, RoutedEventArgs e) => PickCustomColor();

    /// <summary>
    /// Minimal <see cref="System.Windows.Forms.IWin32Window"/> bridge: the WinForms colour dialog
    /// needs a Win32 owner, and this WPF window only exposes one as a HWND. Owning the dialog keeps
    /// it modal above the settings page instead of letting it slide behind it.
    /// </summary>
    private sealed class WindowHandleOwner : System.Windows.Forms.IWin32Window
    {
        public WindowHandleOwner(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }

    /// <summary>
    /// Opens the Windows colour dialog for the active target. The dialog only produces opaque RGB;
    /// the state applies it to the current target while preserving that target's existing alpha, so
    /// a transparent background stays transparent and a semi-transparent one keeps its opacity.
    /// Cancelling changes nothing and never previews. The dialog is owned by this window; if no HWND
    /// can be resolved it falls back to an unowned dialog so colour picking never stops working.
    /// </summary>
    private void PickCustomColor()
    {
        try
        {
            var current = _colorState.ActiveColor;
            using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
            var ownerHandle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            var choice = ownerHandle != IntPtr.Zero
                ? dialog.ShowDialog(new WindowHandleOwner(ownerHandle))
                : dialog.ShowDialog();
            if (choice != System.Windows.Forms.DialogResult.OK) return;
            var rgb = (uint)((dialog.Color.R << 16) | (dialog.Color.G << 8) | dialog.Color.B);
            if (!_colorState.TryApplyRgb(rgb)) return;
            ApplyColorStateToEditor();
            TryPreview();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            AppLog.Write("taskbar colour dialog failed", ex);
        }
    }

    private static System.Windows.Media.Brush BrushFrom(TaskbarArgbColor color) =>
        Frozen(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));

    private static System.Windows.Media.Brush Frozen(System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        // Commit the active advanced hex box first. Invalid text is reported inline and blocks the
        // save instead of silently persisting the previous colour as if it were the edit.
        if (!CommitAdvancedColorEditor())
        {
            System.Windows.MessageBox.Show(this, _colorState.AdvancedError ?? TaskbarColorEditorState.InvalidHexMessage,
                "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TaskbarDisplaySettings settings;
        HotkeySettings hotkeys;
        try
        {
            // Same reader as the live preview, then the existing Core validation decides whether it
            // can be saved. Only this path persists the bounded nudge/colours.
            settings = ReadDisplaySettings();
            settings.Validate();
            // The whitelist/conflict rules live in Core; the dialog only surfaces the message.
            hotkeys = ReadHotkeySettings();
            hotkeys.Validate();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = settings;
        HotkeyResult = hotkeys;
        DialogResult = true;
    }

    private static string LabelFor(TaskbarDisplayItem item) => item switch
    {
        TaskbarDisplayItem.CpuTemperature => "CPU 温度",
        TaskbarDisplayItem.FanRpm => "实际风扇 RPM",
        TaskbarDisplayItem.CpuPackagePower => "CPU 功耗（估算）",
        TaskbarDisplayItem.TargetRpm => "目标 RPM",
        TaskbarDisplayItem.TargetDuty => "当前 duty",
        TaskbarDisplayItem.RpmFeedback => "RPM 闭环状态",
        TaskbarDisplayItem.PowerMode => "性能模式",
        TaskbarDisplayItem.CpuUsage => "CPU 占用",
        TaskbarDisplayItem.MemoryUsage => "内存占用",
        TaskbarDisplayItem.Guardian => "Guardian 状态",
        TaskbarDisplayItem.WmiStatus => "WMI 状态",
        TaskbarDisplayItem.CpuFrequency => "CPU 频率",
        // Read-only LHM GPU temperature; opt-in only, never part of an existing settings file.
        TaskbarDisplayItem.GpuTemperature => "GPU 温度（LHM 只读）",
        _ => item.ToString()
    };

    private sealed class EditableItem(TaskbarDisplayItem item, string label, bool enabled) : INotifyPropertyChanged
    {
        private int _displayIndex;
        public event PropertyChangedEventHandler? PropertyChanged;
        public TaskbarDisplayItem Item { get; } = item;
        public string Label { get; } = label;
        public bool Enabled { get; set; } = enabled;
        /// <summary>Visible 1-based row number; refreshed after every reorder.</summary>
        public int DisplayIndex
        {
            get => _displayIndex;
            set
            {
                if (_displayIndex == value) return;
                _displayIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayIndex)));
            }
        }
        /// <summary>Accessible name of the drag handle; the handle is the only reorder hit target.</summary>
        public string DragHandleName => $"拖动调整 {Label} 的顺序";
    }

    /// <summary>
    /// One editable hotkey row: four modifier checkboxes plus a whitelisted key box. It only reads and
    /// writes the gesture; every rule is enforced by <see cref="HotkeySettings.Validate"/>.
    /// </summary>
    private sealed class HotkeyRow(HotkeyAction action)
    {
        private readonly CheckBox _control = new() { Content = "Ctrl", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _alt = new() { Content = "Alt", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _shift = new() { Content = "Shift", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _win = new() { Content = "Win", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _key = new() { Width = 80, ItemsSource = HotkeyKeyCatalog.Keys, VerticalAlignment = VerticalAlignment.Center };

        public HotkeyAction Action { get; } = action;

        public FrameworkElement Build()
        {
            var actionLabel = HotkeySettings.LabelFor(Action);
            const string gestureHelpText = "全局快捷键组合必须包含 Ctrl、Alt 或 Win；Shift 不能单独使用。主键仅允许 A-Z、0-9、F1-F12，同一组合不能重复；保存后生效。";
            System.Windows.Automation.AutomationProperties.SetName(_control, $"{actionLabel} 快捷键 Ctrl 修饰键");
            System.Windows.Automation.AutomationProperties.SetName(_alt, $"{actionLabel} 快捷键 Alt 修饰键");
            System.Windows.Automation.AutomationProperties.SetName(_shift, $"{actionLabel} 快捷键 Shift 修饰键");
            System.Windows.Automation.AutomationProperties.SetName(_win, $"{actionLabel} 快捷键 Win 修饰键");
            System.Windows.Automation.AutomationProperties.SetName(_key, $"{actionLabel} 快捷键主键");
            System.Windows.Automation.AutomationProperties.SetHelpText(_control, gestureHelpText);
            System.Windows.Automation.AutomationProperties.SetHelpText(_alt, gestureHelpText);
            System.Windows.Automation.AutomationProperties.SetHelpText(_shift, gestureHelpText);
            System.Windows.Automation.AutomationProperties.SetHelpText(_win, gestureHelpText);
            System.Windows.Automation.AutomationProperties.SetHelpText(_key, gestureHelpText);
            var modifiers = new StackPanel { Orientation = Orientation.Horizontal };
            modifiers.Children.Add(_control);
            modifiers.Children.Add(_alt);
            modifiers.Children.Add(_shift);
            modifiers.Children.Add(_win);
            Grid.SetColumn(modifiers, 1);
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = actionLabel, VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(modifiers);
            Grid.SetColumn(_key, 2);
            grid.Children.Add(_key);
            return grid;
        }

        public void Apply(HotkeyGesture gesture)
        {
            _control.IsChecked = gesture.Modifiers.HasFlag(HotkeyModifiers.Control);
            _alt.IsChecked = gesture.Modifiers.HasFlag(HotkeyModifiers.Alt);
            _shift.IsChecked = gesture.Modifiers.HasFlag(HotkeyModifiers.Shift);
            _win.IsChecked = gesture.Modifiers.HasFlag(HotkeyModifiers.Windows);
            _key.SelectedItem = HotkeyKeyCatalog.Normalize(gesture.Key) ?? HotkeyKeyCatalog.Keys[0];
        }

        public HotkeyGesture Read()
        {
            var modifiers = HotkeyModifiers.None;
            if (_control.IsChecked == true) modifiers |= HotkeyModifiers.Control;
            if (_alt.IsChecked == true) modifiers |= HotkeyModifiers.Alt;
            if (_shift.IsChecked == true) modifiers |= HotkeyModifiers.Shift;
            if (_win.IsChecked == true) modifiers |= HotkeyModifiers.Windows;
            return new HotkeyGesture(modifiers, _key.SelectedItem as string ?? string.Empty);
        }
    }
}
