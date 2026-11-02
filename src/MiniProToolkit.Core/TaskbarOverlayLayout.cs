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

    /// <summary>Gap between the overlay and the top/bottom end of a vertical taskbar.</summary>
    public const int VerticalEdgeMargin = 8;

    /// <summary>
    /// Space kept free at the right end of a horizontal taskbar so the overlay does not cover
    /// the notification area / clock. This is a fixed reservation because the tray is reported
    /// by no appbar query; it is intentionally a named constant instead of an inline literal.
    /// </summary>
    public const int HorizontalTrayReserve = 160;

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
        out TaskbarOverlayPlacement placement)
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
        var width = Math.Max(minimumWidth,
            (int)Math.Ceiling(Math.Max(contentWidth, minimumWidth) * scale));
        var height = Math.Max(minimumHeight,
            (int)Math.Ceiling(Math.Max(contentHeight, minimumHeight) * scale));

        // An unknown edge keeps the user's direction and uses the horizontal (bottom-bar)
        // geometry, so the overlay still lands inside the reported rectangle.
        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Left:
                x = right - width;
                y = top + VerticalEdgeMargin;
                break;
            case TaskbarEdge.Right:
                x = right - width;
                y = bottom - height - VerticalEdgeMargin;
                break;
            default:
                x = right - width - HorizontalTrayReserve;
                y = top + ((bottom - top - height) / 2);
                break;
        }

        // Keep the overlay inside the appbar even for tiny taskbars (a short secondary bar, an
        // uncommon DPI) instead of letting a minimum size push it off the strip.
        var maxX = Math.Max(left, right - width);
        var maxY = Math.Max(top, bottom - height);
        x = Math.Clamp(x, left, maxX);
        y = Math.Clamp(y, top, maxY);

        placement = new TaskbarOverlayPlacement(x, y, width, height, vertical);
        return true;
    }
}
