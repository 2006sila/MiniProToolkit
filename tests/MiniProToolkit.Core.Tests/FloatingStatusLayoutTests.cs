using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Pins the pure placement rules of the desktop floating window: exact work-area edges, the
/// bottom-right default, negative virtual-screen origins, a work area smaller than the window,
/// per-monitor DPI conversion and the stored work-area-relative DIP offset. No screen is touched.
/// </summary>
public sealed class FloatingStatusLayoutTests
{
    // A common 1920x1040 primary work area (1080 screen minus a 40 px taskbar).
    private const int WorkLeft = 0;
    private const int WorkTop = 0;
    private const int WorkRight = 1920;
    private const int WorkBottom = 1040;

    private static FloatingStatusPlacement Compute(
        int left, int top, int right, int bottom, double widthDip, double heightDip, double scale,
        double? leftDip = null, double? topDip = null)
    {
        Assert.True(FloatingStatusLayout.TryComputePlacement(
            left, top, right, bottom, widthDip, heightDip, scale, leftDip, topDip, out var placement));
        return placement;
    }

    [Fact]
    public void DefaultPositionIsTheBottomRightCornerWithOneMargin()
    {
        var placement = Compute(WorkLeft, WorkTop, WorkRight, WorkBottom, 200, 80, 1.0);

        Assert.Equal(1920 - 200 - 16, placement.X);
        Assert.Equal(1040 - 80 - 16, placement.Y);
        Assert.Equal(200, placement.Width);
        Assert.Equal(80, placement.Height);
    }

    [Fact]
    public void StoredPositionIsRestoredExactly()
    {
        var placement = Compute(WorkLeft, WorkTop, WorkRight, WorkBottom, 200, 80, 1.0, 100, 240);

        Assert.Equal(100, placement.X);
        Assert.Equal(240, placement.Y);
        Assert.Equal(100, placement.RelativeLeftDip);
        Assert.Equal(240, placement.RelativeTopDip);
    }

    [Fact]
    public void ExactRightAndBottomEdgesAreAcceptedWithoutMovingTheWindow()
    {
        // The window exactly touches the right/bottom work-area edge: no clamp, no 1 px gap.
        var placement = Compute(WorkLeft, WorkTop, WorkRight, WorkBottom, 200, 80, 1.0,
            WorkRight - 200, WorkBottom - 80);

        Assert.Equal(1720, placement.X);
        Assert.Equal(960, placement.Y);
        Assert.Equal(1920, placement.X + placement.Width);
        Assert.Equal(1040, placement.Y + placement.Height);
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(1721, 961)]
    [InlineData(1, 1)] 
    public void OutOfBoundsStoredPositionIsClampedFullyInsideTheWorkArea(int leftDip, int topDip)
    {
        // (1,1) is inside and stays; the two larger values must clamp to the far edge.
        var placement = Compute(WorkLeft, WorkTop, WorkRight, WorkBottom, 200, 80, 1.0, leftDip, topDip);

        Assert.True(placement.X >= WorkLeft);
        Assert.True(placement.Y >= WorkTop);
        Assert.True(placement.X + placement.Width <= WorkRight);
        Assert.True(placement.Y + placement.Height <= WorkBottom);
        Assert.Equal(Math.Min(leftDip, WorkRight - 200), placement.X);
        Assert.Equal(Math.Min(topDip, WorkBottom - 80), placement.Y);
    }

    [Fact]
    public void NegativeStoredOffsetIsClampedToTheWorkAreaOrigin()
    {
        var placement = Compute(WorkLeft, WorkTop, WorkRight, WorkBottom, 200, 80, 1.0, -500, -900);

        Assert.Equal(WorkLeft, placement.X);
        Assert.Equal(WorkTop, placement.Y);
        Assert.Equal(0, placement.RelativeLeftDip);
        Assert.Equal(0, placement.RelativeTopDip);
    }

    [Fact]
    public void NegativeVirtualScreenOriginIsHonoured()
    {
        // A secondary monitor left of the primary: its work area starts at -1920.
        var workLeft = -1920;
        var workRight = 0;

        var restored = Compute(workLeft, 0, workRight, 1080, 200, 80, 1.0, 40, 60);
        Assert.Equal(-1880, restored.X);
        Assert.Equal(60, restored.Y);
        Assert.Equal(40, restored.RelativeLeftDip);

        var defaultCorner = Compute(workLeft, 0, workRight, 1080, 200, 80, 1.0);
        Assert.Equal(0 - 200 - 16, defaultCorner.X);
        Assert.Equal(1080 - 80 - 16, defaultCorner.Y);

        var overflow = Compute(workLeft, 0, workRight, 1080, 200, 80, 1.0, 5000, 5000);
        Assert.Equal(0 - 200, overflow.X);
        Assert.Equal(1080 - 80, overflow.Y);
    }

