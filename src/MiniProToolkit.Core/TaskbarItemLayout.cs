using System.Globalization;

namespace MiniProToolkit.Core;

/// <summary>How one planned cell is drawn. The App side never re-decides wording or placement.</summary>
public enum TaskbarCellStyle
{
    /// <summary>Label, space, value: the historic flat form (<see cref="TaskbarMetric.Text"/>).</summary>
    Plain = 0,
    /// <summary>Colon form without a space (<see cref="TaskbarMetric.ColonText"/>), used by A.</summary>
    Colon = 1,
    /// <summary>Label above value, used by the two leading E items.</summary>
    Card = 2
}

/// <summary>
/// One metric placed in a layout grid. Rows and columns are 0-based.
/// <para>
/// <see cref="SourceIndex"/> is the position of the metric in the configured (saved) item list,
/// so a visible cell can always be mapped back to its canonical item. <see cref="Group"/> is the
/// visual group the cell belongs to: A and C split the ordered list into "first two items" and
/// "the remaining items" and draw exactly one divider between the two groups; D and E keep one
/// group. <see cref="Style"/> selects the wording the renderer must use.
/// </para>
/// </summary>
public readonly record struct TaskbarMetricCell(
    int Row,
    int Column,
    TaskbarMetric Metric,
    int SourceIndex = 0,
    int Group = 0,
    TaskbarCellStyle Style = TaskbarCellStyle.Plain);

/// <summary>
/// The explicit "more metrics were omitted" marker. It is a real, visible <c>+N</c> cell with an
/// accessible name and a tooltip that contains the full text (and detail) of every omitted metric,
/// so no mode ever hides data without saying so.
/// </summary>
public sealed record TaskbarOverflowMarker(
    string Text,
    IReadOnlyList<TaskbarMetric> Hidden,
    string ToolTip,
    string AccessibleName)
{
    public int HiddenCount => Hidden.Count;
}

/// <summary>
/// A resolved, render-agnostic plan for one overlay arrangement. The App side only turns these
/// cells into WPF elements; every ordering/column/overflow decision stays pure and unit-tested
/// here. <see cref="Rows"/> and <see cref="Columns"/> count only the visible metric cells; the
/// overflow marker is appended after them by the renderer.
/// </summary>
public sealed record TaskbarMetricLayoutPlan(
    TaskbarLayoutMode Mode,
    int Rows,
    int Columns,
    IReadOnlyList<TaskbarMetricCell> Cells,
    TaskbarOverflowMarker? Overflow = null,
    double EstimatedWidthDip = 0,
    double EstimatedHeightDip = 0)
{
    public bool IsEmpty => Cells.Count == 0;

    public int VisibleCount => Cells.Count;
}

/// <summary>
/// Pure layout rules for the four taskbar arrangements, following the offline prototype in
/// <c>docs/taskbar-layout-concepts/index.html</c> (A/C/D/E):
/// <list type="bullet">
/// <item>A: pair-stacked two-row columns. Item 0 sits above item 1 in column 0, item 2 above
/// item 3 in column 1, and so on (column-major pairs). The first two visible items form the first
/// group and the remaining items the second group; only the boundary between those two groups
/// gets the 1px vertical divider. Five and more items keep adding columns by D's rule (12 DIP,
/// no further separator) instead of putting a third row into a column, bounded at four columns.</item>
/// <item>C: one horizontal line with two groups (first two ordered items, then the rest), one
/// 1px divider between the groups, ~6 DIP inside a group and 10 DIP on each side of the divider.
/// A vertical taskbar degrades C to one item per line without a colon.</item>
/// <item>D: exactly two rows while more than one item is visible; more items only add columns
/// (12 DIP apart, no separator), bounded at the prototype's four-column/8-item practical cap.
/// Column geometry is derived from a stable per-metric width estimate (label, unit and a
/// high-state numeric allowance), never from the live value, so the column count cannot jitter
/// between samples.</item>
/// <item>E: label-over-value cards. Horizontal: one row of cards. Vertical: the first two items
/// stay cards and every remaining item becomes a plain (no colon) row, per the prototype.</item>
/// </list>
/// No mode relies on WPF clipping or text trimming to drop items: when the estimated geometry
/// does not fit the available width/height, the least important metrics are omitted and reported
/// through <see cref="TaskbarMetricLayoutPlan.Overflow"/> (<c>+N</c>). Visibility is decided by a
/// fixed priority (CPU usage / memory / CPU temperature first, then fan/power/GPU, then the
/// secondary control metrics, then status flags and the power mode), while the displayed order
/// always stays the configured order, so reordering the settings still works.
/// <para>
/// One deliberate exception: a vertical taskbar is only as wide as the bar, so its single column
/// cannot be widened. There the item count is bounded by height only and an over-long label is
/// rendered with an ellipsis while its full text stays in the cell tooltip and accessible name;
/// the item itself is still present and counted. Omitting an item from a vertical strip therefore
/// only happens for height and is always reported with <c>+N</c>.
/// </para>
/// </summary>
public static class TaskbarItemLayout
{
    /// <summary>Gap between D/A columns beyond the first A group boundary, in DIPs.</summary>
    public const double ColumnGapDip = 12;

