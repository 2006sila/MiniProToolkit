namespace MiniProToolkit.Core;

/// <summary>
/// Screen position of the OSD popup inside the cursor monitor's work area. Nine fixed slots
/// (3 columns x 3 rows) so the persisted value is always a closed enum: an unknown name or number
/// can never select an arbitrary coordinate.
/// </summary>
public enum OsdPlacement
{
    TopLeft = 0,
    TopCenter = 1,
    TopRight = 2,
    MiddleLeft = 3,
    MiddleCenter = 4,
    MiddleRight = 5,
    BottomLeft = 6,
    BottomCenter = 7,
    BottomRight = 8
}

/// <summary>
/// Placement catalog: the only placement values that may enter the UI, plus the label and the
/// normalizer that maps corrupted values back to <see cref="DefaultPlacement"/>.
/// </summary>
public static class OsdPlacementCatalog
{
    /// <summary>First-version position (top band, horizontally centered).</summary>
    public const OsdPlacement DefaultPlacement = OsdPlacement.TopCenter;

    public static IReadOnlyList<OsdPlacement> Placements { get; } =
    [
        OsdPlacement.TopLeft, OsdPlacement.TopCenter, OsdPlacement.TopRight,
        OsdPlacement.MiddleLeft, OsdPlacement.MiddleCenter, OsdPlacement.MiddleRight,
        OsdPlacement.BottomLeft, OsdPlacement.BottomCenter, OsdPlacement.BottomRight
    ];

    /// <summary>Maps an undefined enum value (e.g. from a hand-edited file) to the default slot.</summary>
    public static OsdPlacement Normalize(OsdPlacement placement) =>
        Enum.IsDefined(placement) ? placement : DefaultPlacement;

    /// <summary>Parses a persisted placement name (or an in-range number); never throws.</summary>
    public static bool TryParse(string? value, out OsdPlacement placement)
    {
        placement = DefaultPlacement;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!Enum.TryParse<OsdPlacement>(value.Trim(), ignoreCase: true, out var parsed))
            return false;
        if (!Enum.IsDefined(parsed))
            return false;
        placement = parsed;
        return true;
    }

    public static string DisplayName(OsdPlacement placement) => Normalize(placement) switch
    {
        OsdPlacement.TopLeft => "左上",
        OsdPlacement.TopCenter => "顶部居中",
        OsdPlacement.TopRight => "右上",
        OsdPlacement.MiddleLeft => "左侧居中",
        OsdPlacement.MiddleCenter => "屏幕中央",
        OsdPlacement.MiddleRight => "右侧居中",
        OsdPlacement.BottomLeft => "左下",
        OsdPlacement.BottomCenter => "底部居中",
        _ => "右下"
    };
}

/// <summary>
/// Strict color parsing shared by the theme and the user appearance settings. Only
/// <c>#RRGGBB</c> and <c>#AARRGGBB</c> survive, so a corrupted or hostile JSON value can never
/// reach the WPF brush converter as arbitrary text.
/// </summary>
public static class OsdColorRules
{
    /// <summary>Longest accepted text form (<c>#AARRGGBB</c>); also the editor's input limit.</summary>
    public const int MaxInputLength = 9;

    /// <summary>Returns the trimmed color when it matches <c>#RRGGBB</c>/<c>#AARRGGBB</c>, else <c>null</c>.</summary>
    public static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length is not (7 or 9) || trimmed[0] != '#')
            return null;
        return trimmed.Skip(1).All(Uri.IsHexDigit) ? trimmed : null;
    }

    public static bool IsValidColor(string? value) => NormalizeColor(value) is not null;

    /// <summary>
    /// Multiplies the alpha channel of <paramref name="color"/> by <paramref name="opacity"/>.
    /// A no-op for an opacity of 1, so the legacy color strings are returned byte-identical.
    /// </summary>
    public static string WithOpacity(string color, double opacity)
    {
        var normalized = NormalizeColor(color) ??
            throw new ArgumentException("Color must be #RRGGBB or #AARRGGBB.", nameof(color));
        if (!double.IsFinite(opacity) || opacity >= 1)
            return normalized;
        var alpha = normalized.Length == 9 ? Convert.ToInt32(normalized.Substring(1, 2), 16) : 255;
        var rgb = normalized.Length == 9 ? normalized[3..] : normalized[1..];
        var scaled = Math.Clamp((int)Math.Round(alpha * Math.Clamp(opacity, 0, 1), MidpointRounding.AwayFromZero), 0, 255);
        return $"#{scaled:X2}{rgb.ToUpperInvariant()}";
    }
}

