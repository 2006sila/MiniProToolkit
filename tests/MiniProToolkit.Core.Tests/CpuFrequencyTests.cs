using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class CpuFrequencyTests
{
    [Fact]
    public void FirstSampleOnlyEstablishesTheBaselineAndReturnsNull()
    {
        var calculator = new CpuFrequencyCalculator(2600);

        var reading = calculator.Observe(0d);

        Assert.Null(reading.CurrentMhz);
        Assert.Null(reading.PercentOfBase);
        Assert.False(reading.IsAvailable);
        Assert.Equal(CpuFrequencySource.ProcessorInformationPercent, reading.Source);
        Assert.NotNull(reading.Status);
        Assert.True(calculator.HasPercentBaseline);
    }

    [Fact]
    public void SecondSampleConvertsPercentOfBaseClockToMegahertz()
    {
        var calculator = new CpuFrequencyCalculator(2600);
        calculator.Observe(0d);

        var reading = calculator.Observe(150d);

        Assert.Equal(3900d, reading.CurrentMhz!.Value);
        Assert.Equal(150d, reading.PercentOfBase!.Value);
        Assert.Equal(2600d, reading.BaseMhz!.Value);
        Assert.Equal(3.9d, reading.Gigahertz!.Value);
        Assert.Null(reading.Status);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-25d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(5000d)]
    public void UnusableSampleReturnsNullInsteadOfAZeroOrAbsurdFrequency(double raw)
    {
        var calculator = new CpuFrequencyCalculator(2600);
        calculator.Observe(100d);

        var reading = calculator.Observe(raw);

        Assert.Null(reading.CurrentMhz);
        Assert.NotNull(reading.Status);
    }

    [Fact]
    public void MissingNullSampleReturnsNullUntilARealValueArrives()
    {
        var calculator = new CpuFrequencyCalculator(2600);
        calculator.Observe(100d);

        Assert.Null(calculator.Observe(null).CurrentMhz);

        var recovered = calculator.Observe(120d);
        Assert.Equal(3120d, recovered.CurrentMhz!.Value);
    }

    [Fact]
    public void MissingBaseClockKeepsTheRelativePercentageOnly()
    {
        var calculator = new CpuFrequencyCalculator(null);
        calculator.Observe(100d);

        var reading = calculator.Observe(150d);

        Assert.Null(reading.CurrentMhz);
        Assert.Null(reading.BaseMhz);
        Assert.Equal(150d, reading.PercentOfBase!.Value);
        Assert.NotNull(reading.Status);
        Assert.False(calculator.HasBaseFrequency);
    }

    [Fact]
    public void ImplausibleBaseClockIsDiscarded()
    {
        var calculator = new CpuFrequencyCalculator(0);
        Assert.False(calculator.HasBaseFrequency);

        calculator.SetBaseFrequency(-1);
        Assert.False(calculator.HasBaseFrequency);

        calculator.SetBaseFrequency(2600);
        Assert.Equal(2600d, calculator.BaseFrequencyMhz!.Value);
    }

    [Fact]
    public void ResetForcesANewBaselineSample()
    {
        var calculator = new CpuFrequencyCalculator(2600);
        calculator.Observe(100d);
        calculator.Reset();

        var reading = calculator.Observe(150d);

        Assert.Null(reading.CurrentMhz);
        Assert.True(calculator.HasPercentBaseline);
    }

    [Fact]
    public void AbsoluteMhzObservationIsValidatedAndScaled()
    {
        var calculator = new CpuFrequencyCalculator(2000);

        var reading = calculator.ObserveMhz(3200);

        Assert.Equal(3200d, reading.CurrentMhz!.Value);
        Assert.Equal(160d, reading.PercentOfBase!.Value);
        Assert.Equal(CpuFrequencySource.AbsoluteMhz, reading.Source);
        Assert.Null(calculator.ObserveMhz(0d).CurrentMhz);
        Assert.Null(calculator.ObserveMhz(50_000d).CurrentMhz);
    }

    [Fact]
    public void NormalizerRejectsPlaceholdersAndAbsurdValues()
    {
        Assert.Null(CpuFrequencyNormalizer.NormalizeMhz(0));
        Assert.Null(CpuFrequencyNormalizer.NormalizeMhz(99));
        Assert.Null(CpuFrequencyNormalizer.NormalizeMhz(double.NaN));
        Assert.Null(CpuFrequencyNormalizer.NormalizeMhz(20_000));
        Assert.Equal(3800d, CpuFrequencyNormalizer.NormalizeMhz(3800));

        Assert.Null(CpuFrequencyNormalizer.NormalizePercent(0));
        Assert.Null(CpuFrequencyNormalizer.NormalizePercent(0.1));
        Assert.Null(CpuFrequencyNormalizer.NormalizePercent(2_000));
        Assert.Equal(150d, CpuFrequencyNormalizer.NormalizePercent(150));

        Assert.Null(CpuFrequencyNormalizer.PercentToMhz(150, null));
        Assert.Null(CpuFrequencyNormalizer.PercentToMhz(null, 2600));
        Assert.Equal(3900d, CpuFrequencyNormalizer.PercentToMhz(150, 2600));
    }
}
