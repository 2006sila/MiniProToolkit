using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Behaviour of the pure colour-card model: two independent colour values, a target switch that
/// never mutates them, alpha-preserving RGB selection, background opacity/transparent round-trips
/// and invalid advanced-hex handling. No WPF, no window, no settings file is touched.
/// </summary>
public sealed class TaskbarColorEditorStateTests
{
    [Fact]
    public void TargetSwitchNeverChangesEitherColourOrItsStoredFormat()
    {
        var state = new TaskbarColorEditorState("#FFFF0000", "#cc101820");

        Assert.Equal(TaskbarColorTarget.Text, state.ActiveTarget);
        Assert.True(state.TrySelectTarget(TaskbarColorTarget.Background));
        Assert.Equal("#FFFF0000", state.TextHex);
        Assert.Equal("#cc101820", state.BackgroundHex);

        Assert.True(state.TrySelectTarget(TaskbarColorTarget.Text));
        Assert.Equal("#FFFF0000", state.TextHex);
        Assert.Equal("#cc101820", state.BackgroundHex);
        Assert.False(state.HasPendingInvalidInput);
    }

    [Fact]
    public void RgbSelectionOnlyEditsTheActiveTargetAndKeepsTheOtherValueIntact()
    {
        var state = new TaskbarColorEditorState("#FFFFFF", "#CC101820");

        Assert.True(state.TryApplyRgb(0x7AE582));
        Assert.Equal("#7AE582", state.TextHex);
        Assert.Equal("#CC101820", state.BackgroundHex);

        Assert.True(state.TrySelectTarget(TaskbarColorTarget.Background));
        Assert.True(state.TryApplyRgb(0xC792EA));
        // The background keeps its existing alpha, only RGB changes.
        Assert.Equal("#CCC792EA", state.BackgroundHex);
        Assert.Equal("#7AE582", state.TextHex);
    }

    [Fact]
    public void RgbSelectionPreservesAnExistingTextAlpha()
    {
        var state = new TaskbarColorEditorState("#80FF0000", "#00000000");

        Assert.True(state.TryApplyRgb(0x00FF00));

        Assert.Equal("#8000FF00", state.TextHex);
        Assert.Equal(0x80, state.TextColor.A);
    }

    [Fact]
    public void TransparentBackgroundKeepsTheRememberedRgbAndRestoresItAboveZero()
    {
        var state = new TaskbarColorEditorState("#E8EAED", "#00000000");
        state.TrySelectTarget(TaskbarColorTarget.Background);

        // Picking a sample at alpha 0 keeps the background fully transparent but records its RGB.
        Assert.True(state.TryApplyRgb(0xFF0000));
        Assert.Equal("#00FF0000", state.BackgroundHex);
        Assert.Equal(0, state.BackgroundColor.A);

        // 完全透明 writes the canonical value and still remembers the chosen RGB.
        state.SetBackgroundFullyTransparent();
        Assert.Equal(TaskbarColorEditorState.FullyTransparentHex, state.BackgroundHex);
        Assert.Equal(0xFF0000u, state.BackgroundRgbMemory);

        Assert.True(state.TrySetBackgroundOpacity(50));
        Assert.Equal("#80FF0000", state.BackgroundHex);
        Assert.Equal(50, state.BackgroundOpacityPercent);
    }

    [Fact]
    public void OpacityZeroFiftyOneHundredRoundTrip()
    {
        var state = new TaskbarColorEditorState("#E8EAED", "#CC101820");

        Assert.True(state.TrySetBackgroundOpacity(0));
        Assert.Equal("#00000000", state.BackgroundHex);
        Assert.Equal(0, state.BackgroundOpacityPercent);
        Assert.True(state.BackgroundIsFullyTransparent);

        Assert.True(state.TrySetBackgroundOpacity(50));
        Assert.Equal(0x80, state.BackgroundColor.A);
        Assert.Equal(50, state.BackgroundOpacityPercent);
        Assert.Equal(0x101820u, state.BackgroundColor.Rgb);

        Assert.True(state.TrySetBackgroundOpacity(100));
        Assert.Equal(0xFF, state.BackgroundColor.A);
        Assert.Equal(100, state.BackgroundOpacityPercent);
        Assert.Equal("#101820", state.BackgroundHex);
        Assert.False(state.BackgroundIsFullyTransparent);
    }

    [Fact]
    public void OpacityRejectsOutOfRangeWithoutChangingAnything()
    {
        var state = new TaskbarColorEditorState("#E8EAED", "#CC101820");

        Assert.False(state.TrySetBackgroundOpacity(-1));
        Assert.False(state.TrySetBackgroundOpacity(101));
        Assert.Equal("#CC101820", state.BackgroundHex);
    }