    /// <summary>Gap on each side of the single A/C group divider, in DIPs.</summary>
    public const double GroupGapDip = 10;

    /// <summary>Gap between two items inside one C group, in DIPs (~0.55em at 12 DIP).</summary>
    public const double InlineItemGapDip = 6;

    /// <summary>Width of the 1px A/C group divider, in DIPs.</summary>
    public const double SeparatorWidthDip = 1;

    /// <summary>Vertical gap between the two rows of a column, in DIPs.</summary>
    public const double RowGapDip = 1;

    /// <summary>Gap between E cards (horizontal) and between the marker and the strip, in DIPs.</summary>
    public const double CardGapDip = 12;

    /// <summary>
    /// Line-height factor of one taskbar text row. The prototype uses <c>--lh = 1.25em</c>; the
    /// plan reserves 1.35em because a WPF <c>TextBlock</c> adds the font's line spacing (about
    /// 1.33em for Segoe UI), so the height estimate never under-reserves.
    /// </summary>
    public const double RowHeightFactor = 1.35;

    /// <summary>Height factor of one E card: one label line plus one value line.</summary>
    public const double CardHeightFactor = 0.78 * RowHeightFactor + RowHeightFactor;

    /// <summary>D (and A beyond its first pair) never puts a third item into a column.</summary>
    public const int MaximumPairRows = 2;

    /// <summary>
    /// Practical hard cap for the pair-column layouts (A/D): four columns, i.e. the first eight
    /// visible metrics. The prototype calls for this bound because eight items already span about
    /// 500 DIP; anything beyond it is reported in the <c>+N</c> marker instead of widening the
    /// strip across the taskbar.
    /// </summary>
    public const int MaximumPairColumns = 4;

    // The stable marker sample: "+N" never needs more than two digits for the 13 shipped items,
    // so reserving "+12" is a conservative width that does not depend on the live hidden count.
    private const string OverflowSampleText = "+12";

    /// <summary>One row height in DIPs for the configured font size.</summary>
    public static double RowHeightDip(double fontSize) => SanitizeFont(fontSize) * RowHeightFactor;

    /// <summary>One E card height in DIPs for the configured font size.</summary>
    public static double CardHeightDip(double fontSize) => SanitizeFont(fontSize) * CardHeightFactor;

