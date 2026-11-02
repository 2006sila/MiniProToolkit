using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MiniProToolkit.Core;
using Forms = System.Windows.Forms;
// WinForms is enabled in this project, so System.Drawing brings in its own Brush/Color/FontFamily/
// HorizontalAlignment types. Alias the WPF ones explicitly (same convention as TaskbarStatusWindow).
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace MiniProToolkit.App;

/// <summary>
/// A standalone, always available desktop status card. It is deliberately *not* a taskbar overlay:
/// it is never reparented into Explorer, never mounted against an appbar and never re-created when
/// the shell restarts. It has its own lifetime, its own settings file
/// (<see cref="FloatingStatusSettings"/> / <c>floating-status.json</c>) and its own item list, and it
/// renders the exact same live <see cref="HardwareSnapshot"/> the taskbar strip receives - no extra
/// hardware read, no extra sampling timer and no control action.
/// <para>
/// Presentation only: the card formats via the shared <see cref="TaskbarMetricFormatter"/> and lays
/// out via the shared <see cref="TaskbarItemLayout"/> plan (all four A/C/D/E modes). The right-click
/// menu raises events; <see cref="MainWindow"/> executes the existing command paths, so this window
/// can never bypass the Guardian/lease/emergency gates.
/// </para>
/// </summary>
public partial class FloatingStatusWindow : Window
{
    /// <summary>
    /// Hard upper bound of the card's content budget, in DIPs. A card is a small desktop object, so
    /// a very long item list falls back to the plan's explicit <c>+N</c> marker instead of growing
    /// across the screen; nothing is clipped silently. It is also the budget used until the current
    /// monitor's work-area cap is known (<see cref="Window.MaxWidth"/> is still Infinity then).
    /// </summary>
    private const double DefaultContentWidthDip = 560;

    /// <summary>
    /// Border padding of the two card looks, in DIPs (compact: 10 per side, regular: 14 per side).
    /// <see cref="ApplyAppearance"/> writes exactly these into <see cref="RootBorder"/>, and the
    /// layout budget below subtracts the same values, so the planned content width and the real
    /// padding can never drift apart.
    /// </summary>
    private static readonly Thickness CompactCardPadding = new(10, 4, 10, 8);
    private static readonly Thickness RegularCardPadding = new(14, 6, 14, 10);

    // SWP_NOSIZE is not declared by NativeMethods (that file is shared with the taskbar overlay);
    // the card only moves, so the size is left exactly as WPF arranged it.
    private const uint SwpNoSize = 0x0001;

    // Mutable so the settings dialog can live-preview colours/font/layout and the context menu can
    // toggle topmost/lock without a window recreate. Only the owner persists it.
    private FloatingStatusSettings _settings;
    private IntPtr _handle;
    private HardwareSnapshot? _snapshot;
    private bool _guardianRunning = true;
    // True only while DragMove owns the window. Every queued DPI/work-area clamp checks it: a clamp
    // resolved from the *old* settings would otherwise yank a window back to the monitor it was just
    // dragged off, which is exactly what happens on a mixed-DPI multi-monitor desktop.
    private bool _isDragging;
    // One coalesced clamp at a time, so a live value changing the window size cannot recurse.
    private bool _placementQueued;
    // Set by CloseForShutdown: the one close that must really close instead of turning into a hide.
    private bool _allowClose;
    // True while a canceled close has already queued its hide request, so a burst of Alt+F4 /
    // system-menu closes collapses into that single request instead of saving once per close.
    private bool _hideRequestQueued;
    // Set by OnClosed: a queued hide must never reference a window WPF already closed.
    private bool _closed;
    private WpfFontFamily _fontFamily = new(FloatingStatusSettings.DefaultFontFamily);
    private double _fontSize = FloatingStatusSettings.Default.FontSize;
    private WpfBrush _foreground = System.Windows.Media.Brushes.White;
    private WpfBrush _separator = System.Windows.Media.Brushes.Transparent;
    // Topology of the tree currently in StatusLayoutRoot plus the live text elements of that one
    // render pass, so a tick that only changes values rewrites text instead of recreating the
    // whole card (the hidden guard above already keeps off-screen ticks out of the renderer).
    private TaskbarRenderSignature? _renderedSignature;
    private readonly List<RenderedCell> _renderedCells = new(16);
    private MenuItem? _lockItem;
    private MenuItem? _topmostItem;
    private MenuItem? _hideItem;
    private MenuItem? _clickThroughItem;
    // Runtime safety override of the mouse click-through: set by the owner when an external
    // "disable click-through" could not be persisted, so this session stays clickable until the user
    // explicitly re-enables it. Never persisted; a successful save of ClickThrough=true clears it.
    private bool _clickThroughSafetyOverride;
    // The WS_EX_NOACTIVATE bit the card was *born* with, captured once in OnSourceInitialized before
    // this feature touches the extended style. ShowActivated=false (XAML and the constructor) can make
    // WPF/shell set that baseline bit; turning the click-through off must restore the card's real
    // initial value instead of clearing a bit this feature never added.
    private bool _baseNoActivate;

    /// <summary>
    /// True while the floating settings dialog is open. The dialog is the single editor then, so the
    /// card's lock/topmost/hide entries would write a value its cancel path could not restore; they
    /// are disabled with an explanatory tooltip instead of returning silently. Dragging stays allowed
    /// while the position is not locked (that is the dialog's only position editor).
    /// </summary>
    public bool IsSettingsEditing { get; private set; }

