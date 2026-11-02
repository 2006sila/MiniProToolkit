namespace MiniProToolkit.Core;

/// <summary>
/// A resolved rectangle of the desktop floating window in physical pixels, plus the same origin
/// expressed as an offset from the work-area origin in DIPs (the form that is persisted).
/// </summary>
public readonly record struct FloatingStatusPlacement(
    int X,
    int Y,
    int Width,
    int Height,
    double RelativeLeftDip,
    double RelativeTopDip);

/// <summary>
/// Which work-area edges a drag-end snap pulled the floating window onto. Reported separately so the
/// caller (and the tests) can tell "the clamp kept it inside" apart from "the user's release was
/// close enough to an edge and the window was intentionally glued to it".
/// </summary>
[Flags]
public enum FloatingStatusSnappedEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8
}

/// <summary>
/// Pure placement math for the desktop floating window. No Win32, WPF or screen enumeration here:
/// the caller passes the monitor work area it resolved (physical pixels), the measured window size
/// in DIPs and the monitor scale, and gets back an exact physical rectangle. This keeps the rules
/// that matter - "the whole window stays inside the work area", the default bottom-right corner, a
/// negative virtual-screen origin, a work area smaller than the window and per-monitor DPI -
/// unit-testable without a desktop session.
/// <para>
/// The stored position is deliberately relative: monitor device name + origin offset from that
/// monitor's work-area origin in DIPs. An absolute virtual-screen pixel coordinate would drift when
/// a display is unplugged, rearranged or switches DPI; recomputing from the work area and clamping
/// means a window whose monitor disappeared reappears clamped on the primary work area instead of
/// off screen.
/// </para>
/// </summary>
public static class FloatingStatusLayout
{
    /// <summary>Gap between the default position and the bottom-right corner, in DIPs.</summary>
    public const double DefaultMarginDip = 16;

    /// <summary>
    /// Distance, in DIPs, under which the end of a drag glues the window to a work-area edge (0 gap).
    /// Expressed in DIPs and converted with the target monitor scale, so the gesture feels the same
    /// on a 100% and a 200% monitor; the comparison itself is always done in physical pixels.
    /// </summary>
    public const double SnapThresholdDip = 12;

    /// <summary>Placeholder size used until WPF has measured the content, in DIPs.</summary>
    public const double MinimumWidthDip = 120;

    /// <summary>Placeholder height used until WPF has measured the content, in DIPs.</summary>
    public const double MinimumHeightDip = 32;

    /// <summary>Largest accepted stored offset on either axis, in DIPs (shared with the settings).</summary>
    public const double MaximumPositionDip = FloatingStatusSettings.MaximumPositionDip;

    /// <summary>A non-finite or non-positive scale means "not measured yet" and becomes 1.0.</summary>
    public static double SanitizeScale(double scale) =>
        !double.IsFinite(scale) || scale <= 0 ? 1.0 : scale;

