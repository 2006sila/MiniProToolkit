using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class BrightnessPanelSelectionTests
{
    private const string Panel = "fake-panel";
    private static BrightnessPanelCandidate Brightness(object? value = null, string? name = Panel) =>
        new(InstanceName: name, CurrentBrightness: value);
    private static BrightnessPanelCandidate Connection(object? active, object? technology, string? name = Panel) =>
        new(InstanceName: name, Active: active, OutputTechnology: technology);
    private static BrightnessPanelCandidate Method(string? name = Panel) => new(MethodInstanceName: name);

    // Mirrors the actual staged Read -> connection -> method preflight, with no native
    // dependency and deliberately no write delegate or object capable of performing I/O.
    private static bool CanControl(BrightnessPanelCandidate[] brightness,
        BrightnessPanelCandidate[] connections, BrightnessPanelCandidate[] methods)
    {
        var selected = BrightnessPanelSelection.SelectBrightness(brightness);
        return selected is not null &&
            BrightnessPanelSelection.IsBuiltInPanel(selected.InstanceName, connections) &&
            BrightnessPanelSelection.CountMethodInstances(selected.InstanceName, methods) == 1;
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(11u)]
    [InlineData(13u)]
    [InlineData(0x80000000u)]
    public void UniqueBuiltInPanelMatchesOrdinalIgnoreCase(uint technology)
    {
        Assert.True(CanControl([Brightness((byte)42)],
            [Connection(true, technology, "FAKE-PANEL")], [Method("Fake-Panel")]));
        Assert.True(CanControl([Brightness(0)], [Connection(true, technology)], [Method()]));
        Assert.True(CanControl([Brightness(100u)], [Connection(true, technology)], [Method()]));
    }

    [Theory]
    [InlineData(false, 6u)]
    [InlineData(null, 6u)]
    [InlineData(true, 5u)]
    [InlineData(true, 10u)]
    [InlineData(true, uint.MaxValue)]
    [InlineData(true, null)]
    [InlineData(true, "6")]
    [InlineData("true", 6u)]
    [InlineData(1, 6u)]
    public void ExternalDisconnectedOrUnknownFieldsAreNotWritable(object? active, object? technology)
    {
        Assert.False(CanControl([Brightness(42)], [Connection(active, technology)], [Method()]));
    }

    [Fact]
    public void MultipleActiveConnectionsAndDuplicateConnectionsAreAmbiguous()
    {
        Assert.False(CanControl([Brightness(42)],
            [Connection(true, 6u), Connection(true, 6u, "other")], [Method()]));
        Assert.False(CanControl([Brightness(42)],
            [Connection(true, 6u), Connection(true, 5u, "other")], [Method()]));
        Assert.False(CanControl([Brightness(42)],
            [Connection(true, 6u), Connection(false, 6u, "FAKE-PANEL")], [Method()]));
        Assert.True(CanControl([Brightness(42)],
            [Connection(true, 6u), Connection(false, 5u, "other")], [Method()]));
    }

    [Fact]
    public void ExactSingleMethodAndBrightnessAreRequiredNotFirstMatch()
    {
        Assert.False(CanControl([Brightness(42)], [Connection(true, 6u)], []));
        Assert.False(CanControl([Brightness(42)], [Connection(true, 6u)], [Method(), Method("FAKE-PANEL")]));
        Assert.False(CanControl([Brightness(42)], [Connection(true, 6u)], [Method("other"), Method()]));
        Assert.False(CanControl([Brightness(42)], [Connection(true, 6u)], [Method("other")]));
        Assert.False(CanControl([Brightness(42)], [Connection(true, 6u)], [Method(null)]));
        Assert.False(CanControl([Brightness(42), Brightness(42, "FAKE-PANEL")], [Connection(true, 6u)], [Method()]));
        Assert.False(CanControl([Brightness(42), Brightness(null, "other")], [Connection(true, 6u)], [Method()]));
        Assert.False(CanControl([], [Connection(true, 6u)], [Method()]));
    }

    [Fact]
    public void DifferentOrMissingConnectionIdentityCannotBorrowInternalTechnology()
    {
        foreach (var name in new string?[] { null, "", " ", "other", "fake-panel-extra", " fake-panel" })
            Assert.False(CanControl([Brightness(42)], [Connection(true, 6u, name)], [Method()]));
        Assert.False(CanControl([Brightness(42)], [], [Method()]));
        Assert.False(CanControl([Brightness(42)],
            [Connection(true, 6u), Connection(null, 5u, "other")], [Method()]));
        foreach (var name in new string?[] { null, "", " " })
            Assert.Null(BrightnessPanelSelection.SelectBrightness([Brightness(42, name)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    [InlineData(uint.MaxValue)]
    [InlineData("42")]
    [InlineData(42d)]
    [InlineData(true)]
    public void MissingOutOfRangeAndWrongTypeReadbackNeverBecomeZero(object? value)
    {
        Assert.Null(BrightnessPanelSelection.CurrentPercent(value));
        Assert.Null(BrightnessPanelSelection.SelectBrightness([Brightness(value)]));
        Assert.False(CanControl([Brightness(value)], [Connection(true, 6u)], [Method()]));
    }

    [Fact]
    public void SignedInternalTechnologyIsSupportedButWrongNumericTypesAreNot()
    {
        Assert.True(CanControl([Brightness((ushort)42)], [Connection(true, int.MinValue)], [Method()]));
        Assert.False(CanControl([Brightness(42)], [Connection(true, (byte)6)], [Method()]));
        Assert.False(CanControl([Brightness(42)], [Connection(true, -1)], [Method()]));
        Assert.Null(BrightnessPanelSelection.CurrentPercent(42L));
        Assert.False(BrightnessPanelSelection.SameInstance(null, null));
        Assert.False(BrightnessPanelSelection.SameInstance("panel-i", "panel-İ"));
    }

    [Fact]
    public void MissingOrChangedReadbackCannotConfirmWrite()
    {
        var selected = BrightnessPanelSelection.SelectBrightness([Brightness(42)])!;
        foreach (var values in new BrightnessPanelCandidate[][]
        {
            [], [Brightness(null)], [Brightness(101)], [Brightness(42, "other")],
            [Brightness(42), Brightness(42, "FAKE-PANEL")]
        })
        {
            var readback = BrightnessPanelSelection.SelectBrightness(values);
            var same = readback is not null && BrightnessPanelSelection.SameInstance(
                selected.InstanceName, readback.InstanceName);
            var result = SystemControlVerification.VerifyBrightness(42, same
                ? BrightnessReading.FromPercent(BrightnessPanelSelection.CurrentPercent(readback!.CurrentBrightness))
                : BrightnessReading.Unavailable());
            Assert.NotEqual(SystemControlWriteState.Succeeded, result.State);
        }
    }
}