    [Fact]
    public void PresetMatchingIsAlphaAgnostic()
    {
        var state = new TaskbarColorEditorState("#FFFF0000", "#00000000");
        Assert.True(state.IsActiveRgbSelected(0xFF0000));
        Assert.False(state.IsActiveRgbSelected(0x00FF00));

        state.TrySelectTarget(TaskbarColorTarget.Background);
        Assert.True(state.IsActiveRgbSelected(0x000000));
    }

    [Fact]
    public void InvalidAdvancedHexIsRejectedWithoutTouchingEitherColour()
    {
        var state = new TaskbarColorEditorState("#E8EAED", "#00000000");

        Assert.False(state.TryCommitAdvanced("not-a-colour"));
        Assert.Equal(TaskbarColorEditorState.InvalidHexMessage, state.AdvancedError);
        Assert.True(state.HasPendingInvalidInput);
        // Nothing that a save or the live overlay reads has changed.
        Assert.Equal("#E8EAED", state.TextHex);
        Assert.Equal("#00000000", state.BackgroundHex);

        // Invalid text cannot leak into the other target either.
        Assert.False(state.TrySelectTarget(TaskbarColorTarget.Background));
        Assert.Equal(TaskbarColorTarget.Text, state.ActiveTarget);
        Assert.Equal("#00000000", state.BackgroundHex);

        state.CancelAdvanced();
        Assert.False(state.HasPendingInvalidInput);
        Assert.True(state.TrySelectTarget(TaskbarColorTarget.Background));
    }

    [Fact]
    public void ValidAdvancedHexCommitsOnlyToTheActiveTarget()
    {
        var state = new TaskbarColorEditorState("#E8EAED", "#00000000");

        Assert.True(state.TryCommitAdvanced("#112233"));
        Assert.Equal("#112233", state.TextHex);
        Assert.Equal("#00000000", state.BackgroundHex);

        state.TrySelectTarget(TaskbarColorTarget.Background);
        Assert.True(state.TryCommitAdvanced("#80112233"));
        Assert.Equal("#80112233", state.BackgroundHex);
        Assert.Equal("#112233", state.TextHex);
        Assert.Equal(50, state.BackgroundOpacityPercent);
    }

    [Fact]
    public void RecommittingAnUnchangedValueKeepsItsOriginalSpelling()
    {
        var state = new TaskbarColorEditorState("#e8eaed", "#00000000");

        // Leaving the box without editing must not normalise the stored text and mark it dirty.
        Assert.True(state.TryCommitAdvanced("#e8eaed"));
        Assert.Equal("#e8eaed", state.TextHex);
        Assert.True(state.TryCommitAdvanced("  #e8eaed  "));
        Assert.Equal("#e8eaed", state.TextHex);
    }

    [Fact]
    public void RgbSampleSelectionClearsAPreviousAdvancedError()
    {
        var state = new TaskbarColorEditorState("#E8EAED", "#00000000");
        Assert.False(state.TryCommitAdvanced("zzz"));

        Assert.True(state.TryApplyRgb(0x112233));
        Assert.False(state.HasPendingInvalidInput);
        Assert.Equal("#112233", state.TextHex);
    }

    [Theory]
    [InlineData("#FF0000", 0xFF, 0xFF, 0x00, 0x00)]
    [InlineData("#80FF0000", 0x80, 0xFF, 0x00, 0x00)]
    [InlineData("#00000000", 0x00, 0x00, 0x00, 0x00)]
    [InlineData("  #0A0B0C  ", 0xFF, 0x0A, 0x0B, 0x0C)]
    public void ArgbParsesBothAcceptedSpellings(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(TaskbarArgbColor.TryParse(text, out var color));
        Assert.Equal(new TaskbarArgbColor(a, r, g, b), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("FF0000")]
    [InlineData("#GGGGGG")]
    [InlineData("#FF0000001")]
    [InlineData(null)]
    public void ArgbRejectsEverythingElse(string? text)
    {
        Assert.False(TaskbarArgbColor.TryParse(text, out _));
    }

    [Fact]
    public void ConstructionRejectsAnInvalidInitialColour()
    {
        Assert.Throws<ArgumentException>(() => new TaskbarColorEditorState("red", "#00000000"));
        Assert.Throws<ArgumentException>(() => new TaskbarColorEditorState("#FFFFFF", "#12"));
    }
}