    /// <summary>
    /// Converts one DIP length to physical pixels, rounding away from zero so a positive offset
    /// never rounds towards the work-area edge. The input is bounded so a hand-edited settings value
    /// cannot overflow the conversion.
    /// </summary>
    public static int ToPixels(double dip, double scale)
    {
        scale = SanitizeScale(scale);
        if (!double.IsFinite(dip)) return 0;
        var bounded = Math.Clamp(dip, -MaximumPositionDip, MaximumPositionDip);
        return (int)Math.Round(bounded * scale, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Converts one physical coordinate into the persisted work-area-relative DIP offset, rounded to
    /// 0.1 DIP so the JSON stays readable while a drag still restores to within a pixel.
    /// </summary>
    public static double ToRelativeDip(int pixel, int originPixels, double scale)
    {
        scale = SanitizeScale(scale);
        var dip = (pixel - (double)originPixels) / scale;
        return Math.Round(Math.Clamp(dip, -MaximumPositionDip, MaximumPositionDip), 1,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Computes the physical rectangle for one monitor work area from a window size expressed in
    /// DIPs.
    /// <para>
    /// A stored <paramref name="leftDip"/>/<paramref name="topDip"/> pair is resolved from the work
    /// area origin; when the pair is absent (the window was never dragged) the default is the
    /// bottom-right corner with <see cref="DefaultMarginDip"/>. The result is then clamped so the
    /// whole window stays inside the work area: exact edges are kept as-is, a negative virtual-screen
    /// origin (a monitor left of / above the primary) works because the origin is added instead of
    /// assumed zero, and a window larger than the work area is pinned to the work-area origin rather
    /// than centred off screen.
    /// </para>
    /// <para>
    /// Prefer <see cref="TryComputePlacementPixels"/> from the window itself: once a real window
    /// exists its authoritative size is the physical <c>GetWindowRect</c> rectangle, not a DIP value
    /// that has to be converted (and rounded) a second time. This overload stays for callers that
    /// only know the measured DIP size.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> when the work area is empty or inverted; the caller must keep the last
    /// known position instead of parking the window at a stale coordinate.</returns>
    public static bool TryComputePlacement(
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        double windowWidthDip,
        double windowHeightDip,
        double scale,
        double? leftDip,
        double? topDip,
        out FloatingStatusPlacement placement)
    {
        scale = SanitizeScale(scale);
        var widthDip = double.IsFinite(windowWidthDip) && windowWidthDip > 0 ? windowWidthDip : MinimumWidthDip;
        var heightDip = double.IsFinite(windowHeightDip) && windowHeightDip > 0 ? windowHeightDip : MinimumHeightDip;
        return TryComputePlacementPixels(
            workLeft, workTop, workRight, workBottom,
            Math.Max(1, (int)Math.Ceiling(widthDip * scale)),
            Math.Max(1, (int)Math.Ceiling(heightDip * scale)),
            scale, leftDip, topDip, out placement);
    }

    /// <summary>
    /// Computes the physical rectangle for one monitor work area from the window's real physical
    /// size (normally <c>GetWindowRect</c>). Cross-DPI positioning must use physical pixels on both
    /// sides: dividing a global pixel coordinate by the target monitor scale and letting WPF convert
    /// it back is wrong as soon as two monitors have different scales, so the caller passes the exact
    /// Win32 rectangle and receives the exact Win32 rectangle to apply with one <c>SetWindowPos</c>.
    /// </summary>
    /// <returns><c>false</c> when the work area is empty or inverted.</returns>
    public static bool TryComputePlacementPixels(
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int windowWidthPixels,
        int windowHeightPixels,
        double scale,
        double? leftDip,
        double? topDip,
        out FloatingStatusPlacement placement)
    {
        placement = default;
        if (workRight <= workLeft || workBottom <= workTop) return false;
        scale = SanitizeScale(scale);

        var width = Math.Max(1, windowWidthPixels);
        var height = Math.Max(1, windowHeightPixels);

        int x, y;
        if (leftDip is { } storedLeft && topDip is { } storedTop)
        {
            x = workLeft + ToPixels(storedLeft, scale);
            y = workTop + ToPixels(storedTop, scale);
        }
        else
        {
            var margin = Math.Max(0, (int)Math.Round(DefaultMarginDip * scale, MidpointRounding.AwayFromZero));
            x = workRight - width - margin;
            y = workBottom - height - margin;
        }

        // Math.Max keeps the range valid when the window is larger than the work area: the clamp
        // then pins the origin to the work-area start instead of producing min > max.
        var maxX = Math.Max(workLeft, workRight - width);
        var maxY = Math.Max(workTop, workBottom - height);
        x = Math.Clamp(x, workLeft, maxX);
        y = Math.Clamp(y, workTop, maxY);

        placement = new FloatingStatusPlacement(
            x, y, width, height,
            ToRelativeDip(x, workLeft, scale),
            ToRelativeDip(y, workTop, scale));
        return true;
    }

    /// <summary>
    /// Threshold of one edge snap in physical pixels for <paramref name="scale"/>. Rounded away from
    /// zero so the usable band never shrinks below <see cref="SnapThresholdDip"/> on a fractional DPI.
    /// </summary>
    public static int SnapThresholdPixels(double scale) =>
        Math.Max(0, (int)Math.Round(SnapThresholdDip * SanitizeScale(scale), MidpointRounding.AwayFromZero));

    /// <summary>
    /// End-of-drag placement: clamps the released rectangle into the work area and - only when
    /// <paramref name="snapEnabled"/> is set - glues it to every edge it was released within
    /// <see cref="SnapThresholdDip"/> of. Called exactly once per finished drag with the real physical
    /// <c>GetWindowRect</c> rectangle, never from a refresh/resize/DPI pass: snapping on the latter
    /// would fight a live size change and drag a card the user never moved.
    /// <para>
    /// Order matters: clamp first, then measure the distance of the *clamped* window edges to the
    /// work-area edges. A released window that is already half off screen therefore snaps from a
    /// legal position instead of keeping the off-screen coordinate. Both axes are evaluated
    /// independently, so a release near a corner snaps both edges (the common "corner" case). When a
    /// window is so wide/narrow that both of its opposite edges are within the threshold, the nearer
    /// one wins and an exact tie resolves to the leading edge (left/top) deterministically. A work
    /// area smaller than the window pins the origin, as before, and no snap is reported for that
    /// axis because there is no other edge to choose.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> when the work area is empty or inverted.</returns>
    public static bool TryComputeSnappedPlacement(
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int windowLeft,
        int windowTop,
        int windowWidthPixels,
        int windowHeightPixels,
        double scale,
        bool snapEnabled,
        out FloatingStatusPlacement placement,
        out FloatingStatusSnappedEdges snappedEdges)
    {
        placement = default;
        snappedEdges = FloatingStatusSnappedEdges.None;
        if (workRight <= workLeft || workBottom <= workTop) return false;
        scale = SanitizeScale(scale);
        var width = Math.Max(1, windowWidthPixels);
        var height = Math.Max(1, windowHeightPixels);
        var maxX = Math.Max(workLeft, workRight - width);
        var maxY = Math.Max(workTop, workBottom - height);
        var x = Math.Clamp(windowLeft, workLeft, maxX);
        var y = Math.Clamp(windowTop, workTop, maxY);

        if (snapEnabled)
        {
            var threshold = SnapThresholdPixels(scale);
            SnapAxis(workLeft, workRight, width, ref x,
                FloatingStatusSnappedEdges.Left, FloatingStatusSnappedEdges.Right, threshold, ref snappedEdges);
            SnapAxis(workTop, workBottom, height, ref y,
                FloatingStatusSnappedEdges.Top, FloatingStatusSnappedEdges.Bottom, threshold, ref snappedEdges);
        }

        placement = new FloatingStatusPlacement(
            x, y, width, height,
            ToRelativeDip(x, workLeft, scale),
            ToRelativeDip(y, workTop, scale));
        return true;
    }

    /// <summary>Snaps one axis onto the nearer edge it is within <paramref name="threshold"/> px of.</summary>
    private static void SnapAxis(
        int workStart,
        int workEnd,
        int windowLength,
        ref int origin,
        FloatingStatusSnappedEdges startEdge,
        FloatingStatusSnappedEdges endEdge,
        int threshold,
        ref FloatingStatusSnappedEdges snappedEdges)
    {
        // A window that does not fit has no second edge to choose from: keep the pinned origin.
        if (windowLength >= workEnd - workStart) return;
        var distanceToStart = origin - workStart;
        var distanceToEnd = workEnd - (origin + windowLength);
        var canStart = distanceToStart <= threshold;
        var canEnd = distanceToEnd <= threshold;
        if (canStart && (!canEnd || distanceToStart <= distanceToEnd))
        {
            origin = workStart;
            snappedEdges |= startEdge;
        }
        else if (canEnd)
        {
            origin = workEnd - windowLength;
            snappedEdges |= endEdge;
        }
    }

    /// <summary>
    /// Largest useful window size for one work area, in DIPs. The card applies this as
    /// <c>MaxWidth</c>/<c>MaxHeight</c> so a long item list or a long live value grows the card only
    /// up to the work area and scrolls its content instead of silently pushing the window off screen.
    /// A work area smaller than <see cref="MinimumWidthDip"/>/<see cref="MinimumHeightDip"/> keeps
    /// the minimum: the window is then pinned to the work-area origin and may extend past the work
    /// area. That limit is real and is documented; it is not a claim that the whole card always fits
    /// on a monitor too small to hold it.
    /// </summary>
    public static (double WidthDip, double HeightDip) MaximumWindowSizeDip(
        int workWidthPixels, int workHeightPixels, double scale)
    {
        scale = SanitizeScale(scale);
        var width = Math.Max(MinimumWidthDip, Math.Max(1, workWidthPixels) / scale);
        var height = Math.Max(MinimumHeightDip, Math.Max(1, workHeightPixels) / scale);
        return (width, height);
    }
}
