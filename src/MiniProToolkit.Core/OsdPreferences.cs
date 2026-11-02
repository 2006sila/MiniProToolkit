using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniProToolkit.Core;

/// <summary>
/// Minimal persisted OSD preference: the on/off switch, the theme preset and the optional
/// <see cref="OsdAppearance"/> overrides (foreground/background color, background opacity, font size,
/// font family and screen position). Turning the OSD off is display-only: the control paths still run
/// and the fan/Guardian state is never affected, the popup is just silenced. The theme is likewise
/// presentation only and is restricted to <see cref="OsdThemePreset"/> values, while the appearance
/// colors are free-form but restricted to <c>#RRGGBB</c>/<c>#AARRGGBB</c> strings and the font family
/// to a plain family name (see <see cref="OsdFontFamilyRules"/>); unknown names simply fall back to the
/// theme's family when the popup is created. A missing <see cref="Appearance"/> means "theme
/// defaults", so a file written before the feature existed keeps the first-version layout, sizes,
/// fonts and colors.
/// </summary>
public sealed record OsdPreferences(
    bool IsEnabled,
    OsdThemePreset Theme = OsdThemePreset.Default,
    OsdAppearance? Appearance = null,
    IReadOnlyDictionary<string, OsdEventTemplate>? EventTemplates = null)
{
    /// <summary>P1 default: OSD popups are on with the first-version look, which matches v1 behaviour.</summary>
    public static OsdPreferences Default { get; } = new(true);

    /// <summary>Never-null appearance; an absent/null value resolves to the theme defaults.</summary>
    public OsdAppearance ResolvedAppearance => Appearance ?? OsdAppearance.Default;
}

/// <summary>
/// Persisted at <c>%LOCALAPPDATA%\MiniProToolkit\osd.json</c>. A missing file, missing field,
/// unsupported version or unreadable file all fall back to the compatible default
/// (<see cref="OsdPreferences.Default"/>) with a warning instead of throwing, so a bad file can
/// never block startup or fan control. Unknown values are reset field by field: an unknown theme
/// name or an invalid color/font size/font family/opacity/placement only resets that field (with a
/// warning) and keeps the rest of the file, including the on/off switch. Loading and saving never
/// write anything outside this file and never touch hardware.
/// </summary>
public sealed class OsdPreferencesStore
{
    private const int CurrentVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OsdPreferencesStore(string? path = null) => Path = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniProToolkit",
        "osd.json");

    public string Path { get; }

    public OsdPreferences LoadOrDefault(out string? warning)
    {
        warning = null;
        List<string> notes = [];
        try
        {
            if (!File.Exists(Path))
                return OsdPreferences.Default;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), JsonOptions)
                ?? throw new InvalidDataException("OSD preferences are empty.");
            // A missing version is read as the current schema: version was added with the first
            // field, so early/hand-written files stay readable.
            if (document.Version is { } version && (version < 1 || version > CurrentVersion))
                throw new InvalidDataException("Unsupported OSD preferences version.");
            // A missing field keeps the compatible default (OSD enabled, default theme); a theme
            // that is present but unknown only resets the theme, never the on/off switch.
            var theme = OsdThemePreset.Default;
            if (!string.IsNullOrWhiteSpace(document.Theme) &&
                !OsdThemeCatalog.TryParse(document.Theme, out theme))
            {
                theme = OsdThemePreset.Default;
                notes.Add($"未知的 OSD 主题 \"{document.Theme}\"，已回退默认主题");
            }
            var appearance = LoadAppearance(document, notes);
            var templates = OsdEventTemplates.Load(document.EventTemplates, notes);
            warning = notes.Count == 0 ? null : string.Join("；", notes);
            return new OsdPreferences(document.Enabled ?? true, theme, appearance, templates);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or NotSupportedException or ArgumentException)
        {
            notes.Add($"OSD 设置无效，已回退默认（开启、默认主题、默认外观）：{ex.GetBaseException().Message}");
            warning = string.Join("；", notes);
            return OsdPreferences.Default;
        }
    }

    /// <summary>
    /// Reads the appearance fields. Every invalid field falls back to its default and adds a note;
    /// an unknown placement name/number keeps the default slot. Returns <c>null</c> when the file
    /// carries no appearance field at all, so an old file resolves to the pure theme defaults.
    /// </summary>
    private static OsdAppearance? LoadAppearance(Document document, List<string> notes)
    {
        var hasAny = document.BackgroundColor is not null || document.ForegroundColor is not null ||
            document.FontSize is not null || document.BackgroundOpacity is not null ||
            document.Placement is not null || document.FontFamily is not null;
        if (!hasAny)
            return null;

        var placement = OsdPlacementCatalog.DefaultPlacement;
        if (!string.IsNullOrWhiteSpace(document.Placement) &&
            !OsdPlacementCatalog.TryParse(document.Placement, out placement))
        {
            placement = OsdPlacementCatalog.DefaultPlacement;
            notes.Add($"未知的 OSD 位置 \"{document.Placement}\"，已回退默认位置");
        }

        var raw = new OsdAppearance(
            document.BackgroundColor,
            document.ForegroundColor,
            document.FontSize,
            placement,
            document.BackgroundOpacity ?? OsdAppearance.Default.BackgroundOpacity,
            document.FontFamily);
        var appearance = raw.Sanitize(notes);
        return appearance == OsdAppearance.Default ? null : appearance;
    }

    public void Save(OsdPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var directory = System.IO.Path.GetDirectoryName(Path) ??
            throw new InvalidOperationException("OSD preferences path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        var preset = OsdThemeCatalog.Normalize(preferences.Theme);
        // Sanitize so a caller-supplied invalid value can never be written and re-read as "corrupt".
        var appearance = preferences.Appearance?.Sanitize() ?? OsdAppearance.Default;
        try
        {
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(new Document(
                    CurrentVersion,
                    preferences.IsEnabled,
                    preset.ToString(),
                    appearance.BackgroundColor,
                    appearance.ForegroundColor,
                    appearance.FontSize,
                    appearance.BackgroundOpacity,
                    appearance.Placement.ToString(),
                    appearance.FontFamily,
                    JsonSerializer.SerializeToElement(OsdEventTemplates.SanitizeOverrides(preferences.EventTemplates), JsonOptions)), JsonOptions));
            File.Move(temporary, Path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record Document(
        int? Version,
        bool? Enabled = null,
        string? Theme = null,
        string? BackgroundColor = null,
        string? ForegroundColor = null,
        double? FontSize = null,
        double? BackgroundOpacity = null,
        string? Placement = null,
        string? FontFamily = null,
        JsonElement? EventTemplates = null);
}
