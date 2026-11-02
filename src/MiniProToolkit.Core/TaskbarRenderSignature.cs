namespace MiniProToolkit.Core;

/// <summary>
/// One rendered element of the taskbar strip / desktop card, in the exact order the renderer adds
/// it: the visible cells of the plan first, then the optional <c>+N</c> marker.
/// <para>
/// A slot deliberately carries only the structural identity of what is drawn (which item, in which
/// row/column/group, with which wording style) and <em>not</em> the live value, text or tooltip.
/// That is what lets an unchanged topology keep its existing WPF elements and only rewrite their
/// wording, while a changed order/position/style still forces a real rebuild.
/// </para>
/// </summary>
public readonly record struct TaskbarRenderSlot(
    TaskbarDisplayItem Item,
    int SourceIndex,
    int Row,
    int Column,
    int Group,
    TaskbarCellStyle Style,
    bool IsOverflow)
{
    /// <summary>The slot of one planned metric cell.</summary>
    public static TaskbarRenderSlot FromCell(in TaskbarMetricCell cell) =>
        new(cell.Metric.Item, cell.SourceIndex, cell.Row, cell.Column, cell.Group, cell.Style, false);

    /// <summary>The slot of the explicit <c>+N</c> overflow marker (never a metric of its own).</summary>
    public static TaskbarRenderSlot OverflowMarker() =>
        new(default, -1, -1, -1, -1, TaskbarCellStyle.Plain, true);
}

/// <summary>
/// The stable, value-independent text of one slot that the pure planner actually measures: the
/// label, its unit and (for an E card) the card label. These are exactly the inputs of
/// <see cref="TaskbarItemLayout"/>'s width/height estimate, so a metadata change that could flip a
/// fit decision (a longer label, a different unit) is part of the topology and forces a rebuild.
/// The live value and the detail line are deliberately absent: they change on every sample and are
/// written in place instead.
/// </summary>
public readonly record struct TaskbarRenderLayoutText(string Label, string Unit, string CardLabel)
{
    public static TaskbarRenderLayoutText FromMetric(TaskbarMetric metric) =>
        new(metric.Label, metric.Unit, metric.CardLabel);
}

/// <summary>
/// The topology of one rendered plan: everything that decides <em>which</em> elements exist and
/// where they sit, and nothing that decides what text they currently show.
/// <para>
/// Two signatures match only when the mode, the row/column counts, the effective direction
/// (horizontal/vertical), the compact flag, the resolved font size, the safe content budget, every
/// slot (item, source index, row, column, group, wording style), the stable label/unit/card-label
/// text the planner measured, the <c>+N</c> marker and the exact order of its hidden metrics, and
/// the concrete grid column projection (metric/gap/line/marker column kinds and widths) are all
/// identical. A match means the existing WPF tree can be reused, so a 1 s sample never
/// clears/recreates 10-40 <c>TextBlock</c>/<c>Border</c>/<c>Grid</c> elements.
/// </para>
/// <para>
/// Live values, the flat/colon/card wording, tooltips and accessible names are intentionally not
/// part of the key: they change on every sample and are applied in place by
/// <see cref="TaskbarRenderTexts"/>.
/// </para>
/// </summary>
public sealed class TaskbarRenderSignature : IEquatable<TaskbarRenderSignature>
{
    private readonly TaskbarRenderSlot[] _slots;
    private readonly TaskbarRenderLayoutText[] _layoutTexts;
    private readonly TaskbarDisplayItem[] _hiddenItems;
    private readonly TaskbarGridColumn[] _gridColumns;

    private TaskbarRenderSignature(
        TaskbarLayoutMode mode,
        int rows,
        int columns,
        bool vertical,
        bool compact,
        double fontSize,
        double widthBudgetDip,
        double heightBudgetDip,
        TaskbarRenderSlot[] slots,
        TaskbarRenderLayoutText[] layoutTexts,
        TaskbarDisplayItem[] hiddenItems,
        TaskbarGridColumn[] gridColumns)
    {
        Mode = mode;
        Rows = rows;
        Columns = columns;
        Vertical = vertical;
        Compact = compact;
        FontSize = fontSize;
        WidthBudgetDip = widthBudgetDip;
        HeightBudgetDip = heightBudgetDip;
        _slots = slots;
        _layoutTexts = layoutTexts;
        _hiddenItems = hiddenItems;
        _gridColumns = gridColumns;
    }

    /// <summary>The arrangement (A/C/D/E) the tree was built for.</summary>
    public TaskbarLayoutMode Mode { get; }

    /// <summary>Visible grid rows of the plan (without the marker row of a vertical strip).</summary>
    public int Rows { get; }

    /// <summary>Visible metric columns of the plan.</summary>
    public int Columns { get; }

    /// <summary>Effective layout direction of the render pass.</summary>
    public bool Vertical { get; }

