using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class OsdAppearanceTests
{
    [Fact]
    public void DefaultsAreThemeColorsThemeFontSizeTopCenterAndFullOpacity()
    {
        var appearance = OsdAppearance.Default;

        Assert.Null(appearance.BackgroundColor);
        Assert.Null(appearance.ForegroundColor);
        Assert.Null(appearance.FontSize);
        Assert.Equal(OsdPlacement.TopCenter, appearance.Placement);
        Assert.Equal(1.0, appearance.BackgroundOpacity);
        Assert.False(appearance.HasCustomBackground);
        Assert.False(appearance.HasCustomForeground);
        appearance.Validate();
    }

    [Theory]
    [InlineData(OsdThemePreset.Default)]
    [InlineData(OsdThemePreset.Light)]
    [InlineData(OsdThemePreset.Dark)]
    public void DefaultAppearanceResolvesToTheUnchangedPreset(OsdThemePreset preset)
    {
        Assert.Equal(OsdThemeCatalog.Resolve(preset), OsdThemeCatalog.Resolve(preset, OsdAppearance.Default));
        Assert.Equal(OsdThemeCatalog.Resolve(preset), OsdThemeCatalog.Resolve(preset, null));
    }

    [Theory]
    [InlineData("#FFF5F5F5", true)]
    [InlineData("#F5F5F5", true)]
    [InlineData(" #ffF5F5 ", true)]
    [InlineData("#12345", false)]
    [InlineData("F5F5F5", false)]
    [InlineData("#GGGGGGG", false)]
    [InlineData("#FF00FF00FF", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyStrictHexColorsAreAccepted(string? color, bool expected)
    {
        Assert.Equal(expected, OsdColorRules.IsValidColor(color));
        Assert.Equal(expected, OsdColorRules.NormalizeColor(color) is not null);
    }

    [Fact]
    public void ColorNormalizationTrimsButKeepsTheValue()
    {
        Assert.Equal("#FF00FF", OsdColorRules.NormalizeColor("  #FF00FF  "));
        Assert.Equal("#D9212121", OsdColorRules.NormalizeColor("#D9212121"));
    }

    [Fact]
    public void OpacityScalesTheAlphaAndIsANoOpAtOne()
    {
        // The legacy default color must survive byte-identically, otherwise old files would drift.
        Assert.Equal("#D9212121", OsdColorRules.WithOpacity("#D9212121", 1));
        Assert.Equal("#FFFFFFFF", OsdColorRules.WithOpacity("#FFFFFFFF", 1));
        Assert.Equal("#6D212121", OsdColorRules.WithOpacity("#D9212121", 0.5));
        // A color without an alpha channel stays untouched at full opacity (no silent rewrite).
        Assert.Equal("#212121", OsdColorRules.WithOpacity("#212121", 1));
        Assert.Equal("#80212121", OsdColorRules.WithOpacity("#212121", 0.5));
        Assert.Equal("#00212121", OsdColorRules.WithOpacity("#212121", 0));
        // Opacity above 1 is clamped instead of producing an invalid color.
        Assert.Equal("#212121", OsdColorRules.WithOpacity("#212121", 4));
        Assert.Throws<ArgumentException>(() => OsdColorRules.WithOpacity("not-a-color", 0.5));
    }

    [Fact]
    public void SanitizeResetsEveryInvalidFieldAndDescribesWhy()
    {
        var broken = new OsdAppearance(
            BackgroundColor: "red; drop table",
            ForegroundColor: "#12345",
            FontSize: 200,
            Placement: (OsdPlacement)42,
            BackgroundOpacity: 0.01);
        List<string> notes = [];

        var sanitized = broken.Sanitize(notes);

        Assert.Equal(OsdAppearance.Default, sanitized);
        Assert.Equal(5, notes.Count);
    }

    [Fact]
    public void SanitizeKeepsValidValues()
    {
        var appearance = new OsdAppearance("#CC101010", "#FF00FF00", 20, OsdPlacement.BottomRight, 0.5);
        List<string> notes = [];

        var sanitized = appearance.Sanitize(notes);

        Assert.Equal(appearance, sanitized);
        Assert.Empty(notes);
    }

    [Fact]
    public void ValidatingRejectsOutOfRangeValuesAndAcceptsTheBoundaries()
    {
        new OsdAppearance("#FF000000", "#FFFFFFFF", OsdAppearance.MinFontSize, OsdPlacement.TopLeft,
            OsdAppearance.MinOpacity).Validate();
        new OsdAppearance("#000000", "#FFFFFF", OsdAppearance.MaxFontSize, OsdPlacement.BottomRight,
            OsdAppearance.MaxOpacity).Validate();

        Assert.Throws<ArgumentException>(() => new OsdAppearance(BackgroundColor: "FF000000").Validate());
        Assert.Throws<ArgumentException>(() => new OsdAppearance(ForegroundColor: "#1234").Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OsdAppearance(FontSize: 11.9).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OsdAppearance(FontSize: 40.1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OsdAppearance(FontSize: double.NaN).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OsdAppearance(BackgroundOpacity: 0.19).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OsdAppearance(BackgroundOpacity: 1.01).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OsdAppearance(Placement: (OsdPlacement)99).Validate());
    }

    [Fact]
    public void DescribeSummarizesOnlyTheOverrides()
    {
        Assert.Contains("位置 顶部居中", OsdAppearance.Default.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("背景", OsdAppearance.Default.Describe(), StringComparison.Ordinal);

        var described = new OsdAppearance("#FF1B1B1B", "#FFFFFFFF", 30, OsdPlacement.BottomLeft, 0.6).Describe();
        Assert.Contains("#FF1B1B1B", described, StringComparison.Ordinal);
        Assert.Contains("#FFFFFFFF", described, StringComparison.Ordinal);
        Assert.Contains("30", described, StringComparison.Ordinal);
        Assert.Contains("左下", described, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorCatalogsOnlyContainValidColorsWithDistinctLabels()
    {
        foreach (var option in OsdColorCatalog.Foregrounds.Concat(OsdColorCatalog.Backgrounds))
        {
            if (option.Color is null)
                continue;
            Assert.True(OsdColorRules.IsValidColor(option.Color), option.Color);
        }

        Assert.Single(OsdColorCatalog.Foregrounds, option => option.Color is null);
        Assert.Single(OsdColorCatalog.Backgrounds, option => option.Color is null);
        Assert.Distinct(OsdColorCatalog.Foregrounds.Select(option => option.DisplayName));
        Assert.Distinct(OsdColorCatalog.Backgrounds.Select(option => option.DisplayName));
    }

    [Fact]
    public void FontSizeCatalogCoversTheLegacySizeAndStaysInRange()
    {
        Assert.Contains(OsdTheme.Default.TitleFontSize, OsdFontSizeCatalog.Options);
        Assert.All(OsdFontSizeCatalog.Options, size =>
        {
            Assert.InRange(size, OsdAppearance.MinFontSize, OsdAppearance.MaxFontSize);
            new OsdAppearance(FontSize: size).Validate();
        });
    }

    [Fact]
    public void FreeFormColorsAreAcceptedAsLongAsTheyStayStrictHex()
    {
        // Any #RRGGBB / #AARRGGBB is allowed, not just the catalog presets.
        foreach (var color in new[] { "#123456", "#ABCDEF", "#00FF00", "#80123456", "#FF00FF00", "#00000000" })
        {
            Assert.True(OsdColorRules.IsValidColor(color), color);
            Assert.Equal(color, OsdColorRules.NormalizeColor(color));
            new OsdAppearance(BackgroundColor: color).Validate();
            new OsdAppearance(ForegroundColor: color).Validate();
        }

        // The input length is capped at #AARRGGBB, so a long string can never be an accepted color.
        Assert.Equal(9, OsdColorRules.MaxInputLength);
        Assert.False(OsdColorRules.IsValidColor("#FF00FF00FF"));
        Assert.False(OsdColorRules.IsValidColor("#" + new string('0', 32)));
        Assert.Null(OsdColorRules.NormalizeColor("#FF00FF00FF"));
    }

    [Theory]
    [InlineData("Consolas", "Consolas")]
    [InlineData("  Microsoft YaHei UI  ", "Microsoft YaHei UI")]
    [InlineData("Segoe UI Semilight", "Segoe UI Semilight")]
    [InlineData("Noto Sans CJK SC", "Noto Sans CJK SC")]
    [InlineData("Cascadia-Mono_Code.1", "Cascadia-Mono_Code.1")]
    [InlineData("微软雅黑", "微软雅黑")]
    public void PlainFontFamilyNamesAreKeptVerbatim(string input, string expected)
    {
        Assert.Equal(expected, OsdFontFamilyRules.NormalizeFontFamily(input));
        Assert.True(OsdFontFamilyRules.IsSafeFontFamily(input));
        var appearance = new OsdAppearance(FontFamily: input);
        Assert.True(appearance.HasCustomFontFamily);
        appearance.Validate();
        Assert.Equal(expected, appearance.Sanitize().FontFamily);
    }

    [Theory]
    [InlineData(@"..\..\Windows\Fonts\evil.ttf")]
    [InlineData(@"C:\Windows\Fonts\arial.ttf")]
    [InlineData("/usr/share/fonts/x.ttf")]
    [InlineData("file:///C:/temp/evil.ttf")]
    [InlineData("fonts#evil")]
    [InlineData("evil%2ettf")]
    [InlineData("Segoe UI, Arial")]
    [InlineData("semi;bold")]
    [InlineData("font\u0000name")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void PathLikeOrPunctuatedFontNamesAreRejected(string? input)
    {
        Assert.Null(OsdFontFamilyRules.NormalizeFontFamily(input));
        Assert.False(OsdFontFamilyRules.IsSafeFontFamily(input));
        Assert.Equal(OsdFontFamilyRules.DefaultFontFamily,
            OsdFontFamilyRules.Resolve(input, _ => true));
    }

    [Fact]
    public void FontFamilyLengthIsCapped()
    {
        var justFits = new string('a', OsdFontFamilyRules.MaxLength);
        var tooLong = new string('a', OsdFontFamilyRules.MaxLength + 1);

        Assert.Equal(justFits, OsdFontFamilyRules.NormalizeFontFamily(justFits));
        Assert.Null(OsdFontFamilyRules.NormalizeFontFamily(tooLong));
        Assert.Throws<ArgumentException>(() => new OsdAppearance(FontFamily: tooLong).Validate());
        Assert.Throws<ArgumentException>(() => new OsdAppearance(FontFamily: @"C:\evil.ttf").Validate());
    }

    [Fact]
    public void UnsafeFontFamilyIsNeverPassedToTheAvailabilityCheck()
    {
        var probed = new List<string>();

        var resolved = OsdFontFamilyRules.Resolve(@"..\evil.ttf", name =>
        {
            probed.Add(name);
            return true;
        });

        Assert.Equal(OsdFontFamilyRules.DefaultFontFamily, resolved);
        Assert.Empty(probed);
    }

    [Fact]
    public void UnknownFontFamilyFallsBackToTheDefaultFamilyWithoutThrowing()
    {
        // The stored value stays a plain string; the view resolves it against the installed fonts.
        var probeCalls = new List<string>();
        var resolved = OsdFontFamilyRules.Resolve("Definitely Not An Installed Font 123", name =>
        {
            probeCalls.Add(name);
            return false;
        });

        Assert.Equal("Definitely Not An Installed Font 123", Assert.Single(probeCalls));
        Assert.Equal(OsdFontFamilyRules.DefaultFontFamily, resolved);
        Assert.Equal("Segoe UI", OsdFontFamilyRules.DefaultFontFamily);

        // A throwing availability check is treated as "not installed", never propagated.
        Assert.Equal(OsdFontFamilyRules.DefaultFontFamily,
            OsdFontFamilyRules.Resolve("Ghost Font", _ => throw new InvalidOperationException("boom")));
        // Without a check the name is passed through unchanged (the editor's preview path).
        Assert.Equal("Ghost Font", OsdFontFamilyRules.Resolve("Ghost Font"));
        Assert.Equal(OsdFontFamilyRules.DefaultFontFamily, OsdFontFamilyRules.Resolve(null));
    }

    [Fact]
    public void InvalidFontFamilyIsResetFieldByFieldAndNoted()
    {
        List<string> notes = [];

        var sanitized = new OsdAppearance("#FF112233", "#FF445566", 22, OsdPlacement.TopLeft, 0.8,
            @"..\Windows\Fonts\evil.ttf").Sanitize(notes);

        Assert.Null(sanitized.FontFamily);
        Assert.Equal("#FF112233", sanitized.BackgroundColor);
        Assert.Equal("#FF445566", sanitized.ForegroundColor);
        Assert.Equal(22, sanitized.FontSize);
        Assert.Equal(OsdPlacement.TopLeft, sanitized.Placement);
        Assert.Equal(0.8, sanitized.BackgroundOpacity);
        Assert.Contains(notes, note => note.Contains("字体", StringComparison.Ordinal));
    }

    [Fact]
    public void DescribeMentionsACustomFontFamily()
    {
        var described = new OsdAppearance(FontFamily: "Consolas").Describe();

        Assert.Contains("字体 Consolas", described, StringComparison.Ordinal);
        Assert.DoesNotContain("字体", OsdAppearance.Default.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPlacementIsDefinedLabelledAndParsed()
    {
        Assert.Equal(9, OsdPlacementCatalog.Placements.Count);
        Assert.Equal(Enum.GetValues<OsdPlacement>().OrderBy(value => value), OsdPlacementCatalog.Placements);
        Assert.Distinct(OsdPlacementCatalog.Placements.Select(OsdPlacementCatalog.DisplayName));
        Assert.Equal(OsdPlacement.TopCenter, OsdPlacementCatalog.DefaultPlacement);

        foreach (var placement in OsdPlacementCatalog.Placements)
        {
            Assert.Equal(placement, OsdPlacementCatalog.Normalize(placement));
            Assert.True(OsdPlacementCatalog.TryParse(placement.ToString(), out var parsed));
            Assert.Equal(placement, parsed);
            Assert.True(OsdPlacementCatalog.TryParse(((int)placement).ToString(), out var numeric));
            Assert.Equal(placement, numeric);
        }

        Assert.True(OsdPlacementCatalog.TryParse("bottomright", out var lowerCase));
        Assert.Equal(OsdPlacement.BottomRight, lowerCase);
        Assert.False(OsdPlacementCatalog.TryParse("Middle", out var unknownName));
        Assert.Equal(OsdPlacement.TopCenter, unknownName);
        Assert.False(OsdPlacementCatalog.TryParse("42", out var outOfRange));
        Assert.Equal(OsdPlacement.TopCenter, outOfRange);
        Assert.False(OsdPlacementCatalog.TryParse("-1", out _));
        Assert.False(OsdPlacementCatalog.TryParse(null, out _));
        Assert.False(OsdPlacementCatalog.TryParse("  ", out _));
        Assert.Equal(OsdPlacement.TopCenter, OsdPlacementCatalog.Normalize((OsdPlacement)42));
        Assert.Equal(OsdPlacementCatalog.DisplayName(OsdPlacement.TopCenter),
            OsdPlacementCatalog.DisplayName((OsdPlacement)42));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040, 400, 100, 1.0)]
    [InlineData(0, 0, 2560, 1400, 400, 100, 1.5)]
    [InlineData(-2560, 120, 2560, 1400, 320, 80, 1.25)]
    public void TopCenterKeepsTheFirstVersionPosition(
        double left, double top, double width, double height, double itemWidth, double itemHeight, double scale)
    {
        var (x, y) = OsdPlacementLayout.Compute(
            OsdPlacement.TopCenter, left, top, width, height, itemWidth, itemHeight, scale);

        Assert.Equal(left + (width - itemWidth * scale) / 2, x, 3);
        Assert.Equal(top + height * OsdPlacementLayout.TopBandRatio, y, 3);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040, 400, 100, 1.0)]
    [InlineData(0, 0, 3840, 2160, 620, 220, 2.0)]
    [InlineData(-1280, -40, 1280, 1024, 500, 140, 1.5)]
    public void EveryPlacementStaysInsideTheWorkArea(
        double left, double top, double width, double height, double itemWidth, double itemHeight, double scale)
    {
        foreach (var placement in OsdPlacementCatalog.Placements)
        {
            var (x, y) = OsdPlacementLayout.Compute(
                placement, left, top, width, height, itemWidth, itemHeight, scale);
            Assert.InRange(x, left, left + width - itemWidth * scale);
            Assert.InRange(y, top, top + height - itemHeight * scale);
            Assert.True(double.IsFinite(x) && double.IsFinite(y));
        }
    }

    [Fact]
    public void PlacementColumnsAndRowsLandWhereExpected()
    {
        const double left = 100;
        const double top = 50;
        const double width = 1000;
        const double height = 800;
        const double itemWidth = 200;
        const double itemHeight = 100;
        const double scale = 1;
        var margin = OsdPlacementLayout.EdgeMarginDips * scale;

        var (leftX, _) = OsdPlacementLayout.Compute(OsdPlacement.MiddleLeft, left, top, width, height, itemWidth, itemHeight, scale);
        var (centerX, centerY) = OsdPlacementLayout.Compute(OsdPlacement.MiddleCenter, left, top, width, height, itemWidth, itemHeight, scale);
        var (rightX, _) = OsdPlacementLayout.Compute(OsdPlacement.MiddleRight, left, top, width, height, itemWidth, itemHeight, scale);
        Assert.Equal(left + margin, leftX, 3);
        Assert.Equal(left + (width - itemWidth) / 2, centerX, 3);
        Assert.Equal(left + width - itemWidth - margin, rightX, 3);
        Assert.Equal(top + (height - itemHeight) / 2, centerY, 3);
        Assert.True(leftX < centerX && centerX < rightX);

        var (_, topY) = OsdPlacementLayout.Compute(OsdPlacement.TopLeft, left, top, width, height, itemWidth, itemHeight, scale);
        var (_, bottomY) = OsdPlacementLayout.Compute(OsdPlacement.BottomLeft, left, top, width, height, itemWidth, itemHeight, scale);
        Assert.Equal(top + height * OsdPlacementLayout.TopBandRatio, topY, 3);
        Assert.Equal(top + height - itemHeight - margin, bottomY, 3);
        Assert.True(topY < centerY && centerY < bottomY);
    }

    [Fact]
    public void PopupLargerThanTheWorkAreaIsPinnedToTheCornerAndUnknownSlotsUseTheDefault()
    {
        var (x, y) = OsdPlacementLayout.Compute(
            OsdPlacement.BottomRight, 0, 0, 100, 100, 900, 900, 1);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(
            OsdPlacementLayout.Compute(OsdPlacement.TopCenter, 0, 0, 1920, 1080, 400, 100, 1),
            OsdPlacementLayout.Compute((OsdPlacement)42, 0, 0, 1920, 1080, 400, 100, 1));
        Assert.Equal(
            OsdPlacementLayout.Compute(OsdPlacement.TopCenter, 0, 0, 1920, 1080, 400, 100, 1),
            OsdPlacementLayout.Compute(OsdPlacement.TopCenter, 0, 0, 1920, 1080, 400, 100, 0));
    }
}
