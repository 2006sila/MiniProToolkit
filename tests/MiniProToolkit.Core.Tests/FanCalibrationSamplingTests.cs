using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanCalibrationSamplingTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static HardwareSnapshot Sample(DateTimeOffset time, int? rpm = 2000, double? temperature = 60) =>
        new(time, temperature, rpm, null, PowerMode.Balance, null, null, FanControlMode.Curve, 40, null);

    [Fact]
    public void MeasuresAscendingDutiesUsingFiveSamplesAfterSettlingAndMedian()
    {
        var machine = new FanCalibrationSampling(Start);
        var time = Start;
        foreach (var duty in new[] { 40, 50, 60, 70, 80, 90, 100 })
        {
            Assert.Equal(duty, machine.DutyPercent);
            machine.DutyApplied(time);
            machine.Observe(Sample(time.AddSeconds(2), 9999), time.AddSeconds(2), true, true);
            Assert.Equal(0, machine.Progress.StableSamples);
            foreach (var offset in new[] { 0, 100, 20, 80, 40 })
            {
                time = time.AddSeconds(machine.Progress.StableSamples == 0 ? 3 : 1);
                machine.Observe(Sample(time, duty * 50 + offset), time, true, true);
            }
        }
        Assert.Equal(FanCalibrationSamplingState.Completed, machine.State);
        Assert.Equal(new[] { 2040, 2540, 3040, 3540, 4040, 4540, 5040 }, machine.Result!.Points.Select(p => p.Rpm));
        machine.Result.ValidateForFixedRpm();
    }

    [Theory]
    [InlineData(null, 60.0)]
    [InlineData(100, 60.0)]
    [InlineData(10001, 60.0)]
    [InlineData(2000, null)]
    [InlineData(2000, 90.0)]
    [InlineData(2000, 70.0)]
    [InlineData(2000, 0.0)]
    [InlineData(2000, -1.0)]
    public void RejectsInvalidRpmAndTemperature(int? rpm, double? temperature)
    {
        var machine = new FanCalibrationSampling(Start);
        machine.DutyApplied(Start);
        machine.Observe(Sample(Start, rpm, temperature), Start, true, true);
        Assert.Equal(FanCalibrationSamplingState.Aborted, machine.State);
        Assert.Null(machine.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(70)]
    public void StartingTemperatureMustBePositiveAndBelow65(double temperature)
    {
        Assert.NotNull(FanCalibrationSampling.SampleError(Sample(Start, temperature: temperature), Start, starting: true));
    }

    [Fact]
    public void AllowsCoolStartButProcessHasStrict70DegreeLimit()
    {
        Assert.Null(FanCalibrationSampling.SampleError(Sample(Start, temperature: 64.9), Start, starting: true));
        Assert.Null(FanCalibrationSampling.SampleError(Sample(Start, temperature: 65), Start));
        Assert.Null(FanCalibrationSampling.SampleError(Sample(Start, temperature: 69.9), Start));
        Assert.NotNull(FanCalibrationSampling.SampleError(Sample(Start, temperature: 70), Start));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LeaseOrGuardianLossIsTerminal(bool guardian, bool lease)
    {
        var machine = new FanCalibrationSampling(Start);
        machine.Observe(Sample(Start), Start, guardian, lease);
        Assert.Equal(FanCalibrationSamplingState.Aborted, machine.State);
    }

    [Fact]
    public void DuplicateCannotCompleteWindowAndBackwardTimestampAborts()
    {
        var machine = new FanCalibrationSampling(Start);
        machine.DutyApplied(Start);
        var time = Start.AddSeconds(3);
        machine.Observe(Sample(time), time, true, true);
        for (var i = 0; i < 6; i++) machine.Observe(Sample(time), time, true, true);
        Assert.Equal(0, machine.Progress.StableSamples);
        machine.Observe(Sample(time.AddMilliseconds(-1)), time, true, true);
        Assert.Equal(FanCalibrationSamplingState.Aborted, machine.State);
    }

    [Fact]
    public void RejectsStaleFutureErrorFaultAndMissingSamples()
    {
        var time = Start.AddSeconds(3);
        HardwareSnapshot?[] samples = [null, Sample(time.AddSeconds(-2)), Sample(time.AddTicks(1)),
            Sample(time) with { Error = "read failed" }, Sample(time) with { FanFault = "fault" },
            Sample(time) with { RpmFeedbackState = FanRpmFeedbackState.Fault }, Sample(time, temperature: double.NaN)];
        foreach (var sample in samples)
        {
            var machine = new FanCalibrationSampling(Start);
            machine.DutyApplied(Start);
            machine.Observe(sample, time, true, true);
            Assert.Equal(FanCalibrationSamplingState.Aborted, machine.State);
        }
    }

    [Fact]
    public void UnstableWindowWaitsUntilPointTimeout()
    {
        var machine = new FanCalibrationSampling(Start);
        machine.DutyApplied(Start);
        for (var second = 3; second < 30; second++)
        {
            var time = Start.AddSeconds(second);
            machine.Observe(Sample(time, second % 2 == 0 ? 2000 : 2151), time, true, true);
        }
        Assert.Equal(FanCalibrationSamplingState.Sampling, machine.State);
        machine.Observe(Sample(Start.AddSeconds(30)), Start.AddSeconds(30), true, true);
        Assert.Equal(FanCalibrationSamplingState.Aborted, machine.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsNonMonotonicOrFlatMeasuredTableWithoutRepair(bool flat)
    {
        var machine = new FanCalibrationSampling(Start);
        var time = Start;
        for (var point = 0; point < 7; point++)
        {
            machine.DutyApplied(time);
            time = time.AddSeconds(3);
            for (var sample = 0; sample < 5; sample++)
            {
                machine.Observe(Sample(time, flat ? 2000 : 4000 - point * 100), time, true, true);
                time = time.AddMilliseconds(500);
            }
        }
        Assert.Equal(FanCalibrationSamplingState.Aborted, machine.State);
        Assert.Null(machine.Result);
    }

    [Fact]
    public void CancelAndTimeoutAreTerminalAndClockCannotGoBackwards()
    {
        var machine = new FanCalibrationSampling(Start);
        machine.Cancel();
        machine.DutyApplied(Start);
        machine.Observe(Sample(Start), Start, true, true);
        Assert.Equal(FanCalibrationSamplingState.Cancelled, machine.State);
        var timeout = new FanCalibrationSampling(Start);
        timeout.DutyApplied(Start.AddMinutes(4));
        Assert.Equal(FanCalibrationSamplingState.Aborted, timeout.State);
        var backwards = new FanCalibrationSampling(Start);
        backwards.DutyApplied(Start.AddTicks(-1));
        Assert.Equal(FanCalibrationSamplingState.Aborted, backwards.State);
    }
}
