namespace MiniProToolkit.Core;

public enum ProfileFanMode { Automatic, FixedRpm, Curve, FullSpeed }

public sealed record FanProfile(
    string Name,
    PowerMode PowerMode,
    FanCurveSettings FanCurve,
    ProfileFanMode FanMode = ProfileFanMode.Automatic,
    int FixedRpm = 2500)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64 || Name != Name.Trim())
            throw new ArgumentException("方案名称需要 1–64 个字符，首尾不能有空格。");
        if (PowerMode is not (PowerMode.Balance or PowerMode.Performance or PowerMode.Silence))
            throw new ArgumentException("无效的性能模式。");
        if (!Enum.IsDefined(FanMode)) throw new ArgumentException("无效的风扇模式。");
        if (FixedRpm is < 1 or > 10000) throw new ArgumentException("目标 RPM 需要在 1–10000 之间。");
        if (FanCurve is null) throw new ArgumentException("方案缺少曲线。");
        FanCurve.Validate();
    }

    public string Summary => $"{ModeName} · {FanModeName}";
    public string ModeName => PowerMode switch { PowerMode.Silence => "静音", PowerMode.Performance => "性能", _ => "均衡" };
    public string FanModeName => FanMode switch
    {
        ProfileFanMode.FixedRpm => $"固定 {FixedRpm} RPM",
        ProfileFanMode.Curve => "安全曲线",
        ProfileFanMode.FullSpeed => "满速",
        _ => "EC 自动"
    };

    public static IReadOnlyList<FanProfile> Defaults =>
    [
        new("安静办公", PowerMode.Silence, FanCurveSettings.Default, ProfileFanMode.Curve),
        new("均衡", PowerMode.Balance, FanCurveSettings.Default),
        new("性能", PowerMode.Performance, FanCurveSettings.Default),
        new("自定义", PowerMode.Balance, FanCurveSettings.Default)
    ];
}
