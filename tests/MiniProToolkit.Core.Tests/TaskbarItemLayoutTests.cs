using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Pure layout tests for the four taskbar arrangements. Every mode must place each visible item
/// exactly once in the configured order, the visible/hidden split must be deterministic and
/// index-mapped, and no mode may rely on WPF clipping: an item that does not fit is counted in an
/// explicit <c>+N</c> overflow marker whose tooltip lists every omitted metric.
/// </summary>
public sealed class TaskbarItemLayoutTests
{
    private const double Wide = 2000;
    private const double Font = 12;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    public void EveryModePlacesEachItemExactlyOnceInConfiguredOrderWhenWide(int count)
    {
        var metrics = Metrics(count);
        foreach (var vertical in new[] { false, true })
        {
            foreach (var mode in Enum.GetValues<TaskbarLayoutMode>())
            {
                var plan = TaskbarItemLayout.Build(mode, metrics, Wide, Font, vertical);

                var boundedPairColumns = !vertical &&
                    mode is TaskbarLayoutMode.StackedPairs or TaskbarLayoutMode.AdaptiveColumns;
                var expectedVisible = boundedPairColumns
                    ? Math.Min(count, TaskbarItemLayout.MaximumPairColumns * TaskbarItemLayout.MaximumPairRows)
                    : count;
                Assert.Equal(expectedVisible, plan.Cells.Count);
                if (expectedVisible == count)
                {
                    Assert.Null(plan.Overflow);
                }
                else
                {
                    Assert.NotNull(plan.Overflow);
                    Assert.Equal(count - expectedVisible, plan.Overflow!.HiddenCount);
                }
                // Every metric appears exactly once across the visible cells and the marker.
                Assert.Equal(count, plan.Cells.Select(cell => cell.Metric.Item)
                    .Concat(plan.Overflow?.Hidden.Select(metric => metric.Item) ?? Array.Empty<TaskbarDisplayItem>())
                    .Distinct().Count());
                // Cells keep the configured (ascending source-index) order.
                Assert.Equal(plan.Cells.Select(cell => cell.SourceIndex).OrderBy(index => index),
                    plan.Cells.Select(cell => cell.SourceIndex));
                Assert.All(plan.Cells, cell => Assert.Equal(metrics[cell.SourceIndex].Item, cell.Metric.Item));
                Assert.All(plan.Cells, cell =>
                {
                    Assert.InRange(cell.Row, 0, plan.Rows - 1);
                    Assert.InRange(cell.Column, 0, plan.Columns - 1);
                });
                Assert.True(plan.Rows * plan.Columns >= plan.Cells.Count);
            }
        }
    }

