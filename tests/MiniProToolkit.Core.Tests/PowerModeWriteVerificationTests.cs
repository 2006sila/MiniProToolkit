using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class PowerModeWriteVerificationTests
{
    [Theory]
    [InlineData(PowerMode.Balance)]
    [InlineData(PowerMode.Performance)]
    [InlineData(PowerMode.Silence)]
    public void ImmediateExactTargetSucceedsWithoutWaiting(PowerMode mode)
    {
        var fake = new Fake { Read = () => (byte)mode };
        fake.Verify(mode);
        Assert.Equal(1, fake.Writes);
        Assert.Equal(1, fake.Reads);
        Assert.Empty(fake.Waits);
        Assert.Equal(TimeSpan.Zero, fake.Now);
    }

    [Fact]
    public void OldOldNewUsesOnlyReadPollingAfterOneWrite()
    {
        var values = new Queue<byte?>([0, 0, 2]);
        var fake = new Fake { Read = () => values.Dequeue() };
        fake.Verify();
        Assert.Equal(1, fake.Writes);
        Assert.Equal(3, fake.Reads);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)], fake.Waits);
        Assert.Equal(TimeSpan.FromMilliseconds(200), fake.Now);
    }

    [Theory]
    [InlineData(PowerMode.Silence, 0)]
    [InlineData(PowerMode.Balance, 2)]
    public void NeverTargetTimesOutWithoutSecondWrite(PowerMode expected, byte actual)
    {
        var fake = new Fake { Read = () => actual };
        var error = Assert.Throws<PowerModeWriteVerificationException>(() => fake.Verify(expected));
        AssertFailure(error, PowerModeWriteVerificationFailure.Timeout, (byte)expected, actual, 1000, 10);
        Assert.Equal(1, fake.Writes);
        Assert.Equal(10, fake.Reads);
        Assert.Equal(10, fake.Waits.Count);
        Assert.All(fake.Waits, wait => Assert.Equal(TimeSpan.FromMilliseconds(100), wait));
    }

    [Theory]
    [InlineData(null)]
    [InlineData((byte)255)]
    [InlineData((byte)3)]
    public void UnknownReadFailsImmediatelyAndNeverBecomesBalance(byte? raw)
    {
        var fake = new Fake { Read = () => raw };
        var error = Assert.Throws<PowerModeWriteVerificationException>(() => fake.Verify(PowerMode.Balance));
        AssertFailure(error, PowerModeWriteVerificationFailure.InvalidRead, 0, raw, 0, 1);
        Assert.Equal(1, fake.Writes);
        Assert.Empty(fake.Waits);
    }

    [Fact]
    public void GetterExceptionIsSanitizedAndNeverRewrites()
    {
        var values = new Queue<byte?>([0]);
        var fake = new Fake
        {
            Read = () => values.Count > 0 ? values.Dequeue() : throw new InvalidOperationException("secret provider path")
        };
        var error = Assert.Throws<PowerModeWriteVerificationException>(() => fake.Verify());
        AssertFailure(error, PowerModeWriteVerificationFailure.ReadFailed, 2, 0, 100, 2);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(1, fake.Writes);
    }

    [Fact]
    public void SetterExceptionPropagatesUnchangedAndDoesNotReadOrWait()
    {
        var original = new InvalidOperationException("original setter failure");
        var fake = new Fake { Write = () => throw original };
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => fake.Verify()));
        Assert.Equal(1, fake.Writes);
        Assert.Equal(0, fake.Reads);
        Assert.Empty(fake.Waits);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1200)]
    public void SlowSetterConsumesBudgetAndDoesNotStartFirstRead(int milliseconds)
    {
        var fake = new Fake { Read = () => 2 };
        fake.Write = () => fake.Advance(milliseconds);
        var error = Assert.Throws<PowerModeWriteVerificationException>(() => fake.Verify());
        AssertFailure(error, PowerModeWriteVerificationFailure.Timeout, 2, null, milliseconds, 0);
        Assert.Equal(1, fake.Writes);
        Assert.Equal(0, fake.Reads);
        Assert.Empty(fake.Waits);
    }

    [Fact]
    public void SetterTimeIsIncludedAndFinalWaitIsClampedToRemainingBudget()
    {
        var fake = new Fake();
        fake.Write = () => fake.Advance(950);
        var error = Assert.Throws<PowerModeWriteVerificationException>(() => fake.Verify());
        AssertFailure(error, PowerModeWriteVerificationFailure.Timeout, 2, 0, 1000, 1);
        Assert.Equal([TimeSpan.FromMilliseconds(50)], fake.Waits);
        Assert.Equal(1, fake.Writes);
    }

    [Theory]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    [InlineData(1001, false)]
    public void TargetReadMustReturnStrictlyBeforeDeadline(int milliseconds, bool succeeds)
    {
        var fake = new Fake();
        fake.Read = () => { fake.Advance(milliseconds); return 2; };
        if (succeeds)
            fake.Verify();
        else
        {
            var error = Assert.Throws<PowerModeWriteVerificationException>(() => fake.Verify());
            AssertFailure(error, PowerModeWriteVerificationFailure.Timeout, 2, 2, milliseconds, 1);
        }
        Assert.Equal(1, fake.Writes);
        Assert.Equal(1, fake.Reads);
        Assert.Empty(fake.Waits);
    }

    [Fact]
    public void WaitOvershootCannotStartAnotherRead()
    {
        var fake = new Fake { WaitOvershootMilliseconds = 1000 };
        var error = Assert.Throws<PowerModeWriteVerificationException>(() => fake.Verify());
        AssertFailure(error, PowerModeWriteVerificationFailure.Timeout, 2, 0, 1100, 1);
        Assert.Equal(1, fake.Writes);
        Assert.Equal(1, fake.Reads);
    }

    [Fact]
    public void NonzeroClockOriginIsNotPartOfBudget()
    {
        var fake = new Fake { Read = () => 2 };
        fake.Advance(5000);
        fake.Verify();
        Assert.Equal(1, fake.Reads);
        Assert.Empty(fake.Waits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(987654L)]
    public void SetterStatusIsMetadataOnlyAndIncludedOnFailure(long? status)
    {
        var success = new Fake { Read = () => 2, SetterStatus = status };
        success.Verify();
        var failure = new Fake { Read = () => null, SetterStatus = status };
        var error = Assert.Throws<PowerModeWriteVerificationException>(() => failure.Verify());
        Assert.Equal(status, error.SetterStatus);
        Assert.Contains("expected=2", error.Message);
        Assert.Contains("lastActual=null", error.Message);
        Assert.Contains("elapsedMs=0.000", error.Message);
        Assert.Contains("readCount=1", error.Message);
        Assert.Contains($"setterStatus={status?.ToString() ?? "null"}", error.Message);
    }

    [Fact]
    public void RawDecodingIsStrictAndRetainsUnknownByteForDiagnostics()
    {
        foreach (var mode in new byte[] { 0, 1, 2, 255 })
            Assert.Equal(mode, PowerModeWriteVerification.DecodeCurrentPowerMode(mode));
        foreach (var mode in new[] { 0, 1, 2 })
            Assert.Equal((byte)mode, PowerModeWriteVerification.DecodeCurrentPowerMode(mode));
        foreach (var invalid in new object?[] { null, -1, 3, 255, "0", "2", false, 0.0, 2m, (short)0, 0u, new object() })
            Assert.Null(PowerModeWriteVerification.DecodeCurrentPowerMode(invalid));
    }

    [Fact]
    public void InvalidRequestedModeDoesNotWrite()
    {
        var fake = new Fake();
        Assert.Throws<ArgumentOutOfRangeException>(() => fake.Verify(PowerMode.Unknown));
        Assert.Equal(0, fake.Writes);
        Assert.Equal(0, fake.Reads);
    }

    private static void AssertFailure(
        PowerModeWriteVerificationException error,
        PowerModeWriteVerificationFailure failure,
        byte expected,
        byte? actual,
        int elapsedMilliseconds,
        int reads)
    {
        Assert.Equal(failure, error.Failure);
        Assert.Equal(expected, error.Expected);
        Assert.Equal(actual, error.LastActual);
        Assert.Equal(TimeSpan.FromMilliseconds(elapsedMilliseconds), error.Elapsed);
        Assert.Equal(reads, error.ReadCount);
    }

    private sealed class Fake
    {
        public TimeSpan Now { get; private set; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public List<TimeSpan> Waits { get; } = [];
        public Action Write { get; set; } = () => { };
        public Func<byte?> Read { get; set; } = () => 0;
        public long? SetterStatus { get; set; }
        public int WaitOvershootMilliseconds { get; set; }

        public void Advance(int milliseconds) => Now += TimeSpan.FromMilliseconds(milliseconds);

        public void Verify(PowerMode expected = PowerMode.Silence) => PowerModeWriteVerification.Verify(
            expected,
            () => { Writes++; Write(); },
            () => { Reads++; return Read(); },
            () => Now,
            duration =>
            {
                Waits.Add(duration);
                Now += duration + TimeSpan.FromMilliseconds(WaitOvershootMilliseconds);
            },
            () => SetterStatus);
    }
}