    [Fact]
    public void WorkAreaSmallerThanTheWindowPinsTheOriginInsteadOfCentringOffScreen()
    {
        var placement = Compute(0, 0, 50, 20, 200, 80, 1.0);
        Assert.Equal(0, placement.X);
        Assert.Equal(0, placement.Y);
        Assert.Equal(200, placement.Width);
        Assert.Equal(80, placement.Height);

        var stored = Compute(0, 0, 50, 20, 200, 80, 1.0, 10, 5);
        Assert.Equal(0, stored.X);
        Assert.Equal(0, stored.Y);
    }

    [Fact]
    public void EmptyOrInvertedWorkAreaIsRejected()
    {
        Assert.False(FloatingStatusLayout.TryComputePlacement(0, 0, 0, 1040, 200, 80, 1.0, null, null, out _));
        Assert.False(FloatingStatusLayout.TryComputePlacement(0, 0, 1920, 0, 200, 80, 1.0, null, null, out _));
        Assert.False(FloatingStatusLayout.TryComputePlacement(500, 500, 400, 400, 200, 80, 1.0, 10, 10, out _));
    }

    [Fact]
    public void DpiScaleIsAppliedOnceToSizePositionAndMargin()
    {
        // 150% monitor: the 200x80 DIP window is 300x120 px and the default margin is 24 px.
        var placement = Compute(0, 0, 2880, 1560, 200, 80, 1.5);

        Assert.Equal(300, placement.Width);
        Assert.Equal(120, placement.Height);
        Assert.Equal(2880 - 300 - 24, placement.X);
        Assert.Equal(1560 - 120 - 24, placement.Y);

        // A stored DIP offset is converted with the same scale; the relative value round-trips.
        var stored = Compute(0, 0, 2880, 1560, 200, 80, 1.5, 100.5, 10);
        Assert.Equal(151, stored.X);
        Assert.Equal(15, stored.Y);
        // 151 px at 150% is 100.7 DIP, i.e. the persisted value of the pixel the window really got.
        Assert.Equal(100.7, stored.RelativeLeftDip);
        Assert.Equal(10, stored.RelativeTopDip);
    }

