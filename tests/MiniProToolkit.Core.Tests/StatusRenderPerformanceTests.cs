using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Guards for the per-tick rendering strategy of the taskbar strip and the desktop card: a sample
/// whose plan keeps the same topology must reuse the existing WPF elements and only rewrite the
/// live wording, while any structural or presentation change (order, style, direction, font, compact,
/// budget, mode, overflow, label metadata, grid projection) must invalidate and rebuild.
/// <para>
/// The pure <see cref="TaskbarRenderSignature"/> / <see cref="TaskbarRenderTexts"/> pair is tested
/// directly; the two WPF windows are covered by source guards only (no window construction, no
/// screen/hardware access), so real repaint timing stays a target-machine smoke check.
/// </para>
/// </summary>
public sealed class StatusRenderPerformanceTests
{
    private const double Wide = 2000;
    private const double Narrow = 260;
    private const double Font = 12;

    // -----------------------------------------------------------------------------------------
    // Topology key: what must NOT rebuild
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TaskbarLayoutMode.StackedPairs)]
    [InlineData(TaskbarLayoutMode.Inline)]
    [InlineData(TaskbarLayoutMode.AdaptiveColumns)]
    [InlineData(TaskbarLayoutMode.LabelOverValue)]
    public void ValueOnlyChangesKeepTheTopologySoTheExistingTreeIsReused(TaskbarLayoutMode mode)
    {
        // A live 1 s sample changes values (and sometimes the detail line) on every tick. That must
        // never be a rebuild, otherwise the whole point of the cache is lost.
        var first = Metrics(13, value: index => index.ToString());
        var second = Metrics(13, value: index => (index * 37 + 5).ToString(), detail: index => $"detail {index}");

        foreach (var vertical in new[] { false, true })
        {
            var planFirst = TaskbarItemLayout.Build(mode, first, Wide, Font, vertical);
            var planSecond = TaskbarItemLayout.Build(mode, second, Wide, Font, vertical);
            var signatureFirst = TaskbarRenderSignature.Create(planFirst, vertical, compact: false, Font, Wide);
            var signatureSecond = TaskbarRenderSignature.Create(planSecond, vertical, compact: false, Font, Wide);

            Assert.True(signatureFirst.Matches(signatureSecond));
            Assert.Equal(signatureFirst.SlotCount, signatureSecond.SlotCount);

            var updatesFirst = TaskbarRenderTexts.Updates(planFirst);
            var updatesSecond = TaskbarRenderTexts.Updates(planSecond);
            Assert.Equal(updatesFirst.Count, updatesSecond.Count);
            Assert.NotEqual(updatesFirst[0].Text, updatesSecond[0].Text);
            // The detail line is wording too: it changes the tooltip, never the topology.
            Assert.NotEqual(updatesFirst[0].ToolTip, updatesSecond[0].ToolTip);
        }
    }

    [Fact]
    public void RepeatingTheSameSampleProducesIdenticalWordingSoNoAssignmentHappens()
    {
        // The renderer assigns only when the value differs; an unchanged sample therefore writes
        // nothing at all (no TextBlock text/opacity/tooltip invalidation on an idle second).
        var metrics = Metrics(13, value: index => (index * 3).ToString());
        var planA = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, metrics, Wide, Font, vertical: false);
        var planB = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, metrics, Wide, Font, vertical: false);

        var updatesA = TaskbarRenderTexts.Updates(planA);
        var updatesB = TaskbarRenderTexts.Updates(planB);
        Assert.True(updatesA.SequenceEqual(updatesB));
    }

    // -----------------------------------------------------------------------------------------
    // Topology key: what MUST rebuild
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ReorderingTheConfiguredItemsChangesTheTopology()
    {
        var ordered = Metrics(6);
        var reordered = ordered.Reverse().ToArray();
        var planOrdered = TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, ordered, Wide, Font, vertical: false);
        var planReordered = TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, reordered, Wide, Font, vertical: false);

        var signatureOrdered = TaskbarRenderSignature.Create(planOrdered, false, false, Font, Wide);
        var signatureReordered = TaskbarRenderSignature.Create(planReordered, false, false, Font, Wide);
        Assert.False(signatureOrdered.Matches(signatureReordered));
    }

    [Fact]
    public void LabelAndUnitMetadataChangesTheTopologyBecauseItIsWhatThePlannerMeasured()
    {
        // The plan's width/height estimate is built from label + unit (+ the card label). A metadata
        // change can flip the fit decision, so it must not be treated as "wording only".
        var metrics = Metrics(4);
        var expanded = metrics.Select(metric => metric with { Label = metric.Label + " 额外" }).ToArray();
        var otherUnit = metrics.Select(metric => metric with { Unit = "（长单位）" }).ToArray();

        var signature = TaskbarRenderSignature.Create(
            TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, metrics, Wide, Font, vertical: false),
            false, false, Font, Wide);
        var signatureExpanded = TaskbarRenderSignature.Create(
            TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, expanded, Wide, Font, vertical: false),
            false, false, Font, Wide);
        var signatureUnit = TaskbarRenderSignature.Create(
            TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, otherUnit, Wide, Font, vertical: false),
            false, false, Font, Wide);

        Assert.False(signature.Matches(signatureExpanded));
        Assert.False(signature.Matches(signatureUnit));
        // The card label is the third measured input and is carried per slot as well (E cards).
        Assert.Equal(metrics[0].CardLabel, TaskbarRenderLayoutText.FromMetric(metrics[0]).CardLabel);
    }

    [Fact]
    public void ModeDirectionFontCompactAndBudgetChangesInvalidateTheTopology()
    {
        var metrics = Metrics(5);
        var horizontalPlan = TaskbarItemLayout.Build(TaskbarLayoutMode.AdaptiveColumns, metrics, Wide, Font, vertical: false);
        var baseline = TaskbarRenderSignature.Create(horizontalPlan, false, compact: false, Font, Wide);

        // Same plan, different presentation knobs the renderer applies to every element.
        Assert.False(baseline.Matches(TaskbarRenderSignature.Create(horizontalPlan, false, false, Font + 2, Wide)));
        Assert.False(baseline.Matches(TaskbarRenderSignature.Create(horizontalPlan, false, compact: true, Font, Wide)));
        Assert.False(baseline.Matches(TaskbarRenderSignature.Create(horizontalPlan, true, false, Font, Wide)));
        Assert.False(baseline.Matches(TaskbarRenderSignature.Create(horizontalPlan, false, false, Font, Wide - 40)));
        Assert.False(baseline.Matches(TaskbarRenderSignature.Create(horizontalPlan, false, false, Font, Wide, 20)));
        Assert.True(baseline.Matches(TaskbarRenderSignature.Create(horizontalPlan, false, false, Font, Wide)));

        // A different arrangement (A vs D/C/E) is a different tree.
        foreach (var mode in Enum.GetValues<TaskbarLayoutMode>().Where(mode => mode != TaskbarLayoutMode.AdaptiveColumns))
        {
            var other = TaskbarRenderSignature.Create(
                TaskbarItemLayout.Build(mode, metrics, Wide, Font, vertical: false), false, false, Font, Wide);
            Assert.False(baseline.Matches(other));
        }
    }

    [Fact]
    public void AChangingOverflowStructureInvalidatesTheTopology()
    {
        // A narrower safe budget drops metrics into the explicit +N marker: fewer elements, so the
        // tree must be rebuilt (and the reservation for the marker changes the geometry too).
        var metrics = Metrics(13);
        var wide = TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, metrics, Wide, Font, vertical: false);
        var narrow = TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, metrics, Narrow, Font, vertical: false);
        Assert.Null(wide.Overflow);
        Assert.NotNull(narrow.Overflow);

        var wideSignature = TaskbarRenderSignature.Create(wide, false, false, Font, Wide);
        var narrowSignature = TaskbarRenderSignature.Create(narrow, false, false, Font, Narrow);
        Assert.False(wideSignature.Matches(narrowSignature));
        Assert.Equal(13, wideSignature.SlotCount);
        Assert.Equal(narrow.Cells.Count + 1, narrowSignature.SlotCount);

        // Even with identical cell structure, a different hidden list is a different topology.
        var hiddenFirst = new TaskbarOverflowMarker("+1", [metrics[0]], "t", "n");
        var hiddenSecond = new TaskbarOverflowMarker("+1", [metrics[1]], "t", "n");
        var cell = new TaskbarMetricCell(0, 0, metrics[2], 2, 0, TaskbarCellStyle.Plain);
        TaskbarMetricLayoutPlan PlanWith(TaskbarOverflowMarker marker) =>
            new(TaskbarLayoutMode.Inline, 1, 1, [cell], marker, 100, 20);
        Assert.False(TaskbarRenderSignature.Create(PlanWith(hiddenFirst), false, false, Font, Wide)
            .Matches(TaskbarRenderSignature.Create(PlanWith(hiddenSecond), false, false, Font, Wide)));
    }

    [Fact]
    public void MarkerWordingAndCellValuesAreNotPartOfTheTopology()
    {
        // The cached structure must never pin an old +N tooltip or an old value: the same topology
        // with new wording matches, and the update list carries the new text.
        var metrics = Metrics(3);
        var cell = new TaskbarMetricCell(0, 0, metrics[0], 0, 0, TaskbarCellStyle.Plain);
        TaskbarMetricLayoutPlan PlanWith(TaskbarMetric visible, TaskbarOverflowMarker marker) =>
            new(TaskbarLayoutMode.Inline, 1, 1, [cell with { Metric = visible }], marker, 100, 20);

        var oldHidden = new TaskbarOverflowMarker("+2", [metrics[1], metrics[2]], "旧提示", "另有 2 项指标未显示");
        var newHidden = new TaskbarOverflowMarker("+2", [metrics[1], metrics[2]], "新提示", "另有 2 项指标未显示");
        var newValue = metrics[0] with { Value = "99", Detail = "新的来源提示" };

        var before = TaskbarRenderSignature.Create(PlanWith(metrics[0], oldHidden), false, false, Font, Wide);
        var after = TaskbarRenderSignature.Create(PlanWith(newValue, newHidden), false, false, Font, Wide);
        Assert.True(before.Matches(after));

        var updates = TaskbarRenderTexts.Updates(PlanWith(newValue, newHidden));
        Assert.Equal("新提示", updates[^1].ToolTip);
        Assert.Contains("99", updates[0].Text);
        Assert.Contains("新的来源提示", updates[0].ToolTip);
    }

    [Fact]
    public void TheGridProjectionIsPartOfTheTopology()
    {
        var metrics = Metrics(3);
        var horizontal = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, metrics, Wide, Font, vertical: false);
        var vertical = TaskbarItemLayout.Build(TaskbarLayoutMode.StackedPairs, metrics, Wide, Font, vertical: true);
        var horizontalSignature = TaskbarRenderSignature.Create(horizontal, false, false, Font, Wide);
        var verticalSignature = TaskbarRenderSignature.Create(vertical, true, false, Font, Wide);

        // A's single group divider and, on a vertical bar, one column only (the marker gets a row).
        Assert.Equal<TaskbarGridColumnKind[]>(
            [TaskbarGridColumnKind.Metric, TaskbarGridColumnKind.GroupGap, TaskbarGridColumnKind.Separator,
             TaskbarGridColumnKind.GroupGap, TaskbarGridColumnKind.Metric],
            horizontalSignature.GridColumns.Select(column => column.Kind).ToArray());
        Assert.Equal<TaskbarGridColumnKind[]>([TaskbarGridColumnKind.Metric],
            verticalSignature.GridColumns.Select(column => column.Kind).ToArray());
        Assert.False(horizontalSignature.Matches(verticalSignature));

        // C/E do not draw a grid at all, so their projection list is empty.
        var inline = TaskbarRenderSignature.Create(
            TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, metrics, Wide, Font, vertical: false), false, false, Font, Wide);
        Assert.Empty(inline.GridColumns);
    }

    // -----------------------------------------------------------------------------------------
    // The in-place update list
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheOverflowMarkerUpdateCarriesTheLiveTextTooltipAndAccessibleName()
    {
        var metrics = Metrics(13);
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.Inline, metrics, Narrow, Font, vertical: false);
        var marker = Assert.IsType<TaskbarOverflowMarker>(plan.Overflow);
        var updates = TaskbarRenderTexts.Updates(plan);

        var update = updates[^1];
        Assert.True(update.IsOverflow);
        Assert.Equal("+" + marker.HiddenCount, update.Text);
        Assert.Equal(marker.ToolTip, update.ToolTip);
        Assert.Equal(marker.AccessibleName, update.AccessibleName);
        Assert.StartsWith("另有 ", update.AccessibleName);
        // Every omitted metric is still named in the marker tooltip.
        foreach (var hidden in marker.Hidden)
            Assert.Contains(hidden.Text, update.ToolTip);
    }

    [Fact]
    public void CardSlotsCarryTheirLabelValueAndPairTooltip()
    {
        var metrics = Metrics(4);
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.LabelOverValue, metrics, Wide, Font, vertical: false);

        foreach (var update in TaskbarRenderTexts.Updates(plan).Where(update => !update.IsOverflow))
        {
            var metric = plan.Cells.Single(cell => cell.Metric.Item == update.Slot.Item).Metric;
            Assert.True(update.IsCard);
            Assert.Equal(metric.ValueText, update.Text);
            Assert.Equal(metric.ValueText, update.AccessibleName);
            Assert.Equal(metric.CardLabel, update.CardLabel);
            Assert.Equal(metric.CardLabel + " " + metric.ValueText, update.CardToolTip);
        }
    }

    [Fact]
    public void OnlyTheStackedPairArrangementUsesTheColonWording()
    {
        var metrics = Metrics(4);
        foreach (var mode in Enum.GetValues<TaskbarLayoutMode>())
        {
            var plan = TaskbarItemLayout.Build(mode, metrics, Wide, Font, vertical: false);
            var updates = TaskbarRenderTexts.Updates(plan);
            for (var index = 0; index < plan.Cells.Count; index++)
            {
                var cell = plan.Cells[index];
                var expected = cell.Style switch
                {
                    TaskbarCellStyle.Colon => cell.Metric.ColonText,
                    TaskbarCellStyle.Card => cell.Metric.ValueText,
                    _ => cell.Metric.Text
                };
                Assert.Equal(expected, updates[index].Text);
                if (mode == TaskbarLayoutMode.StackedPairs)
                    Assert.Equal(TaskbarCellStyle.Colon, cell.Style);
            }
        }
    }

    [Fact]
    public void EverySlotHasExactlyOneElementAndTheCacheStaysBounded()
    {
        var metrics = Metrics(13);
        foreach (var mode in Enum.GetValues<TaskbarLayoutMode>())
        {
            foreach (var vertical in new[] { false, true })
            {
                var plan = TaskbarItemLayout.Build(mode, metrics, Narrow, Font, vertical);
                var signature = TaskbarRenderSignature.Create(plan, vertical, compact: false, Font, Narrow);
                var expected = plan.Cells.Count + (plan.Overflow is null ? 0 : 1);
                Assert.Equal(expected, signature.SlotCount);
                Assert.Equal(expected, TaskbarRenderTexts.Updates(plan).Count);
                Assert.Equal(expected, signature.LayoutTexts.Count);
                // 13 shipped items plus at most the single +N marker: the reference cache is bounded.
                Assert.InRange(signature.SlotCount, 1, 14);
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // WPF window source guards (no window is constructed)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheOverlayKeepsTheNewestSampleBeforeItSkipsTheHiddenTree()
    {
        // A hidden strip (auto-hidden/absent taskbar) must still record the sample and the control
        // gates, and only then skip building the tree - never the other way round.
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        var snapshot = source.IndexOf("_snapshot = snapshot;", StringComparison.Ordinal);
        var guard = source.IndexOf("_hiddenForUnusableTaskbar || !IsVisible", snapshot, StringComparison.Ordinal);
        var render = source.IndexOf("RenderStatusLayoutCells(snapshot);", guard, StringComparison.Ordinal);
        Assert.True(snapshot >= 0, "UpdateSnapshot must keep the newest snapshot.");
        Assert.True(guard > snapshot, "The hidden/visibility guard must run after the snapshot is kept.");
        Assert.True(render > guard, "The tree must only be built on the visible path.");
        // And the hidden path invalidates, so the next visible render repaints instead of going stale.
        var invalidated = source.IndexOf("InvalidateStatusRender();", guard, StringComparison.Ordinal);
        Assert.InRange(invalidated, guard, render);
    }

    [Fact]
    public void TheOverlayRepaintsTheKeptSampleWhenTheStripReturns()
    {
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        Assert.Contains("RestoreWithLatestSnapshot()", source);
        Assert.Contains("RefreshStatusFromLatestSnapshot()", source);
        // The restore path shows the window and then repaints the newest snapshot.
        var restore = source.IndexOf("private bool RestoreWithLatestSnapshot()", StringComparison.Ordinal);
        Assert.True(restore >= 0);
        Assert.Contains("RestoreFromUnusableTaskbar();", source[restore..]);
        Assert.Contains("RefreshStatusFromLatestSnapshot();", source[restore..]);
        // An attach paints the newest kept sample as well instead of waiting for the next tick.
        var attach = source.IndexOf("public void TryAttachToTaskbar()", StringComparison.Ordinal);
        Assert.Contains("RefreshStatusFromLatestSnapshot();", source[attach..restore]);
    }

    [Fact]
    public void TheWindowsReuseTheTreeAndDropTheCachedReferencesOnRebuildAndClose()
    {
        foreach (var file in new[] { "TaskbarStatusWindow.xaml.cs", "FloatingStatusWindow.xaml.cs" })
        {
            var source = File.ReadAllText(FindAppSource(file));
            Assert.Contains("TaskbarRenderSignature.Create(", source);
            Assert.Contains("previous.Matches(signature)", source);
            Assert.Contains("UpdateRenderedCellWording(plan);", source);
            Assert.Contains("TaskbarRenderTexts.Updates(plan)", source);
            // One entry per slot, dropped on every rebuild and on close (never unbounded).
            Assert.Contains("_renderedCells.Clear();", source);
            var closed = source.IndexOf("protected override void OnClosed(EventArgs e)", StringComparison.Ordinal);
            Assert.True(closed >= 0, $"{file} must drop its cached controls when it closes.");
            var closedBody = source[closed..Math.Min(source.Length, closed + 400)];
            Assert.True(closedBody.Contains("ClearStatusLayout();", StringComparison.Ordinal) ||
                        closedBody.Contains("_renderedCells.Clear();", StringComparison.Ordinal),
                $"{file} must release the cached control references on close.");
            Assert.Contains("InvalidateStatusRender();", source);
        }
    }

    [Fact]
    public void TheAppearanceChangeInvalidatesTheCachedTree()
    {
        // Font/padding/brush changes are not visible to the pure topology, so both windows force a
        // rebuild from their single appearance/layout entry point.
        var taskbar = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        var layout = taskbar.IndexOf("private void ApplyLayout()", StringComparison.Ordinal);
        var layoutEnd = taskbar.IndexOf("private static System.Windows.Media.FontFamily CreateFontFamily", layout, StringComparison.Ordinal);
        Assert.Contains("InvalidateStatusRender();", taskbar[layout..layoutEnd]);
        var display = taskbar.IndexOf("public void UpdateDisplaySettings(TaskbarDisplaySettings settings)", StringComparison.Ordinal);
        Assert.Contains("InvalidateStatusRender();", taskbar[display..layout]);

        var floating = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        var appearance = floating.IndexOf("private void ApplyAppearance()", StringComparison.Ordinal);
        var appearanceEnd = floating.IndexOf("private static WpfFontFamily CreateFontFamily", appearance, StringComparison.Ordinal);
        Assert.Contains("InvalidateStatusRender();", floating[appearance..appearanceEnd]);
    }

    [Fact]
    public void TheOverlaySkipsOnlyTheIdenticalNativePlacementAndKeepsTheFirstFrameChange()
    {
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        Assert.Contains("IsSameNativePlacement(placement)", source);
        Assert.Contains("NativeMethods.SwpFrameChanged", source);
        // A re-attach, a DPI change and a restore must force one real placement again.
        Assert.Contains("_hasPlacedRect = false;", source);
        var attach = source.IndexOf("public void TryAttachToTaskbar()", StringComparison.Ordinal);
        Assert.Contains("_hasPlacedRect = false;", source[attach..]);
        var dpi = source.IndexOf("protected override void OnDpiChanged", StringComparison.Ordinal);
        Assert.True(dpi >= 0, "The overlay must invalidate the placement cache on a DPI change.");
        Assert.Contains("_hasPlacedRect = false;", source[dpi..]);
        var restore = source.IndexOf("private bool RestoreWithLatestSnapshot()", StringComparison.Ordinal);
        Assert.Contains("_hasPlacedRect = false;", source[restore..]);
        // The nudge/anchor strategy itself is untouched.
        Assert.Contains("offsetXDip: _settings.OffsetXDip", source);
        Assert.Contains("offsetYDip: _settings.OffsetYDip", source);
    }

    [Fact]
    public void TryAttachRepaintsOnlyOnTheRealFirstAttachAndJustProbesAfterwards()
    {
        // The owner calls TryAttachToTaskbar on every sampling tick. Only the very first attach (the
        // one that writes the window style) may repaint the kept sample; every later tick must stay
        // a placement probe, because UpdateSnapshot already painted the newest values in that tick.
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        var attach = source.IndexOf("public void TryAttachToTaskbar()", StringComparison.Ordinal);
        var attachEnd = source.IndexOf("protected override void OnClosed", attach, StringComparison.Ordinal);
        Assert.True(attach >= 0 && attachEnd > attach);
        var body = source[attach..attachEnd];

        var attachedTrue = body.IndexOf("_attached = true;", StringComparison.Ordinal);
        var refresh = body.IndexOf("RefreshStatusFromLatestSnapshot();", StringComparison.Ordinal);
        var probe = body.IndexOf("PositionOnTaskbar();", StringComparison.Ordinal);
        Assert.True(attachedTrue >= 0, "The first attach must be detected by the _attached transition.");
        Assert.True(refresh > attachedTrue,
            "The attach refresh must sit inside the !_attached block, not after the per-tick probe.");
        Assert.True(probe > refresh, "The per-tick placement probe runs after the one-time refresh.");
        // Exactly one refresh in the whole method, i.e. no per-tick re-render of the previous sample.
        Assert.Equal(-1, body.IndexOf("RefreshStatusFromLatestSnapshot();", refresh + 1, StringComparison.Ordinal));
        Assert.Equal(-1, body.IndexOf("RenderStatusLayoutCells", StringComparison.Ordinal));
    }

    [Fact]
    public void HiddenAndInvisibleTicksNeverBuildTheTree()
    {
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        var snapshot = source.IndexOf("_snapshot = snapshot;", StringComparison.Ordinal);
        var guard = source.IndexOf("_hiddenForUnusableTaskbar || !IsVisible", snapshot, StringComparison.Ordinal);
        var render = source.IndexOf("RenderStatusLayoutCells(snapshot);", guard, StringComparison.Ordinal);
        Assert.True(guard > snapshot && render > guard);
        // The hidden/not-yet-visible branch only invalidates and probes the taskbar rectangle.
        var hiddenBranch = source[guard..render];
        Assert.DoesNotContain("RenderStatusLayoutCells", hiddenBranch);
        Assert.DoesNotContain("RebuildStatusLayout", hiddenBranch);
        Assert.DoesNotContain("UpdateStatusToolTip", hiddenBranch);
        Assert.Contains("InvalidateStatusRender();", hiddenBranch);

        // The desktop card keeps the same rule: an off-screen/disabled card is never re-rendered.
        var floating = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));
        var cardSnapshot = floating.IndexOf("_snapshot = snapshot;", StringComparison.Ordinal);
        var cardGuard = floating.IndexOf("if (IsVisible && _settings.Enabled) Render();", cardSnapshot, StringComparison.Ordinal);
        Assert.True(cardSnapshot >= 0 && cardGuard > cardSnapshot);
    }

    [Fact]
    public void IsNativeWindowAliveAlsoTracksTheHostedTaskbarWindow()
    {
        // The owner's periodic taskbar discovery treats a dead overlay as stale and rebuilds it. It
        // must notice an Explorer restart even while this overlay HWND is still valid, so the hosted
        // taskbar handle is part of the liveness answer (and a zero handle stays "not alive").
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        var property = source.IndexOf("public bool IsNativeWindowAlive", StringComparison.Ordinal);
        Assert.True(property >= 0);
        var declaration = source[property..source.IndexOf(';', property)];
        Assert.Contains("_handle != IntPtr.Zero", declaration);
        Assert.Contains("NativeMethods.IsWindow(_handle)", declaration);
        Assert.Contains("_taskbarHandle != IntPtr.Zero", declaration);
        Assert.Contains("NativeMethods.IsWindow(_taskbarHandle)", declaration);
    }

    // -----------------------------------------------------------------------------------------
    // Desktop card: the plan budget follows the live window cap
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ATinyHighDpiWorkAreaCapsTheCardBelowItsContentUpperBound()
    {
        // 640x400 physical pixels on a 200% monitor is a 320 DIP window cap - far below the 560 DIP
        // content upper bound the card used to plan with. Planning against the cap is what turns the
        // same thirteen items into a marker instead of an overflowing (scrolling) row.
        var (cap, _) = FloatingStatusLayout.MaximumWindowSizeDip(640, 400, 2.0);
        Assert.Equal(320, cap);
        Assert.True(cap < 560);

        var metrics = Metrics(13);
        const double RegularPaddingPerSide = 14;
        var budget = Math.Max(1, cap - RegularPaddingPerSide * 2);
        Assert.Equal(292, budget);

        var planned = TaskbarItemLayout.Build(TaskbarLayoutMode.LabelOverValue, metrics, budget, Font, vertical: false);
        var unbudgeted = TaskbarItemLayout.Build(TaskbarLayoutMode.LabelOverValue, metrics, 560, Font, vertical: false);

        // The card arrangement keeps the explicit marker for what the real window cannot show.
        var marker = Assert.IsType<TaskbarOverflowMarker>(planned.Overflow);
        Assert.Equal(metrics.Length - planned.VisibleCount, marker.HiddenCount);
        Assert.True(planned.VisibleCount < unbudgeted.VisibleCount,
            "Budgeting against the real window cap must show fewer cells than the 560 DIP plan.");
        Assert.True(planned.EstimatedWidthDip <= budget,
            $"The plan must fit the {budget} DIP window content width, not overflow it.");

        // A cap change is a budget change, so the cached tree can never keep the old decision.
        var plannedSignature = TaskbarRenderSignature.Create(planned, false, compact: false, Font, budget);
        var unbudgetedSignature = TaskbarRenderSignature.Create(unbudgeted, false, compact: false, Font, 560);
        Assert.Equal(budget, plannedSignature.WidthBudgetDip);
        Assert.False(plannedSignature.Matches(unbudgetedSignature));
    }

    [Theory]
    [InlineData(92)]  // 120 DIP window cap (FloatingStatusLayout.MinimumWidthDip) minus the regular 14 per side
    [InlineData(100)] // same cap, compact 10 per side
    [InlineData(48)]
    [InlineData(1)]   // the padding alone is wider than the cap: the floored budget still plans
    public void ATightWorkAreaBudgetStillProducesTheExplicitMarkerAndNeverDropsAnItem(double budget)
    {
        var metrics = Metrics(13);
        var plan = TaskbarItemLayout.Build(TaskbarLayoutMode.LabelOverValue, metrics, budget, Font, vertical: false);
        var marker = Assert.IsType<TaskbarOverflowMarker>(plan.Overflow);

        // At least the most important cell stays visible, everything else is counted, not clipped.
        Assert.InRange(plan.VisibleCount, 1, metrics.Length - 1);
        Assert.Equal(metrics.Length, plan.VisibleCount + marker.HiddenCount);
        Assert.Equal(plan.VisibleCount + 1, TaskbarRenderTexts.Updates(plan).Count);

        // The marker wording/tooltip/accessible name carry the live count and every hidden value, so
        // the smallest budget loses no information (identical to the taskbar strip's semantics).
        var updates = TaskbarRenderTexts.Updates(plan);
        var markerUpdate = updates[^1];
        Assert.True(markerUpdate.IsOverflow);
        Assert.Equal("+" + marker.HiddenCount, markerUpdate.Text);
        Assert.Equal(marker.ToolTip, markerUpdate.ToolTip);
        Assert.Equal(marker.AccessibleName, markerUpdate.AccessibleName);
        foreach (var hidden in marker.Hidden)
            Assert.Contains(hidden.Text, markerUpdate.ToolTip);
        Assert.Contains(marker.HiddenCount.ToString(), marker.AccessibleName);

        // The same budget is a pure function of the cap, so an unchanged cap reuses the tree...
        var signature = TaskbarRenderSignature.Create(plan, false, compact: false, Font, budget);
        var repeated = TaskbarRenderSignature.Create(
            TaskbarItemLayout.Build(TaskbarLayoutMode.LabelOverValue, metrics, budget, Font, vertical: false),
            false, compact: false, Font, budget);
        Assert.True(signature.Matches(repeated));
        Assert.Equal(updates.Count, signature.SlotCount);
        // ...while any other cap re-plans and therefore rebuilds (the renderer's Matches decides).
        Assert.False(signature.Matches(TaskbarRenderSignature.Create(plan, false, compact: false, Font, budget + 0.5)));
    }

    [Fact]
    public void TheCardPlansAndSignsWithOneWorkAreaDerivedBudget()
    {
        var source = File.ReadAllText(FindAppSource("FloatingStatusWindow.xaml.cs"));

        // Both calls receive the very same computed budget symbol, so the +N decision and the cached
        // topology can never disagree about the width they were made for.
        Assert.Contains("TaskbarItemLayout.Build(_settings.LayoutMode, metrics, MaximumContentWidthDip, _fontSize, vertical: false)", source);
        Assert.Contains("TaskbarRenderSignature.Create(plan, vertical: false, _settings.Compact,", source);
        Assert.Contains("_fontSize, MaximumContentWidthDip);", source);
        // No fixed budget reaches either call any more: a hard-coded 560 is what used to overflow.
        Assert.DoesNotContain("metrics, 560", source);
        Assert.DoesNotContain("_fontSize, 560", source);

        // The budget is the *live* cap (ApplyWorkAreaLimits) minus the padding the border really
        // draws, with the documented fallback/cap/floor around it.
        var limits = SourceBetween(source, "private void ApplyWorkAreaLimits", "private static bool IsSameLimit");
        Assert.Contains("FloatingStatusLayout.MaximumWindowSizeDip(", limits);
        Assert.Contains("MaxWidth = maxWidth;", limits);

        var budget = SourceBetween(source, "private double MaximumContentWidthDip", "private void Render()");
        Assert.Contains("var cap = MaxWidth;", budget);
        Assert.Contains("if (!double.IsFinite(cap) || cap <= 0) cap = DefaultContentWidthDip;", budget);
        Assert.Contains("horizontalPadding = border.Padding.Left + border.Padding.Right;", budget);
        Assert.Contains("var padding = _settings.Compact ? CompactCardPadding : RegularCardPadding;", budget);
        Assert.Contains("Math.Max(1, Math.Min(DefaultContentWidthDip, cap - horizontalPadding))", budget);

        // One source for the upper bound and for the two paddings: the appearance writes exactly the
        // constants the budget subtracts, so the planned content width and the real card agree.
        Assert.Contains("private const double DefaultContentWidthDip = 560;", source);
        Assert.Contains("private static readonly Thickness CompactCardPadding = new(10, 4, 10, 8);", source);
        Assert.Contains("private static readonly Thickness RegularCardPadding = new(14, 6, 14, 10);", source);
        Assert.Contains("RootBorder.Padding = _settings.Compact ? CompactCardPadding : RegularCardPadding;", source);
    }

    private static string SourceBetween(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Missing source marker: {start}");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"Missing source marker after {start}: {end}");
        return source[from..to];
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

    private static TaskbarMetric[] Metrics(int count, Func<int, string>? value = null,
        Func<int, string>? label = null, Func<int, string>? detail = null)
    {
        var items = Enum.GetValues<TaskbarDisplayItem>();
        var result = new TaskbarMetric[count];
        for (var index = 0; index < count; index++)
        {
            var item = items[index % items.Length];
            result[index] = new TaskbarMetric(item, label?.Invoke(index) ?? $"L{index}",
                value?.Invoke(index) ?? index.ToString(), UnitFor(item), detail?.Invoke(index));
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
}