/// <summary>One selectable color: <c>null</c> means "follow the selected theme".</summary>
public sealed record OsdColorOption(string? Color, string DisplayName);

/// <summary>
/// Quick-pick color presets for the editor. They are no longer the only accepted values: the editor
/// has a free-text field as well, and every value (preset, typed or loaded from the file) is still
/// restricted to a strict <c>#RRGGBB</c>/<c>#AARRGGBB</c> string by <see cref="OsdColorRules"/>.
/// </summary>
public static class OsdColorCatalog
{
    /// <summary><c>null</c> = follow the theme preset.</summary>
    public static IReadOnlyList<OsdColorOption> Foregrounds { get; } =
    [
        new(null, "跟随主题"),
        new("#FFFFFFFF", "白色"),
        new("#FFE8EAED", "浅灰"),
        new("#FF1F2328", "深色"),
        new("#FFFDD663", "琥珀")
    ];

    public static IReadOnlyList<OsdColorOption> Backgrounds { get; } =
    [
        new(null, "跟随主题"),
        new("#D9212121", "深灰半透明"),
        new("#F2FFFFFF", "近白"),
        new("#FF1B1B1B", "纯黑"),
        new("#D9101A2B", "深蓝")
    ];
}

/// <summary>The finite font sizes offered by the UI. <c>null</c> means "follow the theme preset".</summary>
public static class OsdFontSizeCatalog
{
    public static IReadOnlyList<double> Options { get; } = [14, 16, 18, 20, 22, 26, 30, 36];
}

/// <summary>
/// Font-family rules for the free-text OSD font name. The user may type any installed family, so the
/// value is only checked for <em>shape</em>: a plain family name (letters, digits, spaces and a few
/// harmless separators), never a path, URI or anything a font loader could interpret as a file. The
/// result is stored as a string; resolving it against the installed fonts (and falling back to
/// <see cref="DefaultFontFamily"/> when it is unknown) is the view's job, so a missing font can never
/// make the popup throw.
/// </summary>
public static class OsdFontFamilyRules
{
    /// <summary>Family used by every preset and used again whenever the requested one is unusable.</summary>
    public const string DefaultFontFamily = "Segoe UI";

    /// <summary>Longest accepted family name; also the editor's input limit.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Returns the trimmed family name when it is a safe plain name, else <c>null</c>. Whitespace-only
    /// input is <c>null</c> as well (= "follow the theme"). No path separator, drive colon, <c>#</c>,
    /// <c>%</c>, quote, comma or control character can pass, so nothing here can address a file.
    /// </summary>
    public static string? NormalizeFontFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
            return null;
        var named = false;
        foreach (var character in trimmed)
        {
            if (char.IsLetterOrDigit(character))
            {
                named = true;
                continue;
            }
            if (character is ' ' or '-' or '_' or '.' or '+' or '(' or ')' or '\'')
                continue;
            return null;
        }
        return named ? trimmed : null;
    }

    public static bool IsSafeFontFamily(string? value) => NormalizeFontFamily(value) is not null;

    /// <summary>
    /// Resolves a stored/typed family name to something the view may use. An unsafe or empty name
    /// resolves to <see cref="DefaultFontFamily"/>; when <paramref name="isInstalled"/> is supplied an
    /// unknown family resolves to the default as well (a font that is not installed never reaches the
    /// renderer as-is). The predicate is only ever called with a name that passed the shape rules, and
    /// a throwing predicate is treated as "not installed" so presentation can never fail.
    /// </summary>
    public static string Resolve(string? family, Func<string, bool>? isInstalled = null)
    {
        var normalized = NormalizeFontFamily(family);
        if (normalized is null)
            return DefaultFontFamily;
        if (isInstalled is null)
            return normalized;
        try
        {
            return isInstalled(normalized) ? normalized : DefaultFontFamily;
        }
        catch (Exception)
        {
            return DefaultFontFamily;
        }
    }
}

