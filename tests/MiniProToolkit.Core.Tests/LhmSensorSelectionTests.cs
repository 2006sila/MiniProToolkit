using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class LhmSensorSelectionTests
{
    private static LhmSensorValue Temperature(string name, double? value) =>
        new(name, value, LhmSensorCategory.Temperature);

    private static LhmSensorValue Power(string name, double? value) =>
        new(name, value, LhmSensorCategory.Power);

    [Theory]
    [InlineData("CPU Package")]
    [InlineData("cpu package")]
    [InlineData("  CPU Package  ")]
    [InlineData("PACKAGE")]
    [InlineData("Package")]
    public void TemperaturePrefersCpuPackageRegardlessOfCaseAndSpacing(string name)
    {
        var selection = LhmSensorSelector.SelectTemperature([
            Temperature("Core Max", 61.4),
            Temperature(name, 58.25)
        ]);

        Assert.True(selection.IsSelected);
        Assert.Equal(58.3, selection.Value);
        Assert.Equal(name.Trim(), selection.SensorName);
    }

    [Theory]
    [InlineData("Core Max", "CPU Core")]
    [InlineData("CPU Core", "Tctl")]
    [InlineData("Tctl", "Tdie")]
    public void TemperatureFallbackOrderIsStable(string preferred, string other)
    {
        var selection = LhmSensorSelector.SelectTemperature([
            Temperature(other, 70),
            Temperature(preferred, 60)
        ]);

        Assert.Equal(preferred, selection.SensorName);
        Assert.Equal(60, selection.Value);
    }

    [Fact]
    public void TemperatureFallsBackToPackageWhenCpuPackageValueIsMissing()
    {
        var selection = LhmSensorSelector.SelectTemperature([
            Temperature("CPU Package", null),
            Temperature("Package", 55.5)
        ]);

        Assert.True(selection.IsSelected);
        Assert.Equal("Package", selection.SensorName);
        Assert.Equal(55.5, selection.Value);
    }

    [Fact]
    public void TemperatureMatchesAmdCoreTctlTdieNamePartially()
    {
        var selection = LhmSensorSelector.SelectTemperature([
            Temperature("Core (Tctl/Tdie)", 64.44)
        ]);

        Assert.True(selection.IsSelected);
        Assert.Equal(64.4, selection.Value);
    }

    [Fact]
    public void EmptyAndNullInputsReturnNoCandidate()
    {
        Assert.Equal(LhmSensorSelectionState.NoCandidate,
            LhmSensorSelector.SelectTemperature([]).State);
        Assert.Equal(LhmSensorSelectionState.NoCandidate,
            LhmSensorSelector.SelectTemperature(null).State);
        Assert.Equal(LhmSensorSelectionState.NoCandidate,
            LhmSensorSelector.SelectPower(null).State);
        Assert.Null(LhmSensorSelector.SelectTemperature([]).Value);
    }

    [Fact]
    public void NoMatchingNameReturnsNoCandidateWithoutAnyValue()
    {
        var selection = LhmSensorSelector.SelectTemperature([
            Temperature("Core #1", 55),
            Temperature("Core Average", 54)
        ]);

        Assert.False(selection.IsSelected);
        Assert.Equal(LhmSensorSelectionState.NoCandidate, selection.State);
        Assert.Null(selection.Value);
        Assert.Null(selection.SensorName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1000)]
    [InlineData(-273)]
    public void OutOfRangeOrNaNExactMatchesAreRejectedThenFallThrough(double invalid)
    {
        var selection = LhmSensorSelector.SelectTemperature([
            Temperature("CPU Package", invalid),
            Temperature("Core Max", 66.66)
        ]);

        Assert.True(selection.IsSelected);
        Assert.Equal("Core Max", selection.SensorName);
        Assert.Equal(66.7, selection.Value);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(1000)]
    public void OnlyInvalidValuesReportRejectedValue(double invalid)
    {
        var selection = LhmSensorSelector.SelectTemperature([Temperature("CPU Package", invalid)]);

        Assert.Equal(LhmSensorSelectionState.RejectedValue, selection.State);
        Assert.False(selection.IsSelected);
        Assert.Null(selection.Value);
    }

    [Fact]
    public void PowerRequiresCpuPackageSensor()
    {
        Assert.True(LhmSensorSelector.SelectPower([
            Power("Package", 15.46)
        ]) is { State: LhmSensorSelectionState.Selected, SensorName: "Package", Value: 15.5 });

        Assert.Equal(LhmSensorSelectionState.NoCandidate,
            LhmSensorSelector.SelectPower([Power("Core #1", 5)]).State);
    }

    [Fact]
    public void PowerIgnoresGpuAndBoardSensors()
    {
        var selection = LhmSensorSelector.SelectPower([
            Power("GPU Package", 40),
            Power("GPU Board Power", 80),
            Power("PSU Package", 120)
        ]);

        Assert.Equal(LhmSensorSelectionState.NoCandidate, selection.State);
        Assert.Null(selection.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1500)]
    [InlineData(double.NaN)]
    public void ImplausiblePowerValuesAreRejected(double invalid)
    {
        var selection = LhmSensorSelector.SelectPower([Power("CPU Package", invalid)]);

        Assert.Equal(LhmSensorSelectionState.RejectedValue, selection.State);
        Assert.Null(selection.Value);
    }

    [Fact]
    public void ValuesKeepOneDecimalInsteadOfTruncatingToInteger()
    {
        Assert.Equal(58.3, LhmSensorSelector.NormalizeTemperature(58.26));
        Assert.Equal(58.3, LhmSensorSelector.NormalizeTemperature(58.3));
        Assert.Equal(0.9, LhmSensorSelector.NormalizePower(0.94));
        Assert.Null(LhmSensorSelector.NormalizeTemperature(200));
        Assert.Null(LhmSensorSelector.NormalizePower(0));
    }

    [Fact]
    public void ZeroTemperatureFromDriverlessSensorIsRejectedInsteadOfReported()
    {
        // Observed on a machine without the optional LHM kernel driver: the CPU
        // sensor exists but reports 0.0 °C. That must not look like a usable
        // cross-check reading.
        var selection = LhmSensorSelector.SelectTemperature([Temperature("Core (Tctl/Tdie)", 0)]);

        Assert.Equal(LhmSensorSelectionState.RejectedValue, selection.State);
        Assert.False(selection.IsSelected);
        Assert.Null(selection.Value);
        Assert.False(LhmSensorSelector.IsPlausibleTemperature(0));
        Assert.False(LhmSensorSelector.IsPlausibleTemperature(-1));
        Assert.True(LhmSensorSelector.IsPlausibleTemperature(0.1));
        Assert.Null(LhmSensorSelector.NormalizeTemperature(0));
    }

    // Regression: a rejected zero reading must fall through to the next candidate
    // instead of hiding the real sensor.
    [Fact]
    public void ZeroPackageTemperatureFallsThroughToNextCandidate()
    {
        var selection = LhmSensorSelector.SelectTemperature([
            Temperature("CPU Package", 0),
            Temperature("Core Max", 66.66)
        ]);

        Assert.True(selection.IsSelected);
        Assert.Equal("Core Max", selection.SensorName);
        Assert.Equal(66.7, selection.Value);
    }

    [Fact]
    public void PriorityListKeepsDocumentedOrder()
    {
        Assert.Equal(
            ["CPU Package", "Package", "Core Max", "CPU Core", "Tctl", "Tdie"],
            LhmSensorSelector.TemperaturePriority);
        Assert.Equal(["CPU Package", "Package"], LhmSensorSelector.PowerPriority);
    }

    // Regression: an Intel style CPU exposes a "CPU Package" temperature and a
    // "Package" power sensor at the same time.
    [Fact]
    public void IntelShapeKeepsTemperatureAndPowerApart()
    {
        LhmSensorValue[] sensors =
        [
            Temperature("CPU Package", 58.3),
            Power("Package", 15.4)
        ];

        var temperature = LhmSensorSelector.SelectTemperature(sensors);
        var power = LhmSensorSelector.SelectPower(sensors);

        Assert.Equal(LhmSensorSelectionState.Selected, temperature.State);
        Assert.Equal("CPU Package", temperature.SensorName);
        Assert.Equal(58.3, temperature.Value);

        Assert.Equal(LhmSensorSelectionState.Selected, power.State);
        Assert.Equal("Package", power.SensorName);
        Assert.Equal(15.4, power.Value);
    }

    // Regression: AMD exposes "Core Max" for temperature and "Package" for power,
    // and both names appear in the respective priority lists.
    [Fact]
    public void AmdShapeKeepsCoreMaxAndPackagePowerApart()
    {
        LhmSensorValue[] sensors =
        [
            Temperature("Core Max", 64),
            Power("Package", 15.4)
        ];

        var temperature = LhmSensorSelector.SelectTemperature(sensors);
        var power = LhmSensorSelector.SelectPower(sensors);

        Assert.Equal("Core Max", temperature.SensorName);
        Assert.Equal(64, temperature.Value);
        Assert.Equal("Package", power.SensorName);
        Assert.Equal(15.4, power.Value);
    }

    [Fact]
    public void TemperatureSensorIsNeverSelectedAsPowerAndViceVersa()
    {
        // Only a temperature named like a package: the power selector must stay empty.
        var powerOnlyTemperature = LhmSensorSelector.SelectPower([Temperature("Package", 58.3)]);
        Assert.Equal(LhmSensorSelectionState.NoCandidate, powerOnlyTemperature.State);
        Assert.Null(powerOnlyTemperature.Value);

        // Only a power sensor named like a package: the temperature selector must stay empty.
        var temperatureOnlyPower = LhmSensorSelector.SelectTemperature([Power("CPU Package", 15.4)]);
        Assert.Equal(LhmSensorSelectionState.NoCandidate, temperatureOnlyPower.State);
        Assert.Null(temperatureOnlyPower.Value);
    }

    [Fact]
    public void UnclassifiedSensorsAreNeverSelected()
    {
        LhmSensorValue[] sensors =
        [
            new("CPU Package", 58.3),
            new("Package", 15.4),
            new("CPU Package", 58.3, LhmSensorCategory.Other)
        ];

        Assert.Equal(LhmSensorSelectionState.NoCandidate,
            LhmSensorSelector.SelectTemperature(sensors).State);
        Assert.Equal(LhmSensorSelectionState.NoCandidate,
            LhmSensorSelector.SelectPower(sensors).State);
    }

    [Fact]
    public void LegacyTwoArgumentValueDefaultsToOtherCategory()
    {
        var legacy = new LhmSensorValue("CPU Package", 58.3);

        Assert.Equal(LhmSensorCategory.Other, legacy.Category);
        Assert.Equal(LhmSensorSelectionState.NoCandidate,
            LhmSensorSelector.SelectTemperature([legacy]).State);
    }

    [Theory]
    [InlineData(LhmSensorCategory.Temperature, double.NaN)]
    [InlineData(LhmSensorCategory.Temperature, 1000)]
    [InlineData(LhmSensorCategory.Temperature, -273)]
    public void InvalidTemperatureInMatchingCategoryIsRejectedNotReused(LhmSensorCategory category, double invalid)
    {
        var selection = LhmSensorSelector.SelectTemperature([new LhmSensorValue("CPU Package", invalid, category)]);

        Assert.Equal(LhmSensorSelectionState.RejectedValue, selection.State);
        Assert.Null(selection.Value);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1501)]
    [InlineData(0)]
    public void InvalidPowerInMatchingCategoryIsRejectedNotReused(double invalid)
    {
        var selection = LhmSensorSelector.SelectPower([
            new LhmSensorValue("Package", invalid, LhmSensorCategory.Power),
            new LhmSensorValue("CPU Package", null, LhmSensorCategory.Power)
        ]);

        Assert.Equal(LhmSensorSelectionState.RejectedValue, selection.State);
        Assert.Null(selection.SensorName);
        Assert.Null(selection.Value);
    }
}
