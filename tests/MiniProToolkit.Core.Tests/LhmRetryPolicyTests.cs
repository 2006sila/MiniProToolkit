using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Logic tests for the reader's attempt gate. They exercise the pure rules only:
/// no LibreHardwareMonitor assembly is loaded and no hardware is touched.
/// </summary>
public sealed class LhmRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ImmediateAllowsTheVeryFirstAttempt()
    {
        Assert.True(LhmRetryPolicy.ShouldAttempt(Now, LhmRetryPolicy.Immediate));
    }

    [Fact]
    public void ScheduledAttemptIsBlockedUntilItsDeadline()
    {
        var next = LhmRetryPolicy.Schedule(Now, TimeSpan.FromSeconds(120));

        Assert.False(LhmRetryPolicy.ShouldAttempt(Now, next));
        Assert.False(LhmRetryPolicy.ShouldAttempt(Now.AddSeconds(119.9), next));
        Assert.True(LhmRetryPolicy.ShouldAttempt(Now.AddSeconds(120), next));
        Assert.True(LhmRetryPolicy.ShouldAttempt(Now.AddSeconds(121), next));
    }

    [Fact]
    public void NoCpuSensorAndNoUsableSelectionBothBlockRepeatedSamples()
    {
        Assert.False(LhmRetryPolicy.ShouldAttempt(
            Now.AddSeconds(1),
            LhmRetryPolicy.Schedule(Now, LhmRetryPolicy.NoCpuSensorDelay)));
        Assert.False(LhmRetryPolicy.ShouldAttempt(
            Now.AddSeconds(1),
            LhmRetryPolicy.Schedule(Now, LhmRetryPolicy.NoUsableSelectionDelay)));

        Assert.True(LhmRetryPolicy.NoCpuSensorDelay > TimeSpan.FromSeconds(1));
        Assert.True(LhmRetryPolicy.NoUsableSelectionDelay > TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void BackoffGrowsWithFailuresAndStopsAtTheCap()
    {
        var baseDelay = LhmRetryPolicy.OpenFailureBaseDelay;

        Assert.Equal(baseDelay, LhmRetryPolicy.Backoff(baseDelay, 0));
        Assert.Equal(baseDelay, LhmRetryPolicy.Backoff(baseDelay, 1));
        Assert.Equal(baseDelay * 2, LhmRetryPolicy.Backoff(baseDelay, 2));
        Assert.Equal(baseDelay * 5, LhmRetryPolicy.Backoff(baseDelay, 5));
        Assert.Equal(baseDelay * 5, LhmRetryPolicy.Backoff(baseDelay, 99));
        // Negative counters clamp to the first step instead of shrinking the delay.
        Assert.Equal(baseDelay, LhmRetryPolicy.Backoff(baseDelay, -3));
    }

    [Fact]
    public void DocumentedDelaysMatchTheReaderContract()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), LhmRetryPolicy.OpenFailureBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), LhmRetryPolicy.UpdateFailureBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(120), LhmRetryPolicy.NoCpuSensorDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), LhmRetryPolicy.NoUsableSelectionDelay);
        Assert.Equal(DateTimeOffset.MinValue, LhmRetryPolicy.Immediate);
    }
}