    /// <summary>
    /// Builds the plan for one mode. <paramref name="availableWidthDip"/> and
    /// <paramref name="availableHeightDip"/> are the safe content budget in DIPs (already minus
    /// the overlay padding); use <see cref="double.PositiveInfinity"/> when unknown. A non-finite
    /// or non-positive font size falls back to 12 DIP so an invalid setting can never divide by 0.
    /// </summary>
    public static TaskbarMetricLayoutPlan Build(
        TaskbarLayoutMode mode,
        IReadOnlyList<TaskbarMetric> metrics,
        double availableWidthDip,
        double fontSize,
        bool vertical,
        double availableHeightDip = double.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        fontSize = SanitizeFont(fontSize);
        if (metrics.Count == 0)
            return new TaskbarMetricLayoutPlan(mode, 0, 0, []);

        availableWidthDip = SanitizeAvailable(availableWidthDip);
        availableHeightDip = SanitizeAvailable(availableHeightDip);
        // A vertical taskbar's width is the (narrow, fixed) bar width: no item count can widen it,
        // so the item selection is bounded by height only there. Every item still gets its own row
        // with ellipsis, a full-text tooltip and a full accessible name; items are never dropped by
        // width on a vertical strip.
        var widthForFit = vertical ? double.PositiveInfinity : availableWidthDip;

        var allIndices = new int[metrics.Count];
        for (var index = 0; index < allIndices.Length; index++) allIndices[index] = index;

        // A/D are bounded to four pair columns (eight visible metrics) even on a very wide bar.
        var maximumVisible = !vertical && mode is TaskbarLayoutMode.StackedPairs or TaskbarLayoutMode.AdaptiveColumns
            ? MaximumPairColumns * MaximumPairRows
            : metrics.Count;
        var initialVisible = maximumVisible >= metrics.Count
            ? allIndices
            : TopKIndices(metrics, maximumVisible);
        var initialHidden = maximumVisible >= metrics.Count
            ? Array.Empty<int>()
            : ComplementIndices(metrics.Count, initialVisible);

        var full = BuildGeometry(mode, metrics, initialVisible, fontSize, vertical, overflow: initialHidden.Length > 0);
        if (Fits(full, widthForFit, availableHeightDip))
            return ToPlan(mode, full, BuildOverflowMarker(initialHidden, metrics));

        // Keep as many metrics as fit, dropping the least important ones first. The candidate
        // count is small (one taskbar display list), so an exact search is cheap and deterministic.
        for (var keep = initialVisible.Length - 1; keep >= 1; keep--)
        {
            var visible = TopKIndices(metrics, keep);
            var hidden = ComplementIndices(metrics.Count, visible);
            var geometry = BuildGeometry(mode, metrics, visible, fontSize, vertical, overflow: hidden.Length > 0);
            if (Fits(geometry, widthForFit, availableHeightDip))
                return ToPlan(mode, geometry, BuildOverflowMarker(hidden, metrics));
        }

        // Even a single cell plus the marker does not fit (a degenerate strip narrower than one
        // label). Keep the most important item and the explicit marker instead of showing nothing;
        // the renderer keeps the full text in the cell tooltip and accessible name.
        var only = TopKIndices(metrics, 1);
        var rest = ComplementIndices(metrics.Count, only);
        var fallback = BuildGeometry(mode, metrics, only, fontSize, vertical, overflow: rest.Length > 0);
        return ToPlan(mode, fallback, BuildOverflowMarker(rest, metrics));
    }

    /// <summary>
    /// Rough single-line width of one string in DIPs. Segoe UI digits/latin average below one em;
    /// CJK glyphs are full width. The latin factor is deliberately a little above the measured
    /// average (0.62 em) so the plan over-reserves instead of letting the last column of a strip
    /// run into the window edge. It also drives the fit test, so it is conservative and completely
    /// independent of the live reading.
    /// </summary>
    public static double EstimateTextWidthDip(string? text, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        fontSize = SanitizeFont(fontSize);
        var width = 0.0;
        foreach (var character in text)
            width += character >= '\u2E80' ? fontSize : fontSize * 0.62;
        return width;
    }

    /// <summary>
    /// The stable stand-in value used for every width decision. It keeps the real unit and a
    /// high-state numeric allowance per unit, so a value changing from "9%" to "100%" or a
    /// feedback state changing to its longest wording never changes the column count.
    /// </summary>
    public static string StableWidthText(TaskbarMetric metric) => metric.Item switch
    {
        TaskbarDisplayItem.RpmFeedback => "目标受限/暂不可达",
        TaskbarDisplayItem.PowerMode => "均衡",
        TaskbarDisplayItem.Guardian or TaskbarDisplayItem.WmiStatus => "OK",
        _ => UnitWidthText(metric.Unit)
    };

    /// <summary>
    /// Fixed visibility priority. 0 is kept longest: the three headline metrics; 1 the fan/power/
    /// GPU readings; 2 the secondary control readings; 3 the status flags and power mode (the
    /// prototype's C rule drops the mode first, too).
    /// </summary>
    public static int VisibilityPriority(TaskbarDisplayItem item) => item switch
    {
        TaskbarDisplayItem.CpuUsage or TaskbarDisplayItem.MemoryUsage or TaskbarDisplayItem.CpuTemperature => 0,
        TaskbarDisplayItem.FanRpm or TaskbarDisplayItem.CpuPackagePower or TaskbarDisplayItem.GpuTemperature => 1,
        TaskbarDisplayItem.TargetRpm or TaskbarDisplayItem.TargetDuty or TaskbarDisplayItem.RpmFeedback
            or TaskbarDisplayItem.CpuFrequency => 2,
        _ => 3
    };

