using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MiniProToolkit.Core;
// The App project also enables WinForms implicit usings, so System.Drawing brings in its own
// HorizontalAlignment/Rectangle/Color. Alias the WPF types explicitly instead of qualifying
// every use in the rendering code.
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ShapeRectangle = System.Windows.Shapes.Rectangle;
using VerticalAlignment = System.Windows.VerticalAlignment;
using WpfColor = System.Windows.Media.Color;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace MiniProToolkit.App;

public partial class TaskbarStatusWindow : Window
{
    // Safety ceiling for the rendered strip as a share of the taskbar's length. It keeps the
    // overlay in the start-side portion of the bar and reserves the rest for the centred icons
    // and the notification area. It is deliberately conservative because the tray/icon rectangles
    // are not measured in this pass; no exact avoidance is claimed.
    private const double MaxTaskbarShare = 0.4;

    // SWP_NOMOVE/SWP_NOSIZE are not declared by the shared NativeMethods file (it is used by every
    // window of the app); the light periodic z-order re-assert below never moves or resizes.
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;

    // Mutable only so the settings dialog can live-preview colours and the bounded nudge on the
    // existing overlays without a disk write or a window recreate. A save still replaces the
    // overlays through the normal MainWindow path.
    private TaskbarDisplaySettings _settings;
    private IntPtr _handle;
    private IntPtr _taskbarHandle;
    private bool _attached;
    private bool _guardianRunning;
    private bool _manualFanControlAllowed;
    private bool _fixedRpmAndCurveAllowed;
    private bool _powerModeControlAllowed;
    // Effective layout direction for the current sample: the real taskbar edge wins, the saved
    // Vertical preference is only the fallback for when no edge can be read.
    private bool _verticalLayout;
    // True while the overlay is hidden because the taskbar reported no usable rectangle
    // (auto-hidden taskbar / shell restart). Avoids repeated Hide calls and stale coordinates.
    private bool _hiddenForUnusableTaskbar;
    // Safe drawing bounds derived from the appbar rectangle alone (no notification-area probing).
    // Infinity means "not measured yet"; the first placement pass fills them in.
    private double _maxContentWidthDip = double.PositiveInfinity;
    private double _maxContentHeightDip = double.PositiveInfinity;
    // Resolved visual values, recomputed by ApplyLayout. The layout cells are rebuilt on every
    // sample, so these are applied to each new TextBlock instead of to one shared element.
    private System.Windows.Media.FontFamily _cellFontFamily = new("Segoe UI");
    private double _cellFontSize = 12;
    private System.Windows.Media.Brush _cellForeground = System.Windows.Media.Brushes.White;
    private System.Windows.Media.Brush _columnSeparator = System.Windows.Media.Brushes.Transparent;
    // Topology of the tree that is currently in StatusLayoutRoot, plus the live text elements of
    // that one render pass in plan order. A tick whose plan keeps the same topology only rewrites
    // text/tooltip/accessible name, so the 1 s sample never clears and recreates the 10-40
    // TextBlock/Border/Grid elements (and never invalidates their layout).
    private TaskbarRenderSignature? _renderedSignature;
    private readonly List<RenderedCell> _renderedCells = new(16);
    // The window tooltip is only re-assigned when the composed text really changed.
    private string? _statusToolTip;
    // Last native rectangle this overlay was really placed at, so a periodic tick with an identical
    // placement does not re-issue SetWindowPos + SWP_FRAMECHANGED. Reset on attach/style/DPI/show.
    private bool _hasPlacedRect;
    private int _placedX;
    private int _placedY;
    private int _placedWidth;
    private int _placedHeight;

    /// <summary>
    /// True while both this overlay's window and the taskbar window it was mounted on are alive.
    /// The hosted taskbar handle is part of the answer on purpose: after an Explorer restart the
    /// overlay HWND can still be valid while the appbar it was positioned against is gone, and the
    /// owner relies on this flag (its periodic taskbar discovery) to drop and recreate the overlay
    /// instead of waiting for the next shell event. An all-zero handle is *not* alive, which is the
    /// existing "not created yet" semantic the owner already treats as stale.
    /// </summary>
    public bool IsNativeWindowAlive =>
        _handle != IntPtr.Zero && NativeMethods.IsWindow(_handle) &&
        _taskbarHandle != IntPtr.Zero && NativeMethods.IsWindow(_taskbarHandle);
    public IntPtr TaskbarHandle => _taskbarHandle;

    public event Action? OpenRequested;
    public event Action? RestoreRequested;
    public event Action? ExitRequested;
    // Opens the already existing taskbar display-item settings (TaskbarSettingsWindow) from the
    // right-click menu. Same entry point as the main window button, no second settings path.
    public event Action? TaskbarSettingsRequested;
    public event Action<PowerMode>? PowerModeRequested;
    public event Action? FullSpeedRequested;
    public event Action<int>? FixedRpmRequested;
    public event Action? CurveRequested;
    private HardwareSnapshot? _snapshot;