    /// <summary>Opens the main window (double click on the card or the menu entry).</summary>
    public event Action? OpenRequested;

    /// <summary>Opens the floating window settings dialog through the main window's single entry point.</summary>
    public event Action? SettingsRequested;

    /// <summary>Hides the card; the main window persists <c>Enabled = false</c>.</summary>
    public event Action? HideRequested;

    /// <summary>Runs the existing exit sequence in the main window.</summary>
    public event Action? ExitRequested;

    /// <summary>Requests a new lock state (context menu). The main window persists it.</summary>
    public event Action<bool>? LockRequested;

    /// <summary>Requests a new always-on-top state (context menu). The main window persists it.</summary>
    public event Action<bool>? TopmostRequested;

    /// <summary>
    /// External "disable mouse click-through" from the card's own menu. The owner stops the actual
    /// click-through first and only then persists the flag, so a failed write cannot leave a card the
    /// user can no longer click.
    /// </summary>
    public event Action? ClickThroughDisableRequested;

    /// <summary>
    /// "恢复默认位置" from the card's menu: forget the stored monitor/offset and fall back to the
    /// bottom-right corner. Routed to the owner because only it persists and owns the settings dialog.
    /// </summary>
    public event Action? ResetPositionRequested;

    /// <summary>
    /// Raised after a user drag with the new monitor device name and work-area-relative DIP position.
    /// It is a preview value until the main window persists it, so cancelling a settings dialog can
    /// restore the previous position.
    /// </summary>
    public event Action<FloatingStatusSettings>? PositionChanged;

    /// <summary>The settings this card is currently rendering (including the last dragged position).</summary>
    public FloatingStatusSettings Settings => _settings;

    public FloatingStatusWindow(FloatingStatusSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.Validate();
        InitializeComponent();
        // A status card must never steal focus when it appears.
        ShowActivated = false;
        Topmost = _settings.Topmost;
        BuildContextMenu();
        ApplyAppearance();
        // Deliberately no Render() here: the first render happens in UpdateDisplaySettings once the
        // card is actually shown, so the constructor never paints a discarded "等待采样…" tree.
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ApplyPlacement();
        // A live value can change the measured size (WMI string length, feedback text). Clamping on
        // SizeChanged keeps a grown card inside its work area without ever re-laying out in a loop.
        SizeChanged += (_, _) => QueuePlacementClamp();
    }