    private static string UnitWidthText(string unit) => unit switch
    {
        "%" => "100%",
        "°C" => "100.0°C",
        " RPM" => "9999 RPM",
        " W" => "999.9 W",
        " MHz" => "9999 MHz",
        _ => "9999"
    };

    private sealed record Geometry(
        int Rows,
        int Columns,
        IReadOnlyList<TaskbarMetricCell> Cells,
        double WidthDip,
        double HeightDip);

    private static Geometry BuildGeometry(
        TaskbarLayoutMode mode,
        IReadOnlyList<TaskbarMetric> metrics,
        IReadOnlyList<int> indices,
        double fontSize,
        bool vertical,
        bool overflow) => mode switch
    {
        TaskbarLayoutMode.Inline => vertical
            ? BuildSingleColumn(metrics, indices, fontSize, TaskbarCellStyle.Plain, overflow)
            : BuildInlineLine(metrics, indices, fontSize, overflow),
        TaskbarLayoutMode.LabelOverValue => vertical
            ? BuildCardColumn(metrics, indices, fontSize, overflow)
            : BuildCardRow(metrics, indices, fontSize, overflow),
        TaskbarLayoutMode.AdaptiveColumns => vertical
            ? BuildSingleColumn(metrics, indices, fontSize, TaskbarCellStyle.Plain, overflow)
            : BuildPairColumns(metrics, indices, fontSize, overflow, splitFirstPair: false),
        _ => vertical
            ? BuildSingleColumn(metrics, indices, fontSize, TaskbarCellStyle.Colon, overflow)
            : BuildPairColumns(metrics, indices, fontSize, overflow, splitFirstPair: true)
    };

    /// <summary>Vertical A/C/D: one item per line, no columns, optional marker row at the end.</summary>
    private static Geometry BuildSingleColumn(
        IReadOnlyList<TaskbarMetric> metrics,
        IReadOnlyList<int> indices,
        double fontSize,
        TaskbarCellStyle style,
        bool overflow)
    {
        var count = indices.Count;
        var rowHeight = RowHeightDip(fontSize);
        var cells = new TaskbarMetricCell[count];
        var width = 0.0;
        for (var position = 0; position < count; position++)
        {
            var metric = metrics[indices[position]];
            cells[position] = new TaskbarMetricCell(position, 0, metric, indices[position], 0, style);
            width = Math.Max(width, CellWidthDip(metric, style, fontSize));
        }
        var height = count * rowHeight + Math.Max(0, count - 1) * RowGapDip;
        if (overflow)
        {
            width = Math.Max(width, EstimateTextWidthDip(OverflowSampleText, fontSize));
            height += (count > 0 ? RowGapDip : 0) + rowHeight;
        }
        return new Geometry(count, 1, cells, width, height);
    }

    /// <summary>
    /// Horizontal C: one line, split into the first two ordered items and everything after them,
    /// with exactly one divider at that boundary.
    /// </summary>
    private static Geometry BuildInlineLine(
        IReadOnlyList<TaskbarMetric> metrics,
        IReadOnlyList<int> indices,
        double fontSize,
        bool overflow)
    {
        var count = indices.Count;
        var cells = new TaskbarMetricCell[count];
        var width = 0.0;
        for (var position = 0; position < count; position++)
        {
            var metric = metrics[indices[position]];
            var group = InlineGroup(position);
            cells[position] = new TaskbarMetricCell(0, position, metric, indices[position], group, TaskbarCellStyle.Plain);
            if (position > 0)
                width += cells[position - 1].Group == group ? InlineItemGapDip : GroupBoundaryWidth();
            width += CellWidthDip(metric, TaskbarCellStyle.Plain, fontSize);
        }
        if (overflow)
        {
            var markerGroup = InlineGroup(count);
            var previousGroup = count > 0 ? cells[count - 1].Group : markerGroup;
            width += previousGroup == markerGroup ? InlineItemGapDip : GroupBoundaryWidth();
            width += EstimateTextWidthDip(OverflowSampleText, fontSize);
        }
        return new Geometry(1, count, cells, width, RowHeightDip(fontSize));
    }