    /// <summary>Compact spacing/font flag of the render pass.</summary>
    public bool Compact { get; }

    /// <summary>Resolved cell font size in DIPs.</summary>
    public double FontSize { get; }

    /// <summary>Safe content width budget in DIPs the plan had to fit into.</summary>
    public double WidthBudgetDip { get; }

    /// <summary>Safe content height budget in DIPs the plan had to fit into.</summary>
    public double HeightBudgetDip { get; }

    /// <summary>One slot per visible cell, followed by the marker slot when there is one.</summary>
    public IReadOnlyList<TaskbarRenderSlot> Slots => _slots;

    /// <summary>
    /// One stable layout text per slot (label/unit/card label), i.e. exactly the text the planner
    /// measured. The live value and the detail line are never kept here, so nothing cached can go
    /// stale between two samples.
    /// </summary>
    public IReadOnlyList<TaskbarRenderLayoutText> LayoutTexts => _layoutTexts;

    /// <summary>Identity of the omitted metrics, in the plan's (configured) order.</summary>
    public IReadOnlyList<TaskbarDisplayItem> HiddenItems => _hiddenItems;

    /// <summary>The concrete grid projection, or empty for the inline/card arrangements.</summary>
    public IReadOnlyList<TaskbarGridColumn> GridColumns => _gridColumns;

    /// <summary>Number of elements a render pass must create for this topology (cells + marker).</summary>
    public int SlotCount => _slots.Length;

    /// <summary>
    /// Captures the topology of one resolved plan together with the presentation settings the
    /// renderer used. <paramref name="widthBudgetDip"/>/<paramref name="heightBudgetDip"/> are the
    /// same safe content budget that was handed to <see cref="TaskbarItemLayout.Build"/>; a new
    /// budget (bar resized, direction flip, compact toggle) therefore always invalidates.
    /// </summary>
    public static TaskbarRenderSignature Create(
        TaskbarMetricLayoutPlan plan,
        bool vertical,
        bool compact,
        double fontSize,
        double widthBudgetDip,
        double heightBudgetDip = double.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var hasMarker = plan.Overflow is not null;
        var slots = new TaskbarRenderSlot[plan.Cells.Count + (hasMarker ? 1 : 0)];
        var layoutTexts = new TaskbarRenderLayoutText[slots.Length];
        for (var index = 0; index < plan.Cells.Count; index++)
        {
            slots[index] = TaskbarRenderSlot.FromCell(plan.Cells[index]);
            layoutTexts[index] = TaskbarRenderLayoutText.FromMetric(plan.Cells[index].Metric);
        }
        if (hasMarker)
        {
            slots[^1] = TaskbarRenderSlot.OverflowMarker();
            // The marker's own width decision uses the fixed "+12" sample, never its live count.
            layoutTexts[^1] = new TaskbarRenderLayoutText(string.Empty, string.Empty, string.Empty);
        }

        var hidden = plan.Overflow is { } overflow
            ? overflow.Hidden.Select(metric => metric.Item).ToArray()
            : [];

        // Only the pair arrangements draw a real Grid; the projection is what decides the concrete
        // gap/line/marker columns, so a change of it must rebuild even when the cells look alike.
        var gridColumns = plan.Mode is TaskbarLayoutMode.StackedPairs or TaskbarLayoutMode.AdaptiveColumns
            ? TaskbarGridLayout.Project(plan, vertical).Columns.ToArray()
            : [];

        return new TaskbarRenderSignature(plan.Mode, plan.Rows, plan.Columns, vertical, compact,
            fontSize, widthBudgetDip, heightBudgetDip, slots, layoutTexts, hidden, gridColumns);
    }