/// <summary>
/// User presentation overrides on top of a <see cref="OsdThemePreset"/>. Every field is optional:
/// <c>null</c> keeps the preset value, so a file written before this feature (or a reset) reproduces
/// the first-version look exactly. Defaults: theme colors, theme font family and font size, top-center
/// slot and an opacity of 1 (no change to the theme's own alpha). Colors are free-form but strictly
/// <c>#RRGGBB</c>/<c>#AARRGGBB</c>; the font family is free-form but must be a plain family name.
/// </summary>
public sealed record OsdAppearance(
    string? BackgroundColor = null,
    string? ForegroundColor = null,
    double? FontSize = null,
    OsdPlacement Placement = OsdPlacementCatalog.DefaultPlacement,
    double BackgroundOpacity = 1.0,
    string? FontFamily = null)
{
    public const double MinFontSize = 12;
    public const double MaxFontSize = 40;
    public const double MinOpacity = 0.2;
    public const double MaxOpacity = 1.0;

    public static OsdAppearance Default { get; } = new();

    /// <summary>True when the user picked a background color that overrides the preset.</summary>
    public bool HasCustomBackground => BackgroundColor is not null;

    /// <summary>True when the user picked a foreground (text) color that overrides the preset.</summary>
    public bool HasCustomForeground => ForegroundColor is not null;

    /// <summary>True when the user typed a font family that overrides the preset.</summary>
    public bool HasCustomFontFamily => FontFamily is not null;

    /// <summary>Throws when a field is outside its documented range. Used by the editors.</summary>
    public void Validate()
    {
        if (BackgroundColor is not null && !OsdColorRules.IsValidColor(BackgroundColor))
            throw new ArgumentException("背景色必须是 #RRGGBB 或 #AARRGGBB。", nameof(BackgroundColor));
        if (ForegroundColor is not null && !OsdColorRules.IsValidColor(ForegroundColor))
            throw new ArgumentException("前景色必须是 #RRGGBB 或 #AARRGGBB。", nameof(ForegroundColor));
        if (FontSize is { } size && (!double.IsFinite(size) || size < MinFontSize || size > MaxFontSize))
            throw new ArgumentOutOfRangeException(nameof(FontSize),
                $"OSD 字号必须在 {MinFontSize}–{MaxFontSize} 之间。");
        if (!double.IsFinite(BackgroundOpacity) || BackgroundOpacity < MinOpacity || BackgroundOpacity > MaxOpacity)
            throw new ArgumentOutOfRangeException(nameof(BackgroundOpacity),
                $"OSD 背景不透明度必须在 {MinOpacity}–{MaxOpacity} 之间。");
        if (!Enum.IsDefined(Placement))
            throw new ArgumentOutOfRangeException(nameof(Placement), "未知的 OSD 位置。");
        if (FontFamily is not null && !OsdFontFamilyRules.IsSafeFontFamily(FontFamily))
            throw new ArgumentException(
                $"字体名称只能包含字母、数字、空格及 - _ . + ( ) '，且不超过 {OsdFontFamilyRules.MaxLength} 个字符。",
                nameof(FontFamily));
    }

    /// <summary>
    /// Never throws: every invalid field is replaced by its default (theme color / theme font family
    /// and size / default slot / opacity 1). A description of each reset is appended to
    /// <paramref name="notes"/>.
    /// </summary>
    public OsdAppearance Sanitize(List<string>? notes = null)
    {
        string? background = null;
        if (BackgroundColor is { } rawBackground)
        {
            background = OsdColorRules.NormalizeColor(rawBackground);
            if (background is null)
                notes?.Add($"无效的 OSD 背景色 \"{Shorten(rawBackground)}\"，已回退跟随主题");
        }

        string? foreground = null;
        if (ForegroundColor is { } rawForeground)
        {
            foreground = OsdColorRules.NormalizeColor(rawForeground);
            if (foreground is null)
                notes?.Add($"无效的 OSD 前景色 \"{Shorten(rawForeground)}\"，已回退跟随主题");
        }

        var fontSize = FontSize;
        if (fontSize is { } size && (!double.IsFinite(size) || size < MinFontSize || size > MaxFontSize))
        {
            notes?.Add($"OSD 字号 {Shorten(size.ToString(System.Globalization.CultureInfo.InvariantCulture))} 超出范围 " +
                $"{MinFontSize}–{MaxFontSize}，已回退主题字号");
            fontSize = null;
        }

        var opacity = BackgroundOpacity;
        if (!double.IsFinite(opacity) || opacity < MinOpacity || opacity > MaxOpacity)
        {
            notes?.Add($"OSD 背景不透明度 {Shorten(opacity.ToString(System.Globalization.CultureInfo.InvariantCulture))} " +
                $"超出范围 {MinOpacity}–{MaxOpacity}，已回退 1.0");
            opacity = Default.BackgroundOpacity;
        }

        var placement = Placement;
        if (!Enum.IsDefined(placement))
        {
            notes?.Add("未知的 OSD 位置，已回退默认位置");
            placement = OsdPlacementCatalog.DefaultPlacement;
        }

        string? fontFamily = null;
        if (FontFamily is { } rawFontFamily)
        {
            fontFamily = OsdFontFamilyRules.NormalizeFontFamily(rawFontFamily);
            if (fontFamily is null)
                notes?.Add($"无效的 OSD 字体名称 \"{Shorten(rawFontFamily)}\"，已回退主题字体");
        }

        return new OsdAppearance(background, foreground, fontSize, placement, opacity, fontFamily);
    }

    /// <summary>One-line description of the non-default overrides, for OSD/log summaries.</summary>
    public string Describe()
    {
        var normalized = Sanitize();
        List<string> parts = [];
        if (normalized.BackgroundColor is not null)
            parts.Add($"背景 {normalized.BackgroundColor}");
        if (normalized.ForegroundColor is not null)
            parts.Add($"前景 {normalized.ForegroundColor}");
        if (normalized.FontSize is { } size)
            parts.Add($"字号 {size:0.#}");
        if (normalized.FontFamily is { } fontFamily)
            parts.Add($"字体 {fontFamily}");
        if (normalized.BackgroundOpacity < 1)
            parts.Add($"不透明度 {normalized.BackgroundOpacity:P0}");
        parts.Add($"位置 {OsdPlacementCatalog.DisplayName(normalized.Placement)}");
        return string.Join(" · ", parts);
    }

    /// <summary>Bounds any value echoed into a warning so a hostile file cannot flood the log.</summary>
    private static string Shorten(string value) =>
        value.Length <= 24 ? value : value[..24] + "…";
}

