using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// The taskbar overlay placement is pure arithmetic, so the direction priority (real appbar edge
/// wins over the saved Vertical preference), the fixed taskbar-start origin and the "hide instead
/// of parking at stale coordinates" rule for an empty taskbar rectangle are pinned here without a
/// desktop session.
/// </summary>
public sealed class TaskbarOverlayLayoutTests
{
    // A primary 1920x1080 taskbar at the bottom, 48 px tall, at 100% scale.
    private const int ScreenWidth = 1920;
    private const int ScreenHeight = 1080;
    private const int BottomBarTop = 1032;

    private static TaskbarOverlayPlacement Place(
        TaskbarEdge? edge, bool userVertical, int left = 0, int top = BottomBarTop,
        int right = ScreenWidth, int bottom = ScreenHeight,
        double contentWidth = 120, double contentHeight = 20, double scale = 1.0,
        int offsetXDip = 0, int offsetYDip = 0)
    {
        Assert.True(TaskbarOverlayLayout.TryComputePlacement(
            edge, left, top, right, bottom, userVertical,
            contentWidth, contentHeight, scale, out var placement,
            offsetXDip: offsetXDip, offsetYDip: offsetYDip));
        return placement;
    }

    [Theory]
    [InlineData(TaskbarEdge.Left, 0, 48)]
    [InlineData(TaskbarEdge.Right, 1872, 1920)]
    public void SideTaskbarForcesVerticalLayoutEvenWhenUserPrefersHorizontal(
        TaskbarEdge edge, int left, int right)
    {
        var placement = Place(edge, userVertical: false,
            left: left, top: 0, right: right, bottom: ScreenHeight,
            contentWidth: 20, contentHeight: 30);
        Assert.True(placement.Vertical);
        Assert.InRange(placement.Width, TaskbarOverlayLayout.MinimumVerticalWidth, right - left);
        Assert.InRange(placement.Height, TaskbarOverlayLayout.MinimumVerticalHeight, ScreenHeight);
    }

    [Theory]
    [InlineData(TaskbarEdge.Top)]
    [InlineData(TaskbarEdge.Bottom)]
    public void HorizontalTaskbarForcesHorizontalLayoutEvenWhenUserPrefersVertical(TaskbarEdge edge)
    {
        var placement = Place(edge, userVertical: true);
        Assert.False(placement.Vertical);
        Assert.True(placement.Width >= TaskbarOverlayLayout.MinimumHorizontalWidth);
        Assert.True(placement.Height <= 48, "a horizontal bar must keep the overlay inside the bar height");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownEdgeFallsBackToUserPreference(bool userVertical) =>
        Assert.Equal(userVertical,
            TaskbarOverlayLayout.UseVerticalLayout(null, userVertical));

    [Fact]
    public void UnknownEdgeStillProducesPlacementInsideTaskbarRect()
    {
        var rect = (0, BottomBarTop, ScreenWidth, ScreenHeight);
        Assert.True(TaskbarOverlayLayout.TryComputePlacement(
            null, rect.Item1, rect.Item2, rect.Item3, rect.Item4, userVertical: false,
            120, 20, 1.0, out var placement));
        Assert.False(placement.Vertical);
        Assert.InRange(placement.X, rect.Item1, rect.Item3 - placement.Width);
        Assert.InRange(placement.Y, rect.Item2, rect.Item4 - placement.Height);
    }

    [Theory]
    [InlineData(0, 1032, 0, 1080)]   // zero width, as reported while the taskbar is auto-hidden
    [InlineData(0, 1032, 1920, 1032)] // zero height
    [InlineData(1920, 1032, 0, 1080)] // inverted width (shell restart)
    [InlineData(0, 1080, 1920, 1032)] // inverted height
    public void EmptyOrInvertedTaskbarRectIsNotPlaced(int left, int top, int right, int bottom)
    {
        foreach (var edge in new TaskbarEdge?[]
                 {
                     TaskbarEdge.Left, TaskbarEdge.Top, TaskbarEdge.Right, TaskbarEdge.Bottom, null
                 })
        {
            Assert.False(TaskbarOverlayLayout.TryComputePlacement(
                edge, left, top, right, bottom, false, 120, 20, 1.0, out var placement));
            Assert.Equal(default(TaskbarOverlayPlacement), placement);
        }
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom, BottomBarTop, ScreenHeight)]
    [InlineData(TaskbarEdge.Top, 0, 48)]
    public void HorizontalTaskbarStartsAtTheLeftEdgeAndCentresVertically(
        TaskbarEdge edge, int barTop, int barBottom)
    {
        var placement = Place(edge, userVertical: false,
            top: barTop, bottom: barBottom, contentWidth: 200, contentHeight: 20);

        Assert.False(placement.Vertical);
        // Fixed origin: the left end of the bar plus the small start margin, never the tray side.
        Assert.Equal(TaskbarOverlayLayout.StartMargin, placement.X);
        Assert.Equal(200, placement.Width);
        Assert.Equal(barTop + ((barBottom - barTop - placement.Height) / 2), placement.Y);
    }