    [Fact]
    public void ClampedStoredPositionReportsTheClampedRelativeOffsetForSaving()
    {
        var placement = Compute(WorkLeft, WorkTop, WorkRight, WorkBottom, 200, 80, 1.0, 9999, 9999);

        Assert.Equal(1720, placement.X);
        Assert.Equal(960, placement.Y);
        Assert.Equal(1720, placement.RelativeLeftDip);
        Assert.Equal(960, placement.RelativeTopDip);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-2)]
    public void UnusableScaleFallsBackToOne(double scale)
    {
        var placement = Compute(WorkLeft, WorkTop, WorkRight, WorkBottom, 200, 80, scale);
        Assert.Equal(1920 - 200 - 16, placement.X);
        Assert.Equal(200, placement.Width);
        Assert.Equal(1.0, FloatingStatusLayout.SanitizeScale(scale));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void UnmeasuredWindowSizeUsesThePlaceholderMinimum(double sizeDip)
    {
        var placement = Compute(WorkLeft, WorkTop, WorkRight, WorkBottom, sizeDip, sizeDip, 1.0);

        Assert.Equal((int)Math.Ceiling(FloatingStatusLayout.MinimumWidthDip), placement.Width);
        Assert.Equal((int)Math.Ceiling(FloatingStatusLayout.MinimumHeightDip), placement.Height);
    }

    [Fact]
    public void RelativeDipConversionIsPerMonitorScaleAware()
    {
        Assert.Equal(100, FloatingStatusLayout.ToRelativeDip(250, 50, 2.0));
        Assert.Equal(-100, FloatingStatusLayout.ToRelativeDip(50, 250, 2.0));
        // 0.1 DIP rounding keeps the stored JSON readable without losing a pixel at 100%.
        Assert.Equal(33.3, FloatingStatusLayout.ToRelativeDip(100, 0, 3.0));
        Assert.Equal(0, FloatingStatusLayout.ToRelativeDip(100, 100, 1.0));

        Assert.Equal(300, FloatingStatusLayout.ToPixels(200, 1.5));
        Assert.Equal(1, FloatingStatusLayout.ToPixels(0.5, 1.0));
        Assert.Equal(-1, FloatingStatusLayout.ToPixels(-0.5, 1.0));
        // Hand-edited extreme values are bounded instead of overflowing the conversion.
        Assert.Equal((int)FloatingStatusLayout.MaximumPositionDip,
            FloatingStatusLayout.ToPixels(double.MaxValue, 1.0));
        Assert.Equal(0, FloatingStatusLayout.ToPixels(double.NaN, 1.0));
    }

    [Fact]
    public void PhysicalWindowRectangleIsClampedWithoutASecondDpiConversion()
    {
        // The real window is 300x120 physical pixels on a 150% monitor. A stored offset that would
        // park it past the right edge clamps to (2880-300) and the reported relative DIP is exactly
        // the value that restores the very same physical rectangle.
        Assert.True(FloatingStatusLayout.TryComputePlacementPixels(
            0, 0, 2880, 1560, 300, 120, 1.5, 5000, 0, out var placement));

        Assert.Equal(2880 - 300, placement.X);
        Assert.Equal(0, placement.Y);
        Assert.Equal(300, placement.Width);
        Assert.Equal(120, placement.Height);
        Assert.Equal(1720, placement.RelativeLeftDip);
    }

    [Fact]
    public void CrossMonitorDragEndRoundTripsThroughRelativeDipAndBack()
    {
        // Second monitor to the right of the primary, 150%: work area 1920..3840 x 0..1040 in physical
        // pixels. The drag ended with the window at physical (2200, 300), size 360x150 px.
        const int workLeft = 1920;
        const int workTop = 0;
        const int workRight = 3840;
        const int workBottom = 1040;
        const double scale = 1.5;

        var leftDip = FloatingStatusLayout.ToRelativeDip(2200, workLeft, scale);
        var topDip = FloatingStatusLayout.ToRelativeDip(300, workTop, scale);
        Assert.Equal(186.7, leftDip);
        Assert.Equal(200, topDip);

        Assert.True(FloatingStatusLayout.TryComputePlacementPixels(
            workLeft, workTop, workRight, workBottom, 360, 150, scale, leftDip, topDip, out var restored));

        // Physical in, physical out - no WPF Left/Top round trip, so nothing drifts across DPI.
        Assert.Equal(2200, restored.X);
        Assert.Equal(300, restored.Y);
        Assert.Equal(186.7, restored.RelativeLeftDip);
        Assert.Equal(200, restored.RelativeTopDip);
    }

    [Fact]
    public void DragEndLeftOutsideTheTargetWorkAreaIsClampedAndStoresTheClampedOffset()
    {
        // The drag released the window half off the top-left corner of a 1920x1040 monitor at 100%.
        var leftDip = FloatingStatusLayout.ToRelativeDip(-50, 0, 1.0);
        var topDip = FloatingStatusLayout.ToRelativeDip(1000, 0, 1.0);

        Assert.True(FloatingStatusLayout.TryComputePlacementPixels(
            0, 0, 1920, 1040, 300, 120, 1.0, leftDip, topDip, out var placement));

        Assert.Equal(0, placement.X);
        Assert.Equal(1040 - 120, placement.Y);
        Assert.Equal(0, placement.RelativeLeftDip);
        Assert.Equal(920, placement.RelativeTopDip);
        // Restoring the stored value reproduces the clamped rectangle instead of the off-screen one.
        Assert.True(FloatingStatusLayout.TryComputePlacementPixels(
            0, 0, 1920, 1040, 300, 120, 1.0,
            placement.RelativeLeftDip, placement.RelativeTopDip, out var again));
        Assert.Equal(placement.X, again.X);
        Assert.Equal(placement.Y, again.Y);
    }

    [Fact]
    public void PhysicalSizeLargerThanTheWorkAreaPinsTheOriginAndKeepsTheSize()
    {
        Assert.True(FloatingStatusLayout.TryComputePlacementPixels(
            0, 0, 50, 20, 200, 80, 1.0, null, null, out var placement));

        // Documented limit: the origin is pinned; the oversized window itself cannot fit.
        Assert.Equal(0, placement.X);
        Assert.Equal(0, placement.Y);
        Assert.Equal(200, placement.Width);
        Assert.Equal(80, placement.Height);
    }

    [Fact]
    public void MaximumWindowSizeCapsTheCardAtTheWorkArea()
    {
        var (width, height) = FloatingStatusLayout.MaximumWindowSizeDip(2880, 1560, 1.5);
        Assert.Equal(1920, width);
        Assert.Equal(1040, height);

        // A work area smaller than the card's minimum keeps the minimum (the documented limit).
        var tiny = FloatingStatusLayout.MaximumWindowSizeDip(40, 20, 1.0);
        Assert.Equal(FloatingStatusLayout.MinimumWidthDip, tiny.WidthDip);
        Assert.Equal(FloatingStatusLayout.MinimumHeightDip, tiny.HeightDip);

        // A hand-edited/absent scale is sanitised instead of producing an unusable limit.
        var unusable = FloatingStatusLayout.MaximumWindowSizeDip(1920, 1040, double.NaN);
        Assert.Equal(1920, unusable.WidthDip);
        Assert.Equal(1040, unusable.HeightDip);
    }

    [Fact]
    public void SnapGluesEachOfTheFourEdgesWithZeroGap()
    {
        // Released 5 px from each edge on a 100% monitor: the threshold is 12 px.
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 1920, 1040, 5, 400, 300, 120, 1.0, true, out var left, out var leftEdges));
        Assert.Equal(0, left.X);
        Assert.Equal(FloatingStatusSnappedEdges.Left, leftEdges);
        Assert.Equal(0, left.RelativeLeftDip);

        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 1920, 1040, 1615, 400, 300, 120, 1.0, true, out var right, out var rightEdges));
        Assert.Equal(1920 - 300, right.X);
        Assert.Equal(FloatingStatusSnappedEdges.Right, rightEdges);

        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 1920, 1040, 700, 915, 300, 120, 1.0, true, out var bottom, out var bottomEdges));
        Assert.Equal(1040 - 120, bottom.Y);
        Assert.Equal(FloatingStatusSnappedEdges.Bottom, bottomEdges);

        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 1920, 1040, 700, 7, 300, 120, 1.0, true, out var top, out var topEdges));
        Assert.Equal(0, top.Y);
        Assert.Equal(FloatingStatusSnappedEdges.Top, topEdges);
    }

    [Fact]
    public void SnapGluesBothEdgesOfACorner()
    {
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 1920, 1040, 1610, 930, 300, 120, 1.0, true, out var placement, out var edges));

        Assert.Equal(1920 - 300, placement.X);
        Assert.Equal(1040 - 120, placement.Y);
        Assert.Equal(FloatingStatusSnappedEdges.Right | FloatingStatusSnappedEdges.Bottom, edges);
        // The stored DIP offset is the snapped position converted once, so a reload lands on the same
        // physical rectangle instead of drifting half a DIP per save.
        Assert.Equal(1620, placement.RelativeLeftDip);
        Assert.Equal(920, placement.RelativeTopDip);
    }

    [Theory]
    [InlineData(1.0, 12)]
    [InlineData(1.25, 15)]
    [InlineData(1.5, 18)]
    [InlineData(2.0, 24)]
    public void SnapThresholdIsTwelveDipConvertedWithTheMonitorScale(double scale, int thresholdPixels)
    {
        Assert.Equal(12, FloatingStatusLayout.SnapThresholdDip);
        Assert.Equal(thresholdPixels, FloatingStatusLayout.SnapThresholdPixels(scale));

        var workRight = (int)(1920 * scale);
        var workBottom = (int)(1040 * scale);

        // Exactly on the threshold snaps.
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, workRight, workBottom, thresholdPixels, 400, 300, 120, scale, true, out var exact, out var exactEdges));
        Assert.Equal(0, exact.X);
        Assert.Equal(FloatingStatusSnappedEdges.Left, exactEdges);

        // One pixel further away does not.
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, workRight, workBottom, thresholdPixels + 1, 400, 300, 120, scale, true, out var outside, out var outsideEdges));
        Assert.Equal(thresholdPixels + 1, outside.X);
        Assert.Equal(FloatingStatusSnappedEdges.None, outsideEdges);
    }

    [Fact]
    public void SnapWorksWithANegativeVirtualScreenOrigin()
    {
        // A monitor left of the primary: work area -1920..0.
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            -1920, 0, 0, 1080, -1915, 500, 300, 120, 1.0, true, out var left, out var edges));
        Assert.Equal(-1920, left.X);
        Assert.Equal(FloatingStatusSnappedEdges.Left, edges);

        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            -1920, 0, 0, 1080, -310, 1000, 300, 120, 1.0, true, out var corner, out var cornerEdges));
        Assert.Equal(0 - 300, corner.X);
        Assert.Equal(1080 - 120, corner.Y);
        Assert.Equal(FloatingStatusSnappedEdges.Right | FloatingStatusSnappedEdges.Bottom, cornerEdges);
    }

    [Fact]
    public void SnapIsNeverAppliedWhenDisabledAndOnlyTheClampRemains()
    {
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 1920, 1040, 3, 400, 300, 120, 1.0, false, out var placement, out var edges));

        Assert.Equal(3, placement.X);
        Assert.Equal(400, placement.Y);
        Assert.Equal(FloatingStatusSnappedEdges.None, edges);

        // The clamp itself still runs: a release half off screen is pulled back inside.
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 1920, 1040, -50, 1000, 300, 120, 1.0, false, out var clamped, out var clampedEdges));
        Assert.Equal(0, clamped.X);
        Assert.Equal(1040 - 120, clamped.Y);
        Assert.Equal(FloatingStatusSnappedEdges.None, clampedEdges);
    }

    [Fact]
    public void WorkAreaSmallerThanTheWindowPinsTheOriginWithoutASnap()
    {
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            10, 20, 60, 50, 40, 30, 300, 120, 1.0, true, out var placement, out var edges));

        Assert.Equal(10, placement.X);
        Assert.Equal(20, placement.Y);
        Assert.Equal(300, placement.Width);
        Assert.Equal(120, placement.Height);
        Assert.Equal(FloatingStatusSnappedEdges.None, edges);

        Assert.False(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 0, 1040, 0, 0, 300, 120, 1.0, true, out _, out _));
    }

    [Fact]
    public void NearlyFullWidthWindowSnapsToTheNearerEdgeWithoutASecondScaleFactor()
    {
        // 500 px window on a 512 px wide work area: both edges are inside the 12 DIP threshold.
        // Released nearer the left edge, so the left edge wins (a tie resolves to the leading edge).
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 512, 400, 3, 100, 500, 100, 1.0, true, out var nearLeft, out var leftEdges));
        Assert.Equal(0, nearLeft.X);
        Assert.Equal(FloatingStatusSnappedEdges.Left, leftEdges);

        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 512, 400, 9, 100, 500, 100, 1.0, true, out var nearRight, out var rightEdges));
        Assert.Equal(12, nearRight.X);
        Assert.Equal(FloatingStatusSnappedEdges.Right, rightEdges);

        // 150% monitor: the threshold is 18 px and the DIP conversion uses that scale exactly once.
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 2880, 1560, 2870 - 300, 200, 450, 180, 1.5, true, out var scaled, out var scaledEdges));
        Assert.Equal(2880 - 450, scaled.X);
        Assert.Equal(FloatingStatusSnappedEdges.Right, scaledEdges);
        Assert.Equal(1620, scaled.RelativeLeftDip);
        Assert.Equal(133.3, scaled.RelativeTopDip);
    }

    [Fact]
    public void SnapResultRoundTripsThroughTheStoredRelativeDip()
    {
        // The end-of-drag rectangle that is stored is exactly the rectangle a reload restores.
        Assert.True(FloatingStatusLayout.TryComputeSnappedPlacement(
            0, 0, 2880, 1560, 2860, 1400, 300, 150, 1.5, true, out var snapped, out var edges));
        Assert.Equal(FloatingStatusSnappedEdges.Right | FloatingStatusSnappedEdges.Bottom, edges);

        Assert.True(FloatingStatusLayout.TryComputePlacementPixels(
            0, 0, 2880, 1560, snapped.Width, snapped.Height, 1.5,
            snapped.RelativeLeftDip, snapped.RelativeTopDip, out var restored));
        Assert.Equal(snapped.X, restored.X);
        Assert.Equal(snapped.Y, restored.Y);
    }

    [Fact]
    public void StoredPositionRoundTripsThroughPixelsAndBack()
    {
        var scale = 1.25;
        var placement = Compute(0, 0, 2560, 1400, 220, 90, scale, 320, 88);

        Assert.Equal(400, placement.X);
        Assert.Equal(110, placement.Y);
        var scaleBack = FloatingStatusLayout.SanitizeScale(scale);
        Assert.Equal(320, FloatingStatusLayout.ToRelativeDip(placement.X, 0, scaleBack));
        Assert.Equal(88, FloatingStatusLayout.ToRelativeDip(placement.Y, 0, scaleBack));
        // A pixel that is not exactly representable still comes back within 0.1 DIP.
        Assert.Equal(321.6, FloatingStatusLayout.ToRelativeDip(402, 0, scaleBack));
    }
}
