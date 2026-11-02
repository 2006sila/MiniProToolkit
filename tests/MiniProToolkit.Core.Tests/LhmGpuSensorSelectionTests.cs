using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Logic tests for the optional GPU temperature selection. They exercise the pure
/// rules only: no LibreHardwareMonitor assembly is loaded and no hardware is touched.
/// </summary>
public sealed class LhmGpuSensorSelectionTests
{
    private static LhmGpuSensorValue Temp(
        string name,
        double? value,
        LhmGpuVendor vendor = LhmGpuVendor.Nvidia,
        string hardware = "NVIDIA GeForce RTX 4060") =>
        new(hardware, vendor, name, value, LhmSensorCategory.Temperature);

    [Theory]
    [InlineData("GPU Core")]
    [InlineData("gpu core")]
    [InlineData("  GPU Core  ")]
    [InlineData("GPU Temperature")]
    public void TemperatureAcceptsTheTwoDocumentedGpuNames(string name)
    {
        var selection = LhmGpuSensorSelector.SelectTemperature([Temp(name, 58.26)]);

        Assert.True(selection.IsSelected);
        Assert.Equal(name.Trim(), selection.SensorName);
        Assert.Equal(58.3, selection.Value);
        Assert.Equal("NVIDIA GeForce RTX 4060", selection.HardwareName);
    }

    [Fact]
    public void GpuCoreIsPreferredOverGpuTemperature()
    {
        var selection = LhmGpuSensorSelector.SelectTemperature([
            Temp("GPU Temperature", 61.4),
            Temp("GPU Core", 58.3)
        ]);

        Assert.Equal("GPU Core", selection.SensorName);
        Assert.Equal(58.3, selection.Value);
    }

    [Fact]
    public void PriorityListKeepsDocumentedOrder()
    {
        Assert.Equal(["GPU Core", "GPU Temperature"], LhmGpuSensorSelector.GpuTemperaturePriority);
    }

    [Theory]
    [InlineData(LhmGpuVendor.Nvidia)]
    [InlineData(LhmGpuVendor.Amd)]
    [InlineData(LhmGpuVendor.Intel)]
    public void EverySupportedGpuVendorIsSelected(LhmGpuVendor vendor)
    {
        var selection = LhmGpuSensorSelector.SelectTemperature([Temp("GPU Core", 55, vendor)]);

        Assert.True(selection.IsSelected);
        Assert.Equal(55, selection.Value);
        Assert.True(LhmGpuSensorSelector.IsSupportedVendor(vendor));
    }

    [Fact]
    public void UnknownHardwareFamilyIsNeverSelected()
    {
        // A board/NVMe/network node cannot become a GPU reading even when its sensor
        // is called "GPU Core": the hardware family is the filter, not the name.
        var selection = LhmGpuSensorSelector.SelectTemperature([
            Temp("GPU Core", 55, LhmGpuVendor.Unknown, "Nuvoton NCT6687D"),
            Temp("GPU Core", 55, LhmGpuVendor.Unknown, "Samsung SSD 990 PRO")
        ]);

        Assert.Equal(LhmGpuSensorSelectionState.NoCandidate, selection.State);
        Assert.Null(selection.Value);
        Assert.False(LhmGpuSensorSelector.IsSupportedVendor(LhmGpuVendor.Unknown));
    }

    [Fact]
    public void NonTemperatureCategoriesAreNeverSelectedAsTemperature()
    {
        // "GPU Core" is also a load and a clock sensor name; only the temperature
        // category may be reported as °C.
        var selection = LhmGpuSensorSelector.SelectTemperature([
            new LhmGpuSensorValue("NVIDIA GeForce RTX 4060", LhmGpuVendor.Nvidia, "GPU Core", 55, LhmSensorCategory.Other),
            new LhmGpuSensorValue("NVIDIA GeForce RTX 4060", LhmGpuVendor.Nvidia, "GPU Core", 2100, LhmSensorCategory.Other)
        ]);

        Assert.Equal(LhmGpuSensorSelectionState.NoCandidate, selection.State);
        Assert.Null(selection.Value);
    }

    [Fact]
    public void OtherGpuTemperatureNamesAreNotSubstituted()
    {
        var selection = LhmGpuSensorSelector.SelectTemperature([
            Temp("GPU Hot Spot", 70),
            Temp("GPU Memory Junction", 70),
            Temp("GPU Fan", 70)
        ]);

        Assert.Equal(LhmGpuSensorSelectionState.NoCandidate, selection.State);
        Assert.Null(selection.SensorName);
        Assert.Null(selection.Value);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(150.1)]
    [InlineData(1000)]
    public void ImplausibleValuesAreRejected(double invalid)
    {
        var selection = LhmGpuSensorSelector.SelectTemperature([Temp("GPU Core", invalid)]);

        Assert.Equal(LhmGpuSensorSelectionState.RejectedValue, selection.State);
        Assert.False(selection.IsSelected);
        Assert.Null(selection.Value);
        Assert.Null(LhmGpuSensorSelector.NormalizeTemperature(invalid));
    }

    [Fact]
    public void RejectedValueFallsThroughToTheNextCandidate()
    {
        // A driver-less GPU reports 0.0 °C; that must not hide a usable reading.
        var selection = LhmGpuSensorSelector.SelectTemperature([
            Temp("GPU Core", 0),
            Temp("GPU Temperature", 66.66)
        ]);

        Assert.True(selection.IsSelected);
        Assert.Equal("GPU Temperature", selection.SensorName);
        Assert.Equal(66.7, selection.Value);
    }

    [Fact]
    public void NullAndEmptyInputsReturnNoCandidate()
    {
        Assert.Equal(LhmGpuSensorSelectionState.NoCandidate,
            LhmGpuSensorSelector.SelectTemperature(null).State);
        Assert.Equal(LhmGpuSensorSelectionState.NoCandidate,
            LhmGpuSensorSelector.SelectTemperature([]).State);
        Assert.Null(LhmGpuSensorSelector.SelectTemperature([]).Value);
    }

    [Fact]
    public void PlausibilityWindowMatchesTheCpuSelector()
    {
        Assert.Equal(LhmSensorSelector.MinimumExclusiveTemperatureC, LhmGpuSensorSelector.MinimumExclusiveTemperatureC);
        Assert.Equal(LhmSensorSelector.MaximumTemperatureC, LhmGpuSensorSelector.MaximumTemperatureC);
        Assert.True(LhmGpuSensorSelector.IsPlausibleTemperature(0.1));
        Assert.True(LhmGpuSensorSelector.IsPlausibleTemperature(150));
        Assert.False(LhmGpuSensorSelector.IsPlausibleTemperature(0));
    }

    [Fact]
    public void LegacyFourArgumentValueDefaultsToOtherCategory()
    {
        var legacy = new LhmGpuSensorValue("NVIDIA GeForce RTX 4060", LhmGpuVendor.Nvidia, "GPU Core", 55);

        Assert.Equal(LhmSensorCategory.Other, legacy.Category);
        Assert.Equal(LhmGpuSensorSelectionState.NoCandidate,
            LhmGpuSensorSelector.SelectTemperature([legacy]).State);
    }
}