    /// <summary>Horizontal E: one row of label-over-value cards, marker as one extra cell.</summary>
    private static Geometry BuildCardRow(
        IReadOnlyList<TaskbarMetric> metrics,
        IReadOnlyList<int> indices,
        double fontSize,
        bool overflow)
    {
        var count = indices.Count;
        var cells = new TaskbarMetricCell[count];
        var width = 0.0;
        for (var position = 0; position < count; position++)
        {
            var metric = metrics[indices[position]];
            cells[position] = new TaskbarMetricCell(0, position, metric, indices[position], 0, TaskbarCellStyle.Card);
            if (position > 0) width += CardGapDip;
            width += CardWidthDip(metric, fontSize);
        }
        if (overflow)
        {
            width += CardGapDip + EstimateTextWidthDip(OverflowSampleText, fontSize);
        }
        return new Geometry(1, count, cells, width, CardHeightDip(fontSize));
    }

    /// <summary>
    /// Vertical E per prototype: the first two items stay cards, every further item is a plain
    /// (no colon) row. The overflow marker is a plain row as well.
    /// </summary>
    private static Geometry BuildCardColumn(
        IReadOnlyList<TaskbarMetric> metrics,
        IReadOnlyList<int> indices,
        double fontSize,
        bool overflow)
    {
        var count = indices.Count;
        var cells = new TaskbarMetricCell[count];
        var width = 0.0;
        var height = 0.0;
        for (var position = 0; position < count; position++)
        {
            var metric = metrics[indices[position]];
            var style = position < 2 ? TaskbarCellStyle.Card : TaskbarCellStyle.Plain;
            cells[position] = new TaskbarMetricCell(position, 0, metric, indices[position], 0, style);
            if (position > 0) height += RowGapDip;
            height += style == TaskbarCellStyle.Card ? CardHeightDip(fontSize) : RowHeightDip(fontSize);
            width = Math.Max(width, style == TaskbarCellStyle.Card
                ? CardWidthDip(metric, fontSize)
                : CellWidthDip(metric, TaskbarCellStyle.Plain, fontSize));
        }
        if (overflow)
        {
            height += (count > 0 ? RowGapDip : 0) + RowHeightDip(fontSize);
            width = Math.Max(width, EstimateTextWidthDip(OverflowSampleText, fontSize));
        }
        return new Geometry(count, 1, cells, width, height);
    }

    /// <summary>
    /// Horizontal A/D: at most two rows, one column per consecutive pair. A additionally marks
    /// only the boundary after its first pair group with the prototype's single divider; all
    /// further columns use D's plain 12 DIP gap and no separator.
    /// </summary>
    private static Geometry BuildPairColumns(
        IReadOnlyList<TaskbarMetric> metrics,
        IReadOnlyList<int> indices,
        double fontSize,
        bool overflow,
        bool splitFirstPair)
    {
        var count = indices.Count;
        var rows = Math.Min(MaximumPairRows, count);
        var columns = Math.Min((count + rows - 1) / rows, MaximumPairColumns);
        var cells = new TaskbarMetricCell[count];
        var columnWidths = new double[columns];
        var style = splitFirstPair ? TaskbarCellStyle.Colon : TaskbarCellStyle.Plain;

        for (var position = 0; position < count; position++)
        {
            var row = position % rows;
            var column = position / rows;
            var metric = metrics[indices[position]];
            var group = splitFirstPair && column > 0 ? 1 : 0;
            cells[position] = new TaskbarMetricCell(row, column, metric, indices[position], group, style);
            columnWidths[column] = Math.Max(columnWidths[column], CellWidthDip(metric, style, fontSize));
        }

        var width = 0.0;
        for (var column = 0; column < columns; column++)
        {
            if (column > 0)
                width += splitFirstPair && column == 1 ? GroupBoundaryWidth() : ColumnGapDip;
            width += columnWidths[column];
        }
        if (overflow)
            width += ColumnGapDip + EstimateTextWidthDip(OverflowSampleText, fontSize);

        var height = rows * RowHeightDip(fontSize) + Math.Max(0, rows - 1) * RowGapDip;
        return new Geometry(rows, columns, cells, width, height);
    }

