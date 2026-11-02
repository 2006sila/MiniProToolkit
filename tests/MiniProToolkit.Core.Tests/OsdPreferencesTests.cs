using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class OsdPreferencesTests
{
    [Fact]
    public void MissingFileFallsBackToEnabledAndDefaultTheme()
    {
        var store = new OsdPreferencesStore(Path.Combine(NewDirectory(), "osd.json"));

        var preferences = store.LoadOrDefault(out var warning);

        Assert.True(preferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Default, preferences.Theme);
        Assert.Null(preferences.Appearance);
        Assert.Equal(OsdAppearance.Default, preferences.ResolvedAppearance);
        Assert.Null(warning);
    }

    [Fact]
    public void AppearanceRoundTripsAndKeepsTheResetSemantics()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        var store = new OsdPreferencesStore(path);
        var appearance = new OsdAppearance("#FF1B1B1B", "#FFFDD663", 30, OsdPlacement.BottomLeft, 0.7);
        store.Save(new OsdPreferences(true, OsdThemePreset.Light, appearance));

        var reloaded = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Equal(appearance, reloaded.Appearance);
        Assert.Equal(OsdThemePreset.Light, reloaded.Theme);
        Assert.Null(warning);
        var json = File.ReadAllText(path);
        Assert.Contains("\"backgroundcolor\": \"#FF1B1B1B\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"foregroundcolor\": \"#FFFDD663\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"fontsize\": 30", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"backgroundopacity\": 0.7", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"placement\": \"BottomLeft\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultAppearanceIsPersistedWithoutAnyAppearanceOverrides()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        new OsdPreferencesStore(path).Save(new OsdPreferences(true, OsdThemePreset.Default, OsdAppearance.Default));

        var reloaded = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Null(reloaded.Appearance);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData(OsdPlacement.TopLeft)]
    [InlineData(OsdPlacement.TopCenter)]
    [InlineData(OsdPlacement.TopRight)]
    [InlineData(OsdPlacement.MiddleLeft)]
    [InlineData(OsdPlacement.MiddleCenter)]
    [InlineData(OsdPlacement.MiddleRight)]
    [InlineData(OsdPlacement.BottomLeft)]
    [InlineData(OsdPlacement.BottomCenter)]
    [InlineData(OsdPlacement.BottomRight)]
    public void EveryPlacementRoundTrips(OsdPlacement placement)
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        new OsdPreferencesStore(path).Save(new OsdPreferences(true, OsdThemePreset.Default,
            new OsdAppearance(Placement: placement)));

        var reloaded = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Equal(placement, reloaded.ResolvedAppearance.Placement);
        Assert.Null(warning);
    }

    [Fact]
    public void LegacyFileWithoutAppearanceFieldsKeepsTheDefaultLayout()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, "{\"version\":1,\"enabled\":true,\"theme\":\"Dark\"}");

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Null(preferences.Appearance);
        Assert.Equal(OsdThemePreset.Dark, preferences.Theme);
        Assert.Equal(OsdPlacement.TopCenter, preferences.ResolvedAppearance.Placement);
        Assert.Null(preferences.ResolvedAppearance.FontSize);
        Assert.Null(warning);
        // The legacy look is untouched: same colors, same 22/15 pt sizes.
        var theme = OsdThemeCatalog.Resolve(preferences.Theme, preferences.ResolvedAppearance);
        Assert.Equal("#FF1B1B1B", theme.Resolve(OsdSeverity.Info).BackgroundColor);
        Assert.Equal(22, theme.TitleFontSize);
        Assert.Equal(15, theme.BodyFontSize);
    }

    [Fact]
    public void InvalidAppearanceFieldsFallBackIndividuallyAndKeepTheSwitch()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, """
            {"version":2,"enabled":false,"theme":"Light","backgroundColor":"#GGGGGG",
             "foregroundColor":"#FFFFFFFF","fontSize":500,"backgroundOpacity":9,"placement":"Moon"}
            """);

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.False(preferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Light, preferences.Theme);
        Assert.Equal("#FFFFFFFF", preferences.ResolvedAppearance.ForegroundColor);
        Assert.Null(preferences.ResolvedAppearance.BackgroundColor);
        Assert.Null(preferences.ResolvedAppearance.FontSize);
        Assert.Equal(1.0, preferences.ResolvedAppearance.BackgroundOpacity);
        Assert.Equal(OsdPlacement.TopCenter, preferences.ResolvedAppearance.Placement);
        Assert.NotNull(warning);
    }

    [Fact]
    public void UnknownPlacementNumberFallsBackToTheDefaultSlot()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, "{\"version\":2,\"enabled\":true,\"placement\":\"42\"}");

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Equal(OsdPlacement.TopCenter, preferences.ResolvedAppearance.Placement);
        Assert.NotNull(warning);
    }

    [Fact]
    public void PlacementNamesAreCaseInsensitive()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, "{\"version\":2,\"enabled\":true,\"placement\":\"bottomright\"}");

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Equal(OsdPlacement.BottomRight, preferences.ResolvedAppearance.Placement);
        Assert.Null(warning);
    }

    [Fact]
    public void SavingSanitizesInvalidValuesSoTheNextLoadIsClean()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        new OsdPreferencesStore(path).Save(new OsdPreferences(true, (OsdThemePreset)77,
            new OsdAppearance("not a color", null, 900, (OsdPlacement)77, 42)));

        var reloaded = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Null(warning);
        Assert.Equal(OsdThemePreset.Default, reloaded.Theme);
        Assert.Null(reloaded.Appearance);
        Assert.Equal(OsdAppearance.Default, reloaded.ResolvedAppearance);
    }

    [Fact]
    public void WrongFieldTypesFallBackToTheWholeDefaultWithAWarning()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, "{\"version\":2,\"enabled\":false,\"fontSize\":\"huge\"}");

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.True(preferences.IsEnabled);
        Assert.Null(preferences.Appearance);
        Assert.NotNull(warning);
    }

    [Fact]
    public void SavedChoiceRoundTrips()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        var store = new OsdPreferencesStore(path);
        store.Save(new OsdPreferences(false, OsdThemePreset.Dark));

        var reloaded = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.False(reloaded.IsEnabled);
        Assert.Equal(OsdThemePreset.Dark, reloaded.Theme);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData(OsdThemePreset.Default)]
    [InlineData(OsdThemePreset.Light)]
    [InlineData(OsdThemePreset.Dark)]
    public void EveryThemePresetRoundTrips(OsdThemePreset preset)
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        new OsdPreferencesStore(path).Save(new OsdPreferences(true, preset));

        var reloaded = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Equal(preset, reloaded.Theme);
        Assert.Null(warning);
        Assert.Contains(preset.ToString(), File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentWithoutTheFieldsKeepsTheDefaults()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "osd.json");
        File.WriteAllText(path, "{\"version\":1}");

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.True(preferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Default, preferences.Theme);
        Assert.Null(warning);
    }

    [Fact]
    public void LegacyFileWithOnlyTheSwitchReadsWithTheDefaultTheme()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, "{\"version\":1,\"enabled\":false}");

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.False(preferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Default, preferences.Theme);
        Assert.Null(warning);
    }

    [Fact]
    public void UnknownThemeFallsBackToDefaultWithAWarningAndKeepsTheSwitch()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, "{\"version\":1,\"enabled\":false,\"theme\":\"NeonPink #FF00FF\"}");

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.False(preferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Default, preferences.Theme);
        Assert.NotNull(warning);
    }

    [Fact]
    public void OutOfRangeNumericThemeFallsBackToDefaultWithAWarning()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, "{\"version\":1,\"enabled\":true,\"theme\":42}");

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Equal(OsdThemePreset.Default, preferences.Theme);
        Assert.NotNull(warning);
    }

    [Fact]
    public void ThemeNamesAreCaseInsensitiveAndUnknownEnumValuesAreNormalizedOnSave()
    {
        var directory = NewDirectory();
        var lowerCase = Path.Combine(directory, "lower.json");
        File.WriteAllText(lowerCase, "{\"version\":1,\"enabled\":true,\"theme\":\"light\"}");

        var lower = new OsdPreferencesStore(lowerCase).LoadOrDefault(out var lowerWarning);
        Assert.Equal(OsdThemePreset.Light, lower.Theme);
        Assert.Null(lowerWarning);

        var undefined = Path.Combine(directory, "undefined.json");
        new OsdPreferencesStore(undefined).Save(new OsdPreferences(true, (OsdThemePreset)99));
        var saved = new OsdPreferencesStore(undefined).LoadOrDefault(out var savedWarning);
        Assert.Equal(OsdThemePreset.Default, saved.Theme);
        Assert.Null(savedWarning);
    }

    [Fact]
    public void CorruptOrUnknownVersionFallsBackWithAWarning()
    {
        var directory = NewDirectory();
        var corrupt = Path.Combine(directory, "corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        var unsupported = Path.Combine(directory, "unsupported.json");
        File.WriteAllText(unsupported, "{\"version\":99,\"enabled\":false,\"theme\":\"Light\"}");

        var corruptPreferences = new OsdPreferencesStore(corrupt).LoadOrDefault(out var corruptWarning);
        var unsupportedPreferences = new OsdPreferencesStore(unsupported).LoadOrDefault(out var unsupportedWarning);

        Assert.True(corruptPreferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Default, corruptPreferences.Theme);
        Assert.NotNull(corruptWarning);
        Assert.True(unsupportedPreferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Default, unsupportedPreferences.Theme);
        Assert.NotNull(unsupportedWarning);
    }

    [Fact]
    public void FontFamilyRoundTripsAndIsWrittenAsAPlainName()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        var appearance = new OsdAppearance(FontFamily: "Cascadia Mono");
        new OsdPreferencesStore(path).Save(new OsdPreferences(true, OsdThemePreset.Dark, appearance));

        var reloaded = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Equal("Cascadia Mono", reloaded.ResolvedAppearance.FontFamily);
        Assert.True(reloaded.ResolvedAppearance.HasCustomFontFamily);
        Assert.Null(warning);
        Assert.Contains("\"fontfamily\": \"Cascadia Mono\"", File.ReadAllText(path),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"version\":1,\"enabled\":true}")]
    [InlineData("{\"version\":1,\"enabled\":false,\"theme\":\"Light\"}")]
    [InlineData("{\"version\":2,\"enabled\":true,\"theme\":\"Dark\",\"placement\":\"BottomRight\"}")]
    [InlineData("{\"version\":2,\"enabled\":true,\"backgroundColor\":\"#FF102030\"}")]
    public void FilesFromOlderSchemasKeepTheThemeFontFamily(string json)
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, json);

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Null(preferences.ResolvedAppearance.FontFamily);
        Assert.Null(warning);
        var theme = OsdThemeCatalog.Resolve(preferences.Theme, preferences.ResolvedAppearance);
        Assert.Equal(OsdThemeCatalog.Resolve(preferences.Theme).FontFamily, theme.FontFamily);
        Assert.Equal("Segoe UI", theme.FontFamily);
        theme.Validate();
    }

    [Fact]
    public void HostileFontFamilyAndColorAreNeverWrittenAndReloadAsDefaults()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        new OsdPreferencesStore(path).Save(new OsdPreferences(true, OsdThemePreset.Default,
            new OsdAppearance(ForegroundColor: "red; drop table", FontFamily: @"..\..\evil.ttf")));

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("evil", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ttf", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(new OsdPreferencesStore(path).LoadOrDefault(out var warning).Appearance);
        Assert.Null(warning);
    }

    [Fact]
    public void InvalidFontFamilyInTheFileResetsOnlyThatField()
    {
        var path = Path.Combine(NewDirectory(), "osd.json");
        File.WriteAllText(path, """
            {"version":2,"enabled":true,"foregroundColor":"#FF00FF00","fontFamily":"C:\\Windows\\Fonts\\evil.ttf"}
            """);

        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);

        Assert.Null(preferences.ResolvedAppearance.FontFamily);
        Assert.Equal("#FF00FF00", preferences.ResolvedAppearance.ForegroundColor);
        Assert.NotNull(warning);
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
