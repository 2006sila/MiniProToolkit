using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Pure policy tests for the second-phase interaction features of the desktop floating window:
/// mouse click-through, the runtime safety override, and the two external transforms (disable
/// click-through / restore default position). No window handle, screen, WPF or disk is touched.
/// </summary>
public sealed class FloatingStatusInteractionTests
{
    // WS_EX_LAYERED (WPF's transparent window) and WS_EX_TOOLWINDOW (set by the card itself).
    private const long WsExLayered = 0x00080000;
    private const long WsExToolWindow = 0x00000080;

    [Fact]
    public void OwnedBitsAreExactlyTransparentAndNoActivate() =>
        Assert.Equal(0x08000020L, FloatingStatusInteraction.OwnedExStyleBits);

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]   // settings dialog open suspends the actual click-through
    [InlineData(true, false, true, false)]   // failed external disable keeps the session interactive
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    public void ClickThroughIsOnlyEffectiveWhenTheUserAskedAndNothingOverridesIt(
        bool clickThrough, bool editing, bool safetyOverride, bool expected) =>
        Assert.Equal(expected,
            FloatingStatusInteraction.IsClickThroughEffective(clickThrough, editing, safetyOverride));

    [Fact]
    public void EnablingClickThroughAddsBothOwnedBitsAndKeepsEveryForeignBit()
    {
        var current = WsExLayered | WsExToolWindow;

        var wanted = FloatingStatusInteraction.ComputeExStyle(current, effective: true, baseNoActivate: false);

        Assert.Equal(0x08000020L, wanted & FloatingStatusInteraction.OwnedExStyleBits);
        Assert.Equal(WsExLayered, wanted & WsExLayered);
        Assert.Equal(WsExToolWindow, wanted & WsExToolWindow);
    }

    [Fact]
    public void DisablingClickThroughClearsOnlyTheOwnedBits()
    {
        var current = WsExLayered | WsExToolWindow | FloatingStatusInteraction.OwnedExStyleBits;

        var wanted = FloatingStatusInteraction.ComputeExStyle(current, effective: false, baseNoActivate: false);

        Assert.Equal(0, wanted & FloatingStatusInteraction.OwnedExStyleBits);
        Assert.Equal(WsExLayered, wanted & WsExLayered);
        Assert.Equal(WsExToolWindow, wanted & WsExToolWindow);
    }

    [Fact]
    public void AWindowBornWithNoActivateKeepsThatBitWhenClickThroughTurnsOff()
    {
        // The card is not created with NOACTIVATE, but the helper must not assume that: a window that
        // really was born with it keeps it (the feature only clears what it added itself).
        var current = WsExLayered | FloatingStatusInteraction.OwnedExStyleBits;

        var wanted = FloatingStatusInteraction.ComputeExStyle(current, effective: false, baseNoActivate: true);

        Assert.Equal(FloatingStatusInteraction.WsExNoActivate, wanted & FloatingStatusInteraction.WsExNoActivate);
        Assert.Equal(0, wanted & FloatingStatusInteraction.WsExTransparent);
        Assert.Equal(WsExLayered, wanted & WsExLayered);
    }

    [Fact]
    public void ComputeExStyleNeverRebuildsFromACachedBaseAndIsIdempotent()
    {
        // A bit another component added after the card was created must survive both directions.
        var current = WsExLayered | WsExToolWindow | 0x00000010; // WS_EX_ACCEPTFILES as a stand-in

        var on = FloatingStatusInteraction.ComputeExStyle(current, effective: true, baseNoActivate: false);
        var again = FloatingStatusInteraction.ComputeExStyle(on, effective: true, baseNoActivate: false);
        var off = FloatingStatusInteraction.ComputeExStyle(again, effective: false, baseNoActivate: false);

        Assert.Equal(on, again);
        Assert.Equal(current, off);
        Assert.Equal(0x00000010, off & 0x00000010);
    }

    [Fact]
    public void DisablingClickThroughChangesExactlyThatOneFlag()
    {
        var saved = FloatingStatusSettings.Default with
        {
            Enabled = true,
            ClickThrough = true,
            SnapToEdges = false,
            IsLocked = true,
            Topmost = false,
            FontSize = 17,
            FontFamily = "Consolas",
            TextColor = "#FFF0F3F6",
            BackgroundColor = "#80202124",
            LayoutMode = TaskbarLayoutMode.Inline,
            Compact = false,
            MonitorDeviceName = @"\\.\DISPLAY3",
            LeftDip = 40,
            TopDip = 50
        };

        var updated = FloatingStatusInteraction.WithoutClickThrough(saved);

        Assert.False(updated.ClickThrough);
        // Everything else - including a position and every unrelated preference - is untouched.
        Assert.Equal(saved.Enabled, updated.Enabled);
        Assert.Equal(saved.SnapToEdges, updated.SnapToEdges);
        Assert.Equal(saved.IsLocked, updated.IsLocked);
        Assert.Equal(saved.Topmost, updated.Topmost);
        Assert.Equal(saved.FontSize, updated.FontSize);
        Assert.Equal(saved.FontFamily, updated.FontFamily);
        Assert.Equal(saved.TextColor, updated.TextColor);
        Assert.Equal(saved.BackgroundColor, updated.BackgroundColor);
        Assert.Equal(saved.LayoutMode, updated.LayoutMode);
        Assert.Equal(saved.Compact, updated.Compact);
        Assert.Equal(saved.MonitorDeviceName, updated.MonitorDeviceName);
        Assert.Equal(saved.LeftDip, updated.LeftDip);
        Assert.Equal(saved.TopDip, updated.TopDip);
        Assert.Equal(saved.Items, updated.Items);
    }

    [Fact]
    public void RestoringTheDefaultPositionTouchesOnlyTheThreePositionFields()
    {
        var saved = FloatingStatusSettings.Default with
        {
            Enabled = false,
            ClickThrough = true,
            SnapToEdges = false,
            IsLocked = true,
            MonitorDeviceName = @"\\.\DISPLAY2",
            LeftDip = -120.5,
            TopDip = 33.3
        };

        var updated = FloatingStatusInteraction.WithoutPosition(saved);

        Assert.Null(updated.MonitorDeviceName);
        Assert.Null(updated.LeftDip);
        Assert.Null(updated.TopDip);
        // A reset never forces the card on and never changes any other preference.
        Assert.False(updated.Enabled);
        Assert.True(updated.ClickThrough);
        Assert.False(updated.SnapToEdges);
        Assert.True(updated.IsLocked);
        Assert.Equal(saved.Items, updated.Items);
        Assert.Equal(saved.FontSize, updated.FontSize);
        Assert.Equal(saved.TextColor, updated.TextColor);
        Assert.Equal(saved.BackgroundColor, updated.BackgroundColor);
        updated.Validate();
        // The cleared position is the documented bottom-right default, 16 DIP from the edges.
        Assert.True(FloatingStatusLayout.TryComputePlacementPixels(
            0, 0, 1920, 1040, 300, 120, 1.0, updated.LeftDip, updated.TopDip, out var placement));
        Assert.Equal(1920 - 300 - 16, placement.X);
        Assert.Equal(1040 - 120 - 16, placement.Y);
    }
}