    private static double GroupBoundaryWidth() => GroupGapDip * 2 + SeparatorWidthDip;

    private static int InlineGroup(int position) => position < 2 ? 0 : 1;

    private static double CellWidthDip(TaskbarMetric metric, TaskbarCellStyle style, double fontSize)
    {
        var stable = StableWidthText(metric);
        var text = style switch
        {
            TaskbarCellStyle.Colon => metric.Label.Length == 0 ? stable : metric.Label + ":" + stable,
            _ => metric.Label.Length == 0 ? stable : metric.Label + " " + stable
        };
        return EstimateTextWidthDip(text, fontSize);
    }

    private static double CardWidthDip(TaskbarMetric metric, double fontSize) =>
        Math.Max(EstimateTextWidthDip(metric.CardLabel, fontSize),
            EstimateTextWidthDip(StableWidthText(metric), fontSize));

    private static TaskbarMetricLayoutPlan ToPlan(
        TaskbarLayoutMode mode,
        Geometry geometry,
        TaskbarOverflowMarker? overflow) =>
        new(mode, geometry.Rows, geometry.Columns, geometry.Cells, overflow,
            geometry.WidthDip, geometry.HeightDip);

    private static TaskbarOverflowMarker? BuildOverflowMarker(
        IReadOnlyList<int> hiddenIndices,
        IReadOnlyList<TaskbarMetric> metrics)
    {
        if (hiddenIndices.Count == 0)
            return null;
        var hidden = new TaskbarMetric[hiddenIndices.Count];
        for (var index = 0; index < hidden.Length; index++)
            hidden[index] = metrics[hiddenIndices[index]];
        var lines = hidden.Select(metric => metric.Detail is { Length: > 0 } detail
            ? metric.Text + "（" + detail + "）"
            : metric.Text);
        var toolTip = "未显示的任务栏指标：" + Environment.NewLine +
                      string.Join(Environment.NewLine, lines);
        var accessibleName = "另有 " + hidden.Length.ToString(CultureInfo.InvariantCulture) + " 项指标未显示";
        return new TaskbarOverflowMarker(
            "+" + hidden.Length.ToString(CultureInfo.InvariantCulture),
            hidden,
            toolTip,
            accessibleName);
    }

    /// <summary>
    /// Indices of the <paramref name="keep"/> most important metrics, returned in configured
    /// order so the display still follows the user's saved/dragged sequence.
    /// </summary>
    private static int[] TopKIndices(IReadOnlyList<TaskbarMetric> metrics, int keep)
    {
        var ranked = new int[metrics.Count];
        for (var index = 0; index < ranked.Length; index++) ranked[index] = index;
        Array.Sort(ranked, (left, right) =>
        {
            var priority = VisibilityPriority(metrics[left].Item).CompareTo(VisibilityPriority(metrics[right].Item));
            return priority != 0 ? priority : left.CompareTo(right);
        });
        var selected = new int[Math.Clamp(keep, 0, metrics.Count)];
        Array.Copy(ranked, selected, selected.Length);
        Array.Sort(selected);
        return selected;
    }

    /// <summary>Every configured index that is not visible, in configured order.</summary>
    private static int[] ComplementIndices(int total, IReadOnlyList<int> visible)
    {
        var hidden = new int[total - visible.Count];
        var target = 0;
        var cursor = 0;
        for (var index = 0; index < total; index++)
        {
            if (cursor < visible.Count && visible[cursor] == index)
            {
                cursor++;
                continue;
            }
            hidden[target++] = index;
        }
        return hidden;
    }

    private static bool Fits(Geometry geometry, double availableWidthDip, double availableHeightDip) =>
        (double.IsPositiveInfinity(availableWidthDip) || geometry.WidthDip <= availableWidthDip + 0.5) &&
        (double.IsPositiveInfinity(availableHeightDip) || geometry.HeightDip <= availableHeightDip + 0.5);

    private static double SanitizeAvailable(double value) =>
        double.IsNaN(value) || value < 0 ? double.PositiveInfinity : value;

    private static double SanitizeFont(double fontSize) =>
        !double.IsFinite(fontSize) || fontSize <= 0 ? 12 : fontSize;
}
