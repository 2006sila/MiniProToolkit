namespace MiniProToolkit.Core;

/// <summary>
/// Pure click-count rule for the taskbar status overlay's left button. The overlay only has one
/// gesture: request that the main window be opened. A double click means the same thing as a
/// single click (TrafficMonitor-style habit), so a multi-click sequence must not raise the open
/// request twice - the caller would otherwise show/activate the main window once per click.
///
/// WPF (like Win32) reports <c>MouseButtonEventArgs.ClickCount</c> as 1 for the first
/// down/up pair and 2, 3, ... for the extra pairs of the same sequence, so the rule is simply
/// "only the first release of a sequence opens". No timer, throttle or delayed single click is
/// involved: the first click opens immediately and the follow-up pairs are dropped, which keeps
/// the existing single-click behaviour byte for byte.
///
/// No WPF type, no hardware, no network: the rule is a pure integer predicate so it can be
/// unit-tested without a desktop session.
/// </summary>
public static class TaskbarClickGesture
{
    /// <summary>
    /// True when a left-button release should raise the open request.
    /// <paramref name="clickCount"/> &lt;= 1 (including a synthetic 0) counts as a first click, so
    /// odd input can never swallow a plain single click; anything above 1 is a repeat of a click
    /// sequence that already opened the window.
    /// </summary>
    public static bool RequestsOpen(int clickCount) => clickCount <= 1;
}
