using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class OsdThemeTests
{
    [Fact]
    public void DefaultThemeKeepsTheFirstVersionLookForInfoMessages()
    {
        var style = OsdTheme.Default.Resolve(OsdSeverity.Info);

        Assert.Equal("#D9212121", style.BackgroundColor);
        Assert.Equal("#FFFFFFFF", style.TitleColor);
        Assert.Equal(22, style.TitleFontSize);
        Assert.Equal("Segoe UI", style.FontFamily);
        Assert.False(style.HasAccent);
    }

    [Fact]
    public void SeverityStylesCarryDistinctAccentAndBackgroundColors()
    {
        var success = OsdTheme.Default.Resolve(OsdSeverity.Success);
        var warning = OsdTheme.Default.Resolve(OsdSeverity.Warning);
        var error = OsdTheme.Default.Resolve(OsdSeverity.Error);

        Assert.True(success.HasAccent);
        Assert.True(warning.HasAccent);
        Assert.True(error.HasAccent);
        Assert.Equal(3, new[] { success.AccentColor, warning.AccentColor, error.AccentColor }.Distinct().Count());
        Assert.Equal(3, new[] { success.BackgroundColor, warning.BackgroundColor, error.BackgroundColor }.Distinct().Count());
        // The volatile states are the ones that must stand out from the neutral badge.
        Assert.NotEqual(OsdTheme.Default.BackgroundColor, error.BackgroundColor);
        Assert.Equal(warning.AccentColor, warning.TitleColor);
        Assert.Equal(OsdTheme.Default.BodyColor, error.BodyColor);
    }

    [Fact]
    public void UndefinedSeverityFallsBackToTheInfoStyle()
    {
        var style = OsdTheme.Default.Resolve((OsdSeverity)42);
        var info = OsdTheme.Default.Resolve(OsdSeverity.Info);

        Assert.Equal(info, style);
    }

    [Fact]
    public void CustomThemeColorsAreAppliedToEverySeverity()
    {
        var theme = new OsdTheme("#CC101010", "#FF00FF00", "#FFCCCCCC", 20, 14, "Consolas");
        theme.Validate();

        var error = theme.Resolve(OsdSeverity.Error);
        Assert.Equal("#FFCCCCCC", error.BodyColor);
        Assert.Equal(20, error.TitleFontSize);
        Assert.Equal(14, error.BodyFontSize);
        Assert.Equal("Consolas", error.FontFamily);
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("212121")]
    [InlineData("#GGGGGG")]
    [InlineData("")]
    public void InvalidColorsAreRejected(string background)
    {
        var theme = new OsdTheme(background);
        Assert.Throws<ArgumentException>(theme.Validate);
    }

    [Fact]
    public void OutOfRangeFontsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OsdTheme(TitleFontSize: 200).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OsdTheme(BodyFontSize: 2).Validate());
        Assert.Throws<ArgumentException>(() => new OsdTheme(FontFamily: "  ").Validate());
    }

    [Fact]
    public void DefaultThemeIsValid()
    {
        OsdTheme.Default.Validate();
    }

    [Theory]
    [InlineData(OsdThemePreset.Default)]
    [InlineData(OsdThemePreset.Light)]
    [InlineData(OsdThemePreset.Dark)]
    public void EveryCatalogPresetResolvesToAValidTheme(OsdThemePreset preset)
    {
        var theme = OsdThemeCatalog.Resolve(preset);

        theme.Validate();
        Assert.Equal(preset, theme.Preset);
        // Info keeps the neutral look: no accent bar for any preset.
        var info = theme.Resolve(OsdSeverity.Info);
        Assert.Equal(theme.BackgroundColor, info.BackgroundColor);
        Assert.False(info.HasAccent);
        // Success/warning/error keep distinct accents and tinted backgrounds in every preset.
        var styles = new[]
        {
            theme.Resolve(OsdSeverity.Success),
            theme.Resolve(OsdSeverity.Warning),
            theme.Resolve(OsdSeverity.Error)
        };
        Assert.All(styles, style => Assert.True(style.HasAccent));
        Assert.Equal(3, styles.Select(style => style.AccentColor).Distinct().Count());
        Assert.Equal(3, styles.Select(style => style.BackgroundColor).Distinct().Count());
        Assert.All(styles, style => Assert.Equal(theme.BodyColor, style.BodyColor));
    }

    [Fact]
    public void CatalogPresetsAreOrderedAndLabelled()
    {
        Assert.Equal(new[] { OsdThemePreset.Default, OsdThemePreset.Light, OsdThemePreset.Dark },
            OsdThemeCatalog.Presets);
        Assert.Equal(OsdTheme.Default, OsdThemeCatalog.Resolve(OsdThemePreset.Default));
        Assert.NotEqual(OsdThemeCatalog.Light, OsdThemeCatalog.Dark);
        Assert.Distinct(new[]
        {
            OsdThemeCatalog.DisplayName(OsdThemePreset.Default),
            OsdThemeCatalog.DisplayName(OsdThemePreset.Light),
            OsdThemeCatalog.DisplayName(OsdThemePreset.Dark)
        });
    }

    [Fact]
    public void UnknownPresetValuesAreNormalizedToDefault()
    {
        var invalid = (OsdThemePreset)42;

        Assert.Equal(OsdThemePreset.Default, OsdThemeCatalog.Normalize(invalid));
        Assert.Equal(OsdTheme.Default, OsdThemeCatalog.Resolve(invalid));
        Assert.Equal(OsdThemeCatalog.DisplayName(OsdThemePreset.Default), OsdThemeCatalog.DisplayName(invalid));
        Assert.False(OsdThemeCatalog.TryParse("42", out var parsed));
        Assert.Equal(OsdThemePreset.Default, parsed);
        Assert.True(OsdThemeCatalog.TryParse("dark", out var dark));
        Assert.Equal(OsdThemePreset.Dark, dark);
        Assert.False(OsdThemeCatalog.TryParse("#FF00FF", out _));
        Assert.False(OsdThemeCatalog.TryParse(null, out _));
    }

    [Fact]
    public void CustomForegroundAndBackgroundOverrideThePresetButKeepTheSeverityAccents()
    {
        var appearance = new OsdAppearance("#CC000000", "#FFFF0000", 30, OsdPlacement.BottomRight);

        var theme = OsdThemeCatalog.Resolve(OsdThemePreset.Light, appearance);

        theme.Validate();
        Assert.Equal(OsdThemePreset.Light, theme.Preset);
        Assert.Equal("#CC000000", theme.BackgroundColor);
        Assert.Equal("#FFFF0000", theme.TitleColor);
        Assert.Equal("#FFFF0000", theme.BodyColor);
        Assert.Equal(30, theme.TitleFontSize);
        // The body keeps the preset title/body ratio instead of jumping to the same size.
        Assert.Equal(Math.Round(30 * (15.0 / 22.0), 1), theme.BodyFontSize);
        Assert.Equal("Segoe UI", theme.FontFamily);
        Assert.True(theme.UseCustomBackground);

        var info = theme.Resolve(OsdSeverity.Info);
        Assert.Equal("#CC000000", info.BackgroundColor);
        Assert.False(info.HasAccent);

        var error = theme.Resolve(OsdSeverity.Error);
        Assert.Equal("#CC000000", error.BackgroundColor);
        Assert.True(error.HasAccent);
        Assert.Equal("#FFFF0000", error.BodyColor);
        // Severity stays distinguishable: the accent keeps the preset color, not the user foreground.
        Assert.Equal(OsdThemeCatalog.Light.Resolve(OsdSeverity.Error).AccentColor, error.AccentColor);
    }

    [Fact]
    public void CustomFontSizeOnlyKeepsThePresetColors()
    {
        var theme = OsdThemeCatalog.Resolve(OsdThemePreset.Dark, new OsdAppearance(FontSize: 22));

        Assert.Equal(OsdThemeCatalog.Dark.BackgroundColor, theme.BackgroundColor);
        Assert.Equal(OsdThemeCatalog.Dark.TitleColor, theme.TitleColor);
        Assert.Equal(OsdThemeCatalog.Dark.BodyColor, theme.BodyColor);
        Assert.Equal(22, theme.TitleFontSize);
        Assert.Equal(15, theme.BodyFontSize);
        Assert.False(theme.UseCustomBackground);
    }

    [Fact]
    public void OpacityScalesTheChosenBackgroundForEverySeverity()
    {
        var theme = OsdThemeCatalog.Resolve(OsdThemePreset.Default,
            new OsdAppearance(BackgroundOpacity: 0.5));

        Assert.Equal(OsdColorRules.WithOpacity(OsdTheme.Default.BackgroundColor, 0.5),
            theme.Resolve(OsdSeverity.Info).BackgroundColor);
        Assert.Equal(OsdColorRules.WithOpacity(OsdTheme.SuccessBackgroundColor, 0.5),
            theme.Resolve(OsdSeverity.Success).BackgroundColor);
        // The severity accent bar is untouched by the opacity setting.
        Assert.Equal(OsdTheme.Default.Resolve(OsdSeverity.Success).AccentColor,
            theme.Resolve(OsdSeverity.Success).AccentColor);
    }

    [Fact]
    public void CustomFontFamilyReplacesThePresetFamilyAndStaysAPlainString()
    {
        var theme = OsdThemeCatalog.Resolve(OsdThemePreset.Light, new OsdAppearance(FontFamily: "  Consolas  "));

        theme.Validate();
        Assert.Equal("Consolas", theme.FontFamily);
        // Every severity carries the same (still unvalidated-for-installation) family name.
        Assert.Equal("Consolas", theme.Resolve(OsdSeverity.Info).FontFamily);
        Assert.Equal("Consolas", theme.Resolve(OsdSeverity.Error).FontFamily);
        // An unknown but well-formed family is kept (the view resolves it, never Core).
        var unknown = OsdThemeCatalog.Resolve(OsdThemePreset.Default,
            new OsdAppearance(FontFamily: "Not Installed Font 42"));
        unknown.Validate();
        Assert.Equal("Not Installed Font 42", unknown.FontFamily);
        // Sanitizing an unsafe family keeps the preset family instead of throwing.
        var unsafeTheme = OsdThemeCatalog.Resolve(OsdThemePreset.Dark,
            new OsdAppearance(FontFamily: @"C:\Windows\Fonts\evil.ttf"));
        unsafeTheme.Validate();
        Assert.Equal(OsdThemeCatalog.Dark.FontFamily, unsafeTheme.FontFamily);
    }

    [Fact]
    public void InvalidAppearanceValuesAreSanitizedBeforeTheyReachTheTheme()
    {
        var theme = OsdThemeCatalog.Resolve(OsdThemePreset.Default,
            new OsdAppearance("bogus", "#12", 999, (OsdPlacement)99, 9));

        theme.Validate();
        var info = theme.Resolve(OsdSeverity.Info);
        Assert.Equal(OsdTheme.Default.BackgroundColor, info.BackgroundColor);
        Assert.Equal(OsdTheme.Default.TitleColor, info.TitleColor);
        Assert.Equal(OsdTheme.Default.TitleFontSize, info.TitleFontSize);
    }

    [Fact]
    public void EveryThemeAndPlacementCombinationResolvesAndPositions()
    {
        foreach (var preset in OsdThemeCatalog.Presets)
        {
            foreach (var placement in OsdPlacementCatalog.Placements)
            {
                var appearance = new OsdAppearance(FontSize: 18, Placement: placement, BackgroundOpacity: 0.8);
                var theme = OsdThemeCatalog.Resolve(preset, appearance);
                theme.Validate();
                Assert.Equal(preset, theme.Preset);
                var info = theme.Resolve(OsdSeverity.Info);
                OsdColorRules.NormalizeColor(info.BackgroundColor);
                Assert.True(OsdColorRules.IsValidColor(info.BackgroundColor), info.BackgroundColor);

                var (x, y) = OsdPlacementLayout.Compute(placement, 0, 0, 1920, 1040, 400, 100, 1);
                Assert.InRange(x, 0, 1920 - 400);
                Assert.InRange(y, 0, 1040 - 100);
            }
        }
    }
}
