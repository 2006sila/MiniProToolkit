namespace MiniProToolkit.Core;

/// <summary>What one concrete WPF grid column of the pair layouts (A/D) holds.</summary>
public enum TaskbarGridColumnKind
{
    /// <summary>One planned metric column; its width is auto so the text keeps its natural size.</summary>
    Metric = 0,
    /// <summary>The <c>+N</c> marker column, appended after the last metric column.</summary>
    Overflow = 1,
    /// <summary>A plain gap between two columns of the same group (D's 12 DIP).</summary>
    ColumnGap = 2,
    /// <summary>One side of the single group divider (10 DIP).</summary>
    GroupGap = 3,
    /// <summary>The group divider line itself, exactly <see cref="TaskbarItemLayout.SeparatorWidthDip"/> wide.</summary>
    Separator = 4
}

/// <summary>
/// One concrete grid column. <see cref="WidthDip"/> is <see cref="double.NaN"/> for an auto-sized
/// <see cref="TaskbarGridColumnKind.Metric"/>/<see cref="TaskbarGridColumnKind.Overflow"/> column;
/// a fixed gap/line column carries its DIP width. <see cref="PlanColumn"/> is the planned metric
/// column this grid column renders (0-based), or <c>-1</c> for a gap, line, or marker column.
/// </summary>
public readonly record struct TaskbarGridColumn(
    TaskbarGridColumnKind Kind,
    double WidthDip,
    int PlanColumn = -1)
{
    /// <summary>True for a metric/marker column whose width is decided by its content.</summary>
    public bool IsAuto => double.IsNaN(WidthDip);
}

/// <summary>
/// The projection of a <see cref="TaskbarMetricLayoutPlan"/> onto concrete grid columns: the
/// renderer only walks <see cref="Columns"/> and never recomputes an index. <see cref="MetricColumns"/>
/// maps a planned column to its grid column, <see cref="SeparatorColumn"/> is the grid column of
/// the first (in practice only) divider line or <c>-1</c>, and <see cref="OverflowColumn"/> is the
/// marker's grid column or <c>-1</c> when the marker has no column of its own (vertical strip).
/// </summary>
public sealed record TaskbarGridProjection(
    IReadOnlyList<TaskbarGridColumn> Columns,
    IReadOnlyList<int> MetricColumns,
    int SeparatorColumn = -1,
    int OverflowColumn = -1);

/// <summary>
/// Pure mapping from the pair layout's planned 2-row columns to the grid columns the App adds.
/// A boundary between two groups becomes leading 10 DIP gap + 1 DIP line + trailing 10 DIP gap
/// (three columns, so later columns must shift by three, not by two); any other column boundary is
/// D's single 12 DIP gap. The horizontal <c>+N</c> marker gets a 12 DIP gap plus its own column,
/// while a vertical strip keeps the marker in its own row and adds no column. Keeping this mapping
/// out of the renderer is what makes the exact grid indices unit-testable.
/// </summary>
public static class TaskbarGridLayout
{
    /// <summary>Projects the groups of the planned columns (one group number per column).</summary>
    public static TaskbarGridProjection Project(IReadOnlyList<int> columnGroups, bool hasOverflow, bool vertical)
    {
        ArgumentNullException.ThrowIfNull(columnGroups);

        var columns = new List<TaskbarGridColumn>(columnGroups.Count * 2 + 2);
        var metricColumns = new int[columnGroups.Count];
        var separatorColumn = -1;
        for (var column = 0; column < columnGroups.Count; column++)
        {
            if (column > 0)
            {
                if (columnGroups[column] != columnGroups[column - 1])
                {
                    // The single A group divider: 10 DIP, the 1px line, 10 DIP. The line owns the
                    // middle column, so both gaps keep their full width.
                    columns.Add(new TaskbarGridColumn(TaskbarGridColumnKind.GroupGap, TaskbarItemLayout.GroupGapDip));
                    if (separatorColumn < 0) separatorColumn = columns.Count;
                    columns.Add(new TaskbarGridColumn(TaskbarGridColumnKind.Separator, TaskbarItemLayout.SeparatorWidthDip));
                    columns.Add(new TaskbarGridColumn(TaskbarGridColumnKind.GroupGap, TaskbarItemLayout.GroupGapDip));
                }
                else
                {
                    // D (and A beyond the first pair group): a 12 DIP gap, no separator line.
                    columns.Add(new TaskbarGridColumn(TaskbarGridColumnKind.ColumnGap, TaskbarItemLayout.ColumnGapDip));
                }
            }
            metricColumns[column] = columns.Count;
            columns.Add(new TaskbarGridColumn(TaskbarGridColumnKind.Metric, double.NaN, column));
        }

        var overflowColumn = -1;
        if (hasOverflow && !vertical)
        {
            if (columnGroups.Count > 0)
                columns.Add(new TaskbarGridColumn(TaskbarGridColumnKind.ColumnGap, TaskbarItemLayout.ColumnGapDip));
            overflowColumn = columns.Count;
            columns.Add(new TaskbarGridColumn(TaskbarGridColumnKind.Overflow, double.NaN));
        }

        return new TaskbarGridProjection(columns, metricColumns, separatorColumn, overflowColumn);
    }

    /// <summary>
    /// Projects a resolved plan: the group of a planned column is the group of the first cell in
    /// it (a column exists only while it holds a visible cell). The horizontal marker adds its own
    /// column; the vertical one is placed by the renderer in row <see cref="TaskbarMetricLayoutPlan.Rows"/>.
    /// </summary>
    public static TaskbarGridProjection Project(TaskbarMetricLayoutPlan plan, bool vertical)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var columnGroups = new int[plan.Columns];
        for (var column = 0; column < plan.Columns; column++)
            columnGroups[column] = plan.Cells.FirstOrDefault(cell => cell.Column == column).Group;
        return Project(columnGroups, plan.Overflow is not null, vertical);
    }
}