    [Fact]
    public void StartMarginScalesWithTheMonitorDpi()
    {
        // 150%: the 4 DIP margin becomes 6 px and the 200 DIP text becomes 300 px. Both lengths
        // are converted exactly once; the physical taskbar rectangle is used unchanged.
        var placement = Place(TaskbarEdge.Bottom, userVertical: false,
            contentWidth: 200, contentHeight: 20, scale: 1.5);

        Assert.Equal(6, placement.X);
        Assert.Equal(300, placement.Width);
        Assert.Equal(36, placement.Height);
        Assert.Equal(BottomBarTop + ((ScreenHeight - BottomBarTop - placement.Height) / 2), placement.Y);
    }

    [Theory]
    [InlineData(TaskbarEdge.Left, 0, 48)]
    [InlineData(TaskbarEdge.Right, 1872, 1920)]
    public void SideTaskbarsStartAtTheTopOfTheStripAndKeepTheInnerEdge(TaskbarEdge edge, int left, int right)
    {
        var placement = Place(edge, userVertical: true,
            left: left, top: 0, right: right, bottom: ScreenHeight,
            contentWidth: 20, contentHeight: 30);

        Assert.True(placement.Vertical);
        Assert.Equal(TaskbarOverlayLayout.StartMargin, placement.Y);
        // Both vertical edges hug the taskbar's inner edge, so the text column stays readable.
        Assert.Equal(right - placement.Width, placement.X);
    }

    [Fact]
    public void FixedStartIsUsedInsteadOfTheOldRightEndReservation()
    {
        // The overlay no longer keeps a reserve at the right end and no longer anchors to the
        // tray: a text that fits at the start sits at the start, far away from the tray.
        var placement = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20);