/// <summary>
/// Pure placement math for the OSD window: converts a <see cref="OsdPlacement"/> plus the cursor
/// monitor work area (physical pixels) into the popup's top-left corner. <see cref="TopBandRatio"/>
/// preserves the first-version position for <see cref="OsdPlacement.TopCenter"/>, so the default
/// layout stays pixel-identical to the previous release.
/// </summary>
public static class OsdPlacementLayout
{
    /// <summary>Legacy vertical offset for the top row (20% of the work area height).</summary>
    public const double TopBandRatio = 0.20;

    /// <summary>Gap between the popup and the work-area edge for the left/right/bottom slots.</summary>
    public const double EdgeMarginDips = 16;

    /// <summary>
    /// Returns the top-left corner in physical pixels, clamped so the popup always stays inside the
    /// work area (a popup taller than the work area is pinned to the top-left corner instead).
    /// </summary>
    public static (double X, double Y) Compute(
        OsdPlacement placement,
        double workAreaLeft,
        double workAreaTop,
        double workAreaWidth,
        double workAreaHeight,
        double width,
        double height,
        double scale)
    {
        var effectiveScale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var pixelWidth = Math.Max(1, width * effectiveScale);
        var pixelHeight = Math.Max(1, height * effectiveScale);
        var margin = EdgeMarginDips * effectiveScale;
        var slot = OsdPlacementCatalog.Normalize(placement);

        var x = slot switch
        {
            OsdPlacement.TopLeft or OsdPlacement.MiddleLeft or OsdPlacement.BottomLeft =>
                workAreaLeft + margin,
            OsdPlacement.TopRight or OsdPlacement.MiddleRight or OsdPlacement.BottomRight =>
                workAreaLeft + workAreaWidth - pixelWidth - margin,
            _ => workAreaLeft + (workAreaWidth - pixelWidth) / 2
        };
        var y = slot switch
        {
            OsdPlacement.TopLeft or OsdPlacement.TopCenter or OsdPlacement.TopRight =>
                workAreaTop + workAreaHeight * TopBandRatio,
            OsdPlacement.BottomLeft or OsdPlacement.BottomCenter or OsdPlacement.BottomRight =>
                workAreaTop + workAreaHeight - pixelHeight - margin,
            _ => workAreaTop + (workAreaHeight - pixelHeight) / 2
        };

        var maxX = Math.Max(workAreaLeft, workAreaLeft + workAreaWidth - pixelWidth);
        var maxY = Math.Max(workAreaTop, workAreaTop + workAreaHeight - pixelHeight);
        return (Math.Clamp(x, workAreaLeft, maxX), Math.Clamp(y, workAreaTop, maxY));
    }
}
