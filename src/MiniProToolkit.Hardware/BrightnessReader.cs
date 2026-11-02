using System.Management;
using System.Runtime.InteropServices;
using MiniProToolkit.Core;

namespace MiniProToolkit.Hardware;

/// <summary>
/// Built-in panel brightness reader and explicit writer. Reads use
/// <c>WmiMonitorBrightness.CurrentBrightness</c>; writes use only the sibling
/// <c>WmiMonitorBrightnessMethods.WmiSetBrightness</c> class and are enabled only
/// when there is exactly one method instance and a current-brightness readback.
/// All WMI calls run on one MTA worker with a bounded wait. A timed-out call is not
/// cancelled or retried, and a write is never reported successful without matching
/// <c>CurrentBrightness</c> readback.
/// </summary>
public sealed class BrightnessReader
{
    private const string NamespacePath = @"root\WMI";
    private const string BrightnessQuery = "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness";
    private const string MethodsQuery = "SELECT * FROM WmiMonitorBrightnessMethods";
    private const string ConnectionQuery = "SELECT InstanceName, Active, VideoOutputTechnology FROM WmiMonitorConnectionParams";
    private const string SetMethod = "WmiSetBrightness";
    private const int MaximumStatusLength = 160;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(3);
    private static readonly SingleFlightOperationWorker SharedWorker = new(
        "MiniProToolkit-Wmi-Brightness", thread => thread.SetApartmentState(ApartmentState.MTA));

    private const string NoInstanceStatus =
        "未发现 WmiMonitorBrightness 实例或实例未返回当前亮度（内建屏幕亮度读取不可用）。";
    private const string NoMethodStatus =
        "未发现内建屏幕 WmiMonitorBrightnessMethods 控制实例，写入已禁用。";
    private const string AmbiguousMethodStatus =
        "内建屏幕 WmiMonitorBrightnessMethods 控制实例不唯一，写入已禁用。";

    /// <summary>Reads the current internal-panel brightness on the WMI worker.</summary>
    public BrightnessReading Read() => SharedWorker.Execute(
        ReadCore,
        BrightnessReading.Unavailable("内建屏幕亮度读取未返回结果。"),
        OperationTimeout);

    /// <summary>
    /// Probes only the built-in panel method class. This never invokes a write method.
    /// </summary>
    public SystemControlAvailability ProbeControl() => SharedWorker.Execute(
        ProbeControlCore,
        SystemControlAvailability.Unavailable("内建屏幕亮度控制探测未返回结果。"),
        OperationTimeout);

    /// <summary>Writes once, then reads CurrentBrightness back exactly once.</summary>
    public SystemControlWriteResult SetBrightness(int percent)
    {
        if (percent is < 0 or > 100)
            return SystemControlVerification.VerifyBrightness(percent, BrightnessReading.Unavailable());
        var deadline = Environment.TickCount64 + (long)OperationTimeout.TotalMilliseconds;
        return SharedWorker.Execute(
            () => SetBrightnessCore(percent, deadline),
            new SystemControlWriteResult("brightness", SystemControlWriteState.Unconfirmed,
                "亮度操作超时、忙碌或不可用；未确认成功，调用可能仍在完成，不会自动重试。", RequestedPercent: percent),
            OperationTimeout);
    }

    private static BrightnessReading ReadCore() => ReadPanelCore()?.Reading
        ?? BrightnessReading.Unavailable(NoInstanceStatus);

    private static PanelReading? ReadPanelCore()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(NamespacePath, BrightnessQuery);
            using var results = searcher.Get();
            var candidates = new List<BrightnessPanelCandidate>();
            foreach (ManagementObject instance in results)
            {
                using (instance)
                    candidates.Add(new BrightnessPanelCandidate(
                        InstanceName: instance["InstanceName"] as string,
                        CurrentBrightness: TryReadCurrentBrightness(instance)));
            }