        Assert.Equal(TaskbarOverlayLayout.StartMargin, placement.X);
        Assert.True(placement.X + placement.Width < ScreenWidth,
            "the start-anchored overlay must not reach the taskbar's end");
        Assert.NotEqual(ScreenWidth - placement.Width - 160, placement.X);
    }

    [Fact]
    public void StartPlacementStaysInsideAShortTaskbar()
    {
        // A tiny secondary bar: 1920x16 at the top. The minimum height (24) is larger than the
        // bar, so the overlay is clamped into the rectangle instead of overflowing it.
        var placement = Place(TaskbarEdge.Top, userVertical: false,
            left: 0, top: 0, right: 1920, bottom: 16,
            contentWidth: 200, contentHeight: 20);
        Assert.InRange(placement.Y, 0, 16);
        Assert.InRange(placement.X, 0, 1920 - placement.Width);
    }

    [Fact]
    public void TextWiderThanTheTaskbarIsCappedToTheTaskbarLength()
    {
        // A hand-made/absurd content width can never push the window past the appbar; the window
        // is shortened to the bar and stays inside it.
        var placement = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 5000, contentHeight: 20);

        Assert.Equal(ScreenWidth, placement.Width);
        Assert.InRange(placement.X, 0, ScreenWidth - placement.Width);
    }

    [Fact]
    public void EveryEdgeResolvesInsideItsOwnTaskbarRectangle()
    {
        var bars = new (TaskbarEdge Edge, int Left, int Top, int Right, int Bottom)[]
        {
            (TaskbarEdge.Bottom, 0, BottomBarTop, ScreenWidth, ScreenHeight),
            (TaskbarEdge.Top, 0, 0, ScreenWidth, 48),
            (TaskbarEdge.Left, 0, 0, 48, ScreenHeight),
            (TaskbarEdge.Right, 1872, 0, 1920, ScreenHeight)
        };
        foreach (var bar in bars)
        {
            var placement = Place(bar.Edge, userVertical: false,
                left: bar.Left, top: bar.Top, right: bar.Right, bottom: bar.Bottom,
                contentWidth: 200, contentHeight: 20);
            Assert.InRange(placement.X, bar.Left, bar.Right - placement.Width);
            Assert.InRange(placement.X + placement.Width, bar.Left, bar.Right);
            Assert.InRange(placement.Y, bar.Top, Math.Max(bar.Top, bar.Bottom - placement.Height));
            Assert.Equal(bar.Edge is TaskbarEdge.Left or TaskbarEdge.Right, placement.Vertical);
        }
    }

    [Fact]
    public void FirstLayoutPassKeepsTheMinimumWidthAtTheStart()
    {
        // During the first pass the WPF content may not be measured yet (ActualWidth 0). The
        // window keeps the named minimum width instead of flashing a 1 px sliver, and it still
        // starts at the left edge of the bar.
        var placement = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 0, contentHeight: 0);

        Assert.Equal(TaskbarOverlayLayout.StartMargin, placement.X);
        Assert.Equal(TaskbarOverlayLayout.MinimumHorizontalWidth, placement.Width);
        Assert.Equal(TaskbarOverlayLayout.MinimumHorizontalHeight, placement.Height);
    }

    [Fact]
    public void PlacementUsesPhysicalPixelsFromTheMonitorScale()
    {
        // The minimum size is the floor in DIPs and is scaled like the measured content, matching
        // the pre-existing overlay behaviour on a 150% monitor.
        var placement = Place(TaskbarEdge.Bottom, userVertical: false,
            contentWidth: 200, contentHeight: 20, scale: 1.5);
        Assert.Equal(300, placement.Width);
        Assert.Equal(36, placement.Height);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScaleFallsBackToOneAndMinimumSizesStillApply(double scale)
    {
        var placement = Place(TaskbarEdge.Bottom, userVertical: false,
            contentWidth: 10, contentHeight: 4, scale: scale);
        Assert.Equal(TaskbarOverlayLayout.MinimumHorizontalWidth, placement.Width);
        Assert.Equal(TaskbarOverlayLayout.MinimumHorizontalHeight, placement.Height);
        Assert.Equal(4, placement.X);
    }

    [Fact]
    public void NonFiniteContentSizeDoesNotEscapeTheMinimums()
    {
        var placement = Place(TaskbarEdge.Left, userVertical: false,
            left: 0, top: 0, right: 48, bottom: ScreenHeight,
            contentWidth: double.NaN, contentHeight: double.PositiveInfinity);
        Assert.Equal(TaskbarOverlayLayout.MinimumVerticalWidth, placement.Width);
        Assert.Equal(TaskbarOverlayLayout.MinimumVerticalHeight, placement.Height);
    }

    [Fact]
    public void UserVerticalPreferenceIsIgnoredWhereTheRealBarIsHorizontal()
    {
        // The saved Vertical flag stays compatible (it is not persisted differently), but a real
        // horizontal bar wins: both preferences produce the same horizontal geometry.
        var withPreference = Place(TaskbarEdge.Bottom, userVertical: false);
        var againstPreference = Place(TaskbarEdge.Bottom, userVertical: true);
        Assert.False(withPreference.Vertical);
        Assert.False(againstPreference.Vertical);
        Assert.Equal(withPreference, againstPreference);
    }

    [Fact]
    public void OptionalMaxWidthCapsTheOverlayInsideTheStartSideOfTheBar()
    {
        // The App passes a conservative share of the bar length. The cap wins over the measured
        // text width but the overlay still starts at the fixed left origin and stays inside the
        // appbar rect. This is not notification-area measurement; it only reserves room.
        Assert.True(TaskbarOverlayLayout.TryComputePlacement(
            TaskbarEdge.Bottom, 0, BottomBarTop, ScreenWidth, ScreenHeight, userVertical: false,
            contentWidth: 1200, contentHeight: 20, scale: 1.0, out var placement,
            maxWidthDip: 400, maxHeightDip: 48));

        Assert.Equal(400, placement.Width);
        // contentHeight 20 is below the horizontal minimum of 24, so the minimum wins here.
        Assert.Equal(TaskbarOverlayLayout.MinimumHorizontalHeight, placement.Height);
        Assert.Equal(TaskbarOverlayLayout.StartMargin, placement.X);
        Assert.False(placement.Vertical);
        Assert.True(placement.X + placement.Width <= ScreenWidth);
    }

    [Fact]
    public void MaxWidthCapScalesWithDpiAndNeverExceedsTheMeasuredText()
    {
        Assert.True(TaskbarOverlayLayout.TryComputePlacement(
            TaskbarEdge.Bottom, 0, BottomBarTop, ScreenWidth, ScreenHeight, userVertical: false,
            contentWidth: 300, contentHeight: 20, scale: 1.5, out var placement,
            maxWidthDip: 400));
        // The text is only 300 DIP wide, so the 400 DIP cap must not inflate it.
        Assert.Equal(450, placement.Width);

        Assert.True(TaskbarOverlayLayout.TryComputePlacement(
            TaskbarEdge.Bottom, 0, BottomBarTop, ScreenWidth, ScreenHeight, userVertical: false,
            contentWidth: 1200, contentHeight: 20, scale: 1.5, out var capped,
            maxWidthDip: 400));
        Assert.Equal(600, capped.Width);
    }

    [Fact]
    public void MaxWidthCapStaysWithinAShortTaskbar()
    {
        Assert.True(TaskbarOverlayLayout.TryComputePlacement(
            TaskbarEdge.Bottom, 0, BottomBarTop, 300, ScreenHeight, userVertical: false,
            contentWidth: 900, contentHeight: 20, scale: 1.0, out var placement,
            maxWidthDip: 500));
        Assert.InRange(placement.Width, 1, 300);
        Assert.InRange(placement.X, 0, 300 - placement.Width);
    }

    [Fact]
    public void OverlayResolvesANewDirectionBeforeItMeasuresThePlacement()
    {
        // A display/taskbar change can flip the strip between horizontal and vertical at any
        // refresh. The window must apply the new layout and let WPF re-measure (UpdateLayout)
        // before TryComputePlacement reads ActualWidth/ActualHeight, otherwise the first placement
        // after the flip uses the old orientation's dimensions. This is a source guard because the
        // ordering lives in the WPF window, not in the pure placement math.
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        var direction = source.IndexOf(
            "TaskbarOverlayLayout.UseVerticalLayout(edge, _settings.Vertical)", StringComparison.Ordinal);
        var placement = source.IndexOf(
            "TaskbarOverlayLayout.TryComputePlacement(", StringComparison.Ordinal);
        Assert.True(direction >= 0, "PositionOnTaskbar must resolve the real direction first.");
        Assert.True(placement > direction, "Placement must be computed after the direction is resolved.");
        var measure = source.IndexOf("UpdateLayout();", direction, StringComparison.Ordinal);
        Assert.True(measure > direction && measure < placement,
            "The new orientation must be measured before the window is placed.");
    }

    private static string FindAppSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests", "MiniProToolkit.Core.Tests.csproj");
            var markup = Path.Combine(directory.FullName, "src", "MiniProToolkit.App", fileName);
            if (File.Exists(project) && File.Exists(markup))
                return markup;
        }
        throw new FileNotFoundException($"Could not locate app source {fileName} above the test directory.");
    }
    [Fact]
    public void ZeroNudgeKeepsTheExactPreviousFixedPlacement()
    {
        // A taskbar.json without the nudge fields loads as 0/0, so the placement must stay
        // byte-identical to the pre-nudge behaviour: same start origin, same size, same centre.
        var withoutArguments = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20);
        var withZeroNudge = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20,
            offsetXDip: 0, offsetYDip: 0);

        Assert.Equal(withoutArguments, withZeroNudge);
        Assert.Equal(TaskbarOverlayLayout.StartMargin, withZeroNudge.X);
        Assert.Equal(BottomBarTop + ((ScreenHeight - BottomBarTop - withZeroNudge.Height) / 2), withZeroNudge.Y);
    }

    [Fact]
    public void NudgeMovesTheFixedStartOriginInPhysicalScreenDirections()
    {
        // One click is 8 DIP; positive X moves right, negative Y moves up, both from the unchanged
        // left/centre start coordinate of a bottom taskbar.
        var basePlacement = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20);
        var nudged = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20,
            offsetXDip: 40, offsetYDip: -8);

        Assert.Equal(basePlacement.X + 40, nudged.X);
        Assert.Equal(basePlacement.Y - 8, nudged.Y);
        Assert.Equal(basePlacement.Width, nudged.Width);
        Assert.Equal(basePlacement.Height, nudged.Height);
        Assert.False(nudged.Vertical);
    }

    [Fact]
    public void NudgeIsConvertedToPhysicalPixelsExactlyOnce()
    {
        // 150%: the 40 DIP nudge becomes 60 px and the 4 DIP start margin becomes 6 px - each
        // length is scaled once, never twice.
        var placement = Place(TaskbarEdge.Bottom, userVertical: false,
            contentWidth: 200, contentHeight: 20, scale: 1.5, offsetXDip: 40);

        Assert.Equal(66, placement.X);
        Assert.Equal(300, placement.Width);
    }

    [Fact]
    public void NudgeAcrossATaskbarIsLimitedByTheBarThickness()
    {
        // A 48 px high bottom bar only leaves the strip its own height of travel: a 200 DIP (or even
        // a hand-made 1000 DIP) down-nudge clamps at the bar's bottom edge and an up-nudge clamps at
        // its top edge, so the window always stays on the taskbar.
        var down = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20,
            offsetYDip: TaskbarDisplaySettings.MaximumOffsetDip);
        var up = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20,
            offsetYDip: TaskbarDisplaySettings.MinimumOffsetDip);
        var handMade = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20,
            offsetYDip: 1000);

        Assert.Equal(ScreenHeight - down.Height, down.Y);
        Assert.Equal(BottomBarTop, up.Y);
        Assert.Equal(down.Y, handMade.Y);
        Assert.InRange(down.Y + down.Height, BottomBarTop, ScreenHeight);
        Assert.InRange(up.Y, BottomBarTop, ScreenHeight);
    }

    [Fact]
    public void HandMadeNudgeBeyondTheCoreRangeIsClampedBeforeTheAppbarClamp()
    {
        // The same bound the settings Validate enforces is applied in the placement math; a bogus
        // hand-made value can never travel 1000 px to the right.
        var placement = Place(TaskbarEdge.Bottom, userVertical: false, contentWidth: 200, contentHeight: 20,
            offsetXDip: 1000);

        Assert.Equal(TaskbarOverlayLayout.StartMargin + TaskbarDisplaySettings.MaximumOffsetDip, placement.X);
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom, 0, BottomBarTop, ScreenWidth, ScreenHeight)]
    [InlineData(TaskbarEdge.Top, 0, 0, ScreenWidth, 48)]
    [InlineData(TaskbarEdge.Left, 0, 0, 48, ScreenHeight)]
    [InlineData(TaskbarEdge.Right, 1872, 0, 1920, ScreenHeight)]
    public void NudgeStaysInsideEveryTaskbarEdge(TaskbarEdge edge, int left, int top, int right, int bottom)
    {
        foreach (var offset in new[]
                 {
                     TaskbarDisplaySettings.MinimumOffsetDip, -8, 0, 8, TaskbarDisplaySettings.MaximumOffsetDip
                 })
        {
            var diagonal = Place(edge, userVertical: false, left: left, top: top, right: right, bottom: bottom,
                contentWidth: 200, contentHeight: 20, offsetXDip: offset, offsetYDip: offset);
            var opposite = Place(edge, userVertical: false, left: left, top: top, right: right, bottom: bottom,
                contentWidth: 200, contentHeight: 20, offsetXDip: -offset, offsetYDip: -offset);

            foreach (var placement in new[] { diagonal, opposite })
            {
                Assert.InRange(placement.X, left, Math.Max(left, right - placement.Width));
                Assert.InRange(placement.X + placement.Width, left, right);
                Assert.InRange(placement.Y, top, Math.Max(top, bottom - placement.Height));
                Assert.InRange(placement.Y + placement.Height, top, bottom);
            }
        }
    }

    [Fact]
    public void SideTaskbarNudgeMovesVerticallyAndClampsAgainstItsInnerEdge()
    {
        // A vertical strip hugs the inner edge, so a right-nudge has no room and is clamped while a
        // left-nudge and both vertical directions move it. Orientation never changes.
        var basePlacement = Place(TaskbarEdge.Right, userVertical: true,
            left: 1872, top: 0, right: 1920, bottom: ScreenHeight,
            contentWidth: 20, contentHeight: 30);
        var leftNudge = Place(TaskbarEdge.Right, userVertical: true,
            left: 1872, top: 0, right: 1920, bottom: ScreenHeight,
            contentWidth: 20, contentHeight: 30, offsetXDip: -8, offsetYDip: 8);
        var rightNudge = Place(TaskbarEdge.Right, userVertical: true,
            left: 1872, top: 0, right: 1920, bottom: ScreenHeight,
            contentWidth: 20, contentHeight: 30, offsetXDip: 8);

        Assert.True(basePlacement.Vertical);
        Assert.True(leftNudge.Vertical);
        // The 42 DIP wide strip already hugs the inner edge of a 48 px bar, so only that narrow
        // remainder of travel is available: the left nudge reaches the bar's left bound and the
        // right nudge has no room at all.
        Assert.Equal(1872, leftNudge.X);
        Assert.Equal(basePlacement.Y + 8, leftNudge.Y);
        Assert.Equal(basePlacement.X, rightNudge.X);
    }

    [Fact]
    public void OverlayRepositionsWithTheStoredNudgeAfterEverySample()
    {
        // The WPF overlay must hand the saved nudge to the pure placement math on every reposition,
        // including the timer refresh, and the preview must not write settings.
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        Assert.Contains("offsetXDip: _settings.OffsetXDip", source);
        Assert.Contains("offsetYDip: _settings.OffsetYDip", source);
        Assert.Contains("public void UpdateDisplaySettings(TaskbarDisplaySettings settings)", source);
        Assert.Contains("_settings = settings;", source);
    }
}
