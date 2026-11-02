using System.Management;
using MiniProToolkit.Core;

namespace MiniProToolkit.Hardware;

public sealed class HardwareCapabilityProbe
{
    private const string NamespacePath = @"root\WMI";
    private const string InstanceName = @"ACPI\PNP0C14\IP3POWERSWITCH_0";

    // Hard bound for the WMI object bind: ObjectGetOptions.Timeout makes ManagementObject.Get
    // semisynchronous, so a wedged PowerSwitch provider fails instead of blocking forever.
    private static readonly TimeSpan WmiObjectTimeout = TimeSpan.FromSeconds(2);

    // Whole-probe budget. GetMethodParameters has no per-call timeout overload in
    // System.Management 10.0.10, so the only guarantee available is that the caller (and the
    // shared worker slot) is released within this budget and that probes never stack up.
    private static readonly TimeSpan PowerSwitchProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly SingleFlightOperationWorker PowerSwitchWorker = new(
        "MiniProToolkit-Wmi-PowerSwitch", thread => thread.SetApartmentState(ApartmentState.MTA));

    private const string BrightnessId = "brightness";
    private const string BrightnessName = "屏幕亮度";
    private const string AudioId = "audio";
    private const string AudioName = "音量 / 麦克风";

    /// <summary>States that a read-only reading alone can never open a capability.</summary>
    private const string NotOpenSuffix = "只读读数不构成写回验证证明，该能力保持未验证、未开放。";

    /// <summary>
    /// Read-only capability probe: the power-switch WMI check runs on its own bounded worker
    /// (one probe at a time, released even when the provider wedges), brightness and audio use
    /// their readers' own bounded waits.
    /// </summary>
    public HardwareCapabilities Probe()
    {
        var items = new List<HardwareCapability>
        {
            ProbePowerSwitch(),
            ProbeBrightness(),
            ProbeAudio()
        };
        return new HardwareCapabilities(items);
    }

    /// <summary>
    /// Read-only system indicator snapshot for the diagnostics window: internal panel
    /// brightness plus the default output volume/mute and default microphone mute.
    /// Blocking (the CoreAudio read joins its own MTA thread with a 3 s timeout, the WMI
    /// read is synchronous but now carries a hard query timeout), so call this from a
    /// background thread. Lock keys are passed in because they come from the UI thread's
    /// <c>Keyboard.IsKeyToggled</c> state.
    /// <para>
    /// Nothing here writes: only <see cref="BrightnessReader.Read"/> and
    /// <see cref="AudioEndpointReader.Read"/> are called, and both are read-only. Any
    /// failure degrades to an unavailable reading instead of an exception.
    /// </para>
    /// </summary>
    public static SystemIndicatorSnapshot ReadIndicators(SystemLockKeys lockKeys)
    {
        var brightness = ReadBrightness();
        var audio = ReadAudioEndpoints();
        return new SystemIndicatorSnapshot(lockKeys, brightness, audio.Output, audio.Microphone);
    }

    private static HardwareCapability ProbePowerSwitch() => PowerSwitchWorker.Execute(
        ProbePowerSwitchCore,
        new HardwareCapability("power-switch", "性能模式与风扇控制", CapabilityState.Error,
            "PowerSwitchInterface 只读探测未在时限内返回（provider 可能卡住）；本次按错误处理，未写入任何硬件。"),
        PowerSwitchProbeTimeout);

    private static HardwareCapability ProbePowerSwitchCore()
    {
        try
        {
            using var instance = new ManagementObject(
                NamespacePath,
                $"PowerSwitchInterface.InstanceName='{InstanceName}'",
                new ObjectGetOptions { Timeout = WmiObjectTimeout });
            instance.Get();
            var methods = new[] { "GetFanControl", "SetFanControl", "GetPowerMode", "SetPowerMode", "GetHwTemp" };
            var available = methods.Where(name => HasMethod(instance, name)).ToArray();
            return available.Length == methods.Length
                ? new HardwareCapability("power-switch", "性能模式与风扇控制", CapabilityState.Confirmed,
                    "PowerSwitchInterface 实例和核心方法可访问；写入仍需逐次读回确认。")
                : new HardwareCapability("power-switch", "性能模式与风扇控制", CapabilityState.Error,
                    $"核心方法不完整：{string.Join(", ", available)}");
        }
        catch (Exception ex)
        {
            return new HardwareCapability("power-switch", "性能模式与风扇控制", CapabilityState.Error,
                ex.GetBaseException().Message);
        }
    }

