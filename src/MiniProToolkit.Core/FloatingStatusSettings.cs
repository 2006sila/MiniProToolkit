using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniProToolkit.Core;

/// <summary>
/// Durable settings of the standalone desktop floating status window. It is deliberately a
/// separate type and a separate file from <see cref="TaskbarDisplaySettings"/>: the floating window
/// can be enabled next to the taskbar strip (not instead of it), keeps its own item list / layout /
/// colours / font and its own lifetime, and its schema can change without touching the taskbar
/// contract (<c>taskbar.json</c> schema 5). It also stores where the user dragged the window:
/// the monitor's device name plus the window origin relative to that monitor's work-area origin in
/// DIPs, so a resolution/DPI/monitor change can clamp it back instead of restoring an absolute
/// virtual-screen pixel coordinate.
/// <para>
/// <see cref="Enabled"/> defaults to <c>false</c>, so installing this build changes nothing until
/// the user explicitly turns the floating window on. <see cref="LeftDip"/>/<see cref="TopDip"/> are
/// <c>null</c> until the user drags the window; <c>null</c> means "default corner of the work area"
/// (bottom-right with a small margin), which is resolution independent.
/// </para>
/// <para>
/// <see cref="ClickThrough"/> and <see cref="SnapToEdges"/> are the second-phase interaction
/// switches and were appended as optional record parameters so every existing construction (and a
/// file written before they existed) keeps a defined value. Mouse click-through is opt-in and
/// therefore defaults to <c>false</c>: the card must stay clickable unless the user explicitly asked
/// for the mouse to pass through it, and the main window always keeps an external escape hatch
/// (tray entry, global hotkey, main-window button) that does not need the card to receive a click.
/// Edge snapping at the end of a drag is a positioning convenience and defaults to <c>true</c>; it
/// only ever runs once when the user finishes dragging, never on a refresh/resize/DPI pass.
/// </para>
/// </summary>
public sealed record FloatingStatusSettings(
    bool Enabled,
    IReadOnlyList<TaskbarDisplayItem> Items,
    TaskbarLayoutMode LayoutMode,
    bool Compact,
    string FontFamily,
    double FontSize,
    string TextColor,
    string BackgroundColor,
    bool Topmost,
    bool IsLocked,
    string? MonitorDeviceName,
    double? LeftDip,
    double? TopDip,
    bool ClickThrough = false,
    bool SnapToEdges = true)
{
    /// <summary>Smallest accepted independent font size, in DIPs.</summary>
    public const double MinimumFontSize = 8;

    /// <summary>Largest accepted independent font size, in DIPs.</summary>
    public const double MaximumFontSize = 32;

    /// <summary>Upper bound of the stored placement offset, in DIPs; keeps hand-edited files sane.</summary>
    public const double MaximumPositionDip = 100_000;

    /// <summary>Longest accepted monitor device name (a <c>\\.\DISPLAYn</c> string is far shorter).</summary>
    public const int MaximumMonitorNameLength = 200;

    public const string DefaultFontFamily = "Segoe UI";

    /// <summary>
    /// The documented default: disabled, the four static headline metrics (the same four the
    /// taskbar defaults to), compact pair layout and a small semi-opaque dark card at the
    /// bottom-right of the primary work area. No control action is ever triggered from it.
    /// </summary>
    public static FloatingStatusSettings Default => new(
        Enabled: false,
        Items: [TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm, TaskbarDisplayItem.CpuPackagePower, TaskbarDisplayItem.PowerMode],
        LayoutMode: TaskbarLayoutMode.StackedPairs,
        Compact: true,
        FontFamily: DefaultFontFamily,
        FontSize: 12,
        TextColor: "#E8EAED",
        // A visible card is the point of a floating window (unlike the transparent taskbar text),
        // but it still lets the desktop show through and can be replaced by the user.
        BackgroundColor: "#CC1E2228",
        Topmost: true,
        IsLocked: false,
        MonitorDeviceName: null,
        LeftDip: null,
        TopDip: null,
        ClickThrough: false,
        SnapToEdges: true);

    public void Validate()
    {
        if (Items is null || Items.Count == 0)
            throw new ArgumentException("At least one floating display item is required.", nameof(Items));
        if (Items.Count != Items.Distinct().Count() || Items.Any(item => !Enum.IsDefined(item)))
            throw new ArgumentException("Floating display items are invalid.", nameof(Items));
        if (!Enum.IsDefined(LayoutMode))
            throw new ArgumentException("Floating layout mode is invalid.", nameof(LayoutMode));
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 100)
            throw new ArgumentException("Invalid floating font.", nameof(FontFamily));
        if (!double.IsFinite(FontSize) || FontSize is < MinimumFontSize or > MaximumFontSize)
            throw new ArgumentOutOfRangeException(nameof(FontSize));
        ValidateColor(TextColor, nameof(TextColor));
        ValidateColor(BackgroundColor, nameof(BackgroundColor));
        if (MonitorDeviceName is { Length: > MaximumMonitorNameLength })
            throw new ArgumentException("Invalid monitor device name.", nameof(MonitorDeviceName));
        ValidatePosition(LeftDip, nameof(LeftDip));
        ValidatePosition(TopDip, nameof(TopDip));
        // A half-stored position (only one axis) is not a placement; both fields are a pair.
        if ((LeftDip is null) != (TopDip is null))
            throw new ArgumentException("A floating position needs both LeftDip and TopDip.", nameof(LeftDip));
    }

    private static void ValidatePosition(double? value, string name)
    {
        if (value is not { } dip) return;
        if (!double.IsFinite(dip) || dip is < -MaximumPositionDip or > MaximumPositionDip)
            throw new ArgumentOutOfRangeException(name, $"Floating position must stay within ±{MaximumPositionDip} DIP.");
    }

    private static void ValidateColor(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('#') || (value.Length is not (7 or 9)) ||
            !value.Skip(1).All(c => Uri.IsHexDigit(c)))
            throw new ArgumentException("Color must be #RRGGBB or #AARRGGBB.", name);
    }
}