    /// <summary>
    /// The single real close. The owner calls this during teardown so WPF can close the window
    /// instead of the normal "hide" translation (see <see cref="OnClosing"/>).
    /// </summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// A plain close (Alt+F4, the system menu, a shell close) becomes "hide": the main window owns
    /// <c>Enabled</c>, so closing the window behind its back left <c>_floatingStatusWindow == null</c>
    /// with <c>Enabled = true</c> and no card on screen. The hide request runs the exact same save
    /// path as the context menu; if that save fails the window stays visible and <c>Enabled</c> stays
    /// true, which is the truthful state.
    /// <para>
    /// The hide is only <em>queued</em> here and never raised synchronously: the owner's hide path ends
    /// in <see cref="UpdateDisplaySettings"/> calling <c>Hide()</c>, and changing Visibility while WPF
    /// is still unwinding its own close stack can throw <see cref="InvalidOperationException"/>. The
    /// dispatcher runs the request after the canceled close returned (see <see cref="QueueHideRequest"/>).
    /// </para>
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || e.Cancel) return;
        e.Cancel = true;
        QueueHideRequest();
    }

    /// <summary>WPF really closed the window (shutdown close); a queued hide is dropped from now on.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        // Drop every cached control reference with the tree, so a closed card keeps nothing alive.
        _renderedCells.Clear();
        _renderedSignature = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// Queues the single deferred hide request of a canceled close. <see cref="DispatcherPriority.Background"/>
    /// runs it after the WPF close stack has been unwound, so the owner persists <c>Enabled = false</c>
    /// and hides the card from a normal dispatcher frame instead of from inside Window.Closing. The
    /// callback re-checks the real-close/closed/dispatcher-shutdown state before it references the
    /// window again, and the settings-editing semantics stay the owner's: an open dialog is only
    /// activated and never hidden by this path.
    /// </summary>
    private void QueueHideRequest()
    {
        if (_hideRequestQueued) return;
        var dispatcher = Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        _hideRequestQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _hideRequestQueued = false;
            // A real shutdown close supersedes the queued hide, and a closed window is never touched.
            if (_allowClose || _closed) return;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            HideRequested?.Invoke();
        }));
    }

    /// <summary>
    /// Called by the owner while its settings dialog is open/closed. Only the mutating menu entries
    /// are affected; the settings entry itself stays usable and brings the existing dialog forward.
    /// </summary>
    internal void SetSettingsEditing(bool editing)
    {
        IsSettingsEditing = editing;
        UpdateMenuState();
        // An open settings dialog suspends the actual click-through: the editor must stay operable
        // (and a cancel must be able to push the opened state back through a clickable card).
        ApplyMouseInteraction();
    }

    /// <summary>
    /// Owner-driven runtime safety override of the click-through. Set when an external "disable
    /// click-through" could not be written to disk: the session is made interactive immediately and
    /// stays that way until the user explicitly re-enables the option (or a successful save carries
    /// <c>ClickThrough = true</c> and clears it again). Nothing is persisted here.
    /// </summary>
    internal void SetClickThroughSafetyOverride(bool active)
    {
        if (_clickThroughSafetyOverride == active) return;
        _clickThroughSafetyOverride = active;
        UpdateMenuState();
        ApplyMouseInteraction();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        ObsidianWindowChrome.ApplyDontRoundCorners(_handle);
        // Read the live extended style *before* writing anything: the NOACTIVATE bit the window was
        // born with (WPF/shell baselining of ShowActivated=false, or any later shell bit) is the value
        // this feature has to restore when the click-through is turned off. It is cached once here and
        // never rebuilt into a full style - ApplyMouseInteraction still reads the live style itself.
        var exStyle = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        _baseNoActivate = (exStyle & NativeMethods.WsExNoActivate) != 0;
        // Tool window only (out of Alt+Tab / taskbar). The card must receive clicks, the context menu
        // and the settings dialog, so this call only adds WS_EX_TOOLWINDOW.
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle,
            new IntPtr(exStyle | NativeMethods.WsExToolWindow));
        // The handle exists only now, so the click-through state (never on by default) is applied
        // here for the first time instead of being guessed before the window was created.
        ApplyMouseInteraction();
    }

    /// <summary>
    /// The single place that writes the card's extended window style. It reads the *live* style and
    /// lets <see cref="FloatingStatusInteraction.ComputeExStyle"/> add/clear only the two bits this
    /// feature owns, so WPF's <c>WS_EX_LAYERED</c>, the tool-window bit and anything added by another
    /// component survive. Because the base style is never cached, a style change made elsewhere is not
    /// silently reverted on the next call. The only cached value is the boolean born-with
    /// <c>WS_EX_NOACTIVATE</c> baseline captured in <see cref="OnSourceInitialized"/>: it is restored
    /// (not assumed off) when the click-through is disabled.
    /// <para>
    /// Effective click-through is <c>ClickThrough &amp;&amp; !IsSettingsEditing &amp;&amp;
    /// !safetyOverride</c>: while the settings dialog is open the card stays operable, and after a
    /// failed external disable the session stays operable. This method never reads hardware, never
    /// persists anything and never changes the window size.</para>
    /// </summary>
    private void ApplyMouseInteraction()
    {
        var handle = _handle;
        if (handle == IntPtr.Zero) return;
        var current = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        var effective = FloatingStatusInteraction.IsClickThroughEffective(
            _settings.ClickThrough, IsSettingsEditing, _clickThroughSafetyOverride);
        // The baseline NOACTIVATE bit is the one read in OnSourceInitialized, never a hard-coded
        // "false": with click-through off, ComputeExStyle restores what the card was born with and
        // clears only the transparency bit this feature added.
        var wanted = FloatingStatusInteraction.ComputeExStyle(current, effective, _baseNoActivate);
        if (wanted == current) return;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(wanted));
    }

    /// <summary>
    /// Feeds the card the same <see cref="HardwareSnapshot"/> the taskbar overlays receive in the
    /// existing 1 s sampling pass. The three control-gate flags are accepted so the main window can
    /// pass the exact same tuple it passes to the taskbar strip; this card exposes no power-mode or
    /// fan action, so they never enable anything here. Nothing here reads hardware, writes the
    /// EC/fan or starts a control pass.
    /// </summary>
    public void UpdateSnapshot(
        HardwareSnapshot snapshot,
        bool guardianRunning,
        bool manualFanControlAllowed = true,
        bool fixedRpmAndCurveAllowed = true,
        bool powerModeControlAllowed = true)
    {
        _ = manualFanControlAllowed;
        _ = fixedRpmAndCurveAllowed;
        _ = powerModeControlAllowed;
        _guardianRunning = guardianRunning;
        // The newest sample is always kept, so showing the card later paints current data. The
        // visual tree is only rebuilt while the card is really on screen: a hidden/disabled card
        // used to re-render an invisible tree on every 1 s sampling tick.
        _snapshot = snapshot;
        if (IsVisible && _settings.Enabled) Render();
    }

    /// <summary>
    /// Applies a settings instance to the existing card: colours, font, compact spacing, layout mode,
    /// item list, always-on-top, lock state, enabled visibility and the stored position. No disk
    /// write and no window recreate, so the settings dialog can live-preview it and cancel by
    /// pushing the opened instance back through this same method.
    /// </summary>
    public void UpdateDisplaySettings(FloatingStatusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings;
        Topmost = settings.Topmost;
        ApplyAppearance();
        UpdateLockState();
        // Colours/font/layout are not the only live-previewed values: a previewed click-through
        // preference is applied - or suspended while the dialog edits - through the same call.
        ApplyMouseInteraction();
        if (!settings.Enabled)
        {
            if (IsVisible) Hide();
            return;
        }
        // Show before rendering/measuring: the card is only ever painted while visible, and the
        // latest snapshot kept by UpdateSnapshot is drawn here, so enabling it never shows an empty
        // card until the next sampling tick.
        if (!IsVisible) Show();
        Render();
        // Let WPF measure the new font/padding before the placement reads the window rectangle.
        UpdateLayout();
        ApplyPlacement();
        // Re-assert once the card is really shown: showing a WPF transparent window may touch the
        // extended style, and re-reading the live style here can only re-add the two owned bits.
        ApplyMouseInteraction();
    }

    /// <summary>
    /// Re-clamps the card inside the current work area of its monitor. Used after a display/DPI
    /// change and when the window is shown; a monitor that disappeared falls back to the primary
    /// screen and the stored relative offset is clamped there instead of restoring an off-screen
    /// absolute position.
    /// </summary>
    public void EnsureOnVisibleMonitor()
    {
        if (!IsVisible || _isDragging) return;
        UpdateLayout();
        ApplyPlacement();
    }

    /// <summary>
    /// Positions the card with one native call using physical pixels only.
    /// <para>
    /// Deliberately no <c>Left = pixel / scale</c>: on a mixed-DPI desktop a global pixel coordinate
    /// divided by the target monitor scale is not a WPF coordinate (WPF converts it back with its own
    /// scale, double-correcting it), so the stored rectangle would drift. WPF receives WM_MOVE and
    /// keeps <c>Left</c>/<c>Top</c> in sync by itself. The topmost state is only applied through the
    /// WPF <c>Topmost</c> property; <c>SetWindowPos</c> never re-raises a normal window with
    /// HWND_NOTOPMOST, and the size is never touched (<c>SWP_NOSIZE</c>).
    /// </para>
    /// </summary>
    private void ApplyPlacement()
    {
        // A drag owns the position; a queued DPI/work-area clamp must not fight it with the old
        // settings (RecordDraggedPosition clamps once against the monitor the drag ended on).
        if (_isDragging) return;
        var handle = _handle;
        if (handle == IntPtr.Zero) return;
        if (ResolveScreen(_settings.MonitorDeviceName) is not { } screen) return;
        var scale = ScaleFor(screen);
        var work = screen.WorkingArea;
        ApplyWorkAreaLimits(work.Width, work.Height, scale);
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return;
        var widthPixels = rect.Right - rect.Left;
        var heightPixels = rect.Bottom - rect.Top;
        if (widthPixels <= 0 || heightPixels <= 0)
        {
            // Not measured yet (first placement): fall back to the measured DIP size times the
            // monitor scale; the queued re-clamp after the next layout pass uses the real rectangle.
            widthPixels = Math.Max(1, (int)Math.Ceiling((ActualWidth > 0 ? ActualWidth : FloatingStatusLayout.MinimumWidthDip) * scale));
            heightPixels = Math.Max(1, (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : FloatingStatusLayout.MinimumHeightDip) * scale));
        }
        if (!FloatingStatusLayout.TryComputePlacementPixels(
                work.Left, work.Top, work.Right, work.Bottom,
                widthPixels, heightPixels, scale, _settings.LeftDip, _settings.TopDip, out var placement))
            return;
        if (rect.Left == placement.X && rect.Top == placement.Y) return;
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            placement.X, placement.Y, 0, 0,
            SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>
    /// Caps the card at the work area so a long item list or a long live value scrolls inside a
    /// visible window instead of growing off screen. Only the cap is set here (never a forced size),
    /// and only when it actually changes, so no layout recursion is created.
    /// </summary>
    private void ApplyWorkAreaLimits(int workWidthPixels, int workHeightPixels, double scale)
    {
        var (maxWidth, maxHeight) = FloatingStatusLayout.MaximumWindowSizeDip(workWidthPixels, workHeightPixels, scale);
        if (!IsSameLimit(MaxWidth, maxWidth)) MaxWidth = maxWidth;
        if (!IsSameLimit(MaxHeight, maxHeight)) MaxHeight = maxHeight;
    }

    private static bool IsSameLimit(double current, double wanted) =>
        double.IsFinite(current) && Math.Abs(current - wanted) < 0.5;

    /// <summary>Queues one placement pass (DPI change, work-area change or size change).</summary>
    private void QueuePlacementClamp()
    {
        if (_isDragging || _placementQueued || _handle == IntPtr.Zero) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _placementQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _placementQueued = false;
            // A drag that started meanwhile owns the position; RecordDraggedPosition clamps once
            // it ends, against the monitor the drag actually ended on.
            if (_isDragging) return;
            UpdateLayout();
            ApplyPlacement();
        }));
    }

    /// <summary>
    /// Stores the position after a user drag: the monitor's device name plus the origin offset from
    /// that monitor's work-area origin in DIPs. Called once when the drag loop ends (not per mouse
    /// move), so a drag never writes the settings file in a loop.
    /// </summary>
    private void RecordDraggedPosition()
    {
        if (_handle == IntPtr.Zero || !NativeMethods.GetWindowRect(_handle, out var rect)) return;
        var widthPixels = Math.Max(1, rect.Right - rect.Left);
        var heightPixels = Math.Max(1, rect.Bottom - rect.Top);
        var centerX = rect.Left + widthPixels / 2;
        var centerY = rect.Top + heightPixels / 2;
        // The monitor is resolved from where the window physically ended up, never from the stored
        // (old) settings that a mixed-DPI drag just left behind.
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(centerX, centerY));
        var scale = ScaleFor(screen);
        var work = screen.WorkingArea;
        // One call does the clamp and (when enabled) the edge snap in physical pixels: the real
        // GetWindowRect rectangle goes in, the exact physical rectangle comes out, so nothing is
        // scaled twice and the stored value is the work-area-relative DIP of what is on screen. A
        // drag may leave part of the window outside the monitor; clamping first means the snap moves
        // from a legal position. The snap is only computed here, once per finished drag - never on a
        // refresh/resize/DPI pass, which would glue a card the user never moved.
        if (!FloatingStatusLayout.TryComputeSnappedPlacement(
                work.Left, work.Top, work.Right, work.Bottom, rect.Left, rect.Top, widthPixels, heightPixels,
                scale, _settings.SnapToEdges, out var placement, out _))
            return;
        _settings = _settings with
        {
            MonitorDeviceName = screen.DeviceName,
            LeftDip = placement.RelativeLeftDip,
            TopDip = placement.RelativeTopDip
        };
        // Re-apply the placement now: it snaps the card back only when the clamp had to move it and
        // it also refreshes the work-area size cap for the monitor the drag ended on.
        ApplyPlacement();
        PositionChanged?.Invoke(_settings);
    }

    private static Forms.Screen? ResolveScreen(string? deviceName)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return null;
        if (!string.IsNullOrWhiteSpace(deviceName) &&
            screens.FirstOrDefault(screen => string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                is { } stored)
            return stored;
        return Forms.Screen.PrimaryScreen ?? screens[0];
    }

    /// <summary>Scale of the monitor the card should live on (1.0 when it cannot be read).</summary>
    private static double ScaleFor(Forms.Screen screen)
    {
        var work = screen.WorkingArea;
        var center = new NativeMethods.Point
        {
            X = work.Left + Math.Max(1, work.Width) / 2,
            Y = work.Top + Math.Max(1, work.Height) / 2
        };
        var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MonitorDefaultToNearest);
        return FloatingStatusLayout.SanitizeScale(NativeMethods.GetMonitorScale(monitor));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Never resolve the placement immediately: during a cross-monitor drag this callback fires
        // while DragMove owns the window, and applying the stored (old-monitor) DIPs here is what
        // pulled the card back to the monitor it was leaving. The queued pass is skipped while
        // dragging and re-clamps only after the drag recorded its new monitor.
        QueuePlacementClamp();
    }

    private void DragHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_settings.IsLocked)
        {
            e.Handled = true;
            return;
        }
        if (e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        _isDragging = true;
        try
        {
            // Modal move loop: WPF keeps dragging until the button is released.
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released between the check and DragMove; nothing moved.
            return;
        }
        finally
        {
            // Cleared on every exit path (including the exception above), so a DPI/visibility clamp
            // queued during the drag can never keep believing a drag owns the position.
            _isDragging = false;
        }
        RecordDraggedPosition();
    }

    /// <summary>
    /// A double click opens the main window exactly once. A single click is deliberately inert (the
    /// card is not a taskbar item), and the extra pairs of a triple click are ignored.
    /// </summary>
    private void StatusMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;
        e.Handled = true;
        OpenRequested?.Invoke();
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "打开主窗口", () => OpenRequested?.Invoke());
        // The settings entry stays enabled while it is open: a click brings the existing dialog
        // forward instead of opening a second one (the owner enforces the single instance).
        AddMenuItem(menu, "桌面悬浮窗设置…", () => SettingsRequested?.Invoke());
        menu.Items.Add(new Separator());
        _lockItem = AddMenuItem(menu, "锁定位置", () => LockRequested?.Invoke(!_settings.IsLocked));
        _lockItem.IsCheckable = true;
        _topmostItem = AddMenuItem(menu, "总在最前", () => TopmostRequested?.Invoke(!_settings.Topmost));
        _topmostItem.IsCheckable = true;
        menu.Items.Add(new Separator());
        // The external escape hatches live in the menu *and* in the tray / main window / global
        // hotkey, because an actually click-through card cannot receive this right click at all.
        _clickThroughItem = AddMenuItem(menu, "关闭悬浮窗鼠标穿透", () => ClickThroughDisableRequested?.Invoke());
        AddMenuItem(menu, "恢复默认位置", () => ResetPositionRequested?.Invoke());
        menu.Items.Add(new Separator());
        _hideItem = AddMenuItem(menu, "隐藏悬浮窗", () => HideRequested?.Invoke());
        AddMenuItem(menu, "退出", () => ExitRequested?.Invoke());
        ContextMenu = menu;
        ContextMenuOpening += (_, _) => UpdateMenuState();
    }

    /// <summary>
    /// Syncs the check marks and, while the settings dialog is open, disables the three entries that
    /// would write a value the dialog's cancel path could not restore. Each disabled entry explains
    /// itself instead of a click silently doing nothing.
    /// </summary>
    private void UpdateMenuState()
    {
        var editing = IsSettingsEditing;
        var hint = editing ? SettingsEditingHint : null;
        if (_lockItem is not null)
        {
            _lockItem.IsChecked = _settings.IsLocked;
            _lockItem.IsEnabled = !editing;
            _lockItem.ToolTip = hint;
        }
        if (_topmostItem is not null)
        {
            _topmostItem.IsChecked = _settings.Topmost;
            _topmostItem.IsEnabled = !editing;
            _topmostItem.ToolTip = hint;
        }
        if (_hideItem is not null)
        {
            _hideItem.IsEnabled = !editing;
            _hideItem.ToolTip = hint;
        }
        if (_clickThroughItem is not null)
        {
            // While the dialog edits, the actual click-through is suspended anyway; the entry stays
            // reachable but explains itself instead of pretending to do something.
            _clickThroughItem.IsEnabled = !editing;
            _clickThroughItem.ToolTip = hint;
        }
    }

    /// <summary>Explains a disabled menu entry while the settings dialog is the single editor.</summary>
    internal const string SettingsEditingHint =
        "桌面悬浮窗设置正在编辑：请先在设置窗口点“保存”或“取消”，再切换锁定/置顶/隐藏。设置窗口本身仍可再次打开（会切到前台）。";

    private static MenuItem AddMenuItem(ItemsControl menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        AutomationProperties.SetName(item, label);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    private void UpdateLockState()
    {
        if (DragHandle is null) return;
        // The drag handle stays visible (it is the card's grip) but explains the locked state and
        // no longer moves the window; the context menu still reaches the lock toggle itself.
        DragHandle.Cursor = _settings.IsLocked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll;
        DragHandle.Opacity = _settings.IsLocked ? 0.45 : 1.0;
        AutomationProperties.SetHelpText(DragHandle, _settings.IsLocked
            ? "位置已锁定：拖动条不会移动窗口，取消右键菜单的“锁定位置”后可再次拖动。"
            : "按住左键拖动整个悬浮窗。锁定位置后拖动条不再移动窗口，但仍可双击指标打开主窗口或右键打开菜单。");
    }

    private void ApplyAppearance()
    {
        if (RootBorder is null) return;
        _foreground = ParseBrush(_settings.TextColor) ?? System.Windows.Media.Brushes.White;
        RootBorder.Background = ParseBrush(_settings.BackgroundColor) ?? ParseBrush(FloatingStatusSettings.Default.BackgroundColor)
            ?? System.Windows.Media.Brushes.Transparent;
        var accent = _foreground is SolidColorBrush solid ? solid.Color : Colors.White;
        RootBorder.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(48, accent.R, accent.G, accent.B));
        _separator = new SolidColorBrush(WpfColor.FromArgb(56, accent.R, accent.G, accent.B));
        _fontFamily = CreateFontFamily(_settings.FontFamily);
        _fontSize = _settings.Compact ? _settings.FontSize : _settings.FontSize + 1;
        RootBorder.Padding = _settings.Compact ? CompactCardPadding : RegularCardPadding;
        // Brushes/padding/font are not part of the pure topology, so a settings change always
        // rebuilds the card instead of keeping elements with the old look.
        InvalidateStatusRender();
        UpdateLockState();
    }

    private static WpfFontFamily CreateFontFamily(string name)
    {
        try { return new WpfFontFamily(name); }
        catch (ArgumentException) { return new WpfFontFamily(FloatingStatusSettings.DefaultFontFamily); }
    }

    private static WpfBrush? ParseBrush(string value)
    {
        try { return new BrushConverter().ConvertFromString(value) as WpfBrush; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            // Settings.Validate rejects malformed colours; this keeps a hand-edited file from
            // breaking the paint path.
            return null;
        }
    }

    /// <summary>
    /// Safe content width in DIPs for one layout plan: the window cap that
    /// <see cref="ApplyWorkAreaLimits"/> applied to <see cref="Window.MaxWidth"/>, minus the border
    /// padding the card really draws (<see cref="RootBorder"/>).
    /// <para>
    /// A very small or high-DPI work area caps the window far below
    /// <see cref="DefaultContentWidthDip"/>. Planning against that fixed upper bound kept more cells
    /// than the window can show, and WPF then clipped/scrolled them horizontally without a count.
    /// Budgeting against the real cap instead turns the same item list into the plan's explicit
    /// <c>+N</c> marker, whose tooltip/accessible name still carries every omitted item's full text.
    /// </para>
    /// <para>
    /// Infinity/NaN (not measured yet: WPF's default <see cref="Window.MaxWidth"/>, or a sanitised
    /// unusable scale) keeps <see cref="DefaultContentWidthDip"/>. The result never exceeds that
    /// upper bound (a huge work area must not let the card grow across the screen) and is floored at
    /// 1 DIP (a cap narrower than the padding still plans one cell plus the marker).
    /// </para>
    /// </summary>
    private double MaximumContentWidthDip
    {
        get
        {
            // The padding actually in use; before the first ApplyAppearance the border is not
            // created yet, so the same values are taken from the compact/regular constants.
            double horizontalPadding;
            if (RootBorder is { } border)
            {
                horizontalPadding = border.Padding.Left + border.Padding.Right;
            }
            else
            {
                var padding = _settings.Compact ? CompactCardPadding : RegularCardPadding;
                horizontalPadding = padding.Left + padding.Right;
            }

            var cap = MaxWidth;
            if (!double.IsFinite(cap) || cap <= 0) cap = DefaultContentWidthDip;
            return Math.Max(1, Math.Min(DefaultContentWidthDip, cap - horizontalPadding));
        }
    }

    private void Render()
    {
        if (_snapshot is not { } snapshot)
        {
            RenderWaitingPlaceholder();
            return;
        }

        var metrics = _settings.Items
            .Select(item => TaskbarMetricFormatter.Format(item, snapshot, _guardianRunning))
            .ToArray();
        // The budget is the *live* window cap minus the real border padding, read twice in the same
        // synchronous pass: nothing can change MaxWidth/RootBorder.Padding between the plan and the
        // signature, so both calls always receive the identical safe width (the +N decision and the
        // cached topology cannot disagree).
        var plan = TaskbarItemLayout.Build(_settings.LayoutMode, metrics, MaximumContentWidthDip, _fontSize, vertical: false);
        var signature = TaskbarRenderSignature.Create(plan, vertical: false, _settings.Compact,
            _fontSize, MaximumContentWidthDip);
        // Same topology (mode, font, compact, budget, every cell's position/style, the +N marker and
        // its hidden list, the grid projection): keep the existing elements and only rewrite the
        // live wording/tooltips/names instead of recreating the whole card tree.
        if (_renderedSignature is { } previous && previous.Matches(signature) &&
            _renderedCells.Count == signature.SlotCount)
        {
            UpdateRenderedCellWording(plan);
            SetAccessibleName(StatusLayoutRoot, "桌面悬浮窗状态");
            return;
        }

        ClearStatusLayout();
        switch (plan.Mode)
        {
            case TaskbarLayoutMode.Inline:
                RenderInline(plan);
                break;
            case TaskbarLayoutMode.LabelOverValue:
                RenderCards(plan);
                break;
            default:
                RenderGrid(plan);
                break;
        }
        // At most one kept reference per slot (13 shipped items plus the marker); dropped again by
        // the next rebuild and by OnClosed, so the cache never grows with the sampling rate.
        _renderedSignature = signature;
        AutomationProperties.SetName(StatusLayoutRoot, "桌面悬浮窗状态");
    }

    /// <summary>
    /// The pre-first-sample placeholder ("等待采样…"). It has no plan and therefore no topology, so
    /// it is replaced by the first real render.
    /// </summary>
    private void RenderWaitingPlaceholder()
    {
        ClearStatusLayout();
        var waiting = new TextBlock
        {
            Text = "等待采样…",
            FontFamily = _fontFamily,
            FontSize = _fontSize,
            Foreground = _foreground,
            Opacity = 0.7
        };
        AutomationProperties.SetName(waiting, "等待采样");
        StatusLayoutRoot.Children.Add(waiting);
    }

    /// <summary>Drops the rendered tree together with every cached reference and the topology.</summary>
    private void ClearStatusLayout()
    {
        StatusLayoutRoot.Children.Clear();
        _renderedCells.Clear();
        _renderedSignature = null;
    }

    /// <summary>Forces the next render to rebuild the tree (brushes/appearance changes, hidden ticks).</summary>
    private void InvalidateStatusRender() => _renderedSignature = null;

    /// <summary>Remembers one created text element in plan order (cell, card value, marker).</summary>
    private void TrackCell(TextBlock text) => _renderedCells.Add(new RenderedCell(text));

    /// <summary>Remembers one created E card: its value line, its label line and the card tooltip host.</summary>
    private void TrackCard(StackPanel card, TextBlock label, TextBlock value) =>
        _renderedCells.Add(new RenderedCell(value, label, card));

    /// <summary>
    /// Applies the current wording to the existing tree. Text, tooltip and accessible name are only
    /// assigned when they really differ, so an unchanged value never re-invalidates WPF.
    /// </summary>
    private void UpdateRenderedCellWording(TaskbarMetricLayoutPlan plan)
    {
        var updates = TaskbarRenderTexts.Updates(plan);
        if (updates.Count != _renderedCells.Count)
        {
            // Cannot happen for a matching signature; rebuild instead of guessing an alignment.
            InvalidateStatusRender();
            return;
        }
        for (var index = 0; index < updates.Count; index++)
        {
            var update = updates[index];
            var cell = _renderedCells[index];
            SetText(cell.Text, update.Text);
            SetToolTip(cell.Text, update.ToolTip);
            SetAccessibleName(cell.Text, update.AccessibleName);
            if (cell.Label is { } label)
            {
                SetText(label, update.CardLabel);
                SetToolTip(label, update.ToolTip);
                SetAccessibleName(label, update.CardLabel);
            }
            if (cell.Card is { } card)
                SetToolTip(card, update.CardToolTip);
        }
    }

    private static void SetText(TextBlock block, string text)
    {
        if (string.Equals(block.Text, text, StringComparison.Ordinal)) return;
        block.Text = text;
    }

    private static void SetToolTip(FrameworkElement element, string? toolTip)
    {
        if (element.ToolTip is not string current)
        {
            if (toolTip is null && element.ToolTip is null) return;
        }
        else if (string.Equals(current, toolTip, StringComparison.Ordinal))
        {
            return;
        }
        element.ToolTip = toolTip;
    }

    private static void SetAccessibleName(DependencyObject element, string? name)
    {
        if (string.Equals(AutomationProperties.GetName(element), name, StringComparison.Ordinal)) return;
        AutomationProperties.SetName(element, name);
    }

    /// <summary>One kept text element of the current tree, in plan/slot order.</summary>
    private sealed class RenderedCell(TextBlock text, TextBlock? label = null, FrameworkElement? card = null)
    {
        public TextBlock Text { get; } = text;
        public TextBlock? Label { get; } = label;
        public FrameworkElement? Card { get; } = card;
    }

    /// <summary>C: one line, two groups (first two items / the rest) and one 1px divider.</summary>
    private void RenderInline(TaskbarMetricLayoutPlan plan)
    {
        var panel = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var currentGroup = -1;
        foreach (var cell in plan.Cells)
        {
            AddInlineSeparator(panel, currentGroup, cell.Group);
            currentGroup = cell.Group;
            // C keeps the unchanged flat wording (no colon), exactly like the taskbar strip.
            var block = CreateCellText(cell.Metric.Text, cell.Metric, AccessibleNameFor(cell.Metric));
            TrackCell(block);
            panel.Children.Add(block);
        }
        if (plan.Overflow is { } overflow)
        {
            AddInlineSeparator(panel, currentGroup, group: 1);
            var overflowBlock = CreateOverflowMarker(overflow);
            TrackCell(overflowBlock);
            panel.Children.Add(overflowBlock);
        }
        StatusLayoutRoot.Children.Add(panel);
    }

    private void AddInlineSeparator(StackPanel panel, int previousGroup, int group)
    {
        if (previousGroup < 0) return;
        if (previousGroup == group)
        {
            panel.Children.Add(new Border { Width = TaskbarItemLayout.InlineItemGapDip });
            return;
        }
        panel.Children.Add(new Border { Width = TaskbarItemLayout.GroupGapDip });
        panel.Children.Add(new WpfRectangle
        {
            Width = TaskbarItemLayout.SeparatorWidthDip,
            Height = Math.Max(1, _fontSize * 1.05),
            Fill = _separator,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new Border { Width = TaskbarItemLayout.GroupGapDip });
    }

    /// <summary>E: one row of label-above-value cards.</summary>
    private void RenderCards(TaskbarMetricLayoutPlan plan)
    {
        var panel = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var first = true;
        foreach (var cell in plan.Cells)
        {
            AddCardSeparator(panel, first);
            first = false;
            var (card, label, value) = CreateCard(cell.Metric);
            TrackCard(card, label, value);
            panel.Children.Add(card);
        }
        if (plan.Overflow is { } overflow)
        {
            AddCardSeparator(panel, first);
            var overflowBlock = CreateOverflowMarker(overflow);
            TrackCell(overflowBlock);
            panel.Children.Add(overflowBlock);
        }
        StatusLayoutRoot.Children.Add(panel);
    }

    private void AddCardSeparator(StackPanel panel, bool first)
    {
        if (first) return;
        panel.Children.Add(new Border { Width = TaskbarItemLayout.CardGapDip });
    }

    private (StackPanel Card, TextBlock Label, TextBlock Value) CreateCard(TaskbarMetric metric)
    {
        var card = new StackPanel { Orientation = WpfOrientation.Vertical };
        var label = CreateCellText(metric.CardLabel, metric, metric.CardLabel);
        label.FontSize = Math.Max(8, _fontSize * 0.78);
        label.Opacity = 0.78;
        var value = CreateCellText(metric.ValueText, metric, metric.ValueText);
        value.FontWeight = FontWeights.SemiBold;
        card.ToolTip = AccessibleNameFor(metric);
        card.Children.Add(label);
        card.Children.Add(value);
        return (card, label, value);
    }

    /// <summary>A/D: the plan's two-row columns with the single group divider from the pure projection.</summary>
    private void RenderGrid(TaskbarMetricLayoutPlan plan)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var layout = TaskbarGridLayout.Project(plan, vertical: false);
        foreach (var column in layout.Columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = column.IsAuto ? GridLength.Auto : new GridLength(column.WidthDip)
            });
        }
        for (var row = 0; row < plan.Rows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < layout.Columns.Count; index++)
        {
            if (layout.Columns[index].Kind != TaskbarGridColumnKind.Separator) continue;
            var line = new WpfRectangle
            {
                Width = TaskbarItemLayout.SeparatorWidthDip,
                Fill = _separator,
                Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(line, index);
            Grid.SetRowSpan(line, Math.Max(1, plan.Rows));
            grid.Children.Add(line);
        }

        foreach (var cell in plan.Cells)
        {
            var metric = cell.Metric;
            var text = cell.Style == TaskbarCellStyle.Colon ? metric.ColonText : metric.Text;
            var block = CreateCellText(text, metric, AccessibleNameFor(metric));
            Grid.SetRow(block, cell.Row);
            Grid.SetColumn(block, layout.MetricColumns[cell.Column]);
            TrackCell(block);
            grid.Children.Add(block);
        }

        if (plan.Overflow is { } overflow)
        {
            var marker = CreateOverflowMarker(overflow);
            TrackCell(marker);
            Grid.SetRow(marker, 0);
            Grid.SetRowSpan(marker, Math.Max(1, plan.Rows));
            Grid.SetColumn(marker, layout.OverflowColumn);
            grid.Children.Add(marker);
        }
        StatusLayoutRoot.Children.Add(grid);
    }

    private TextBlock CreateCellText(string text, TaskbarMetric? metric, string? accessibleName = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = _fontFamily,
            FontSize = _fontSize,
            Foreground = _foreground,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Tabular digits keep a live sample from re-widening the card.
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        if (metric is { } value)
            block.ToolTip = value.Detail is { Length: > 0 } detail
                ? value.Text + Environment.NewLine + detail
                : value.Text;
        AutomationProperties.SetName(block, accessibleName ?? text);
        return block;
    }

    private static string AccessibleNameFor(TaskbarMetric metric) => metric.CardLabel + " " + metric.ValueText;

    private TextBlock CreateOverflowMarker(TaskbarOverflowMarker overflow)
    {
        var block = CreateCellText(overflow.Text, metric: null, overflow.AccessibleName);
        block.ToolTip = overflow.ToolTip;
        block.Opacity = 0.9;
        return block;
    }
}