    [Fact]
    public void StackedPairsUsesTwoRowColumnsWithRowMajorPairsAndOneGroupBoundary()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, Metrics(4), Wide, Font, vertical: false);

        Assert.Equal(2, plan.Rows);
        Assert.Equal(2, plan.Columns);
        // item 0 row 0 col 0, item 1 row 1 col 0, item 2 row 0 col 1, item 3 row 1 col 1.
        AssertCell(plan, index: 0, row: 0, column: 0, group: 0);
        AssertCell(plan, index: 1, row: 1, column: 0, group: 0);
        AssertCell(plan, index: 2, row: 0, column: 1, group: 1);
        AssertCell(plan, index: 3, row: 1, column: 1, group: 1);
        Assert.Equal(1, GroupBoundaryCount(plan));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    public void StackedPairsKeepsTwoRowsOneDividerAndJoinsFurtherColumnsLikeD(int count)
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, Metrics(count), Wide, Font, vertical: false);

        var expectedVisible = Math.Min(count, TaskbarItemLayout.MaximumPairColumns * TaskbarItemLayout.MaximumPairRows);
        // The prototype's "4 items and above keep adding columns" rule: never a third row.
        Assert.Equal(2, plan.Rows);
        Assert.Equal((expectedVisible + 1) / 2, plan.Columns);
        Assert.Equal(expectedVisible, plan.VisibleCount);
        if (count > expectedVisible)
            Assert.Equal(count - expectedVisible, plan.Overflow!.HiddenCount);
        // Only the first pair group is separated; every later column has group 1 and no line.
        var groupsByColumn = Enumerable.Range(0, plan.Columns)
            .Select(column => plan.Cells.First(cell => cell.Column == column).Group)
            .ToArray();
        Assert.Equal(0, groupsByColumn[0]);
        Assert.All(groupsByColumn.Skip(1), group => Assert.Equal(1, group));
        Assert.Equal(1, GroupBoundaryCount(plan));
    }

    [Theory]
    [InlineData(TaskbarLayoutMode.StackedPairs)]
    [InlineData(TaskbarLayoutMode.AdaptiveColumns)]
    public void PairColumnModesCapAtFourColumnsAndReportTheRest(TaskbarLayoutMode mode)
    {
        // A very wide bar must not grow a 7-column strip: the practical cap is 4 columns
        // (8 metrics), and items 9..13 are listed in the explicit +N marker instead.
        var metrics = RealMetrics();
        var plan = TaskbarItemLayout.Build(mode, metrics, availableWidthDip: 10000, fontSize: Font, vertical: false);

        Assert.Equal(TaskbarItemLayout.MaximumPairColumns, plan.Columns);
        Assert.Equal(TaskbarItemLayout.MaximumPairColumns * TaskbarItemLayout.MaximumPairRows, plan.VisibleCount);
        Assert.Equal(2, plan.Rows);
        Assert.NotNull(plan.Overflow);
        Assert.Equal("+5", plan.Overflow!.Text);
        Assert.Equal(5, plan.Overflow.HiddenCount);
        foreach (var hidden in plan.Overflow.Hidden)
            Assert.Contains(hidden.Text, plan.Overflow.ToolTip);
        // The visible eight are the priority order, not simply the last five dropped.
        Assert.Contains(TaskbarDisplayItem.CpuUsage, plan.Cells.Select(cell => cell.Metric.Item));
        Assert.Contains(TaskbarDisplayItem.MemoryUsage, plan.Cells.Select(cell => cell.Metric.Item));
        Assert.Contains(TaskbarDisplayItem.CpuTemperature, plan.Cells.Select(cell => cell.Metric.Item));
    }

    [Fact]
    public void EstimatedStackedPairsWidthUsesColumnGapBeyondTheGroupBoundary()
    {
        var four = Metrics(4);
        var six = Metrics(6);
        var twoColumns = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, four, Wide, Font, vertical: false);
        var threeColumns = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, six, Wide, Font, vertical: false);

        double ColonWidth(TaskbarMetric metric) =>
            TaskbarItemLayout.EstimateTextWidthDip(metric.Label + ":" + TaskbarItemLayout.StableWidthText(metric), Font);

        var firstColumn = Math.Max(ColonWidth(four[0]), ColonWidth(four[1]));
        var secondColumn = Math.Max(ColonWidth(four[2]), ColonWidth(four[3]));
        var thirdColumn = Math.Max(ColonWidth(six[4]), ColonWidth(six[5]));

        // The second column starts after the 10+1+10 group divider; the third one after 12 DIP.
        var groupBoundary = TaskbarItemLayout.GroupGapDip * 2 + TaskbarItemLayout.SeparatorWidthDip;
        Assert.Equal(firstColumn + groupBoundary + secondColumn, twoColumns.EstimatedWidthDip, 3);
        Assert.Equal(twoColumns.EstimatedWidthDip + TaskbarItemLayout.ColumnGapDip + thirdColumn,
            threeColumns.EstimatedWidthDip, 3);
    }

    [Fact]
    public void InlineIsOneLineInConfiguredOrderWithTwoGroupsAndOneDivider()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, Metrics(5), Wide, Font, vertical: false);
        Assert.Equal(1, plan.Rows);
        Assert.Equal(5, plan.Columns);
        for (var index = 0; index < 5; index++)
            AssertCell(plan, index, row: 0, column: index, group: index < 2 ? 0 : 1);
        Assert.Equal(1, GroupBoundaryCount(plan));
    }

    [Fact]
    public void InlineKeepsItsOrderedTextOutputExceptForTheSeparators()
    {
        var metrics = Metrics(4);
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, metrics, Wide, Font, vertical: false);
        // The renderer prints cell.Metric.Text verbatim, so the plan must not rewrite any wording.
        Assert.Equal(metrics.Select(metric => metric.Text), plan.Cells.Select(cell => cell.Metric.Text));
        Assert.All(plan.Cells, cell => Assert.Equal(TaskbarCellStyle.Plain, cell.Style));
    }

    [Fact]
    public void LabelOverValueKeepsOneCardPerItemInOneRow()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.LabelOverValue, Metrics(5), Wide, Font, vertical: false);
        Assert.Equal(1, plan.Rows);
        Assert.Equal(5, plan.Columns);
        for (var index = 0; index < 5; index++)
            AssertCell(plan, index, row: 0, column: index, group: 0);
        Assert.All(plan.Cells, cell => Assert.Equal(TaskbarCellStyle.Card, cell.Style));
        Assert.Equal(0, GroupBoundaryCount(plan));
    }

    [Fact]
    public void AdaptiveColumnsUsesTheNaturalTwoRowShapeWhenWide()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, Metrics(8), Wide, Font, vertical: false);
        Assert.Equal(2, plan.Rows);
        Assert.Equal(4, plan.Columns);
        AssertCell(plan, 0, row: 0, column: 0, group: 0);
        AssertCell(plan, 1, row: 1, column: 0, group: 0);
        AssertCell(plan, 2, row: 0, column: 1, group: 0);
        AssertCell(plan, 7, row: 1, column: 3, group: 0);
        Assert.Equal(0, GroupBoundaryCount(plan));
    }

    [Fact]
    public void AdaptiveColumnsNeverExceedsTwoRowsForAnyWidth(  )
    {
        var metrics = Metrics(13);
        foreach (var width in new double[] { 2000, 900, 600, 400, 300, 220, 160, 120, 80, 40, 10, 1 })
        {
            var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, metrics, width, Font, vertical: false);
            Assert.InRange(plan.Rows, 1, 2);
            Assert.True(plan.VisibleCount > 0);
            Assert.Equal(metrics.Length, plan.VisibleCount + (plan.Overflow?.HiddenCount ?? 0));
            if (plan.VisibleCount > 1)
                Assert.Equal(2, plan.Rows);
        }
    }

    [Fact]
    public void AdaptiveColumnsKeepsTheColumnCountStableRegardlessOfLiveValues()
    {
        var smallValues = Metrics(13, value: position => (position % 9).ToString() + "%");
        var largeValues = Metrics(13, value: position => (100 - position).ToString() + "%");
        var tinyValues = Metrics(13, value: _ => "--%");

        foreach (var width in new double[] { 2000, 700, 450, 300 })
        {
            var a = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, smallValues, width, Font, vertical: false);
            var b = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, largeValues, width, Font, vertical: false);
            var c = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, tinyValues, width, Font, vertical: false);

            Assert.Equal(a.Rows, b.Rows);
            Assert.Equal(a.Columns, b.Columns);
            Assert.Equal(a.VisibleCount, b.VisibleCount);
            Assert.Equal(a.Rows, c.Rows);
            Assert.Equal(a.Columns, c.Columns);
            Assert.Equal(a.EstimatedWidthDip, b.EstimatedWidthDip, 3);
            Assert.Equal(a.EstimatedWidthDip, c.EstimatedWidthDip, 3);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    public void AdaptiveColumnsOverflowMarkerCountsEveryOmittedItem(int count)
    {
        var metrics = Metrics(count);
        // A width that holds one 2-row column at most for these tiny test labels.
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, metrics,
            availableWidthDip: 1, fontSize: Font, vertical: false);

        Assert.True(plan.Rows <= 2);
        var hidden = plan.Overflow?.HiddenCount ?? 0;
        Assert.Equal(count, plan.VisibleCount + hidden);
        if (count == 1)
        {
            // One item has nothing to overflow; it is shown and its tooltip carries the full text.
            Assert.Null(plan.Overflow);
            Assert.Equal(1, plan.VisibleCount);
            return;
        }
        Assert.NotNull(plan.Overflow);
        Assert.Equal("+" + hidden, plan.Overflow!.Text);
        Assert.Equal(count, plan.Overflow.Hidden.Count + plan.VisibleCount);
        Assert.Equal(count, plan.Overflow.Hidden.Select(metric => metric.Item).Distinct().Count()
                              + plan.Cells.Select(cell => cell.Metric.Item).Distinct().Count());
        // The tooltip must contain the full text of every omitted metric.
        foreach (var metric in plan.Overflow.Hidden)
            Assert.Contains(metric.Text, plan.Overflow.ToolTip);
        Assert.Contains(hidden.ToString(), plan.Overflow.AccessibleName);
    }

    [Fact]
    public void OverflowNeverDuplicatesAMetricAndKeepsCanonicalSourceIndices()
    {
        var metrics = Metrics(13);
        foreach (var mode in Enum.GetValues<TaskbarLayoutMode>())
        {
            foreach (var vertical in new[] { false, true })
            {
                var plan = TaskbarItemLayout.Build(mode, metrics, availableWidthDip: 90,
                    fontSize: Font, vertical: vertical, availableHeightDip: 60);
                var visibleItems = plan.Cells.Select(cell => cell.Metric.Item).ToArray();
                var hiddenItems = plan.Overflow?.Hidden.Select(metric => metric.Item).ToArray() ?? [];
                Assert.Equal(13, visibleItems.Length + hiddenItems.Length);
                Assert.Equal(13, visibleItems.Concat(hiddenItems).Distinct().Count());
                Assert.Equal(plan.Cells.Select(cell => cell.SourceIndex), plan.Cells.Select(cell => cell.SourceIndex).Order());
                foreach (var cell in plan.Cells)
                    Assert.Equal(metrics[cell.SourceIndex].Item, cell.Metric.Item);
                // Horizontal plans never claim more width/height than the budget they were given.
                // A vertical strip is width-tolerant by design (the bar width is fixed); there the
                // height budget is the one that is enforced.
                if (!vertical)
                    Assert.True(plan.EstimatedWidthDip <= 90 + 0.5 || plan.VisibleCount == 1,
                        $"{mode}/{vertical}: estimated {plan.EstimatedWidthDip} > 90");
                Assert.True(plan.EstimatedHeightDip <= 60 + 0.5 || plan.VisibleCount == 1,
                    $"{mode}/{vertical}: estimated {plan.EstimatedHeightDip} > 60");
            }
        }
    }

    [Fact]
    public void OverflowKeepsTheMostImportantMetricsAndDropsTheLeastImportantTail()
    {
        var metrics = RealMetrics();
        var priority = metrics.Select(metric => TaskbarItemLayout.VisibilityPriority(metric.Item)).ToArray();
        var headlinePriority = 0;

        foreach (var mode in new[] { TaskbarLayoutMode.Inline, TaskbarLayoutMode.AdaptiveColumns })
        {
            var plan = TaskbarItemLayout.Build(mode, metrics, availableWidthDip: 260, fontSize: Font, vertical: false);

            Assert.True(plan.VisibleCount < metrics.Length);
            Assert.NotNull(plan.Overflow);
            // The visible set is exactly the configured-index-preserving top-k by priority, so a
            // hidden metric is never more important than a visible one.
            var expectedIndices = Enumerable.Range(0, metrics.Length)
                .OrderBy(index => priority[index])
                .ThenBy(index => index)
                .Take(plan.VisibleCount)
                .OrderBy(index => index)
                .ToArray();
            Assert.Equal(expectedIndices, plan.Cells.Select(cell => cell.SourceIndex));
            Assert.Equal(metrics.Length, plan.VisibleCount + plan.Overflow!.HiddenCount);
            var hiddenPriorities = plan.Overflow.Hidden
                .Select(metric => TaskbarItemLayout.VisibilityPriority(metric.Item));
            Assert.All(hiddenPriorities, value => Assert.True(value >= headlinePriority));
            // The three headline metrics are always kept while any other metric is still shown.
            var visibleItems = plan.Cells.Select(cell => cell.Metric.Item).ToArray();
            Assert.Contains(TaskbarDisplayItem.CpuUsage, visibleItems);
            Assert.Contains(TaskbarDisplayItem.MemoryUsage, visibleItems);
            Assert.Contains(TaskbarDisplayItem.CpuTemperature, visibleItems);
            Assert.DoesNotContain(TaskbarDisplayItem.PowerMode, visibleItems);
        }
    }

    [Fact]
    public void InlineOverflowMarkerIsCountedAndExplained()
    {
        // Enough width for roughly two items plus the marker, not for all 13.
        var metrics = RealMetrics();
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, metrics, availableWidthDip: 260,
            fontSize: Font, vertical: false);

        Assert.Equal(1, plan.Rows);
        Assert.True(plan.VisibleCount < 13);
        Assert.NotNull(plan.Overflow);
        Assert.Equal(13 - plan.VisibleCount, plan.Overflow!.HiddenCount);
        Assert.Equal("+" + plan.Overflow.HiddenCount, plan.Overflow.Text);
        Assert.Contains("项指标未显示", plan.Overflow.AccessibleName);
        foreach (var hidden in plan.Overflow.Hidden)
            Assert.Contains(hidden.Text, plan.Overflow.ToolTip);
        // Exactly one divider transition among the visible cells and the second-group marker.
        var groups = plan.Cells.Select(cell => cell.Group)
            .Concat(plan.Overflow.HiddenCount > 0 ? new[] { 1 } : Array.Empty<int>())
            .ToArray();
        Assert.Single(groups.Zip(groups.Skip(1)), pair => pair.First != pair.Second);
    }

    [Fact]
    public void StackedPairsOverflowsAtANarrowWidthWithoutLeavingTheTwoRowShape()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, RealMetrics(),
            availableWidthDip: 260, fontSize: Font, vertical: false);

        Assert.Equal(2, plan.Rows);
        Assert.NotNull(plan.Overflow);
        Assert.Equal(13, plan.VisibleCount + plan.Overflow!.HiddenCount);
        Assert.True(plan.EstimatedWidthDip <= 260.5);
        // The remaining columns have no extra divider beyond the first pair group.
        Assert.Equal(1, GroupBoundaryCount(plan));
    }

    [Theory]
    [InlineData(TaskbarLayoutMode.StackedPairs)]
    [InlineData(TaskbarLayoutMode.Inline)]
    [InlineData(TaskbarLayoutMode.AdaptiveColumns)]
    [InlineData(TaskbarLayoutMode.LabelOverValue)]
    public void VerticalTaskbarUsesOneItemPerLine(TaskbarLayoutMode mode)
    {
        var plan = TaskbarItemLayout.Build(mode, Metrics(4), Wide, Font, vertical: true);
        Assert.Equal(4, plan.Rows);
        Assert.Equal(1, plan.Columns);
        for (var index = 0; index < 4; index++)
            AssertCell(plan, index, row: index, column: 0, group: 0);
        Assert.Equal(4, plan.Cells.Select(cell => cell.Metric.Item).Distinct().Count());
    }

    [Fact]
    public void VerticalCardLayoutKeepsTheFirstTwoItemsAsCardsAndTheRestAsPlainRows()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.LabelOverValue, Metrics(5), Wide, Font, vertical: true);
        Assert.Equal(5, plan.Rows);
        Assert.Equal(1, plan.Columns);
        Assert.Equal(TaskbarCellStyle.Card, plan.Cells[0].Style);
        Assert.Equal(TaskbarCellStyle.Card, plan.Cells[1].Style);
        for (var index = 2; index < 5; index++)
            Assert.Equal(TaskbarCellStyle.Plain, plan.Cells[index].Style);
    }

    [Theory]
    [InlineData(TaskbarLayoutMode.StackedPairs, TaskbarCellStyle.Colon)]
    [InlineData(TaskbarLayoutMode.Inline, TaskbarCellStyle.Plain)]
    [InlineData(TaskbarLayoutMode.AdaptiveColumns, TaskbarCellStyle.Plain)]
    public void VerticalTextModesUseTheirPrototypeWording(TaskbarLayoutMode mode, TaskbarCellStyle expected)
    {
        var plan = TaskbarItemLayout.Build(mode, Metrics(4), Wide, Font, vertical: true);
        Assert.All(plan.Cells, cell => Assert.Equal(expected, cell.Style));
    }

    [Fact]
    public void VerticalOverflowIsCountedAndDoesNotExceedTheHeightBudget()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, RealMetrics(),
            availableWidthDip: 200, fontSize: Font, vertical: true, availableHeightDip: 80);

        Assert.Equal(1, plan.Columns);
        Assert.True(plan.VisibleCount < 13);
        Assert.NotNull(plan.Overflow);
        Assert.Equal(13, plan.VisibleCount + plan.Overflow!.HiddenCount);
        Assert.True(plan.EstimatedHeightDip <= 80.5);
        foreach (var hidden in plan.Overflow.Hidden)
            Assert.Contains(hidden.Text, plan.Overflow.ToolTip);
    }

    [Fact]
    public void AdaptiveColumnsFallsBackToOneVisibleItemWhenEvenOneRowDoesNotFit()
    {
        // A strip shorter than two rows (degenerate 40% cap) keeps the highest-priority item and
        // reports the rest instead of silently clipping rows.
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, RealMetrics(),
            availableWidthDip: 500, fontSize: Font, vertical: false, availableHeightDip: 10);

        Assert.Equal(1, plan.VisibleCount);
        Assert.Equal(1, plan.Rows);
        // The three headline metrics share priority 0, so the configured order decides the one
        // that survives; the rest are counted in the marker.
        Assert.Equal(TaskbarDisplayItem.CpuTemperature, plan.Cells[0].Metric.Item);
        Assert.NotNull(plan.Overflow);
        Assert.Equal(12, plan.Overflow!.HiddenCount);
    }

    [Fact]
    public void EmptyItemListProducesAnEmptyPlan()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, [], 500, Font, vertical: false);
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.Rows);
        Assert.Equal(0, plan.Columns);
        Assert.Empty(plan.Cells);
        Assert.Null(plan.Overflow);
    }

    [Fact]
    public void InvalidFontSizeFallsBackAndNeverProducesAnEmptyLayout()
    {
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, Metrics(4), 500, double.NaN, vertical: false);
        Assert.Equal(4, plan.Cells.Count);
        Assert.True(plan.Columns >= 1);
        Assert.True(plan.Rows >= 1);
    }

    [Fact]
    public void StableWidthTextKeepsTheUnitAndIsIndependentOfTheLiveValue()
    {
        var metric = new TaskbarMetric(TaskbarDisplayItem.CpuTemperature, "CPU", "58.4", "°C");
        Assert.Equal("100.0°C", TaskbarItemLayout.StableWidthText(metric));
        Assert.Equal("100%", TaskbarItemLayout.StableWidthText(metric with { Item = TaskbarDisplayItem.CpuUsage, Unit = "%" }));
        Assert.Equal("9999 RPM", TaskbarItemLayout.StableWidthText(metric with { Item = TaskbarDisplayItem.FanRpm, Unit = " RPM" }));
        Assert.Equal("目标受限/暂不可达", TaskbarItemLayout.StableWidthText(metric with { Item = TaskbarDisplayItem.RpmFeedback, Unit = "" }));
    }

    private static int GroupBoundaryCount(TaskbarMetricLayoutPlan plan)
    {
        var groupsByColumn = Enumerable.Range(0, plan.Columns)
            .Select(column => plan.Cells.First(cell => cell.Column == column).Group)
            .ToArray();
        return groupsByColumn.Zip(groupsByColumn.Skip(1)).Count(pair => pair.First != pair.Second);
    }

    private static void AssertCell(TaskbarMetricLayoutPlan plan, int index, int row, int column, int group)
    {
        var cell = plan.Cells[index];
        Assert.Equal(row, cell.Row);
        Assert.Equal(column, cell.Column);
        Assert.Equal(group, cell.Group);
        Assert.Equal(index, cell.SourceIndex);
    }

    private static TaskbarMetric[] Metrics(int count, Func<int, string>? value = null)
    {
        var items = Enum.GetValues<TaskbarDisplayItem>();
        var result = new TaskbarMetric[count];
        for (var index = 0; index < count; index++)
        {
            var item = items[index % items.Length];
            result[index] = new TaskbarMetric(item, $"L{index}", value?.Invoke(index) ?? index.ToString(), UnitFor(item));
        }
        return result;
    }

    /// <summary>Realistic unit per item so the stable width estimate includes the real unit.</summary>
    private static string UnitFor(TaskbarDisplayItem item) => item switch
    {
        TaskbarDisplayItem.CpuUsage or TaskbarDisplayItem.MemoryUsage or TaskbarDisplayItem.TargetDuty => "%",
        TaskbarDisplayItem.CpuTemperature or TaskbarDisplayItem.GpuTemperature => "°C",
        TaskbarDisplayItem.FanRpm or TaskbarDisplayItem.TargetRpm => " RPM",
        TaskbarDisplayItem.CpuPackagePower => " W",
        TaskbarDisplayItem.CpuFrequency => " MHz",
        _ => ""
    };

    /// <summary>
    /// One metric per shipped display item, in an arbitrary saved order, using the real labels and
    /// units so the width/height estimates behave like the production overlay.
    /// </summary>
    private static TaskbarMetric[] RealMetrics()
    {
        var snapshot = new HardwareSnapshot(
            DateTimeOffset.UnixEpoch,
            CpuTemperatureC: 61.4,
            Fan1Rpm: 2480,
            Fan2Rpm: null,
            PowerMode: PowerMode.Balance,
            CpuUsagePercent: 13,
            MemoryUsagePercent: 58,
            FanControlMode: FanControlMode.Curve,
            TargetDuty: 42,
            Error: null)
        {
            DisplayCpuTemperatureC = 58.4,
            DisplayTemperatureFromLhm = true,
            CpuPackagePowerWatts = 18.6,
            DisplayCpuPackagePowerWatts = 18.6,
            CpuFrequencyMhz = 4200,
            TargetRpm = 2500,
            RpmFeedbackState = FanRpmFeedbackState.Stable,
            GpuTemperatureC = 56.3
        };
        return new[]
        {
            TaskbarDisplayItem.PowerMode,
            TaskbarDisplayItem.CpuTemperature,
            TaskbarDisplayItem.FanRpm,
            TaskbarDisplayItem.CpuPackagePower,
            TaskbarDisplayItem.CpuUsage,
            TaskbarDisplayItem.MemoryUsage,
            TaskbarDisplayItem.TargetRpm,
            TaskbarDisplayItem.TargetDuty,
            TaskbarDisplayItem.RpmFeedback,
            TaskbarDisplayItem.Guardian,
            TaskbarDisplayItem.WmiStatus,
            TaskbarDisplayItem.CpuFrequency,
            TaskbarDisplayItem.GpuTemperature
        }.Select(item => TaskbarMetricFormatter.Format(item, snapshot)).ToArray();
    }
}