    /// <summary>
    /// True when <paramref name="other"/> has exactly the same topology, i.e. the existing WPF tree
    /// can be kept and only its text/tooltip/accessible name need to be refreshed.
    /// </summary>
    public bool Matches(TaskbarRenderSignature? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Mode != other.Mode || Rows != other.Rows || Columns != other.Columns) return false;
        if (Vertical != other.Vertical || Compact != other.Compact) return false;
        if (!FontSize.Equals(other.FontSize)) return false;
        if (!WidthBudgetDip.Equals(other.WidthBudgetDip)) return false;
        if (!HeightBudgetDip.Equals(other.HeightBudgetDip)) return false;
        if (!_slots.AsSpan().SequenceEqual(other._slots)) return false;
        if (_layoutTexts.Length != other._layoutTexts.Length) return false;
        for (var index = 0; index < _layoutTexts.Length; index++)
        {
            // Metadata only: the planner's own width inputs must match, so a changed label/unit/card
            // label (which could flip the fit decision) always rebuilds.
            if (!string.Equals(_layoutTexts[index].Label, other._layoutTexts[index].Label, StringComparison.Ordinal)) return false;
            if (!string.Equals(_layoutTexts[index].Unit, other._layoutTexts[index].Unit, StringComparison.Ordinal)) return false;
            if (!string.Equals(_layoutTexts[index].CardLabel, other._layoutTexts[index].CardLabel, StringComparison.Ordinal)) return false;
        }
        if (!_hiddenItems.AsSpan().SequenceEqual(other._hiddenItems)) return false;
        if (_gridColumns.Length != other._gridColumns.Length) return false;
        for (var index = 0; index < _gridColumns.Length; index++)
        {
            if (_gridColumns[index].Kind != other._gridColumns[index].Kind) return false;
            if (_gridColumns[index].PlanColumn != other._gridColumns[index].PlanColumn) return false;
            // NaN (an auto column) compares as "same" here; the fixed gap/line widths must match.
            if (Math.Abs(_gridColumns[index].WidthDip - other._gridColumns[index].WidthDip) > 0.001) return false;
        }
        return true;
    }

    public bool Equals(TaskbarRenderSignature? other) => Matches(other);

    public override bool Equals(object? obj) => obj is TaskbarRenderSignature other && Matches(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Mode);
        hash.Add(Rows);
        hash.Add(Columns);
        hash.Add(Vertical);
        hash.Add(Compact);
        hash.Add(FontSize);
        hash.Add(WidthBudgetDip);
        hash.Add(HeightBudgetDip);
        hash.Add(_slots.Length);
        hash.Add(_layoutTexts.Length);
        hash.Add(_hiddenItems.Length);
        hash.Add(_gridColumns.Length);
        return hash.ToHashCode();
    }
}

/// <summary>
/// The wording one rendered slot must show <em>now</em>: the only thing an unchanged topology has
/// to write into the existing elements. Positions, gaps and separators come from the signature and
/// are never recomputed here.
/// </summary>
public readonly record struct TaskbarRenderSlotUpdate(
    TaskbarRenderSlot Slot,
    string Text,
    string? ToolTip,
    string AccessibleName,
    string CardLabel = "",
    string CardToolTip = "")
{
    /// <summary>True for an E card slot: <see cref="Text"/> is the value line, <see cref="CardLabel"/> the label line.</summary>
    public bool IsCard => Slot.Style == TaskbarCellStyle.Card;

    /// <summary>True for the <c>+N</c> marker slot.</summary>
    public bool IsOverflow => Slot.IsOverflow;
}

/// <summary>
/// Pure "what to write" projection of a resolved plan. The renderer creates its elements from the
/// same plan; on a tick whose topology is unchanged it walks this list and only assigns a value that
/// really differs, so an unchanged text/tooltip/accessible name never re-invalidates WPF.
/// </summary>
public static class TaskbarRenderTexts
{
    /// <summary>One update per slot, in the renderer's element order (cells, then the marker).</summary>
    public static IReadOnlyList<TaskbarRenderSlotUpdate> Updates(TaskbarMetricLayoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var updates = new TaskbarRenderSlotUpdate[plan.Cells.Count + (plan.Overflow is null ? 0 : 1)];
        for (var index = 0; index < plan.Cells.Count; index++)
            updates[index] = ForCell(plan.Cells[index]);
        if (plan.Overflow is { } overflow)
            updates[^1] = new TaskbarRenderSlotUpdate(TaskbarRenderSlot.OverflowMarker(),
                overflow.Text, overflow.ToolTip, overflow.AccessibleName);
        return updates;
    }

    /// <summary>
    /// The wording of one metric cell: A's colon row, C/D's flat row, or the value line of an E card
    /// (whose label line and pair tooltip are carried separately).
    /// </summary>
    public static TaskbarRenderSlotUpdate ForCell(in TaskbarMetricCell cell)
    {
        var metric = cell.Metric;
        var toolTip = MetricToolTip(metric);
        if (cell.Style == TaskbarCellStyle.Card)
        {
            return new TaskbarRenderSlotUpdate(TaskbarRenderSlot.FromCell(cell), metric.ValueText,
                toolTip, metric.ValueText, metric.CardLabel, AccessibleName(metric));
        }
        var text = cell.Style == TaskbarCellStyle.Colon ? metric.ColonText : metric.Text;
        return new TaskbarRenderSlotUpdate(TaskbarRenderSlot.FromCell(cell), text, toolTip,
            AccessibleName(metric));
    }

    /// <summary>The one cell tooltip: the full flat text plus the optional detail line.</summary>
    public static string MetricToolTip(TaskbarMetric metric) =>
        metric.Detail is { Length: > 0 } detail
            ? metric.Text + Environment.NewLine + detail
            : metric.Text;

    /// <summary>The one semantic name of one metric, independent of the per-mode wording.</summary>
    public static string AccessibleName(TaskbarMetric metric) =>
        metric.CardLabel + " " + metric.ValueText;
}
