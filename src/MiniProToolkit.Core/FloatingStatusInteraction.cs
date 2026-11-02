namespace MiniProToolkit.Core;

/// <summary>
/// Pure, screen-free policy of the desktop floating window's second-phase interaction features:
/// mouse click-through (<c>WS_EX_TRANSPARENT</c>), the runtime safety override that always wins, and
/// the two settings transforms used by the external escape hatches.
/// <para>
/// Everything here is deliberately a pure function over plain values so the rules that matter can be
/// unit-tested without a desktop session, a window handle or a WPF message loop: which window style
/// bits this feature is allowed to own, that it never rebuilds the whole extended style from a cached
/// (stale) base value, that an open settings dialog suspends the actual click-through, and that the
/// external "disable click-through" / "restore default position" paths change exactly one thing.
/// </para>
/// </summary>
public static class FloatingStatusInteraction
{
    /// <summary><c>WS_EX_TRANSPARENT</c>: mouse input passes through the window.</summary>
    public const int WsExTransparent = 0x00000020;

    /// <summary><c>WS_EX_NOACTIVATE</c>: a click-through card must never take the foreground.</summary>
    public const int WsExNoActivate = 0x08000000;

    /// <summary>
    /// The only extended-style bits this feature may add or clear. Every other bit
    /// (<c>WS_EX_LAYERED</c> from WPF's transparent window, <c>WS_EX_TOOLWINDOW</c>, ...) is owned by
    /// somebody else and must survive untouched.
    /// </summary>
    public const long OwnedExStyleBits = WsExTransparent | WsExNoActivate;

    /// <summary>
    /// Whether the card is actually click-through right now.
    /// <para>
    /// Three independent conditions: the user asked for it, no settings dialog is open (the dialog is
    /// the single editor and it must stay operable - a previewed value is not a committed one, and a
    /// cancel must be able to restore the opened state through a still clickable card), and no
    /// runtime safety override is active (set when an external "disable click-through" could not be
    /// persisted: the session stays interactive until the user explicitly re-enables it).
    /// </para>
    /// </summary>
    public static bool IsClickThroughEffective(
        bool clickThrough, bool isSettingsEditing, bool safetyRecoveryOverride) =>
        clickThrough && !isSettingsEditing && !safetyRecoveryOverride;

    /// <summary>
    /// Computes the extended window style to write for <paramref name="effective"/>.
    /// <para>
    /// The caller passes the style it just read from the live window (<c>GetWindowLongPtr</c>), never
    /// a cached "base" style: rebuilding the style from a snapshot taken when the window was created
    /// would silently drop every bit another component added later (WPF's <c>WS_EX_LAYERED</c>, the
    /// tool-window bit, a future shell bit). When click-through is on, both owned bits are ORed in;
    /// when it is off, only the bit this feature added for transparency is cleared and
    /// <c>NOACTIVATE</c> is restored to whatever the window was born with
    /// (<paramref name="baseNoActivate"/>) instead of being assumed off.
    /// </para>
    /// </summary>
    public static long ComputeExStyle(long currentExStyle, bool effective, bool baseNoActivate)
    {
        if (effective)
            return currentExStyle | OwnedExStyleBits;
        var style = currentExStyle & ~(long)WsExTransparent;
        return baseNoActivate ? style | WsExNoActivate : style & ~(long)WsExNoActivate;
    }

    /// <summary>
    /// The external "disable click-through" transform. It changes exactly one flag of the instance it
    /// receives - callers must pass the *saved* settings (or the dialog's cancel baseline), never a
    /// dialog edit buffer, so an unrelated unsaved edit is not persisted behind the editor's back.
    /// </summary>
    public static FloatingStatusSettings WithoutClickThrough(FloatingStatusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { ClickThrough = false };
    }

    /// <summary>
    /// The "restore default position" transform: forgets the monitor and the stored work-area-relative
    /// offset so the next placement is the documented bottom-right corner with a 16 DIP margin.
    /// <see cref="FloatingStatusSettings.Enabled"/> is deliberately untouched - a reset never forces
    /// the card on, and a disabled card simply uses the new position the next time it is shown.
    /// </summary>
    public static FloatingStatusSettings WithoutPosition(FloatingStatusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { MonitorDeviceName = null, LeftDip = null, TopDip = null };
    }
}
