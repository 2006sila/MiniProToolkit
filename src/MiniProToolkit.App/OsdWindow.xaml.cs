using MiniProToolkit.Core;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
// WinForms is enabled in this project, so System.Drawing types would otherwise collide.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace MiniProToolkit.App;

/// <summary>
/// Non-activating OSD popup. Accepts both the legacy <see cref="ShowMessage(string)"/> text API and
/// structured <see cref="OsdMessage"/> values (title / multi-line body / severity / duration).
/// All entry points are non-throwing: a bad message or a display failure is logged and dropped so a
/// sampling or automation thread can never observe an exception from the OSD.
/// </summary>
public partial class OsdWindow : Window
{
    private readonly DispatcherTimer _hideTimer = new()
    {
        Interval = TimeSpan.FromSeconds(OsdMessageNormalizer.DefaultDurationSeconds)
    };
    private readonly OsdMessageQueue _pending = new();
    private OsdTheme _theme = OsdTheme.Default;
    private OsdPlacement _placement = OsdPlacementCatalog.DefaultPlacement;
    private bool _showing;
    private bool _enabled = true;
    private string? _currentKey;

    public OsdWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => ShowNextOrHide();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle,
                new IntPtr(NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow));
        };
    }

    /// <summary>
    /// Component-level on/off switch. Disabling drops queued messages and hides the window;
    /// persisting the user's choice is the caller's job (<see cref="MainWindow.IsOsdEnabled"/>
    /// writes it to <c>osd.json</c> next to the theme preset).
    /// </summary>
    public bool IsOsdEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (value)
                return;
            try
            {
                _pending.Clear();
                _hideTimer.Stop();
                _showing = false;
                _currentKey = null;
                Hide();
            }
            catch (Exception ex)
            {
                AppLog.Write("OSD could not be disabled cleanly", ex);
            }
        }
    }

    /// <summary>Current theme. Replaced through <see cref="ApplyTheme"/>; defaults to <see cref="OsdTheme.Default"/>.</summary>
    public OsdTheme Theme => _theme;

    /// <summary>
    /// Current placement slot inside the cursor monitor's work area. Defaults to
    /// <see cref="OsdPlacementCatalog.DefaultPlacement"/> (the first-version position).
    /// </summary>
    public OsdPlacement Placement => _placement;

    /// <summary>
    /// Replaces the placement slot. Unknown enum values are normalized to the default slot; a visible
    /// popup is repositioned immediately. Any failure is logged and swallowed, like the theme.
    /// </summary>
    public void ApplyPlacement(OsdPlacement placement)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyPlacement(placement)));
            return;
        }
        _placement = OsdPlacementCatalog.Normalize(placement);
        if (!IsVisible)
            return;
        try
        {
            UpdateLayout();
            PositionOnCursorScreen();
        }
        catch (Exception ex)
        {
            AppLog.Write("OSD placement could not be applied", ex);
        }
    }

    /// <summary>
    /// Replaces the theme. An invalid theme is rejected (logged) and the previous theme is kept;
    /// passing <c>null</c> restores <see cref="OsdTheme.Default"/>. Callers must stay on the UI
    /// thread (<see cref="MainWindow.ApplyOsdAppearance"/> marshals and swallows failures).
    /// </summary>
    public void ApplyTheme(OsdTheme? theme)
    {
        var candidate = theme ?? OsdTheme.Default;
        try
        {
            candidate.Validate();
            _theme = candidate;
        }
        catch (Exception ex)
        {
            AppLog.Write("OSD theme rejected; keeping the previous theme", ex);
        }
    }

    /// <summary>Legacy API: shows <paramref name="message"/> as a plain, 2 second, info-level OSD.</summary>
    public void ShowMessage(string message) => ShowMessage(OsdMessage.FromText(message));

    /// <summary>
    /// Shows a structured message. Normalized (lengths, line count, duration) before display;
    /// messages with no visible body are ignored. Safe to call from any thread.
    /// </summary>
    public void ShowMessage(OsdMessage? message)
    {
        try
        {
            if (!_enabled)
                return;
            if (!OsdMessageNormalizer.TryNormalize(message, out var normalized))
                return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowMessage(normalized)));
                return;
            }
            ShowNormalized(normalized);
        }
        catch (Exception ex)
        {
            // OSD must never fault the caller (sampling thread, automation, hotkey handler).
            AppLog.Write("OSD message was dropped", ex);
        }
    }

    private void ShowNormalized(OsdMessage message)
    {
        if (_showing && message.CoalesceKey == _currentKey)
        {
            // Same logical event: refresh in place instead of queueing a duplicate popup.
            Present(message);
            return;
        }

        _pending.Enqueue(message);
        if (!_showing)
            ShowNextOrHide();
    }

    private void ShowNextOrHide()
    {
        _hideTimer.Stop();
        if (!_pending.TryDequeue(out var message))
        {
            _showing = false;
            _currentKey = null;
            Hide();
            return;
        }

        try
        {
            Present(message);
        }
        catch (Exception ex)
        {
            // A display failure must not wedge the queue or the UI thread.
            AppLog.Write("OSD message could not be presented", ex);
            _showing = false;
            _currentKey = null;
            Hide();
        }
    }

    private void Present(OsdMessage message)
    {
        var hasTitle = !string.IsNullOrEmpty(message.Title);
        ApplyStyle(_theme.Resolve(message.Severity), hasTitle, message.Icon);

        TitleText.Text = message.Title ?? string.Empty;
        TitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = message.Body;

        _showing = true;
        _currentKey = message.CoalesceKey;

        // Showing an already visible window is a no-op in WPF, so repeated messages never hide/show
        // (that is what used to flicker) - only the content and size change.
        if (!IsVisible)
            Show();
        UpdateLayout();
        PositionOnCursorScreen();

        _hideTimer.Interval = message.Duration is { } duration && duration > TimeSpan.Zero
            ? duration
            : TimeSpan.FromSeconds(OsdMessageNormalizer.DefaultDurationSeconds);
        _hideTimer.Start();
    }

    private void ApplyStyle(OsdSeverityStyle style, bool hasTitle, OsdIcon icon)
    {
        if (BrushOf(style.BackgroundColor) is { } background)
            Container.Background = background;
        if (BrushOf(style.TitleColor) is { } titleColor)
            TitleText.Foreground = titleColor;
        if (BrushOf(style.BodyColor) is { } bodyColor)
            MessageText.Foreground = bodyColor;

        IconText.Text = IconGlyph(icon);
        IconText.Visibility = string.IsNullOrEmpty(IconText.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (BrushOf(style.HasAccent ? style.AccentColor : style.TitleColor) is { } iconColor)
            IconText.Foreground = iconColor;

        var accent = style.HasAccent ? BrushOf(style.AccentColor) : null;
        if (accent is not null)
        {
            AccentBar.Background = accent;
            AccentBar.Visibility = Visibility.Visible;
        }
        else
        {
            AccentBar.Visibility = Visibility.Collapsed;
        }

        var family = FontFamilyOf(style.FontFamily);
        TitleText.FontFamily = family;
        MessageText.FontFamily = family;

        TitleText.FontSize = style.TitleFontSize;
        // Text-only messages keep the first-version typography (22 / SemiBold).
        MessageText.FontSize = hasTitle ? style.BodyFontSize : style.TitleFontSize;
        MessageText.FontWeight = hasTitle ? FontWeights.Normal : FontWeights.SemiBold;
        IconText.FontFamily = new FontFamily("Segoe UI Symbol");
        IconText.FontSize = Math.Max(18, style.TitleFontSize + 2);
    }

    private static string IconGlyph(OsdIcon icon) => icon switch
    {
        OsdIcon.Info => "ⓘ",
        OsdIcon.Success => "✓",
        OsdIcon.Warning => "⚠",
        OsdIcon.Error => "✕",
        OsdIcon.Temperature => "♨",
        OsdIcon.Fan => "✣",
        OsdIcon.Shield => "⬟",
        _ => string.Empty
    };

    private void PositionOnCursorScreen()
    {
        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        var workArea = screen.WorkingArea;
        var nativePoint = new NativeMethods.Point { X = cursor.X, Y = cursor.Y };
        var monitor = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MonitorDefaultToNearest);
        var scale = NativeMethods.GetMonitorScale(monitor);
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Pure slot math (OsdPlacementLayout); TopCenter reproduces the first-version position.
        var (x, y) = OsdPlacementLayout.Compute(
            _placement, workArea.Left, workArea.Top, workArea.Width, workArea.Height,
            ActualWidth, ActualHeight, scale);
        Left = x / scale;
        Top = y / scale;
        NativeMethods.SetWindowPos(
            new WindowInteropHelper(this).Handle,
            NativeMethods.HwndTopmost,
            (int)Math.Round(x),
            (int)Math.Round(y),
            (int)Math.Ceiling(Math.Max(1, ActualWidth) * scale),
            (int)Math.Ceiling(Math.Max(1, ActualHeight) * scale),
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private static Brush? BrushOf(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        try
        {
            if (ColorConverter.ConvertFromString(color) is not Color parsed)
                return null;
            var brush = new SolidColorBrush(parsed);
            brush.Freeze();
            return brush;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a theme font family to a usable WPF family: an unsafe name (path/URI shaped), an empty name
    /// or a family that is not installed resolves to <see cref="OsdFontFamilyRules.DefaultFontFamily"/>,
    /// so a missing or hostile family can never throw or make the popup render with a broken fallback.
    /// </summary>
    private static FontFamily FontFamilyOf(string family) =>
        OsdFontResolver.Resolve(family);
}

/// <summary>
/// Resolves a user-typed font family against the fonts installed on this machine. The stored value is
/// a plain string (any family the user wants); this helper is the only place that turns it into a WPF
/// <see cref="FontFamily"/>. An unsafe name, an unknown family or a WPF failure resolves to
/// <see cref="OsdFontFamilyRules.DefaultFontFamily"/>, so the popup always renders and a bad name can
/// never throw. No font file, path or URI is ever loaded: only installed family names are matched.
/// </summary>
internal static class OsdFontResolver
{
    private static readonly Lazy<HashSet<string>> Installed = new(
        static () => new HashSet<string>(EnumerateInstalledNames(), StringComparer.OrdinalIgnoreCase),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when the family is installed; unknown names are <c>false</c>. Never throws: a font
    /// enumeration failure reports <c>true</c> so WPF applies its own fallback instead of us dropping a
    /// possibly valid choice.</summary>
    public static bool IsInstalled(string family)
    {
        try
        {
            return Installed.Value.Contains(family);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Never throws; falls back to the default family.</summary>
    public static FontFamily Resolve(string? family)
    {
        var name = OsdFontFamilyRules.Resolve(family, IsInstalled);
        try
        {
            return new FontFamily(name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            AppLog.Write($"OSD font family \"{name}\" could not be created; using the default", ex);
            return new FontFamily(OsdFontFamilyRules.DefaultFontFamily);
        }
    }

    private static IEnumerable<string> EnumerateInstalledNames()
    {
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            if (!string.IsNullOrWhiteSpace(family.Source))
                yield return family.Source;
            foreach (var name in family.FamilyNames.Values)
                if (!string.IsNullOrWhiteSpace(name))
                    yield return name;
        }
    }
}
