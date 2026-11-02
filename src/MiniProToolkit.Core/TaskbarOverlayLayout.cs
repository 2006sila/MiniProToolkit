namespace MiniProToolkit.Core;

/// <summary>
/// The edge of the screen an appbar (taskbar) sits on. The numeric values match the Win32
/// ABM_GETTASKBARPOS <c>uEdge</c> values so the App side can map its native enum with a cast,
/// but this type exists so the placement math stays pure and unit-testable.
/// </summary>
public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3
}

/// <summary>
/// A resolved overlay rectangle in physical pixels (the units <c>SetWindowPos</c> expects).
/// <paramref name="Vertical"/> is the layout direction that was actually used.
/// </summary>
public readonly record struct TaskbarOverlayPlacement(int X, int Y, int Width, int Height, bool Vertical);

/// <summary>
/// Pure placement math for the taskbar status overlay. Everything here is arithmetic on the
/// rectangle reported by <c>SHAppBarMessage(ABM_GETTASKBARPOS)</c>: no Win32 call, no WPF type,
/// no hardware or network access, so the rules can be tested without a desktop session.
///
/// The overlay is pinned to the taskbar's starting edge: the left end of a horizontal bar and the
/// top of a vertical bar, with a small fixed DIP margin. It is never anchored to the notification
/// area. The optional <c>maxWidthDip</c>/<c>maxHeightDip</c> ceilings let the App keep the rendered
/// strip to a conservative share of the bar; that is a safety cap, not a measurement of the
/// notification area or the centred icon cluster, so no exact tray avoidance is claimed here.
/// The optional bounded DIP nudge (<c>offsetXDip</c>/<c>offsetYDip</c>, two physical screen
/// directions) is applied on top of that fixed origin and then clamped inside the appbar, so it
/// moves the strip without turning into a free screen coordinate or an anchor.
///
/// Two robustness rules live here:
/// <list type="number">
/// <item>The real taskbar edge wins over the user's <c>Vertical</c> preference: a left/right
/// taskbar always lays the overlay out vertically, a top/bottom taskbar always horizontally.
/// The saved preference is only a fallback for when the edge cannot be determined.</item>
/// <item>An empty or degenerate taskbar rectangle (an auto-hidden taskbar reports one) yields
/// <c>false</c> from <see cref="TryComputePlacement"/>, so the caller hides the overlay instead
/// of parking it at a stale position.</item>
/// </list>
/// </summary>
public static class TaskbarOverlayLayout
{
    /// <summary>Minimum overlay width in DIPs; the vertical taskbar strip is narrow.</summary>
    public const int MinimumVerticalWidth = 42;

    /// <summary>Minimum overlay height in DIPs; a vertical strip keeps enough room for text.</summary>
    public const int MinimumVerticalHeight = 120;

    /// <summary>Minimum overlay width in DIPs for a horizontal taskbar.</summary>
    public const int MinimumHorizontalWidth = 180;

    /// <summary>Minimum overlay height in DIPs for a horizontal taskbar.</summary>
    public const int MinimumHorizontalHeight = 24;

    /// <summary>
    /// Small gap between the overlay and the taskbar's starting edge (left end of a horizontal
    /// bar, top of a vertical bar), in DIPs. It is scaled with the monitor DPI like every other
    /// length here and kept inside the bar, so the text never touches the screen corner.
    /// </summary>
    public const int StartMargin = 4;

    /// <summary>
    /// Resolves the layout direction. A known edge always wins; <paramref name="userVertical"/>
    /// is only used when the edge is unknown, which keeps the saved setting meaningful while
    /// the real taskbar orientation stays authoritative.
    /// </summary>
    public static bool UseVerticalLayout(TaskbarEdge? edge, bool userVertical) => edge switch
    {
        TaskbarEdge.Left or TaskbarEdge.Right => true,
        TaskbarEdge.Top or TaskbarEdge.Bottom => false,
        _ => userVertical
    };

