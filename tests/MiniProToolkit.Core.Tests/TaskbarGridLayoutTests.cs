using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Regression tests for the exact grid columns the A/D renderer adds. A group boundary is three
/// columns (10 DIP gap, 1 DIP line, 10 DIP gap), so the planned column after it is not shifted by
/// two but by three, and the 1 DIP line owns the middle column instead of the leading gap. These
/// checks assert real indices (not source strings), because that off-by-one was the actual bug.
/// </summary>
public sealed class TaskbarGridLayoutTests
{
    private const double Wide = 2000;
    private const double Font = 12;

    [Fact]
    public void StackedPairsFourItemsPutsTheLineInItsOwnColumnBetweenTwoGaps()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, Metrics(4), Wide, Font, vertical: false);
        var layout = TaskbarGridLayout.Project(plan, vertical: false);

        Assert.Equal(
            new[]
            {
                TaskbarGridColumnKind.Metric,
                TaskbarGridColumnKind.GroupGap,
                TaskbarGridColumnKind.Separator,
                TaskbarGridColumnKind.GroupGap,
                TaskbarGridColumnKind.Metric
            },
            layout.Columns.Select(column => column.Kind));
        Assert.Equal(new[] { 0, 4 }, layout.MetricColumns);
        Assert.Equal(2, layout.SeparatorColumn);
        Assert.Equal(-1, layout.OverflowColumn);
        Assert.True(layout.Columns[0].IsAuto);
        Assert.Equal(TaskbarItemLayout.GroupGapDip, layout.Columns[1].WidthDip);
        Assert.Equal(TaskbarItemLayout.SeparatorWidthDip, layout.Columns[2].WidthDip);
        Assert.Equal(TaskbarItemLayout.GroupGapDip, layout.Columns[3].WidthDip);
        Assert.True(layout.Columns[4].IsAuto);
        AssertProjectionIsConsistent(plan, layout);
        // Items 0/1 are the first pair (grid column 0), items 2/3 the second pair (grid column 4).
        Assert.Equal(new[] { 0, 0, 4, 4 }, plan.Cells.Select(cell => layout.MetricColumns[cell.Column]));
    }

    [Fact]
    public void StackedPairsSixItemsStartTheThirdPairAfterAPlainGapColumn()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, Metrics(6), Wide, Font, vertical: false);
        var layout = TaskbarGridLayout.Project(plan, vertical: false);

        Assert.Equal(
            new[]
            {
                TaskbarGridColumnKind.Metric,
                TaskbarGridColumnKind.GroupGap,
                TaskbarGridColumnKind.Separator,
                TaskbarGridColumnKind.GroupGap,
                TaskbarGridColumnKind.Metric,
                TaskbarGridColumnKind.ColumnGap,
                TaskbarGridColumnKind.Metric
            },
            layout.Columns.Select(column => column.Kind));
        Assert.Equal(new[] { 0, 4, 6 }, layout.MetricColumns);
        Assert.Equal(2, layout.SeparatorColumn);
        // The third pair is separated from the second by D's plain 12 DIP gap and no line.
        Assert.Equal(TaskbarItemLayout.ColumnGapDip, layout.Columns[5].WidthDip);
        AssertProjectionIsConsistent(plan, layout);
        Assert.Equal(new[] { 0, 0, 4, 4, 6, 6 }, plan.Cells.Select(cell => layout.MetricColumns[cell.Column]));
    }

    [Fact]
    public void AdaptiveColumnsFourItemsUseOnlyPlainTwelveDipGaps()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, Metrics(4), Wide, Font, vertical: false);
        var layout = TaskbarGridLayout.Project(plan, vertical: false);

        Assert.Equal(
            new[] { TaskbarGridColumnKind.Metric, TaskbarGridColumnKind.ColumnGap, TaskbarGridColumnKind.Metric },
            layout.Columns.Select(column => column.Kind));
        Assert.Equal(new[] { 0, 2 }, layout.MetricColumns);
        Assert.Equal(-1, layout.SeparatorColumn);
        Assert.Equal(TaskbarItemLayout.ColumnGapDip, layout.Columns[1].WidthDip);
        AssertProjectionIsConsistent(plan, layout);
        Assert.Equal(new[] { 0, 0, 2, 2 }, plan.Cells.Select(cell => layout.MetricColumns[cell.Column]));
    }

    [Fact]
    public void HorizontalOverflowMarkerGetsItsOwnColumnAfterAPlainGap()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, Metrics(13),
            availableWidthDip: 10000, fontSize: Font, vertical: false);
        Assert.Equal(4, plan.Columns);
        Assert.NotNull(plan.Overflow);
        var layout = TaskbarGridLayout.Project(plan, vertical: false);

        Assert.Equal(new[] { 0, 4, 6, 8 }, layout.MetricColumns);
        Assert.Equal(10, layout.OverflowColumn);
        Assert.Equal(11, layout.Columns.Count);
        Assert.Equal(TaskbarGridColumnKind.ColumnGap, layout.Columns[9].Kind);
        Assert.Equal(TaskbarItemLayout.ColumnGapDip, layout.Columns[9].WidthDip);
        Assert.Equal(TaskbarGridColumnKind.Overflow, layout.Columns[10].Kind);
        Assert.True(layout.Columns[10].IsAuto);
        AssertProjectionIsConsistent(plan, layout);
    }

    [Fact]
    public void VerticalStripKeepsTheMarkerInItsOwnRowAndAddsNoColumn()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, Metrics(13),
            availableWidthDip: 200, fontSize: Font, vertical: true, availableHeightDip: 60);
        Assert.Equal(1, plan.Columns);
        Assert.NotNull(plan.Overflow);
        var layout = TaskbarGridLayout.Project(plan, vertical: true);

        Assert.Equal(new[] { 0 }, layout.MetricColumns);
        Assert.Equal(-1, layout.OverflowColumn);
        Assert.Equal(new[] { TaskbarGridColumnKind.Metric }, layout.Columns.Select(column => column.Kind));
        AssertProjectionIsConsistent(plan, layout);
    }

    [Fact]
    public void ColumnGroupsAloneProjectTheSameIndicesAsThePlan()
    {
        // The renderer's contract in its simplest form: 0/1/1 means pair one, then two further
        // pairs joined by a plain gap, with exactly one divider in the middle.
        var layout = TaskbarGridLayout.Project(new[] { 0, 1, 1 }, hasOverflow: false, vertical: false);

        Assert.Equal(new[] { 0, 4, 6 }, layout.MetricColumns);
        Assert.Equal(2, layout.SeparatorColumn);
        Assert.Equal(-1, layout.OverflowColumn);
        Assert.Equal(2, layout.Columns.Where(column => column.Kind == TaskbarGridColumnKind.GroupGap).Count());
        Assert.Single(layout.Columns, column => column.Kind == TaskbarGridColumnKind.Separator);
        Assert.Single(layout.Columns, column => column.Kind == TaskbarGridColumnKind.ColumnGap);
    }

    /// <summary>
    /// Every planned column must map to a real auto-sized metric column that names it, and the
    /// marker (when it has a column) must sit after all metric columns with a gap in between.
    /// </summary>
    private static void AssertProjectionIsConsistent(TaskbarMetricLayoutPlan plan, TaskbarGridProjection layout)
    {
        Assert.Equal(plan.Columns, layout.MetricColumns.Count);
        for (var column = 0; column < plan.Columns; column++)
        {
            var gridColumn = layout.MetricColumns[column];
            Assert.InRange(gridColumn, 0, layout.Columns.Count - 1);
            Assert.Equal(TaskbarGridColumnKind.Metric, layout.Columns[gridColumn].Kind);
            Assert.Equal(column, layout.Columns[gridColumn].PlanColumn);
            Assert.True(layout.Columns[gridColumn].IsAuto);
        }
        var metricGridColumns = Enumerable.Range(0, plan.Columns).Select(column => layout.MetricColumns[column]).ToArray();
        Assert.Equal(metricGridColumns.Order(), metricGridColumns);
        if (layout.OverflowColumn < 0)
        {
            Assert.DoesNotContain(layout.Columns, column => column.Kind == TaskbarGridColumnKind.Overflow);
            return;
        }
        Assert.Equal(layout.Columns.Count - 1, layout.OverflowColumn);
        Assert.Equal(TaskbarGridColumnKind.Overflow, layout.Columns[layout.OverflowColumn].Kind);
        if (metricGridColumns.Length > 0)
            Assert.Equal(TaskbarGridColumnKind.ColumnGap, layout.Columns[layout.OverflowColumn - 1].Kind);
    }

    private static TaskbarMetric[] Metrics(int count)
    {
        var items = Enum.GetValues<TaskbarDisplayItem>();
        var result = new TaskbarMetric[count];
        for (var index = 0; index < count; index++)
        {
            var item = items[index % items.Length];
            result[index] = new TaskbarMetric(item, $"L{index}", index.ToString(), "");
        }
        return result;
    }
}