    private static bool HasMethod(ManagementObject instance, string method)
    {
        try
        {
            // No timeout parameter exists for GetMethodParameters; the object's
            // ObjectGetOptions only bounds the bind above. The enclosing PowerSwitchWorker
            // budget is what stops a wedged method lookup from blocking indefinitely.
            using var parameters = instance.GetMethodParameters(method);
            return parameters is not null;
        }
        catch (ManagementException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reports the read-only internal-panel brightness. A real reading is
    /// <see cref="CapabilityState.NotVerified"/> (a read never proves a write), an
    /// unavailable reading is <see cref="CapabilityState.Unsupported"/>. The row is never
    /// <see cref="CapabilityState.Confirmed"/> and never opens the capability.
    /// </summary>
    private static HardwareCapability ProbeBrightness()
    {
        try
        {
            var reading = ReadBrightness();
            var text = SystemIndicatorText.Brightness(reading);
            return reading.IsAvailable
                ? new HardwareCapability(BrightnessId, BrightnessName, CapabilityState.NotVerified,
                    $"{text}。未执行亮度写入和读回验证；{NotOpenSuffix}")
                : new HardwareCapability(BrightnessId, BrightnessName, CapabilityState.Unsupported,
                    $"未取得内建屏幕亮度读数：{text}。能力暂按不支持处理。");
        }
        catch (Exception ex)
        {
            return BrightnessErrorCapability(ex);
        }
    }

    /// <summary>
    /// Reports default output volume/mute and default microphone mute. Both readable →
    /// NotVerified; only one readable → NotVerified with an explicit partial note; none →
    /// Unsupported. Never Confirmed, and no volume/mute write path exists at all.
    /// </summary>
    private static HardwareCapability ProbeAudio()
    {
        try
        {
            var audio = ReadAudioEndpoints();
            var text = SystemIndicatorText.Audio(audio.Output, audio.Microphone);
            if (audio.IsAvailable)
                return new HardwareCapability(AudioId, AudioName, CapabilityState.NotVerified,
                    $"{text}。未执行音量/静音写入和读回验证；{NotOpenSuffix}");
            if (audio.Output.IsAvailable || audio.Microphone.IsAvailable)
                return new HardwareCapability(AudioId, AudioName, CapabilityState.NotVerified,
                    $"{text}。默认输出与默认输入未同时可读，无法构成能力证明；{NotOpenSuffix}");
            return new HardwareCapability(AudioId, AudioName, CapabilityState.Unsupported,
                $"未取得默认输出音量或默认麦克风静音读数：{text}。能力暂按不支持处理。");
        }
        catch (Exception ex)
        {
            return AudioErrorCapability(ex);
        }
    }

    /// <summary>Reads brightness without ever throwing; failures become unavailable readings.</summary>
    private static BrightnessReading ReadBrightness()
    {
        try
        {
            return new BrightnessReader().Read();
        }
        catch (Exception ex)
        {
            return BrightnessReading.Unavailable(Describe(ex));
        }
    }

    /// <summary>
    /// Reads both default endpoints in one call so only one CoreAudio MTA thread is
    /// created. Never throws; failures become unavailable readings.
    /// </summary>
    private static AudioEndpointReadResult ReadAudioEndpoints()
    {
        try
        {
            return new AudioEndpointReader().Read();
        }
        catch (Exception ex)
        {
            return AudioEndpointReadResult.Unavailable(Describe(ex));
        }
    }

    private static HardwareCapability BrightnessErrorCapability(Exception ex) =>
        new(BrightnessId, BrightnessName, CapabilityState.Unsupported,
            $"内建屏幕亮度只读探测异常：{SystemIndicatorText.Brightness(BrightnessReading.Unavailable(Describe(ex)))}。能力暂按不支持处理。");

    private static HardwareCapability AudioErrorCapability(Exception ex) =>
        new(AudioId, AudioName, CapabilityState.Unsupported,
            $"默认音频端点只读探测异常：{SystemIndicatorText.Audio(
                AudioVolumeReading.Unavailable(Describe(ex)),
                AudioMuteReading.Unavailable(Describe(ex)))}。能力暂按不支持处理。");

    /// <summary>
    /// Short base-exception message. It is only ever handed to
    /// <see cref="SystemIndicatorText"/>, which redacts paths and truncates to 160 chars,
    /// so a device name or local path cannot leak into the capability row.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var inner = exception.GetBaseException();
        return inner.HResult != 0
            ? $"{inner.Message}（HRESULT 0x{inner.HResult:X8}）"
            : inner.Message;
    }
}