    /// <summary>
    /// Computes the overlay rectangle for one taskbar. Returns <c>false</c> when the taskbar
    /// rectangle is empty or inverted (auto-hidden taskbar, shell restart, bogus ABM reply);
    /// the caller must hide the window in that case rather than reuse the previous position.
    /// <para>
    /// The window is only as wide as the rendered text (never below the named minimum while the
    /// text is still unmeasured) and always starts at the taskbar's starting edge plus
    /// <see cref="StartMargin"/> DIPs: left on a horizontal bar, top on a vertical bar. The optional
    /// bounded nudge is added to that start coordinate and the window is still clamped inside the
    /// appbar rectangle.
    /// </para>
    /// </summary>
    /// <param name="edge">Real taskbar edge, or <c>null</c> when it could not be determined.</param>
    /// <param name="left">Taskbar rectangle (physical pixels, virtual-screen coordinates).</param>
    /// <param name="top">Taskbar rectangle top edge.</param>
    /// <param name="right">Taskbar rectangle right edge.</param>
    /// <param name="bottom">Taskbar rectangle bottom edge.</param>
    /// <param name="userVertical">Saved <c>Vertical</c> preference, used only as a fallback.</param>
    /// <param name="contentWidth">Current overlay width in DIPs (<c>ActualWidth</c>).</param>
    /// <param name="contentHeight">Current overlay height in DIPs (<c>ActualHeight</c>).</param>
    /// <param name="scale">Monitor scale factor (1.0 = 96 DPI); invalid values fall back to 1.0.</param>
    /// <param name="placement">Resolved physical-pixel rectangle, always inside the taskbar rect.</param>
    /// <param name="maxWidthDip">Optional width ceiling in DIPs (a conservative share of the bar).
    /// The App side derives it from the appbar rectangle only; no notification-area or icon
    /// geometry is measured yet, so this is a safety cap rather than verified tray avoidance.</param>
    /// <param name="maxHeightDip">Optional height ceiling in DIPs, same contract as <paramref name="maxWidthDip"/>.</param>
    /// <param name="offsetXDip">Bounded horizontal nudge in DIPs relative to the start origin
    /// (<see cref="TaskbarDisplaySettings.OffsetXDip"/>). It is converted to physical pixels once,
    /// with the same monitor scale as every other length, and the result is still clamped inside the
    /// appbar rectangle. Positive values move the window to the right on screen.</param>
    /// <param name="offsetYDip">Bounded vertical nudge in DIPs relative to the start origin
    /// (<see cref="TaskbarDisplaySettings.OffsetYDip"/>). Positive values move the window down on
    /// screen, so the four dialog directions stay physical regardless of the taskbar edge.</param>
    public static bool TryComputePlacement(
        TaskbarEdge? edge,
        int left,
        int top,
        int right,
        int bottom,
        bool userVertical,
        double contentWidth,
        double contentHeight,
        double scale,
        out TaskbarOverlayPlacement placement,
        double maxWidthDip = double.PositiveInfinity,
        double maxHeightDip = double.PositiveInfinity,
        int offsetXDip = 0,
        int offsetYDip = 0)
    {
        placement = default;
        // An auto-hidden taskbar reports an empty rectangle; a shell restart can report an
        // inverted one. Both mean "there is no slot to draw in".
        if (right <= left || bottom <= top) return false;

        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;
        if (!double.IsFinite(contentWidth) || contentWidth < 0) contentWidth = 0;
        if (!double.IsFinite(contentHeight) || contentHeight < 0) contentHeight = 0;

        var vertical = UseVerticalLayout(edge, userVertical);
        var minimumWidth = vertical ? MinimumVerticalWidth : MinimumHorizontalWidth;
        var minimumHeight = vertical ? MinimumVerticalHeight : MinimumHorizontalHeight;

        // The measured text is the width; the minimum only covers the first layout pass, where
        // the content has not been measured yet, and a degenerate hand-made value.
        var width = ScaledSize(minimumWidth, contentWidth, scale);
        var height = ScaledSize(minimumHeight, contentHeight, scale);

        // A caller-provided ceiling wins even over the named minimum: staying inside the safe
        // part of the bar matters more than a minimum width on a very short/secondary bar.
        width = CapSize(width, maxWidthDip, scale);
        height = CapSize(height, maxHeightDip, scale);

        // A minimum size must never be wider than the bar it is drawn on.
        var barLength = right - left;
        if (width > barLength) width = barLength;

        // One single DIP -> physical-pixel conversion of the start margin; never scaled twice.
        var startMargin = Math.Max(0,
            (int)Math.Round(StartMargin * scale, MidpointRounding.AwayFromZero));

        int x, y;
        if (vertical)
        {
            // A vertical strip hugs the taskbar's inner edge horizontally and starts at its top.
            x = right - width;
            y = top + startMargin;
        }
        else
        {
            // A horizontal bar starts at its left end and stays vertically centred in the bar.
            x = left + startMargin;
            y = top + ((bottom - top - height) / 2);
        }

        // The optional user nudge is converted the same way (once, from DIPs) and applied to the
        // fixed start coordinate. It is not an anchor: the clamp below still keeps the whole window
        // inside the appbar rectangle, so a nudge can move the strip but never off the bar.
        x += ToPhysicalOffset(offsetXDip, scale);
        y += ToPhysicalOffset(offsetYDip, scale);

        // Keep the overlay inside the appbar even for tiny taskbars (a short secondary bar, an
        // uncommon DPI) instead of letting a minimum size push it off the strip.
        var maxX = Math.Max(left, right - width);
        var maxY = Math.Max(top, bottom - height);
        x = Math.Clamp(x, left, maxX);
        y = Math.Clamp(y, top, maxY);

        placement = new TaskbarOverlayPlacement(x, y, width, height, vertical);
        return true;
    }

    /// <summary>
    /// Scales one length from DIPs to physical pixels, keeping the named minimum as the floor.
    /// The content size was already sanitized, so the result is always finite and positive.
    /// </summary>
    private static int ScaledSize(int minimumDip, double contentDip, double scale) =>
        Math.Max(minimumDip, (int)Math.Ceiling(Math.Max(contentDip, minimumDip) * scale));

    /// <summary>
    /// Converts one DIP nudge to physical pixels, once. The Core range is bounded
    /// (<see cref="TaskbarDisplaySettings.MinimumOffsetDip"/>..<see cref="TaskbarDisplaySettings.MaximumOffsetDip"/>);
    /// an out-of-range hand-made value is clamped instead of being allowed to move the window
    /// arbitrarily far before the appbar clamp takes over.
    /// </summary>
    private static int ToPhysicalOffset(int offsetDip, double scale)
    {
        var bounded = Math.Clamp(offsetDip, TaskbarDisplaySettings.MinimumOffsetDip, TaskbarDisplaySettings.MaximumOffsetDip);
        return (int)Math.Round(bounded * scale, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Applies an optional DIP ceiling in physical pixels. A non-finite/non-positive ceiling means
    /// "no cap"; the result never drops below one pixel.
    /// </summary>
    private static int CapSize(int size, double maxDip, double scale)
    {
        if (!double.IsFinite(maxDip) || maxDip <= 0)
            return size;
        var cap = (int)Math.Ceiling(maxDip * scale);
        return Math.Clamp(cap, 1, Math.Max(1, size));
    }
}
