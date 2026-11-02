using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class HardwareCapabilitiesTests
{
    [Fact]
    public void OptionalCapabilitiesAreNotOpenBeforeReadWriteReadbackProof()
    {
        var optional = HardwareCapabilities.Demo.Items
            .Where(item => item.Id is "brightness" or "audio")
            .ToArray();

        Assert.NotEmpty(optional);
        Assert.All(optional, item =>
        {
            Assert.False(item.IsOpen);
            Assert.Equal(CapabilityState.NotVerified, item.State);
        });
    }

    [Fact]
    public void ExcludedCapabilitiesAreNotListed()
    {
        var ids = HardwareCapabilities.Demo.Items.Select(item => item.Id).ToArray();

        Assert.DoesNotContain("keyboard-backlight", ids);
        Assert.DoesNotContain("touchpad-fn", ids);
        Assert.Contains("power-switch", ids);
    }

    [Fact]
    public void ConfirmedStateIsTheOnlyOpenState()
    {
        Assert.True(new HardwareCapability("x", "x", CapabilityState.Confirmed, "proof").IsOpen);
        Assert.False(new HardwareCapability("x", "x", CapabilityState.NotVerified, "no proof").IsOpen);
        Assert.False(new HardwareCapability("x", "x", CapabilityState.Unsupported, "missing").IsOpen);
    }

    [Fact]
    public void CapabilityIdsAreTheKnownReadOnlySet()
    {
        var ids = HardwareCapabilities.Demo.Items.Select(item => item.Id).ToArray();

        Assert.Equal(new[] { "audio", "brightness", "power-switch" }, ids.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void ReadOnlyIndicatorRowsStayUnverifiedAndSayNotOpen()
    {
        var rows = HardwareCapabilities.Demo.Items
            .Where(item => item.Id is "brightness" or "audio")
            .ToArray();

        Assert.All(rows, row =>
        {
            Assert.Equal(CapabilityState.NotVerified, row.State);
            Assert.False(row.IsOpen);
            Assert.Contains("未开放", row.Details);
            Assert.DoesNotContain("已确认", row.Details);
        });
    }
}
