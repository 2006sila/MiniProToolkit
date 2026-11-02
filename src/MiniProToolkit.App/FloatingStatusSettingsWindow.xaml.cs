using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MiniProToolkit.Core;
// WinForms implicit usings also bring in System.Drawing and System.Windows.Forms types.
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace MiniProToolkit.App;

/// <summary>
/// Editor for the desktop floating window (<c>floating-status.json</c>). It is a separate dialog
/// from <see cref="TaskbarSettingsWindow"/> on purpose: the two windows have independent item lists,
/// layouts, colours and lifetimes, so one file/dialog can never rewrite the other's settings.
/// <para>
/// It live-previews through one callback and writes nothing until 保存; 取消 / ESC / the title-bar
/// close push the opened settings (including the opened position) back through the same callback.
/// 保存 calls a synchronous save receiver and only closes when it reported success: a failed disk
/// write leaves the dialog open with every control and the dragged position intact and shows the
/// error in its own status line, so a retry saves the same values. It has no hardware, fan, Guardian
/// or sampling path: only display settings are read and written.
/// The dialog is deliberately non-modal (no owner, shown non-modally by the main window, which keeps
/// it single-instance and closes it during teardown) so the floating card stays draggable while it is
/// open: a drag updates the position this dialog would save, and cancelling restores the opened
/// position. The window position is therefore edited by dragging the card (no numeric position
/// editor), and the item order by dragging each row's handle or Ctrl+Up/Ctrl+Down.
/// </para>
/// </summary>
public partial class FloatingStatusSettingsWindow : Window
{
    private readonly ObservableCollection<EditableItem> _items;
    // Not readonly: an external "disable click-through" while this dialog is open patches the cancel
    // baseline so a later preview/cancel can never turn the click-through back on.
    private FloatingStatusSettings _initialSettings;
    // The position the saved result should carry: the opened position until MainWindow reports a
    // live drag of the floating window. Never edited by this dialog's own controls.
    private FloatingStatusSettings _position;
    private readonly Action<FloatingStatusSettings>? _preview;
    // Synchronous save receiver. It returns null after a successful disk write and a message when
    // the write failed; the dialog then stays open with every control (and the dragged position)
    // intact instead of losing the edits on a closed event.
    private readonly Func<FloatingStatusSettings, string?>? _save;
    private bool _saveFailed;
    // Row drag state: the row being dragged, its live index and the list's scroll viewer.
    private EditableItem? _draggedItem;
    private int _draggedIndex = -1;
    private ScrollViewer? _itemsScrollViewer;
    // The single source of truth for both floating colours (same pure state as the taskbar card).
    private readonly TaskbarColorEditorState _colorState;
    private bool _updatingColorEditor;
    // Enabled after the opened colours were pushed into the card; until then the Checked /
    // ValueChanged events fired by that initialisation must not preview or touch the state.
    private bool _colorEditorReady;
    // Enabled only after the whole dialog is initialised: Checked/ValueChanged fire during
    // InitializeComponent, before the item list and the colour state exist.
    private bool _ready;
    private static readonly System.Windows.Media.Brush SwatchBorderBrush = Frozen(System.Windows.Media.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly System.Windows.Media.Brush SelectedSwatchBrush = Frozen(System.Windows.Media.Color.FromRgb(0x66, 0xDE, 0xC4));

    /// <summary>
    /// The accepted settings, or <c>null</c> when the dialog was cancelled. The dialog closes itself
    /// with <see cref="Window.Close"/> (never <c>DialogResult</c>) because it is shown non-modally so
    /// the floating card can still be dragged while it is open.
    /// </summary>
    public FloatingStatusSettings? Result { get; private set; }

    /// <param name="settings">Settings the dialog opens with (position included; the dialog never edits it).</param>
    /// <param name="preview">Optional live-preview receiver. It must only apply the settings to the
    /// already existing floating window; a null receiver keeps the dialog save-only.</param>
    /// <param name="save">Optional synchronous save receiver: <c>null</c> means "written", a message
    /// means "not written" and is shown in the dialog, which then stays open. It is called once per
    /// 保存 click, never from a close event, so a failed write keeps every edit on screen and a retry
    /// writes the same values.</param>
    public FloatingStatusSettingsWindow(
        FloatingStatusSettings settings,
        Action<FloatingStatusSettings>? preview = null,
        Func<FloatingStatusSettings, string?>? save = null)
    {
        InitializeComponent();
        ObsidianWindowChrome.Apply(this);
        settings.Validate();
        _initialSettings = settings;
        _position = settings;
        _preview = preview;
        _save = save;
        _colorState = new TaskbarColorEditorState(settings.TextColor, settings.BackgroundColor);

        _items = new ObservableCollection<EditableItem>(
            Enum.GetValues<TaskbarDisplayItem>().Select(item => new EditableItem(
                item,
                TaskbarMetricFormatter.CardLabelFor(item),
                settings.Items.Contains(item))));
        // Configured order first, then the currently hidden items, so the list is a stable,
        // predictable superset of the saved list (same convention as the taskbar dialog).
        var ordered = settings.Items
            .Concat(Enum.GetValues<TaskbarDisplayItem>().Except(settings.Items))
            .Select(item => _items.First(entry => entry.Item == item))
            .ToArray();
        _items.Clear();
        foreach (var item in ordered) _items.Add(item);
        foreach (var item in _items) item.PropertyChanged += OnItemChanged;
        UpdateDisplayIndexes();
        ItemsList.ItemsSource = _items;

        EnabledCheck.IsChecked = settings.Enabled;
        CompactCheck.IsChecked = settings.Compact;
        TopmostCheck.IsChecked = settings.Topmost;
        LockedCheck.IsChecked = settings.IsLocked;
        // The two interaction switches: initialised from the saved settings before _ready is set, so
        // these assignments never preview or touch the card.
        ClickThroughCheck.IsChecked = settings.ClickThrough;
        SnapToEdgesCheck.IsChecked = settings.SnapToEdges;
        SelectLayoutMode(settings.LayoutMode);
        UpdateLayoutModeHint();
        FontFamilyText.Text = settings.FontFamily;
        FontSizeText.Text = settings.FontSize.ToString(CultureInfo.InvariantCulture);
        UpdatePositionStatus(settings);
        // Push the opened colours into the card last, then let the colour events preview.
        ApplyColorStateToEditor();
        _colorEditorReady = true;
        _ready = true;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditableItem.Enabled)) TryPreview();
    }

    private void UpdatePositionStatus(FloatingStatusSettings settings)
    {
        if (PositionStatusText is null) return;
        var monitor = string.IsNullOrWhiteSpace(settings.MonitorDeviceName)
            ? "主显示器（默认右下角）"
            : settings.MonitorDeviceName;
        PositionStatusText.Text = settings.LeftDip is { } left && settings.TopDip is { } top
            ? $"打开时位置：{monitor}，距该显示器工作区左上 {left.ToString("0.#", CultureInfo.InvariantCulture)}, " +
              $"{top.ToString("0.#", CultureInfo.InvariantCulture)} DIP。直接拖动悬浮窗即可修改，保存后记住；取消会恢复。"
            : $"打开时位置：{monitor}，位于工作区右下角（尚未拖动过）。直接拖动悬浮窗即可修改，保存后记住；取消会恢复。";
    }

    private void PreviewChanged(object sender, RoutedEventArgs e) => TryPreview();

    private void DisplayTextChanged(object sender, TextChangedEventArgs e) => TryPreview();

    /// <summary>
    /// Keeps the dialog's saved position in sync with a live drag of the floating window, so a later
    /// preview (and 保存) carries the dragged position instead of the position it opened with.
    /// </summary>
    public void UpdatePosition(FloatingStatusSettings settings)
    {
        _position = settings;
        UpdatePositionStatus(settings);
    }

    /// <summary>
    /// Moves the selected row one slot, keeps it selected and previews immediately. The list orders
    /// by drag handle or Ctrl+Up/Ctrl+Down only (same interaction as the taskbar dialog): the old
    /// up/down buttons were removed because they duplicated the taskbar's physical direction buttons.
    /// </summary>
    private void MoveSelected(int delta)
    {
        if (ItemsList.SelectedItem is not EditableItem item) return;
        var index = _items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _items.Count) return;
        _items.Move(index, target);
        UpdateDisplayIndexes();
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        TryPreview();
    }

    /// <summary>Ctrl+Up / Ctrl+Down only: a plain arrow key keeps moving the list selection.</summary>
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
    /// Starts a row drag. Only the row's own <c>Thumb</c> handle reorders, so clicking the Enabled
    /// checkbox still only toggles visibility (the checkbox is never a drag target).
    /// </summary>
    private void ItemDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _draggedItem = null;
        _draggedIndex = -1;
        if (sender is not System.Windows.Controls.Primitives.Thumb thumb) return;
        if (ItemsControl.ContainerFromElement(ItemsList, thumb) is not ListBoxItem { DataContext: EditableItem item }) return;
        _draggedItem = item;
        _draggedIndex = _items.IndexOf(item);
        ItemsList.SelectedItem = item;
        _itemsScrollViewer = FindScrollViewer(ItemsList);
    }

    /// <summary>Reorders live while the handle is dragged and previews each applied move.</summary>
    private void ItemDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_draggedItem is null || _draggedIndex < 0) return;
        var pointerY = System.Windows.Input.Mouse.GetPosition(ItemsList).Y;
        AutoScrollWhileDragging(pointerY);
        var target = TargetIndexFor(pointerY);
        if (target < 0 || target == _draggedIndex) return;
        _items.Move(_draggedIndex, target);
        _draggedIndex = target;
        UpdateDisplayIndexes();
        ItemsList.SelectedItem = _draggedItem;
        TryPreview();
    }

    /// <summary>A cancelled drag keeps the order already applied; only the transient state ends.</summary>
    private void ItemDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
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
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container) continue;
            var origin = container.TranslatePoint(new System.Windows.Point(0, 0), ItemsList);
            if (pointerY < origin.Y + (container.ActualHeight / 2)) return index;
            lastRealized = index;
        }
        return lastRealized;
    }

    /// <summary>Scrolls the list while the handle is dragged at its edge.</summary>
    private void AutoScrollWhileDragging(double pointerY)
    {
        const double Edge = 16;
        if (_itemsScrollViewer is not { } viewer) return;
        if (pointerY < Edge) viewer.LineUp();
        else if (pointerY > ItemsList.ActualHeight - Edge) viewer.LineDown();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, index)) is { } found)
                return found;
        return null;
    }

    /// <summary>Refreshes the visible 1-based row numbers after any reorder.</summary>
    private void UpdateDisplayIndexes()
    {
        for (var index = 0; index < _items.Count; index++) _items[index].DisplayIndex = index + 1;
    }

    private void LayoutModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLayoutModeHint();
        if (!_ready) return;
        TryPreview();
    }

    private void UpdateLayoutModeHint()
    {
        if (LayoutModeHint is null) return;
        LayoutModeHint.Text = ReadLayoutMode() switch
        {
            TaskbarLayoutMode.Inline => "C · 单行分段：一条横线，前 2 项一组、其余一组，中间一条细分隔线。",
            TaskbarLayoutMode.AdaptiveColumns => "D · 自适应多列：始终两行，按估算宽度自动增加列，列间 12 DIP、无分隔线。",
            TaskbarLayoutMode.LabelOverValue => "E · 标签上、数值下：每个指标一张小卡片。",
            _ => "A · 双列堆叠：两行一列，第 0 项在上、第 1 项在下（冒号格式），前 2 项与其余项目之间一条细分隔线。"
        };
    }

    private void SelectLayoutMode(TaskbarLayoutMode mode)
    {
        foreach (var entry in LayoutModeCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(entry.Tag as string, mode.ToString(), StringComparison.Ordinal))
            {
                LayoutModeCombo.SelectedItem = entry;
                return;
            }
        }
        LayoutModeCombo.SelectedIndex = 0;
    }

    private TaskbarLayoutMode ReadLayoutMode() =>
        LayoutModeCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
        Enum.TryParse<TaskbarLayoutMode>(tag, out var mode) && Enum.IsDefined(mode)
            ? mode
            : TaskbarLayoutMode.StackedPairs;

    private FloatingStatusSettings ReadSettings()
    {
        var selected = _items.Where(item => item.Enabled).Select(item => item.Item).ToArray();
        return _position with
        {
            Enabled = EnabledCheck.IsChecked == true,
            Items = selected,
            LayoutMode = ReadLayoutMode(),
            Compact = CompactCheck.IsChecked == true,
            FontFamily = FontFamilyText.Text.Trim(),
            FontSize = double.Parse(FontSizeText.Text, CultureInfo.InvariantCulture),
            TextColor = _colorState.TextHex,
            BackgroundColor = _colorState.BackgroundHex,
            Topmost = TopmostCheck.IsChecked == true,
            IsLocked = LockedCheck.IsChecked == true,
            ClickThrough = ClickThroughCheck.IsChecked == true,
            SnapToEdges = SnapToEdgesCheck.IsChecked == true
        };
    }

    /// <summary>
    /// "恢复默认位置": forgets the stored monitor/offset in the edit buffer only and previews the
    /// documented bottom-right corner. 保存 persists it, 取消 pushes the opened position back, and
    /// nothing else (Enabled, colours, items, font, lock, topmost, interaction switches) is touched.
    /// A locked card is deliberately still resettable: this is an explicit user request, not a drag.
    /// </summary>
    public void ResetPosition()
    {
        if (!_ready) return;
        _position = FloatingStatusInteraction.WithoutPosition(_position);
        UpdatePositionStatus(_position);
        TryPreview();
    }

    private void ResetPositionClick(object sender, RoutedEventArgs e) => ResetPosition();

    /// <summary>
    /// Called by the owner when an external "disable mouse click-through" succeeded or is at least
    /// applied for this session. Only the click-through preference is forced off - the cancel
    /// baseline and the position copy - so every other unsaved edit stays exactly as it is and a
    /// later preview or 取消 can never restore the click-through the user just escaped from. The
    /// checkbox is set behind the <c>_ready</c> guard so it does not fire a preview of its own; the
    /// owner has already applied the runtime state.
    /// </summary>
    public void ApplyClickThroughRecovery()
    {
        _initialSettings = FloatingStatusInteraction.WithoutClickThrough(_initialSettings);
        _position = FloatingStatusInteraction.WithoutClickThrough(_position);
        if (ClickThroughCheck is null) return;
        var wasReady = _ready;
        _ready = false;
        try { ClickThroughCheck.IsChecked = false; }
        finally { _ready = wasReady; }
    }

    /// <summary>
    /// Quick show/hide while this dialog is the single editor (tray entry / global hotkey). Only the
    /// 启用 checkbox is flipped and previewed; nothing is written to disk, so 保存 keeps the value and
    /// 取消 still restores the opened Enabled state.
    /// </summary>
    /// <returns>The previewed Enabled value after the toggle.</returns>
    public bool TogglePreviewVisibility()
    {
        if (!_ready || EnabledCheck is null) return false;
        EnabledCheck.IsChecked = EnabledCheck.IsChecked != true;
        return EnabledCheck.IsChecked == true;
    }

    /// <summary>
    /// Pushes the current edit buffer to the existing floating window. A half-typed value (empty
    /// font box, invalid colour) is not previewed yet; it is reported when saving. A failed preview
    /// is logged and never closes the dialog. No store, no disk write, no hardware call.
    /// </summary>
    private void TryPreview()
    {
        if (!_ready || _preview is null) return;
        FloatingStatusSettings preview;
        try
        {
            preview = ReadSettings();
            preview.Validate();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            ShowValidation("设置尚不完整，未预览（保存时会提示）。");
            return;
        }
        // A failed save stays visible while the user keeps editing; the next successful 保存 clears it.
        ShowValidation(_saveFailed ? "预览已更新，但上次保存失败，改动仍未写入磁盘。" : string.Empty);
        try
        {
            _preview(preview);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("floating status settings preview failed", ex);
        }
    }

    /// <summary>
    /// Any close that is not 保存 (取消, ESC, the title-bar close button) restores the exact settings
    /// and position the dialog opened with through the same preview callback, so no previewed value
    /// and no dragged position stays on screen.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (Result is null && _preview is not null)
        {
            try
            {
                _preview(_initialSettings);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("floating status settings preview revert failed", ex);
            }
        }
        base.OnClosed(e);
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!CommitAdvancedColorEditor())
        {
            ShowValidation(_colorState.AdvancedError ?? TaskbarColorEditorState.InvalidHexMessage);
            return;
        }
        FloatingStatusSettings settings;
        try
        {
            settings = ReadSettings();
            settings.Validate();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            ShowValidation(ex.GetBaseException().Message);
            return;
        }
        if (_save is not null)
        {
            string? error;
            try
            {
                error = _save(settings);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = ex.GetBaseException().Message;
            }
            if (error is not null)
            {
                // The disk write failed: keep the dialog open with every control and the dragged
                // position intact, state plainly that nothing was saved, and let 保存 retry.
                _saveFailed = true;
                ShowValidation("保存失败，未写入磁盘，改动仍未保存：" + error);
                return;
            }
            _saveFailed = false;
        }
        // Set the accepted result *before* closing: the close path then knows this was a save, not
        // a cancel, and the owner does not need to save a second time from its closed handler.
        Result = settings;
        Close();
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
    }

    private void ColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !TaskbarArgbColor.TryParse(tag, out var color)) return;
        if (!_colorState.TryApplyRgb(color.Rgb)) return;
        ApplyColorStateToEditor();
        TryPreview();
    }

    /// <summary>
    /// Re-points the shared samples/hex box/background-only controls at the selected target. It
    /// never changes a colour and never previews; a rejected advanced value keeps the target.
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

    /// <summary>Minimal IWin32Window bridge so the system colour dialog is owned by this window.</summary>
    private sealed class WindowHandleOwner : System.Windows.Forms.IWin32Window
    {
        public WindowHandleOwner(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }

    /// <summary>
    /// Opens the Windows colour dialog for the active target. It only supplies opaque RGB; the
    /// shared state keeps the target's existing alpha, so a semi-transparent background stays
    /// semi-transparent. Cancelling changes nothing and never previews.
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
            AppLog.Write("floating colour dialog failed", ex);
        }
    }

    /// <summary>Pushes the shared colour state into every part of the one card, guarded.</summary>
    private void ApplyColorStateToEditor()
    {
        if (TextColorTargetRadio is null || ColorSwatches is null) return;
        _updatingColorEditor = true;
        try
        {
            var isBackground = _colorState.ActiveTarget == TaskbarColorTarget.Background;
            TextColorTargetRadio.IsChecked = !isBackground;
            BackgroundColorTargetRadio.IsChecked = isBackground;
            TextColorChip.Background = BrushFrom(_colorState.TextColor);
            BackgroundColorChip.Background = BrushFrom(_colorState.BackgroundColor);
            ColorPreviewCard.Background = BrushFrom(_colorState.BackgroundColor);
            ColorPreviewText.Foreground = BrushFrom(_colorState.TextColor);
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
            BackgroundOpacityHint.Visibility = _colorState.BackgroundIsFullyTransparent
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _updatingColorEditor = false;
        }
    }

    private void UpdateColorSwatchSelection()
    {
        foreach (var button in ColorSwatches.Children.OfType<Button>())
        {
            if (button.Tag is not string tag || !TaskbarArgbColor.TryParse(tag, out var color) ||
                button.Content is not Border border)
                continue;
            var selected = _colorState.IsActiveRgbSelected(color.Rgb);
            border.BorderThickness = new Thickness(selected ? 2 : 1);
            border.BorderBrush = selected ? SelectedSwatchBrush : SwatchBorderBrush;
        }
    }

    private bool IsPresetRgb(uint rgb) => ColorSwatches.Children.OfType<Button>()
        .Any(button => button.Tag is string tag && TaskbarArgbColor.TryParse(tag, out var color) && color.Rgb == rgb);

    private static System.Windows.Media.Brush BrushFrom(TaskbarArgbColor color) =>
        Frozen(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));

    private static System.Windows.Media.Brush Frozen(System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class EditableItem(TaskbarDisplayItem item, string label, bool enabled) : INotifyPropertyChanged
    {
        private bool _enabled = enabled;
        private int _displayIndex;

        public event PropertyChangedEventHandler? PropertyChanged;

        public TaskbarDisplayItem Item { get; } = item;

        public string Label { get; } = label;

        /// <summary>1-based visible row number; refreshed after every drag/keyboard reorder.</summary>
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

        /// <summary>Accessible name of the drag handle, so the row is draggable for a screen reader too.</summary>
        public string DragHandleName => $"拖动调整顺序：{Label}";

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            }
        }
    }
}
