namespace MiniProToolkit.Core;

/// <summary>
/// The small, fixed set of selectable OSD themes. Only these presets can be persisted. The optional
/// user overrides in <see cref="OsdAppearance"/> are validated strictly: colors must be
/// <c>#RRGGBB</c>/<c>#AARRGGBB</c>, font sizes stay inside 12–40 and the font family must be a plain
/// family name (see <see cref="OsdFontFamilyRules"/>), so free-form text can never reach the brush or
/// font loader as a path or an arbitrary URI.
/// </summary>
public enum OsdThemePreset
{
    /// <summary>First-version look (semi-transparent dark chip, white text).</summary>
    Default = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// Preset catalog: maps the persisted <see cref="OsdThemePreset"/> to a validated
/// <see cref="OsdTheme"/>. Unknown or undefined presets always resolve to
/// <see cref="OsdThemePreset.Default"/> so presentation can never throw.
/// </summary>
public static class OsdThemeCatalog
{
    public static IReadOnlyList<OsdThemePreset> Presets { get; } =
        [OsdThemePreset.Default, OsdThemePreset.Light, OsdThemePreset.Dark];

    public static OsdTheme Default { get; } = OsdTheme.Default;

    /// <summary>Light chip: dark text on a near-opaque white background, darker severity accents.</summary>
    public static OsdTheme Light { get; } = new("#F2FFFFFF", "#FF1F2328", "#FF3C4043", 22, 15, "Segoe UI", OsdThemePreset.Light);

    /// <summary>Opaque dark chip: same layout as the legacy look but fully opaque.</summary>
    public static OsdTheme Dark { get; } = new("#FF1B1B1B", "#FFF5F5F5", "#FFD0D4D9", 22, 15, "Segoe UI", OsdThemePreset.Dark);

    public static OsdTheme Resolve(OsdThemePreset preset) => Normalize(preset) switch
    {
        OsdThemePreset.Light => Light,
        OsdThemePreset.Dark => Dark,
        _ => Default
    };

    /// <summary>
    /// Resolves a preset and then layers the user's <see cref="OsdAppearance"/> overrides on top.
    /// Documented precedence: the theme preset supplies the palette, font family and severity accents;
    /// the user background replaces the chip background of every severity (the accent bar and its
    /// color are kept, so success/warning/error stay distinguishable); the user foreground replaces
    /// the body text color and the neutral <see cref="OsdSeverity.Info"/> title color; the user font
    /// size scales title and body while keeping the preset ratio; the user font family replaces the
    /// preset family (a name that is not installed is resolved to the default family by the view, not
    /// here, so this stays UI-framework free); the opacity multiplies the alpha of whichever
    /// background ended up being used. An appearance of <c>null</c> or an all-default appearance
    /// returns exactly <see cref="Resolve(OsdThemePreset)"/>, so old settings are unchanged.
    /// </summary>
    public static OsdTheme Resolve(OsdThemePreset preset, OsdAppearance? appearance)
    {
        var theme = Resolve(preset);
        var custom = (appearance ?? OsdAppearance.Default).Sanitize();
        if (custom == OsdAppearance.Default)
            return theme;
        // The body keeps the preset's title/body ratio; an unset font size keeps both preset sizes.
        var titleFontSize = custom.FontSize ?? theme.TitleFontSize;
        var bodyFontSize = custom.FontSize is null
            ? theme.BodyFontSize
            : Math.Round(custom.FontSize.Value * (theme.BodyFontSize / theme.TitleFontSize), 1);
        return theme with
        {
            BackgroundColor = custom.BackgroundColor ?? theme.BackgroundColor,
            TitleColor = custom.ForegroundColor ?? theme.TitleColor,
            BodyColor = custom.ForegroundColor ?? theme.BodyColor,
            TitleFontSize = titleFontSize,
            BodyFontSize = bodyFontSize,
            FontFamily = custom.FontFamily ?? theme.FontFamily,
            UseCustomBackground = custom.HasCustomBackground,
            BackgroundOpacity = custom.BackgroundOpacity
        };
    }

    /// <summary>Maps an undefined enum value (e.g. from a hand-edited file) to <see cref="OsdThemePreset.Default"/>.</summary>
    public static OsdThemePreset Normalize(OsdThemePreset preset) =>
        Enum.IsDefined(preset) ? preset : OsdThemePreset.Default;

    /// <summary>Parses a persisted preset name (or an in-range number); never throws.</summary>
    public static bool TryParse(string? value, out OsdThemePreset preset)
    {
        preset = OsdThemePreset.Default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!Enum.TryParse(value.Trim(), ignoreCase: true, out preset))
            return false;
        if (!Enum.IsDefined(preset))
        {
            preset = OsdThemePreset.Default;
            return false;
        }
        return true;
    }

    /// <summary>Short UI label for a preset (unknown values are labelled as the default).</summary>
    public static string DisplayName(OsdThemePreset preset) => Normalize(preset) switch
    {
        OsdThemePreset.Light => "浅色",
        OsdThemePreset.Dark => "深色",
        _ => "默认"
    };
}

