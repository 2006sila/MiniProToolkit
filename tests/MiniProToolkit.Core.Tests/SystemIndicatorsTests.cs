using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class SystemIndicatorsTests
{
    [Fact]
    public void LockKeyTextMirrorsTheToggleState()
    {
        var text = SystemIndicatorText.LockKeys(new SystemLockKeys(true, false));

        Assert.Contains("CapsLock：开", text);
        Assert.Contains("NumLock：关", text);
        Assert.Contains("只读", text);
        Assert.DoesNotContain("写入", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(100)]
    public void BrightnessAcceptsPlausiblePercentages(int percent)
    {
        var reading = BrightnessReading.FromPercent(percent);

        Assert.True(reading.IsAvailable);
        Assert.Equal(percent, reading.Percent);
        Assert.Null(reading.Status);
        Assert.Contains($"{percent}%", SystemIndicatorText.Brightness(reading));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void BrightnessRejectsOutOfRangePercentages(int percent)
    {
        var reading = BrightnessReading.FromPercent(percent);

        Assert.False(reading.IsAvailable);
        Assert.Null(reading.Percent);
        Assert.Equal(BrightnessReading.OutOfRangeStatus, reading.Status);
    }

    [Fact]
    public void MissingBrightnessIsUnknownInsteadOfZero()
    {
        var reading = BrightnessReading.FromPercent(null);

        Assert.False(reading.IsAvailable);
        Assert.Equal(BrightnessReading.MissingStatus, reading.Status);

        var text = SystemIndicatorText.Brightness(reading);
        Assert.Contains(SystemIndicatorText.UnknownMark, text);
        Assert.DoesNotContain("0%", text);
        Assert.Contains(SystemIndicatorText.ReadOnlyMark, text);
    }

    [Theory]
    [InlineData(0d, false, 0)]
    [InlineData(0.4249d, false, 42)]
    [InlineData(0.5d, true, 50)]
    [InlineData(1d, false, 100)]
    public void VolumeAcceptsPlausibleScalars(double scalar, bool muted, int expectedPercent)
    {
        var reading = AudioVolumeReading.FromScalar(scalar, muted);

        Assert.True(reading.IsAvailable);
        Assert.Equal(expectedPercent, reading.Percent);
        Assert.Equal(muted, reading.Muted);
        Assert.Null(reading.Status);
    }

    [Theory]
    [InlineData(-0.1d)]
    [InlineData(1.1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void VolumeRejectsOutOfRangeScalars(double scalar)
    {
        var reading = AudioVolumeReading.FromScalar(scalar, false);

        Assert.False(reading.IsAvailable);
        Assert.Null(reading.Percent);
        Assert.Equal(AudioVolumeReading.OutOfRangeStatus, reading.Status);
    }

    [Fact]
    public void VolumeWithoutMuteStateIsUnavailable()
    {
        var missingScalar = AudioVolumeReading.FromScalar(null, false);
        var missingMute = AudioVolumeReading.FromScalar(0.5d, null);

        Assert.False(missingScalar.IsAvailable);
        Assert.Equal(AudioVolumeReading.IncompleteStatus, missingScalar.Status);
        Assert.False(missingMute.IsAvailable);
        Assert.Equal(AudioVolumeReading.IncompleteStatus, missingMute.Status);
    }

    [Fact]
    public void MicrophoneMuteIsUnknownUntilReported()
    {
        Assert.True(AudioMuteReading.FromMute(true).IsAvailable);
        Assert.True(AudioMuteReading.FromMute(false).IsAvailable);

        var unknown = AudioMuteReading.FromMute(null);
        Assert.False(unknown.IsAvailable);
        Assert.Equal(AudioMuteReading.MissingStatus, unknown.Status);
        Assert.Contains(SystemIndicatorText.UnknownMark, SystemIndicatorText.Microphone(unknown));
    }

    [Fact]
    public void AudioTextCombinesOutputVolumeAndMicrophoneMute()
    {
        var text = SystemIndicatorText.Audio(
            AudioVolumeReading.FromScalar(0.42d, false),
            AudioMuteReading.FromMute(true));

        Assert.Contains("默认输出音量：42%（未静音）", text);
        Assert.Contains("默认输入麦克风：已静音", text);
        Assert.Contains(SystemIndicatorText.ReadOnlyMark, text);
    }

    [Fact]
    public void StatusTextIsRedactedAndTruncated()
    {
        var withPath = BrightnessReading.Unavailable(@"C:\synthetic\someone\App.log 读取失败");
        var text = SystemIndicatorText.Brightness(withPath);

        Assert.DoesNotContain(@"C:\synthetic", text);
        Assert.Contains("[LOCAL_PATH]", text);

        var longStatus = AudioVolumeReading.Unavailable(new string('x', 400));
        var longText = SystemIndicatorText.OutputVolume(longStatus);
        Assert.True(longText.Length < 260);
        Assert.Contains("…", longText);
    }

    [Fact]
    public void SnapshotLinesCoverEveryIndicator()
    {
        var snapshot = new SystemIndicatorSnapshot(
            new SystemLockKeys(true, true),
            BrightnessReading.FromPercent(64),
            AudioVolumeReading.FromScalar(0.3d, false),
            AudioMuteReading.FromMute(false));

        var lines = snapshot.Lines();

        Assert.Equal(4, lines.Count);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.Contains("CapsLock", lines[0]);
        Assert.Contains("64%", lines[1]);
        Assert.Contains("30%", lines[2]);
        Assert.Contains("默认输入麦克风", lines[3]);
    }

    [Fact]
    public void UnavailableSnapshotStillProducesFourUnknownLines()
    {
        var lines = SystemIndicatorSnapshot.Unavailable("演示模式不访问硬件。").Lines();

        Assert.Equal(4, lines.Count);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.Contains(SystemIndicatorText.UnknownMark, lines[1]);
        Assert.Contains(SystemIndicatorText.UnknownMark, lines[2]);
        Assert.Contains(SystemIndicatorText.UnknownMark, lines[3]);
    }

    [Fact]
    public void EveryIndicatorLineIsMarkedReadOnlyAndOffersNoControl()
    {
        var lines = new SystemIndicatorSnapshot(
            new SystemLockKeys(false, true),
            BrightnessReading.FromPercent(55),
            AudioVolumeReading.FromScalar(0.8d, true),
            AudioMuteReading.FromMute(false)).Lines();

        // The lock-key line only says "只读"; the value lines carry the full read-only mark.
        Assert.Contains("只读", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.Contains(SystemIndicatorText.ReadOnlyMark, line));
        Assert.All(lines, line => Assert.DoesNotContain("设置", line));
        Assert.All(lines, line => Assert.DoesNotContain("Set", line));
    }

    [Fact]
    public void DemoUnavailableSnapshotKeepsTheReasonVisible()
    {
        const string reason = "演示模式不采集系统指示：不读取键盘切换状态、屏幕亮度或音频端点。";

        var lines = SystemIndicatorSnapshot.Unavailable(reason).Lines();

        Assert.Contains(reason, lines[1]);
        Assert.Contains(reason, lines[2]);
        Assert.Contains(reason, lines[3]);
    }

    [Fact]
    public void MicrophoneStatusIsRedactedAndTruncated()
    {
        var withPath = AudioMuteReading.Unavailable(@"\\server\share\mic.log 不可用");
        var redacted = SystemIndicatorText.Microphone(withPath);
        Assert.DoesNotContain("server", redacted);
        Assert.Contains("[NETWORK_PATH]", redacted);

        var longText = SystemIndicatorText.Microphone(AudioMuteReading.Unavailable(new string('y', 400)));
        Assert.True(longText.Length < 260);
        Assert.Contains("…", longText);
    }

    [Fact]
    public void ControlVerificationAcceptsOnlyMatchingOutputReadback()
    {
        var success = SystemControlVerification.VerifyOutputVolume(42, AudioVolumeReading.FromScalar(0.42d, false));
        var mismatch = SystemControlVerification.VerifyOutputVolume(42, AudioVolumeReading.FromScalar(0.5d, false));
        var unavailable = SystemControlVerification.VerifyOutputVolume(42, AudioVolumeReading.Unavailable());

        Assert.Equal(SystemControlWriteState.Succeeded, success.State);
        Assert.Equal(SystemControlWriteState.Unconfirmed, mismatch.State);
        Assert.Equal(SystemControlWriteState.Unconfirmed, unavailable.State);
        Assert.False(mismatch.IsSuccess);
        Assert.False(unavailable.IsSuccess);
    }

    [Fact]
    public void ControlVerificationRejectsOutOfRangeTargetWithoutSuccess()
    {
        var volume = SystemControlVerification.VerifyOutputVolume(101, AudioVolumeReading.FromScalar(1d, false));
        var brightness = SystemControlVerification.VerifyBrightness(-1, BrightnessReading.FromPercent(0));

        Assert.Equal(SystemControlWriteState.Failed, volume.State);
        Assert.Equal(SystemControlWriteState.Failed, brightness.State);
        Assert.False(volume.IsSuccess);
        Assert.False(brightness.IsSuccess);
    }

    [Fact]
    public void ControlVerificationRequiresMatchingMuteReadback()
    {
        var success = SystemControlVerification.VerifyMicrophoneMute(true, AudioMuteReading.FromMute(true));
        var mismatch = SystemControlVerification.VerifyMicrophoneMute(true, AudioMuteReading.FromMute(false));
        var unavailable = SystemControlVerification.VerifyMicrophoneMute(false, AudioMuteReading.Unavailable());

        Assert.Equal(SystemControlWriteState.Succeeded, success.State);
        Assert.Equal(SystemControlWriteState.Unconfirmed, mismatch.State);
        Assert.Equal(SystemControlWriteState.Unconfirmed, unavailable.State);
        Assert.False(mismatch.IsSuccess);
        Assert.False(unavailable.IsSuccess);
    }

    [Fact]
    public void BrightnessVerificationRequiresMatchingCurrentBrightnessReadback()
    {
        var success = SystemControlVerification.VerifyBrightness(65, BrightnessReading.FromPercent(65));
        var mismatch = SystemControlVerification.VerifyBrightness(65, BrightnessReading.FromPercent(64));
        var unavailable = SystemControlVerification.VerifyBrightness(65, BrightnessReading.Unavailable());

        Assert.Equal(SystemControlWriteState.Succeeded, success.State);
        Assert.Equal(SystemControlWriteState.Unconfirmed, mismatch.State);
        Assert.Equal(SystemControlWriteState.Unconfirmed, unavailable.State);
        Assert.False(mismatch.IsSuccess);
        Assert.False(unavailable.IsSuccess);
    }

    [Fact]
    public void AudioTextMarksTheMissingHalfAsUnknownInsteadOfZero()
    {
        var text = SystemIndicatorText.OutputVolume(AudioVolumeReading.Unavailable());

        Assert.Contains(SystemIndicatorText.UnknownMark, text);
        Assert.DoesNotContain("0%", text);
        Assert.DoesNotContain("未静音", text);
    }
}
