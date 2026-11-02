using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class HardwareSnapshotTests
{
    [Fact]
    public void ErrorSnapshotHasNoHardwareValues()
    {
        var snapshot = HardwareSnapshot.ErrorSnapshot("WMI unavailable");

        Assert.Equal("WMI unavailable", snapshot.Error);
        Assert.Null(snapshot.CpuTemperatureC);
        Assert.Null(snapshot.Fan1Rpm);
        Assert.Equal(PowerMode.Unknown, snapshot.PowerMode);
        Assert.Equal(FanControlMode.Unknown, snapshot.FanControlMode);
    }

    [Theory]
    [InlineData(PowerMode.Balance, 0)]
    [InlineData(PowerMode.Performance, 1)]
    [InlineData(PowerMode.Silence, 2)]
    public void PowerModeValuesMatchAcpiContract(PowerMode mode, int value)
    {
        Assert.Equal(value, (int)mode);
    }

    [Fact]
    public void SnapshotPreservesPackedFanChannelsAsSeparateValues()
    {
        var snapshot = new HardwareSnapshot(
            DateTimeOffset.UtcNow,
            52,
            2180,
            0,
            PowerMode.Balance,
            25,
            40,
            FanControlMode.FirmwareAutomatic,
            null,
            null)
        {
            TargetRpm = 2200
        };

        Assert.Equal(2180, snapshot.Fan1Rpm);
        Assert.Equal(0, snapshot.Fan2Rpm);
        Assert.Equal(2200, snapshot.TargetRpm);
    }
}
