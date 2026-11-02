using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// The taskbar overlay placement is pure arithmetic, so the direction priority (real appbar edge
/// wins over the saved Vertical preference) and the "hide instead of parking at stale
/// coordinates" rule for an empty taskbar rectangle are pinned here without a desktop session.
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
        double contentWidth = 120, double contentHeight = 20, double scale = 1.0)
    {
        Assert.True(TaskbarOverlayLayout.TryComputePlacement(
            edge, left, top, right, bottom, userVertical,
            contentWidth, contentHeight, scale, out var placement));
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

    [Fact]
    public void BottomTaskbarKeepsTheTrayReserveAndCentresVertically()
    {
        var placement = Place(TaskbarEdge.Bottom, userVertical: false);
        Assert.Equal(ScreenWidth - placement.Width - TaskbarOverlayLayout.HorizontalTrayReserve, placement.X);
        Assert.Equal(BottomBarTop + ((ScreenHeight - BottomBarTop - placement.Height) / 2), placement.Y);
    }

    [Fact]
    public void RightTaskbarSitsAtTheBottomOfTheStripWithAnEdgeMargin()
    {
        var placement = Place(TaskbarEdge.Right, userVertical: false,
            left: 1872, top: 0, right: 1920, bottom: ScreenHeight);
        Assert.Equal(1872, placement.X);
        Assert.Equal(ScreenHeight - placement.Height - TaskbarOverlayLayout.VerticalEdgeMargin, placement.Y);
    }

    [Fact]
    public void LeftTaskbarSitsAtTheTopOfTheStripWithAnEdgeMargin()
    {
        var placement = Place(TaskbarEdge.Left, userVertical: true,
            left: 0, top: 0, right: 48, bottom: ScreenHeight);
        Assert.Equal(0, placement.X);
        Assert.Equal(TaskbarOverlayLayout.VerticalEdgeMargin, placement.Y);
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
    public void MinimumSizeNeverPushesTheOverlayOutsideAShortTaskbar()
    {
        // A tiny secondary bar: 1920x16 at the top. The minimum height (24) is larger than the
        // bar, so the overlay is clamped into the rectangle instead of overflowing it.
        var placement = Place(TaskbarEdge.Top, userVertical: false,
            left: 0, top: 0, right: 1920, bottom: 16,
            contentWidth: 120, contentHeight: 20);
        Assert.InRange(placement.Y, 0, 16);
        Assert.InRange(placement.X, 0, 1920 - placement.Width);
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
}