/// <summary>
/// Persistence for the desktop floating window (<c>%LOCALAPPDATA%\MiniProToolkit\floating-status.json</c>,
/// schema 1). This store never touches <c>taskbar.json</c>: the two windows are independent, so a
/// corrupt floating file resets only the floating window.
/// <para>
/// The loader is deliberately field-tolerant for a value that is present but unusable: an unknown
/// layout mode, an out-of-range font size, a malformed colour, an unknown item name, a duplicate
/// item or a half-written position keeps every readable field and falls back only for the unusable
/// one (duplicates are dropped in first-seen order); the caller receives a warning so the damage is
/// visible in the log. Structural damage (unparseable JSON, unsupported version, a missing/emptied
/// item list, or a field written with the wrong JSON type - for example a string where a bool is
/// expected, which System.Text.Json refuses to bind) still resets the whole file to
/// <see cref="FloatingStatusSettings.Default"/>, again with a warning instead of an exception.
/// The two second-phase interaction switches (<c>clickThrough</c>/<c>snapToEdges</c>) are the only
/// exception: they are read from their raw JSON element, so a wrong *type* for one of them falls
/// back for that field only, while the rest of the file keeps the rule above.
/// </para>
/// </summary>
public sealed class FloatingStatusSettingsStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FloatingStatusSettingsStore(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiniProToolkit", "floating-status.json");

    public string Path { get; }

    public FloatingStatusSettings LoadOrDefault(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path)) return FloatingStatusSettings.Default;
            var document = JsonSerializer.Deserialize<ReadDocument>(File.ReadAllText(Path), JsonOptions)
                           ?? throw new InvalidDataException("Floating status settings are empty.");
            if (document.Version is < 1 or > CurrentVersion)
                throw new InvalidDataException("Unsupported floating status settings version.");
            if (document.Items is null)
                throw new InvalidDataException("Floating status settings have no item list.");

            var defaults = FloatingStatusSettings.Default;
            var ignoredFields = new List<string>();
            var seenItems = new HashSet<TaskbarDisplayItem>();
            var duplicateCount = 0;
            var items = new List<TaskbarDisplayItem>(document.Items.Count);
            foreach (var element in document.Items)
            {
                if (!TryReadItem(element, out var item))
                {
                    ignoredFields.Add("显示项");
                    continue;
                }
                // A duplicated entry used to survive into Validate() and reset the whole file to
                // defaults. Keep the first occurrence (the saved order) and only report the extra one.
                if (!seenItems.Add(item))
                {
                    duplicateCount++;
                    continue;
                }
                items.Add(item);
            }
            if (items.Count == 0)
                throw new InvalidDataException("Floating status settings have no supported display item.");

            // Each field is optional on its own: absent means "documented default", present but
            // unusable means "default for this field only" plus a warning. One hand-edited field
            // can therefore never reset the rest of the file.
            // The two interaction switches are read from their raw JSON element, so a wrong type
            // falls back for that field only instead of failing the whole document (every other
            // field keeps the store's original rule).
            var clickThrough = defaults.ClickThrough;
            if (document.ClickThrough is { ValueKind: not JsonValueKind.Null } clickThroughElement)
            {
                if (clickThroughElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    clickThrough = clickThroughElement.GetBoolean();
                else
                    ignoredFields.Add("鼠标穿透");
            }

            var snapToEdges = defaults.SnapToEdges;
            if (document.SnapToEdges is { ValueKind: not JsonValueKind.Null } snapElement)
            {
                if (snapElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    snapToEdges = snapElement.GetBoolean();
                else
                    ignoredFields.Add("靠边吸附");
            }

            var layoutMode = TaskbarLayoutMode.StackedPairs;
            if (document.LayoutMode is { ValueKind: not JsonValueKind.Null } layoutElement)
            {
                if (TryReadLayoutMode(layoutElement, out var storedMode)) layoutMode = storedMode;
                else ignoredFields.Add("布局模式");
            }

            var fontFamily = defaults.FontFamily;
            if (document.FontFamily is { } storedFamily)
            {
                if (string.IsNullOrWhiteSpace(storedFamily) || storedFamily.Length > 100)
                    ignoredFields.Add("字体");
                else fontFamily = storedFamily;
            }

            var fontSize = defaults.FontSize;
            if (document.FontSize is { } storedSize)
            {
                if (double.IsFinite(storedSize) &&
                    storedSize is >= FloatingStatusSettings.MinimumFontSize and <= FloatingStatusSettings.MaximumFontSize)
                    fontSize = storedSize;
                else ignoredFields.Add("字号");
            }

            var textColor = defaults.TextColor;
            if (document.TextColor is { } storedTextColor)
            {
                if (IsColor(storedTextColor)) textColor = storedTextColor;
                else ignoredFields.Add("文字色");
            }

            var backgroundColor = defaults.BackgroundColor;
            if (document.BackgroundColor is { } storedBackgroundColor)
            {
                if (IsColor(storedBackgroundColor)) backgroundColor = storedBackgroundColor;
                else ignoredFields.Add("背景色");
            }

            double? leftDip = null;
            double? topDip = null;
            if (document.LeftDip is not null || document.TopDip is not null)
            {
                if (IsPosition(document.LeftDip) && IsPosition(document.TopDip))
                {
                    leftDip = document.LeftDip;
                    topDip = document.TopDip;
                }
                else
                {
                    ignoredFields.Add("窗口位置");
                }
            }

            var monitor = string.IsNullOrWhiteSpace(document.MonitorDeviceName) ||
                          document.MonitorDeviceName.Length > FloatingStatusSettings.MaximumMonitorNameLength
                ? null
                : document.MonitorDeviceName;

            var settings = new FloatingStatusSettings(
                Enabled: document.Enabled ?? defaults.Enabled,
                Items: items.AsReadOnly(),
                LayoutMode: layoutMode,
                Compact: document.Compact ?? defaults.Compact,
                FontFamily: fontFamily,
                FontSize: fontSize,
                TextColor: textColor,
                BackgroundColor: backgroundColor,
                Topmost: document.Topmost ?? defaults.Topmost,
                IsLocked: document.IsLocked ?? defaults.IsLocked,
                MonitorDeviceName: monitor,
                LeftDip: leftDip,
                TopDip: topDip,
                ClickThrough: clickThrough,
                SnapToEdges: snapToEdges);
            settings.Validate();
            string? fieldWarning = null;
            if (ignoredFields.Count > 0)
                fieldWarning = $"桌面悬浮窗设置中有 {ignoredFields.Count} 个字段不可用（{string.Join("、", ignoredFields.Distinct())}），" +
                               "已回退为默认值，其余设置保留。";
            // A duplicate item is not a field fallback: it is dropped and everything else is kept.
            var duplicateWarning = duplicateCount > 0
                ? $"桌面悬浮窗设置中有 {duplicateCount} 个重复显示项，已按首次出现的顺序去重，其余设置保留。"
                : null;
            warning = fieldWarning is null
                ? duplicateWarning
                : duplicateWarning is null ? fieldWarning : fieldWarning + " " + duplicateWarning;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                       InvalidDataException or ArgumentException or NotSupportedException)
        {
            warning = $"Floating status settings were reset: {ex.GetBaseException().Message}";
            return FloatingStatusSettings.Default;
        }
    }

    public void Save(FloatingStatusSettings settings)
    {
        settings.Validate();
        var directory = System.IO.Path.GetDirectoryName(Path) ??
                        throw new InvalidOperationException("Floating status path has no directory.");
        Directory.CreateDirectory(directory);
        // Atomic replace like the taskbar store: a crash mid-write never leaves a half file that
        // the next start would have to reset.
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new Document(CurrentVersion, settings.Enabled, settings.Items.ToList(), settings.LayoutMode,
                    settings.Compact, settings.FontFamily, settings.FontSize, settings.TextColor,
                    settings.BackgroundColor, settings.Topmost, settings.IsLocked, settings.MonitorDeviceName,
                    settings.LeftDip, settings.TopDip, settings.ClickThrough, settings.SnapToEdges), JsonOptions));
            File.Move(temporary, Path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsColor(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith('#') && value.Length is 7 or 9 &&
        value.Skip(1).All(c => Uri.IsHexDigit(c));

    private static bool IsPosition(double? value) =>
        value is { } dip && double.IsFinite(dip) &&
        dip is >= -FloatingStatusSettings.MaximumPositionDip and <= FloatingStatusSettings.MaximumPositionDip;

    // Every non-item field is nullable on read so a file written before a field existed loads with
    // its documented default instead of failing. Unmapped members (a field from a newer build) are
    // skipped by System.Text.Json and never reset the readable settings.
    private sealed record ReadDocument(
        int Version,
        List<JsonElement>? Items,
        bool? Enabled = null,
        bool? Compact = null,
        bool? Topmost = null,
        bool? IsLocked = null,
        string? FontFamily = null,
        double? FontSize = null,
        string? TextColor = null,
        string? BackgroundColor = null,
        JsonElement? LayoutMode = null,
        string? MonitorDeviceName = null,
        double? LeftDip = null,
        double? TopDip = null,
        JsonElement? ClickThrough = null,
        JsonElement? SnapToEdges = null);

    private sealed record Document(
        int Version,
        bool Enabled,
        List<TaskbarDisplayItem> Items,
        TaskbarLayoutMode LayoutMode,
        bool Compact,
        string FontFamily,
        double FontSize,
        string TextColor,
        string BackgroundColor,
        bool Topmost,
        bool IsLocked,
        string? MonitorDeviceName,
        double? LeftDip,
        double? TopDip,
        bool ClickThrough,
        bool SnapToEdges);

    /// <summary>
    /// Reads a stored <see cref="TaskbarLayoutMode"/> from its name or its numeric form without
    /// throwing on a value written by a newer or hand-edited build. An unusable value returns
    /// <c>false</c>, and the loader falls back to A while every other setting stays untouched.
    /// </summary>
    private static bool TryReadLayoutMode(JsonElement element, out TaskbarLayoutMode mode)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when Enum.TryParse<TaskbarLayoutMode>(element.GetString(), ignoreCase: true, out var named) &&
                                          Enum.IsDefined(named):
                mode = named;
                return true;
            case JsonValueKind.Number when element.TryGetInt32(out var number) &&
                                          Enum.IsDefined((TaskbarLayoutMode)number):
                mode = (TaskbarLayoutMode)number;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>
    /// Reads one stored display item, accepting the enum name and the numeric form. An unknown name
    /// or an undefined number (a metric this build no longer ships) is skipped on its own.
    /// </summary>
    private static bool TryReadItem(JsonElement element, out TaskbarDisplayItem item)
    {
        string? text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
        if (text is not null && Enum.TryParse<TaskbarDisplayItem>(text, ignoreCase: true, out item) && Enum.IsDefined(item))
            return true;
        item = default;
        return false;
    }
}