            var selected = BrightnessPanelSelection.SelectBrightness(candidates);
            if (selected is null)
                return null;
            return new PanelReading(selected.InstanceName!, BrightnessReading.FromPercent(
                BrightnessPanelSelection.CurrentPercent(selected.CurrentBrightness)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SystemControlAvailability ProbeControlCore()
    {
        var panel = ReadPanelCore();
        if (panel is null)
            return SystemControlAvailability.Unavailable("内建屏幕当前亮度不可读，亮度写入已禁用。");

        try
        {
            if (!IsBuiltInPanel(panel.InstanceName))
                return SystemControlAvailability.Unavailable("无法唯一确认目标为活动内建面板，亮度写入已禁用。");
            var count = CountMethodInstances(panel.InstanceName);
            return count switch
            {
                1 => SystemControlAvailability.Available(
                    "内建屏幕 WmiMonitorBrightnessMethods 控制方法可用；写入仍需显式请求并读回确认。"),
                0 => SystemControlAvailability.Unavailable(NoMethodStatus),
                _ => SystemControlAvailability.Unavailable(AmbiguousMethodStatus)
            };
        }
        catch (Exception ex)
        {
            return SystemControlAvailability.Unavailable(Sanitize(ex));
        }
    }

    private static SystemControlWriteResult SetBrightnessCore(int percent, long deadline)
    {
        if (percent is < BrightnessReading.MinimumPercent or > BrightnessReading.MaximumPercent)
            return new SystemControlWriteResult("brightness", SystemControlWriteState.Failed,
                "目标屏幕亮度超出 0-100 范围，未执行写入。", RequestedPercent: percent);

        try
        {
            var panel = ReadPanelCore();
            if (panel is null)
                return new SystemControlWriteResult("brightness", SystemControlWriteState.Unavailable,
                    "内建屏幕当前亮度不可读，亮度写入已禁用。", RequestedPercent: percent);

            if (!IsBuiltInPanel(panel.InstanceName))
                return new SystemControlWriteResult("brightness", SystemControlWriteState.Unavailable,
                    "无法唯一确认目标为活动内建面板，未执行写入。", RequestedPercent: percent);
            var methodCount = CountMethodInstances(panel.InstanceName);
            if (methodCount != 1)
                return new SystemControlWriteResult("brightness", SystemControlWriteState.Unavailable,
                    methodCount == 0 ? NoMethodStatus : AmbiguousMethodStatus,
                    RequestedPercent: percent);

            using var searcher = new ManagementObjectSearcher(NamespacePath, MethodsQuery);
            using var results = searcher.Get();
            if (results.Count != 1)
                return new SystemControlWriteResult("brightness", SystemControlWriteState.Unavailable,
                    AmbiguousMethodStatus, RequestedPercent: percent);
            foreach (ManagementObject instance in results)
            {
                using (instance)
                {
                    if (!BrightnessPanelSelection.SameInstance(
                        instance["InstanceName"] as string, panel.InstanceName))
                        continue;
                    using var parameters = instance.GetMethodParameters(SetMethod);
                    if (parameters is null)
                        return new SystemControlWriteResult("brightness", SystemControlWriteState.Unavailable,
                            NoMethodStatus, RequestedPercent: percent);

                    parameters["Timeout"] = (uint)0;
                    parameters["Brightness"] = (byte)percent;
                    if (Environment.TickCount64 >= deadline)
                        return new SystemControlWriteResult("brightness", SystemControlWriteState.Unconfirmed,
                            "写入前已超过操作预算，未发起亮度写入。", RequestedPercent: percent);
                    using var output = instance.InvokeMethod(SetMethod, parameters, null);
                    var returnValue = ReadReturnValue(output);
                    if (returnValue != 0)
                        return new SystemControlWriteResult("brightness", SystemControlWriteState.Failed,
                            $"内建屏幕亮度写入失败（WMI 返回码 {returnValue}）。", RequestedPercent: percent);

                    // Exactly one write attempt, followed by exactly one current-value
                    // readback. A missing/mismatched value is unconfirmed, never success.
                    var readback = ReadPanelCore();
                    var samePanel = readback is not null && BrightnessPanelSelection.SameInstance(
                        readback.InstanceName, panel.InstanceName);
                    return SystemControlVerification.VerifyBrightness(percent, samePanel
                        ? readback!.Reading
                        : BrightnessReading.Unavailable("写入后的目标面板读回不可用或目标已改变。"));
                }
            }

            return new SystemControlWriteResult("brightness", SystemControlWriteState.Unavailable,
                NoMethodStatus, RequestedPercent: percent);
        }
        catch (Exception ex)
        {
            return new SystemControlWriteResult("brightness", SystemControlWriteState.Failed,
                Sanitize(ex), RequestedPercent: percent);
        }
    }

    // WMI brightness support alone is not proof of an internal connector. Match
    // the active panel to WmiMonitorConnectionParams and accept only documented
    // LVDS/embedded DP/embedded UDI/INTERNAL connection types (D3DKMDT enum).
    private static bool IsBuiltInPanel(string targetInstanceName)
    {
        using var searcher = new ManagementObjectSearcher(NamespacePath, ConnectionQuery);
        using var results = searcher.Get();
        var candidates = new List<BrightnessPanelCandidate>();
        foreach (ManagementObject instance in results)
        {
            using (instance)
                candidates.Add(new BrightnessPanelCandidate(
                    InstanceName: instance["InstanceName"] as string,
                    Active: instance["Active"], OutputTechnology: instance["VideoOutputTechnology"]));
        }
        return BrightnessPanelSelection.IsBuiltInPanel(targetInstanceName, candidates);
    }

    private static int CountMethodInstances(string targetInstanceName)
    {
        var count = 0;
        using var searcher = new ManagementObjectSearcher(NamespacePath, MethodsQuery);
        using var results = searcher.Get();
        // Any extra method instance is ambiguous, even if one lookup fails or has
        // a different identifier. Never silently pick the first writable instance.
        if (results.Count > 1)
            return BrightnessPanelSelection.CountMethodInstances(targetInstanceName,
                Enumerable.Repeat(new BrightnessPanelCandidate(), results.Count).ToArray());
        foreach (ManagementObject instance in results)
        {
            using (instance)
            {
                if (BrightnessPanelSelection.CountMethodInstances(targetInstanceName,
                    new[] { new BrightnessPanelCandidate(MethodInstanceName: instance["InstanceName"] as string) }) != 1)
                    continue;
                try
                {
                    using var parameters = instance.GetMethodParameters(SetMethod);
                    if (parameters is not null)
                        count++;
                }
                catch (ManagementException)
                {
                    // An instance without the exact write method is not a safe target.
                }
                catch (COMException)
                {
                    // Same safe-failure behavior for a provider/COM method lookup error.
                }
            }
        }

        return count;
    }

    private static int ReadReturnValue(ManagementBaseObject output)
    {
        return output["ReturnValue"] switch
        {
            byte value => value,
            ushort value => value,
            uint value when value <= int.MaxValue => (int)value,
            int value => value,
            _ => -1
        };
    }

    private sealed record PanelReading(string InstanceName, BrightnessReading Reading);

    private static int? TryReadCurrentBrightness(ManagementObject instance)
    {
        try
        {
            return BrightnessPanelSelection.CurrentPercent(instance["CurrentBrightness"]);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Sanitize(Exception exception)
    {
        var inner = exception.GetBaseException();
        // Do not expose provider messages: they may embed panel IDs or object paths.
        var text = $"内建屏幕亮度 WMI 操作失败（HRESULT 0x{inner.HResult:X8}）。";
        return Sanitize(text);
    }

    private static string Sanitize(string text)
    {
        var redacted = DiagnosticRedactor.Redact(text);
        return redacted.Length <= MaximumStatusLength
            ? redacted
            : redacted[..MaximumStatusLength] + "…";
    }
}