    public TaskbarStatusWindow(TaskbarDisplaySettings settings, IntPtr taskbarHandle)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.Validate();
        _taskbarHandle = taskbarHandle;
        _verticalLayout = _settings.Vertical;
        InitializeComponent();
        // Restoring the overlay after an auto-hidden taskbar reappears must never steal focus.
        ShowActivated = false;
        ApplyLayout();
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonUp += OnStatusMouseLeftButtonUp;
        var menu = new ContextMenu();
        AddMenuItem(menu, "打开 MiniProToolkit", () => OpenRequested?.Invoke());
        AddMenuItem(menu, "任务栏显示项…", () => TaskbarSettingsRequested?.Invoke());
        menu.Items.Add(new Separator());
        var modes = new MenuItem { Header = "性能模式" };
        AddModeMenuItem(modes, "静音", PowerMode.Silence);
        AddModeMenuItem(modes, "均衡", PowerMode.Balance);
        AddModeMenuItem(modes, "性能", PowerMode.Performance);
        menu.Items.Add(modes);
        var fan = new MenuItem { Header = "风扇控制" };
        AddMenuItem(fan, "恢复 EC 自动", () => RestoreRequested?.Invoke());
        var full = AddMenuItem(fan, "满速", () => FullSpeedRequested?.Invoke());
        var curve = AddMenuItem(fan, "安全曲线", () => CurveRequested?.Invoke());
        var fixedRpmMenu = new MenuItem { Header = "固定 RPM" };
        foreach (var rpm in new[] { 2100, 2500, 2800, 3200, 3500, 4000 })
            AddMenuItem(fixedRpmMenu, $"{rpm} RPM", () => FixedRpmRequested?.Invoke(rpm));
        fan.Items.Add(fixedRpmMenu);
        var fixedRpm = fixedRpmMenu;
        menu.Items.Add(fan);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "退出", () => ExitRequested?.Invoke());
        ContextMenu = menu;
        ContextMenuOpening += (_, _) =>
        {
            var allowed = _guardianRunning && _manualFanControlAllowed;
            foreach (var mode in modes.Items.OfType<MenuItem>())
                mode.IsEnabled = _powerModeControlAllowed;
            full.IsEnabled = allowed;
            var rpmAllowed = allowed && _fixedRpmAndCurveAllowed &&
                _snapshot is { Error: null, CpuTemperatureC: < 90, FanFault: null } snapshot &&
                snapshot.RpmFeedbackState != FanRpmFeedbackState.Fault;
            curve.IsEnabled = rpmAllowed; fixedRpm.IsEnabled = rpmAllowed;
            foreach (var mode in modes.Items.OfType<MenuItem>())
                mode.IsChecked = _snapshot?.PowerMode == (PowerMode)mode.Tag!;
        };
    }

    /// <summary>
    /// Left click opens the main window. A double click means exactly the same thing, so only the
    /// first release of a click sequence raises <see cref="OpenRequested"/>: WPF reports
    /// <c>ClickCount == 1</c> for the first pair and 2/3... for the extra pairs of a multi-click, and
    /// the extra ones are dropped instead of opening/activating twice. This adds no second gesture
    /// and no hardware action - it only makes the existing single-click path idempotent per click
    /// sequence, so the right-click menu and its gating are untouched.
    /// </summary>
    private void OnStatusMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!TaskbarClickGesture.RequestsOpen(e.ClickCount)) return;
        OpenRequested?.Invoke();
    }

    private void AddModeMenuItem(ItemsControl menu, string label, PowerMode mode)
    {
        var item = AddMenuItem(menu, label, () => PowerModeRequested?.Invoke(mode));
        item.IsCheckable = true;
        item.Tag = mode;
    }

    private static MenuItem AddMenuItem(ItemsControl menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    public void UpdateSnapshot(HardwareSnapshot snapshot, bool guardianRunning, bool manualFanControlAllowed = true, bool fixedRpmAndCurveAllowed = true, bool powerModeControlAllowed = true)
    {
        _guardianRunning = guardianRunning;
        _manualFanControlAllowed = manualFanControlAllowed;
        _fixedRpmAndCurveAllowed = fixedRpmAndCurveAllowed;
        _powerModeControlAllowed = powerModeControlAllowed;
        _snapshot = snapshot;
        // The newest sample and the gating flags are always kept, but the visual tree is only
        // touched while the strip is really on screen: an auto-hidden overlay used to rebuild its
        // whole text tree (and its window tooltip) on every sampling tick. Nothing is lost in
        // between - RestoreWithLatestSnapshot()/TryAttachToTaskbar paint the kept sample.
        if (_hiddenForUnusableTaskbar || !IsVisible)
        {
            InvalidateStatusRender();
            if (_attached)
                PositionOnTaskbar();
            return;
        }
        RenderStatusLayoutCells(snapshot);
        UpdateStatusToolTip(snapshot);
        if (_attached)
            PositionOnTaskbar();
    }

    /// <summary>
    /// Re-assigns the overlay tooltip only when the composed text changed; handing WPF an identical
    /// string would invalidate the tooltip for nothing on every tick.
    /// </summary>
    private void UpdateStatusToolTip(HardwareSnapshot snapshot)
    {
        var toolTip = string.Join(Environment.NewLine, BuildDetails(snapshot));
        if (string.Equals(toolTip, _statusToolTip, StringComparison.Ordinal)) return;
        _statusToolTip = toolTip;
        ToolTip = toolTip;
    }

    // Separator follows the effective direction and the selected arrangement. A/C/D/E share the
    // same TaskbarMetric structure; only the placement and the per-row wording differ.
    private void RenderStatusLayoutCells(HardwareSnapshot snapshot)
    {
        var metrics = _settings.Items
            .Select(item => TaskbarMetricFormatter.Format(item, snapshot, _guardianRunning))
            .ToArray();
        RebuildStatusLayout(metrics);
    }

    /// <summary>
    /// Rebuilds the visible cells for one sample. Every ordering/column/overflow decision comes
    /// from the pure <see cref="TaskbarItemLayout.Build"/> plan; this method only maps that plan
    /// to WPF. The safe content budget (window cap minus the border padding) is passed to the
    /// plan, so a narrow strip produces an explicit "+N" marker instead of relying on a silent
    /// WPF clip. Every cell carries its own accessible name, so no hidden forwarding element is
    /// needed.
    /// </summary>
    private void RebuildStatusLayout(IReadOnlyList<TaskbarMetric> metrics)
    {
        if (metrics.Count == 0)
        {
            ClearStatusLayout();
            return;
        }

        var (widthBudget, heightBudget) = ContentBudgetDip();
        var plan = TaskbarItemLayout.Build(_settings.LayoutMode, metrics,
            widthBudget, _cellFontSize, _verticalLayout, heightBudget);
        var signature = TaskbarRenderSignature.Create(plan, _verticalLayout, _settings.Compact,
            _cellFontSize, widthBudget, heightBudget);
        // Same topology (mode, direction, font, budget, every cell's item/row/column/group/style,
        // the +N marker and its hidden list, the grid projection): the existing tree already shows
        // the right elements, so only the live wording/tooltips/names are rewritten in place.
        if (_renderedSignature is { } previous && previous.Matches(signature) &&
            _renderedCells.Count == signature.SlotCount)
        {
            UpdateRenderedCellWording(plan);
            SetAccessibleName(StatusLayoutRoot, "任务栏状态");
            return;
        }

        ClearStatusLayout();
        switch (plan.Mode)
        {
            case TaskbarLayoutMode.Inline:
                RebuildInline(plan);
                break;
            case TaskbarLayoutMode.LabelOverValue:
                RebuildCards(plan);
                break;
            default:
                RebuildGrid(plan);
                break;
        }
        // At most one kept reference per slot (13 shipped items plus the marker), dropped again by
        // the next rebuild and by OnClosed, so the cache never grows with the sampling rate.
        _renderedSignature = signature;
        AutomationProperties.SetName(StatusLayoutRoot, "任务栏状态");
    }

    /// <summary>
    /// Drops the rendered tree together with every cached control reference and the remembered
    /// topology, so no text element stays alive after a rebuild and the next render starts clean.
    /// </summary>
    private void ClearStatusLayout()
    {
        StatusLayoutRoot.Children.Clear();
        StatusLayoutRoot.RowDefinitions.Clear();
        StatusLayoutRoot.ColumnDefinitions.Clear();
        _renderedCells.Clear();
        _renderedSignature = null;
    }

    /// <summary>
    /// Makes the next render rebuild the tree even when the pure topology is unchanged. Used for
    /// changes the plan cannot see (brushes/colours) and for a strip that was kept off screen while
    /// the sampling kept running.
    /// </summary>
    private void InvalidateStatusRender() => _renderedSignature = null;

    /// <summary>Remembers one created text element in plan order (cell, card value, marker).</summary>
    private void TrackCell(TextBlock text) => _renderedCells.Add(new RenderedCell(text));

    /// <summary>Remembers one created E card: its value line, its label line and the card tooltip host.</summary>
    private void TrackCard(StackPanel card, TextBlock label, TextBlock value) =>
        _renderedCells.Add(new RenderedCell(value, label, card));

    /// <summary>
    /// Applies the current wording to the existing tree: text, tooltip and accessible name are only
    /// assigned when they differ, so an unchanged value never re-invalidates WPF. This is the whole
    /// per-tick cost of a sample whose topology did not change.
    /// </summary>
    private void UpdateRenderedCellWording(TaskbarMetricLayoutPlan plan)
    {
        var updates = TaskbarRenderTexts.Updates(plan);
        if (updates.Count != _renderedCells.Count)
        {
            // Cannot happen for a matching signature; rebuild instead of guessing an alignment.
            InvalidateStatusRender();
            return;
        }
        for (var index = 0; index < updates.Count; index++)
        {
            var update = updates[index];
            var cell = _renderedCells[index];
            SetText(cell.Text, update.Text);
            SetToolTip(cell.Text, update.ToolTip);
            SetAccessibleName(cell.Text, update.AccessibleName);
            if (cell.Label is { } label)
            {
                SetText(label, update.CardLabel);
                SetToolTip(label, update.ToolTip);
                SetAccessibleName(label, update.CardLabel);
            }
            if (cell.Card is { } card)
                SetToolTip(card, update.CardToolTip);
        }
    }

    private static void SetText(TextBlock block, string text)
    {
        if (string.Equals(block.Text, text, StringComparison.Ordinal)) return;
        block.Text = text;
    }

    private static void SetToolTip(FrameworkElement element, string? toolTip)
    {
        if (element.ToolTip is not string current)
        {
            if (toolTip is null && element.ToolTip is null) return;
        }
        else if (string.Equals(current, toolTip, StringComparison.Ordinal))
        {
            return;
        }
        element.ToolTip = toolTip;
    }

    private static void SetAccessibleName(DependencyObject element, string? name)
    {
        if (string.Equals(AutomationProperties.GetName(element), name, StringComparison.Ordinal)) return;
        AutomationProperties.SetName(element, name);
    }

    /// <summary>
    /// One kept text element of the current tree. Created only by a rebuild, in plan/slot order; the
    /// value line of an E card also keeps its label line and the card panel (for the pair tooltip).
    /// </summary>
    private sealed class RenderedCell(TextBlock text, TextBlock? label = null, FrameworkElement? card = null)
    {
        public TextBlock Text { get; } = text;
        public TextBlock? Label { get; } = label;
        public FrameworkElement? Card { get; } = card;
    }

    /// <summary>
    /// Safe content size in DIPs: the appbar-derived cap minus the border padding that
    /// <see cref="ApplyLayout"/> applies. This is the budget the pure planner fits into; Infinity
    /// means the bounds have not been measured yet.
    /// </summary>
    private (double Width, double Height) ContentBudgetDip()
    {
        var along = _settings.Compact ? 2 : 4;
        var across = _settings.Compact ? 0 : 1;
        var horizontalPadding = _verticalLayout ? across : along;
        var verticalPadding = _verticalLayout ? along : across;
        return (Budget(_maxContentWidthDip, horizontalPadding), Budget(_maxContentHeightDip, verticalPadding));

        static double Budget(double cap, double padding) =>
            double.IsPositiveInfinity(cap) ? double.PositiveInfinity : Math.Max(1, cap - padding * 2);
    }

    private void RebuildInline(TaskbarMetricLayoutPlan plan)
    {
        if (_verticalLayout)
        {
            // Vertical taskbar: the prototype degrades C to one item per line, without a colon.
            var stack = new StackPanel
            {
                Orientation = WpfOrientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            foreach (var cell in plan.Cells)
            {
                var block = CreateCellText(cell.Metric.Text, cell.Metric, AccessibleNameFor(cell.Metric));
                TrackCell(block);
                stack.Children.Add(block);
            }
            if (plan.Overflow is { } marker)
            {
                var overflowBlock = CreateOverflowMarker(marker);
                TrackCell(overflowBlock);
                stack.Children.Add(overflowBlock);
            }
            StatusLayoutRoot.Children.Add(stack);
            return;
        }

        // Horizontal taskbar: one line split into two groups by exactly one 1px divider. The
        // wording of every item stays the unchanged TaskbarMetric.Text; only the separators and
        // the group gap are new.
        var panel = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var currentGroup = -1;
        foreach (var cell in plan.Cells)
        {
            AddInlineSeparator(panel, currentGroup, cell.Group);
            currentGroup = cell.Group;
            var block = CreateCellText(cell.Metric.Text, cell.Metric, AccessibleNameFor(cell.Metric));
            TrackCell(block);
            panel.Children.Add(block);
        }
        if (plan.Overflow is { } overflow)
        {
            // The marker belongs to the second group, so it can introduce the divider itself.
            AddInlineSeparator(panel, currentGroup, group: 1);
            var overflowBlock = CreateOverflowMarker(overflow);
            TrackCell(overflowBlock);
            panel.Children.Add(overflowBlock);
        }
        StatusLayoutRoot.Children.Add(panel);
    }

    private void AddInlineSeparator(StackPanel panel, int previousGroup, int group)
    {
        if (previousGroup < 0) return;
        if (previousGroup == group)
        {
            panel.Children.Add(new Border { Width = TaskbarItemLayout.InlineItemGapDip });
            return;
        }
        panel.Children.Add(new Border { Width = TaskbarItemLayout.GroupGapDip });
        panel.Children.Add(new ShapeRectangle
        {
            Width = TaskbarItemLayout.SeparatorWidthDip,
            Height = Math.Max(1, _cellFontSize * 1.05),
            Fill = _columnSeparator,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new Border { Width = TaskbarItemLayout.GroupGapDip });
    }

    private void RebuildCards(TaskbarMetricLayoutPlan plan)
    {
        var panel = new StackPanel
        {
            Orientation = _verticalLayout ? WpfOrientation.Vertical : WpfOrientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var first = true;
        foreach (var cell in plan.Cells)
        {
            AddCardSeparator(panel, first);
            first = false;
            if (cell.Style == TaskbarCellStyle.Card)
            {
                var (card, label, value) = CreateCard(cell.Metric);
                TrackCard(card, label, value);
                panel.Children.Add(card);
            }
            else
            {
                // Vertical E: every item after the first two is one plain, colon-free row.
                var block = CreateCellText(cell.Metric.Text, cell.Metric, AccessibleNameFor(cell.Metric));
                TrackCell(block);
                panel.Children.Add(block);
            }
        }
        if (plan.Overflow is { } overflow)
        {
            AddCardSeparator(panel, first);
            var overflowBlock = CreateOverflowMarker(overflow);
            TrackCell(overflowBlock);
            panel.Children.Add(overflowBlock);
        }
        StatusLayoutRoot.Children.Add(panel);
    }

    private void AddCardSeparator(StackPanel panel, bool first)
    {
        if (first) return;
        panel.Children.Add(_verticalLayout
            ? new Border { Height = TaskbarItemLayout.RowGapDip }
            : new Border { Width = TaskbarItemLayout.CardGapDip });
    }

    /// <summary>
    /// One E card: a small secondary label line above a bold value line. The label's accessible
    /// name is only the label and the value's only the value, so a screen reader never announces
    /// the combined label/value pair twice; the pair is available in the card tooltip.
    /// </summary>
    private (StackPanel Card, TextBlock Label, TextBlock Value) CreateCard(TaskbarMetric metric)
    {
        var card = new StackPanel { Orientation = WpfOrientation.Vertical };
        var label = CreateCellText(metric.CardLabel, metric, metric.CardLabel);
        // The label line stays secondary; the value keeps the full text size and is bold.
        label.FontSize = Math.Max(8, _cellFontSize * 0.78);
        label.Opacity = 0.78;
        var value = CreateCellText(metric.ValueText, metric, metric.ValueText);
        value.FontWeight = FontWeights.SemiBold;
        card.ToolTip = AccessibleNameFor(metric);
        card.Children.Add(label);
        card.Children.Add(value);
        return (card, label, value);
    }

    /// <summary>
    /// A/D: the plan's two-row grid (a single row when only one item is visible, a single column
    /// on a vertical taskbar). A's only separator sits between its first pair group and the rest;
    /// every further column uses D's 12 DIP gap and no line. The whole column order (group boundary
    /// gap/line/gap, plain gap, packed metric columns, marker column) comes from the pure
    /// <see cref="TaskbarGridLayout.Project(TaskbarMetricLayoutPlan, bool)"/> projection, so a
    /// rendered grid index can never drift from the planned column it shows.
    /// </summary>
    private void RebuildGrid(TaskbarMetricLayoutPlan plan)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var layout = TaskbarGridLayout.Project(plan, _verticalLayout);
        foreach (var column in layout.Columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = column.IsAuto ? GridLength.Auto : new GridLength(column.WidthDip)
            });
        }
        var rowCount = plan.Rows + (plan.Overflow is null ? 0 : 1);
        for (var row = 0; row < rowCount; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The 1px line is drawn inside its own 1 DIP column, so both 10 DIP gap columns keep their
        // full width and the line can never be pushed into the leading gap.
        for (var index = 0; index < layout.Columns.Count; index++)
        {
            if (layout.Columns[index].Kind != TaskbarGridColumnKind.Separator) continue;
            var line = new ShapeRectangle
            {
                Width = TaskbarItemLayout.SeparatorWidthDip,
                Fill = _columnSeparator,
                Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(line, index);
            Grid.SetRowSpan(line, Math.Max(1, plan.Rows));
            grid.Children.Add(line);
        }

        foreach (var cell in plan.Cells)
        {
            var metric = cell.Metric;
            var text = cell.Style == TaskbarCellStyle.Colon ? metric.ColonText : metric.Text;
            var block = CreateCellText(text, metric, AccessibleNameFor(metric));
            Grid.SetRow(block, cell.Row);
            Grid.SetColumn(block, layout.MetricColumns[cell.Column]);
            TrackCell(block);
            grid.Children.Add(block);
        }

        if (plan.Overflow is { } overflow)
        {
            var marker = CreateOverflowMarker(overflow);
            TrackCell(marker);
            if (_verticalLayout)
            {
                // A vertical strip is only as wide as the bar: the marker gets its own row under
                // the last metric instead of a column that the bar could not show.
                Grid.SetRow(marker, plan.Rows);
                Grid.SetColumn(marker, 0);
            }
            else
            {
                Grid.SetRow(marker, 0);
                Grid.SetRowSpan(marker, Math.Max(1, plan.Rows));
                Grid.SetColumn(marker, layout.OverflowColumn);
            }
            grid.Children.Add(marker);
        }
        StatusLayoutRoot.Children.Add(grid);
    }

    private TextBlock CreateCellText(string text, TaskbarMetric? metric, string? accessibleName = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = _cellFontFamily,
            FontSize = _cellFontSize,
            Foreground = _cellForeground,
            TextWrapping = TextWrapping.NoWrap,
            // Last-resort guard only: the plan already fits the estimated strip into the safe
            // budget. A cell whose fixed label alone is wider than the whole strip still trims,
            // but its tooltip/accessible name keep the full text and every omitted item is
            // counted in the visible +N marker.
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = double.IsPositiveInfinity(_maxContentWidthDip) ? double.PositiveInfinity : Math.Max(1, _maxContentWidthDip)
        };
        // Tabular digits keep "9%" and "100%" the same width, so a live sample never makes the
        // text jitter; the plan's geometry is based on stable widths regardless.
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        if (metric is { } value)
            block.ToolTip = value.Detail is { Length: > 0 } detail
                ? value.Text + Environment.NewLine + detail
                : value.Text;
        AutomationProperties.SetName(block, accessibleName ?? text);
        return block;
    }

    /// <summary>The one semantic name of one metric, independent of the per-mode wording.</summary>
    private static string AccessibleNameFor(TaskbarMetric metric) =>
        metric.CardLabel + " " + metric.ValueText;

    /// <summary>
    /// The explicit overflow cell: "+N" stays visible, its accessible name says how many metrics
    /// are hidden and its tooltip lists every omitted metric with its full text and detail.
    /// </summary>
    private TextBlock CreateOverflowMarker(TaskbarOverflowMarker overflow)
    {
        var block = CreateCellText(overflow.Text, metric: null, overflow.AccessibleName);
        block.ToolTip = overflow.ToolTip;
        block.Opacity = 0.9;
        return block;
    }

    /// <summary>
    /// Applies a live-previewed settings instance to this existing overlay: the new colours, font,
    /// padding, layout mode items and the bounded DIP nudge are applied to the window and the last
    /// sample is re-rendered, then the window is repositioned. Pure presentation: no disk write, no
    /// overlay recreate, no hardware call and no snapshot read. A throw is caught by the caller
    /// (the settings dialog), so an unusable preview value can never close that dialog.
    /// </summary>
    public void UpdateDisplaySettings(TaskbarDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings;
        // Colours/brushes are not part of the pure topology, so the render is forced here.
        InvalidateStatusRender();
        ApplyLayout();
        if (_snapshot is { } snapshot) RenderStatusLayoutCells(snapshot);
        // Let WPF measure the new padding/font before the placement reads ActualWidth/ActualHeight.
        UpdateLayout();
        if (_attached) PositionOnTaskbar();
    }

    /// <summary>
    /// Re-renders an existing sample after the user changed the display source. Keeps
    /// the current control-gating flags and never touches hardware.
    /// </summary>
    public void UpdateDisplaySnapshot(HardwareSnapshot snapshot) =>
        UpdateSnapshot(snapshot, _guardianRunning, _manualFanControlAllowed,
            _fixedRpmAndCurveAllowed, _powerModeControlAllowed);

    public void TryAttachToTaskbar()
    {
        if (_handle == IntPtr.Zero || !NativeMethods.IsWindow(_handle) ||
            _taskbarHandle == IntPtr.Zero || !NativeMethods.IsWindow(_taskbarHandle))
            return;

        if (!_attached)
        {
            // Keep this as a real WPF top-level window. Reparenting its HWND into
            // Explorer can yield a correctly positioned child that WPF never paints.
            // A no-activate topmost tool window provides the same taskbar placement
            // without crossing the Explorer process / integrity boundary.
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlStyle,
                new IntPtr(NativeMethods.WsPopup | NativeMethods.WsVisible));
            // The extended style is OR-ed, never replaced: AllowsTransparency makes WPF render the
            // window as a per-pixel layered window (WS_EX_LAYERED), and dropping that bit would
            // leave a blank, unpainted overlay.
            var exStyle = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle,
                new IntPtr(exStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow));
            _attached = true;
            // A fresh attach writes the window style, so the next placement must really run with
            // SWP_FRAMECHANGED even when the rectangle happens to be unchanged.
            _hasPlacedRect = false;
            AppLog.Write("taskbar status using topmost non-activating overlay");
            // Only the real first attach paints here: the owner calls TryAttachToTaskbar on every
            // sampling tick, and a repaint of the previous sample (plus UpdateLayout) on each of
            // them would duplicate the render UpdateSnapshot already did for the new values.
            // The newest kept sample is handled by UpdateSnapshot; a return from the hidden state
            // is handled by PositionOnTaskbar -> RestoreWithLatestSnapshot. A null snapshot (the
            // very first Show before any sample) is a no-op, so the normal sampling tick paints.
            RefreshStatusFromLatestSnapshot();
        }

        // Every tick (already attached and visible): placement probe only, no re-render.
        PositionOnTaskbar();
    }

    /// <summary>
    /// Really closed (shutdown or a taskbar change that recreates the overlay): every cached control
    /// reference and the remembered topology are dropped, so nothing keeps a dead visual alive.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        ClearStatusLayout();
        _statusToolTip = null;
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        // The overlay is a transparent layered window: the Windows 11 automatic rounded corners
        // (and the DWM border that comes with them) are turned off for this window only, so the
        // text sits directly on the taskbar pixels. The OSD and the normal windows keep theirs.
        ObsidianWindowChrome.ApplyDontRoundCorners(_handle);
        TryAttachToTaskbar();
    }

    private void ApplyLayout()
    {
        // TrafficMonitor-like padding: the window is the rendered text plus at most a few DIPs, so
        // the clickable/target area is essentially the text itself and no large transparent block
        // is kept around it (a fully transparent layered window additionally lets the mouse fall
        // through its unpainted pixels, so the taskbar's empty area and its right-click menu keep
        // working). Compact still means slightly tighter font and padding.
        var along = _settings.Compact ? 2 : 4;
        var across = _settings.Compact ? 0 : 1;
        RootBorder.Padding = _verticalLayout
            ? new Thickness(across, along, across, along)
            : new Thickness(along, across, along, across);
        // Safety cap from the appbar rectangle only. The window and the content both stop here,
        // so a long C line or many A columns are clipped instead of reaching the system icons.
        RootBorder.MaxWidth = double.IsPositiveInfinity(_maxContentWidthDip)
            ? double.PositiveInfinity
            : Math.Max(48, _maxContentWidthDip);
        RootBorder.MaxHeight = double.IsPositiveInfinity(_maxContentHeightDip)
            ? double.PositiveInfinity
            : Math.Max(16, _maxContentHeightDip);
        // The configured background stays a user choice: the default #00000000 paints nothing (text
        // directly on the taskbar), while any #RRGGBB / #AARRGGBB value is honoured as an explicit
        // contrast card. The window itself always stays transparent so an unset/unusable value can
        // never bring the old opaque black plate back.
        RootBorder.Background = ParseBrush(_settings.BackgroundColor) ?? System.Windows.Media.Brushes.Transparent;
        _cellForeground = ParseBrush(_settings.TextColor) ?? System.Windows.Media.Brushes.White;
        _cellFontFamily = CreateFontFamily(_settings.FontFamily);
        _cellFontSize = _settings.Compact ? _settings.FontSize : _settings.FontSize + 1;
        // The prototype uses a 22% column rule; derive it from the configured text colour so it
        // stays visible on a light taskbar and subtle on a dark one.
        var separatorColor = _cellForeground is SolidColorBrush solid ? solid.Color : Colors.White;
        _columnSeparator = new SolidColorBrush(WpfColor.FromArgb(56, separatorColor.R, separatorColor.G, separatorColor.B));
        // Brushes, padding and the font/compact values are not part of the pure topology, so a
        // settings/direction/budget change always rebuilds instead of keeping old styled elements.
        InvalidateStatusRender();
    }

    /// <summary>
    /// A DPI change invalidates the remembered native rectangle: the next placement really runs so
    /// a scale change can never be swallowed by the periodic no-op.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _hasPlacedRect = false;
    }

    private static System.Windows.Media.FontFamily CreateFontFamily(string name)
    {
        try { return new System.Windows.Media.FontFamily(name); }
        catch (ArgumentException) { return new System.Windows.Media.FontFamily("Segoe UI"); }
    }

    private static System.Windows.Media.Brush? ParseBrush(string value)
    {
        try { return new BrushConverter().ConvertFromString(value) as System.Windows.Media.Brush; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            // Settings.Validate already rejects malformed colors; this keeps a hand-edited file
            // from breaking the overlay paint path.
            return null;
        }
    }

    private void PositionOnTaskbar()
    {
        if (_handle == IntPtr.Zero) return;

        var appBar = NativeMethods.GetTaskbarPosition(_taskbarHandle);
        var dpi = NativeMethods.GetDpiForWindow(_taskbarHandle);
        var scale = dpi > 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;

        // Derive the safe drawing bounds from the appbar rectangle only. No notification-area or
        // icon geometry is probed in this pass, so this is a conservative width/height ceiling
        // (start side of the bar) rather than verified tray avoidance. This also re-applies the
        // layout and re-plans the current sample, so the content already fits the new cap.
        UpdateSafeContentBounds(appBar.Edge, appBar.Rect.Left, appBar.Rect.Top, appBar.Rect.Right, appBar.Rect.Bottom, scale);

        // Resolve the real direction before the placement math. A left/right taskbar can turn the
        // strip from horizontal to vertical (or back) at any refresh: applying the layout and
        // letting WPF measure it first means the placement below never reuses the old orientation's
        // ActualWidth/ActualHeight, i.e. the first placement after a direction change is correct.
        var edge = ToTaskbarEdge(appBar.Edge);
        var targetVertical = TaskbarOverlayLayout.UseVerticalLayout(edge, _settings.Vertical);
        if (targetVertical != _verticalLayout)
        {
            _verticalLayout = targetVertical;
            ApplyLayout();
            if (_snapshot is { } directionSnapshot) RenderStatusLayoutCells(directionSnapshot);
            UpdateLayout();
        }

        // Pure placement math (MiniProToolkit.Core.TaskbarOverlayLayout): the real appbar edge
        // decides the direction, the measured text width decides the window width, and an empty
        // rect aborts the placement. The overlay is pinned to the taskbar's starting edge (left
        // end of a horizontal bar, top of a vertical bar).
        bool TryComputeOverlayPlacement(out TaskbarOverlayPlacement resolved) =>
            TaskbarOverlayLayout.TryComputePlacement(
                edge,
                appBar.Rect.Left,
                appBar.Rect.Top,
                appBar.Rect.Right,
                appBar.Rect.Bottom,
                _settings.Vertical,
                ActualWidth,
                ActualHeight,
                scale,
                out resolved,
                _maxContentWidthDip,
                _maxContentHeightDip,
                // Bounded user nudge in physical screen directions, still clamped inside the appbar.
                offsetXDip: _settings.OffsetXDip,
                offsetYDip: _settings.OffsetYDip);

        if (!TryComputeOverlayPlacement(out var placement))
        {
            HideForUnusableTaskbar();
            return;
        }

        // Coming back from a hidden state (taskbar popped up again) must not activate the window;
        // WS_EX_NOACTIVATE plus ShowActivated=false already keep focus where it was. It also must
        // not come back blank: the tree is not maintained while the strip is off screen, so the
        // newest kept sample is painted here. The repaint can change the measured size, so the
        // rectangle is resolved once more before it is applied.
        var restored = RestoreWithLatestSnapshot();
        if (restored && !TryComputeOverlayPlacement(out placement))
        {
            HideForUnusableTaskbar();
            return;
        }

        // Pure periodic no-op: the native rectangle already has exactly this origin and size and
        // nothing was re-attached/re-shown/DPI-changed. A full SetWindowPos with SWP_FRAMECHANGED on
        // every sampling tick would only churn the window manager (frame change, WM_WINDOWPOSCHANGED,
        // DWM update) for an unchanged rectangle, so only the topmost z-order is re-asserted with a
        // move/size/frame-free call. The first placement after a style/attach/DPI/show change always
        // runs in full (_hasPlacedRect is reset there), so that necessary frame change is kept.
        if (!restored && IsSameNativePlacement(placement))
        {
            NativeMethods.SetWindowPos(_handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | NativeMethods.SwpNoActivate);
            return;
        }

        // Make the overlay topmost but non-activating so it paints above the taskbar
        // without stealing focus. Its small window stays confined to the appbar area.
        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpShowWindow |
            NativeMethods.SwpFrameChanged);
        _hasPlacedRect = true;
        _placedX = placement.X;
        _placedY = placement.Y;
        _placedWidth = placement.Width;
        _placedHeight = placement.Height;
    }

    /// <summary>
    /// True when the last real placement already put the window at exactly this native rectangle.
    /// Only the pure periodic reposition compares; any size/style/show/DPI change resets the cache.
    /// </summary>
    private bool IsSameNativePlacement(TaskbarOverlayPlacement placement) =>
        _hasPlacedRect && _placedX == placement.X && _placedY == placement.Y &&
        _placedWidth == placement.Width && _placedHeight == placement.Height;

    /// <summary>
    /// Coming back from <see cref="HideForUnusableTaskbar"/>: shows the overlay again and repaints
    /// the newest kept sample, so a strip that was hidden while its taskbar was auto-hidden (or
    /// absent) returns with current values instead of a blank or stale tree. Returns true when the
    /// overlay was really restored, so the caller re-resolves and re-applies the placement.
    /// </summary>
    private bool RestoreWithLatestSnapshot()
    {
        if (!_hiddenForUnusableTaskbar) return false;
        RestoreFromUnusableTaskbar();
        _hasPlacedRect = false;
        RefreshStatusFromLatestSnapshot();
        return true;
    }

    /// <summary>
    /// Paints the newest kept snapshot into the existing tree (an unchanged topology only rewrites
    /// the wording). Used after an attach/show, never on the hidden sampling path.
    /// </summary>
    private void RefreshStatusFromLatestSnapshot()
    {
        if (_snapshot is not { } snapshot) return;
        RenderStatusLayoutCells(snapshot);
        UpdateStatusToolTip(snapshot);
        // Let WPF measure the repainted strip before the placement reads ActualWidth/ActualHeight.
        UpdateLayout();
    }

    /// <summary>
    /// Resolves the safe content size from the appbar rectangle alone and re-renders when it
    /// changed. A horizontal bar keeps its full height but at most <see cref="MaxTaskbarShare"/> of
    /// the length; a vertical bar keeps its full width but the same share of the height. This is a
    /// conservative cap that leaves the rest of the bar to the centred icons and the notification
    /// area; those rectangles are not measured, so exact avoidance is not claimed.
    /// </summary>
    private void UpdateSafeContentBounds(NativeMethods.AppBarEdge edge, int left, int top, int right, int bottom, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;
        var barWidth = Math.Max(1, right - left) / scale;
        var barHeight = Math.Max(1, bottom - top) / scale;
        var vertical = edge switch
        {
            NativeMethods.AppBarEdge.Left or NativeMethods.AppBarEdge.Right => true,
            NativeMethods.AppBarEdge.Top or NativeMethods.AppBarEdge.Bottom => false,
            _ => _settings.Vertical
        };
        var width = vertical ? barWidth : barWidth * MaxTaskbarShare;
        var height = vertical ? barHeight * MaxTaskbarShare : barHeight;
        if (Math.Abs(width - _maxContentWidthDip) < 0.5 && Math.Abs(height - _maxContentHeightDip) < 0.5)
            return;
        _maxContentWidthDip = width;
        _maxContentHeightDip = height;
        ApplyLayout();
        if (_snapshot is { } snapshot) RenderStatusLayoutCells(snapshot);
        // Let WPF measure the new cap before PositionOnTaskbar reads ActualWidth/ActualHeight.
        UpdateLayout();
    }

    /// <summary>
    /// Maps the native appbar edge to the pure layout enum. An unexpected value maps to null so
    /// the layout falls back to the saved Vertical preference instead of guessing.
    /// </summary>
    private static TaskbarEdge? ToTaskbarEdge(NativeMethods.AppBarEdge edge) => edge switch
    {
        NativeMethods.AppBarEdge.Left => TaskbarEdge.Left,
        NativeMethods.AppBarEdge.Top => TaskbarEdge.Top,
        NativeMethods.AppBarEdge.Right => TaskbarEdge.Right,
        NativeMethods.AppBarEdge.Bottom => TaskbarEdge.Bottom,
        _ => null
    };

    /// <summary>
    /// Hides the overlay while the taskbar has no usable rectangle (auto-hide in effect, shell
    /// restarting). The window keeps its handle, events and last snapshot, so the next valid
    /// rectangle can show it again at the new position; no control logic runs in between.
    /// </summary>
    private void HideForUnusableTaskbar()
    {
        if (_hiddenForUnusableTaskbar) return;
        // Still inside the first Show() (this runs from SourceInitialized): a Hide() here would
        // be undone by the pending Show, so defer to the next refresh tick.
        if (!IsLoaded) return;
        _hiddenForUnusableTaskbar = true;
        AppLog.Write("taskbar status hidden: taskbar rect unavailable (auto-hidden or empty)");
        try { Hide(); }
        catch (InvalidOperationException) { _hiddenForUnusableTaskbar = false; }
    }

    /// <summary>
    /// Shows the overlay again after <see cref="HideForUnusableTaskbar"/>. Uses the WPF Show so
    /// the normal window state is restored before it is repositioned.
    /// </summary>
    private void RestoreFromUnusableTaskbar()
    {
        if (!_hiddenForUnusableTaskbar) return;
        _hiddenForUnusableTaskbar = false;
        try { Show(); }
        catch (InvalidOperationException) { return; }
        AppLog.Write("taskbar status shown again: taskbar rect restored");
    }

    private IEnumerable<string> BuildDetails(HardwareSnapshot snapshot)
    {
        var sourceLabel = snapshot.DisplayCpuSource == CpuDisplaySource.LibreHardwareMonitor
            ? "LHM"
            : "EC/Energy Meter";
        yield return $"主显示源: {sourceLabel}（温度 {(snapshot.DisplayTemperatureFromLhm ? "LHM" : "EC")}，功耗 {(snapshot.DisplayPowerFromLhm ? "LHM" : "Energy Meter")}）";
        yield return $"CPU 温度（显示）: {TaskbarMetricFormatter.Temperature(snapshot.EffectiveDisplayCpuTemperatureC, snapshot.DisplayTemperatureFromLhm)}";
        yield return $"CPU 温度（EC，风扇控制/保护）: {TaskbarMetricFormatter.Nullable(snapshot.CpuTemperatureC, "°C")}";
        yield return $"CPU 封装功耗（显示，非整机）: {TaskbarMetricFormatter.Power(snapshot.EffectiveDisplayCpuPackagePowerWatts)}";
        yield return $"Energy Meter 功耗: {TaskbarMetricFormatter.Power(snapshot.CpuPackagePowerWatts)}";
        if (snapshot.DisplaySourceStatus is { Length: > 0 } displayStatus)
            yield return $"显示源提示: {displayStatus}";
        yield return $"CPU 频率: {MetricDisplayFormat.Frequency(snapshot.CpuFrequencyMhz, snapshot.CpuFrequencyBaseMhz, snapshot.CpuFrequencyPercentOfBase)}";
        if (snapshot.CpuFrequencyStatus is { Length: > 0 } frequencyStatus)
            yield return $"频率提示: {frequencyStatus}";
        // GPU is an optional read-only LHM value; the sensor name and the redacted reader state
        // are shown here so an unusable reading is distinguishable from "not sampled yet".
        yield return $"GPU 温度（LHM 只读，不参与控制）: {TaskbarMetricFormatter.GpuTemperature(snapshot.GpuTemperatureC)}";
        if (snapshot.GpuSensorName is { Length: > 0 } gpuSensor)
            yield return $"GPU 传感器: {gpuSensor}";
        if (snapshot.GpuSensorStatus is { Length: > 0 } gpuStatus)
            yield return $"GPU 提示: {gpuStatus}";
        yield return $"实际 RPM: {TaskbarMetricFormatter.Nullable(snapshot.Fan1Rpm, " RPM")}";
        yield return $"目标 RPM: {TaskbarMetricFormatter.Nullable(snapshot.TargetRpm, " RPM")}";
        if (snapshot.RequestedTargetRpm is { } requested && requested != snapshot.TargetRpm)
            yield return $"请求 RPM: {requested} RPM";
        yield return $"RPM 误差: {TaskbarMetricFormatter.Nullable(snapshot.RpmError, " RPM")}";
        yield return $"闭环: {TaskbarMetricFormatter.Feedback(snapshot.RpmFeedbackState)}";
        yield return $"目标 duty: {TaskbarMetricFormatter.Nullable(snapshot.TargetDuty, "%")}";
        if (snapshot.FanFault is not null)
            yield return $"风扇保护: {snapshot.FanFault}";
        yield return $"模式: {TaskbarMetricFormatter.Mode(snapshot.PowerMode)}";
        yield return $"控制: {snapshot.FanControlMode}";
        yield return $"Guardian: {(_guardianRunning ? "正常" : "未运行")}";
        yield return $"WMI: {(snapshot.Error ?? "正常")}";
    }
}