/// <summary>
/// Resolved presentation for one OSD message: everything the view needs, expressed as validated
/// <c>#RRGGBB</c> / <c>#AARRGGBB</c> strings so this logic stays free of any UI framework type.
/// </summary>
public sealed record OsdSeverityStyle(
    string BackgroundColor,
    string AccentColor,
    string TitleColor,
    string BodyColor,
    double TitleFontSize,
    double BodyFontSize,
    string FontFamily)
{
    /// <summary>
    /// <see cref="OsdSeverity.Info"/> keeps the legacy look (no accent bar, original background).
    /// </summary>
    public bool HasAccent => !string.Equals(AccentColor, OsdTheme.TransparentColor, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// OSD theme: neutral colors plus small per-severity accents. <see cref="Default"/> reproduces the
/// first-version appearance for <see cref="OsdSeverity.Info"/> messages.
/// </summary>
public sealed record OsdTheme(
    string BackgroundColor = "#D9212121",
    string TitleColor = "#FFFFFFFF",
    string BodyColor = "#FFE8EAED",
    double TitleFontSize = 22,
    double BodyFontSize = 15,
    string FontFamily = "Segoe UI",
    OsdThemePreset Preset = OsdThemePreset.Default,
    bool UseCustomBackground = false,
    double BackgroundOpacity = 1.0)
{
    public const string TransparentColor = "#00000000";
    public const string SuccessAccentColor = "#FF81C995";
    public const string WarningAccentColor = "#FFFDD663";
    public const string ErrorAccentColor = "#FFF28B82";
    public const string SuccessBackgroundColor = "#D91C2A20";
    public const string WarningBackgroundColor = "#D92B2410";
    public const string ErrorBackgroundColor = "#D92B1412";

    public static OsdTheme Default { get; } = new();

    /// <summary>
    /// Resolves the colors and font sizes for <paramref name="severity"/>. Never throws for an
    /// undefined enum value (unknown severities fall back to <see cref="OsdSeverity.Info"/>).
    /// </summary>
    public OsdSeverityStyle Resolve(OsdSeverity severity)
    {
        if (severity is OsdSeverity.Success or OsdSeverity.Warning or OsdSeverity.Error)
        {
            var (background, accent) = SeverityAccent(severity);
            // A user-picked background replaces the severity tint; the accent bar stays per-severity.
            if (UseCustomBackground)
                background = BackgroundColor;
            return new OsdSeverityStyle(
                OsdColorRules.WithOpacity(background, BackgroundOpacity), accent, accent, BodyColor,
                TitleFontSize, BodyFontSize, FontFamily);
        }
        return new OsdSeverityStyle(
            OsdColorRules.WithOpacity(BackgroundColor, BackgroundOpacity), TransparentColor, TitleColor, BodyColor,
            TitleFontSize, BodyFontSize, FontFamily);
    }

    /// <summary>
    /// Accent bar + title color for a volatile severity. The presets only change the tint and the
    /// contrast of the accent; success/warning/error always stay distinct and readable.
    /// </summary>
    private (string Background, string Accent) SeverityAccent(OsdSeverity severity)
    {
        if (Preset == OsdThemePreset.Light)
            return severity switch
            {
                OsdSeverity.Success => ("#F2E6F4EA", "#FF1E7B34"),
                OsdSeverity.Warning => ("#F2FEF7E0", "#FF9A5B00"),
                _ => ("#F2FCE8E6", "#FFB3261E")
            };
        if (Preset == OsdThemePreset.Dark)
            return severity switch
            {
                OsdSeverity.Success => ("#FF1B2B21", SuccessAccentColor),
                OsdSeverity.Warning => ("#FF2C2712", WarningAccentColor),
                _ => ("#FF2C1618", ErrorAccentColor)
            };
        return severity switch
        {
            OsdSeverity.Success => (SuccessBackgroundColor, SuccessAccentColor),
            OsdSeverity.Warning => (WarningBackgroundColor, WarningAccentColor),
            _ => (ErrorBackgroundColor, ErrorAccentColor)
        };
    }

    public void Validate()
    {
        ValidateColor(BackgroundColor, nameof(BackgroundColor));
        ValidateColor(TitleColor, nameof(TitleColor));
        ValidateColor(BodyColor, nameof(BodyColor));
        if (!double.IsFinite(TitleFontSize) || TitleFontSize is < 10 or > 64)
            throw new ArgumentOutOfRangeException(nameof(TitleFontSize));
        if (!double.IsFinite(BodyFontSize) || BodyFontSize is < 8 or > 48)
            throw new ArgumentOutOfRangeException(nameof(BodyFontSize));
        if (!OsdFontFamilyRules.IsSafeFontFamily(FontFamily))
            throw new ArgumentException("Invalid OSD font family.", nameof(FontFamily));
        if (!double.IsFinite(BackgroundOpacity) ||
            BackgroundOpacity < OsdAppearance.MinOpacity || BackgroundOpacity > OsdAppearance.MaxOpacity)
            throw new ArgumentOutOfRangeException(nameof(BackgroundOpacity));
    }

    private static void ValidateColor(string value, string name)
    {
        if (!OsdColorRules.IsValidColor(value))
            throw new ArgumentException("Color must be #RRGGBB or #AARRGGBB.", name);
    }
}
