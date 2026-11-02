namespace MiniProToolkit.Core;

public enum CapabilityState
{
    NotChecked,
    Confirmed,
    Unsupported,
    NotVerified,
    Error
}

public sealed record HardwareCapability(
    string Id,
    string DisplayName,
    CapabilityState State,
    string Details)
{
    public bool IsOpen => State == CapabilityState.Confirmed;
}

public sealed record HardwareCapabilities(IReadOnlyList<HardwareCapability> Items)
{
    public static HardwareCapabilities Demo => new([
        new("power-switch", "性能模式与风扇控制", CapabilityState.NotChecked, "演示模式不访问硬件。"),
        new("brightness", "屏幕亮度", CapabilityState.NotVerified, "未纳入本控制中心硬件控制，未开放。"),
        new("audio", "音量 / 麦克风", CapabilityState.NotVerified, "未纳入本控制中心硬件控制，未开放。")
    ]);
}
