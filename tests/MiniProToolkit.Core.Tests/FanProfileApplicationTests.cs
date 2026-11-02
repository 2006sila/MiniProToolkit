using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanProfileApplicationTests
{
    [Theory]
    [InlineData(ProfileFanMode.Automatic)]
    [InlineData(ProfileFanMode.FixedRpm)]
    [InlineData(ProfileFanMode.Curve)]
    [InlineData(ProfileFanMode.FullSpeed)]
    public void ApplyOrdersRestoreBeforeModeAndFanControl(ProfileFanMode fanMode)
    {
        var target = new Target();
        var profile = new FanProfile("测试", PowerMode.Performance, FanCurveSettings.Default, fanMode, 2800);
        var result = FanProfileApplication.Apply(profile, PowerMode.Balance, FanCurveSettings.Default, target);
        Assert.True(result.Success);
        Assert.Equal(new[] { "restore", "mode:Performance", "curve", "fan:" + fanMode }, target.Calls);
    }

    [Fact]
    public void FailedOldRestorePreventsNewHardwareWrites()
    {
        var target = new Target { FailRestore = true };
        var result = FanProfileApplication.Apply(FanProfile.Defaults[0], PowerMode.Balance, FanCurveSettings.Default, target);
        Assert.False(result.Success);
        Assert.True(result.RecoveryRequired);
        Assert.Equal(new[] { "restore" }, target.Calls);
    }

    [Fact]
    public void FailedFanModeRestoresConfigurationAndPowerButNotOldManualControl()
    {
        var target = new Target { FailFan = true };
        var result = FanProfileApplication.Apply(FanProfile.Defaults[0], PowerMode.Balance, FanCurveSettings.Default, target);
        Assert.False(result.Success);
        Assert.False(result.RecoveryRequired);
        Assert.Equal(new[] { "restore", "mode:Silence", "curve", "fan:Curve", "restore", "curve", "mode:Balance" }, target.Calls);
    }

    [Fact]
    public void FailedRecoveryDoesNotReplaceCurveController()
    {
        var target = new Target { FailFan = true, FailSecondRestore = true };
        var result = FanProfileApplication.Apply(FanProfile.Defaults[0], PowerMode.Balance, FanCurveSettings.Default, target);
        Assert.True(result.RecoveryRequired);
        Assert.Equal(1, target.Calls.Count(c => c == "curve"));
        Assert.Contains("恢复失败", result.Message);
    }

    private sealed class Target : IFanProfileTarget
    {
        public List<string> Calls { get; } = [];
        public bool FailRestore, FailFan, FailSecondRestore;
        private int _restores;
        public void RestoreAutomatic()
        {
            Calls.Add("restore");
            if (FailRestore || (++_restores > 1 && FailSecondRestore)) throw new IOException("restore failed");
        }
        public void SetPowerMode(PowerMode mode) => Calls.Add("mode:" + mode);
        public void SetCurve(FanCurveSettings settings) => Calls.Add("curve");
        public void ApplyFanMode(FanProfile profile)
        {
            Calls.Add("fan:" + profile.FanMode);
            if (FailFan) throw new IOException("write failed");
        }
    }
}
