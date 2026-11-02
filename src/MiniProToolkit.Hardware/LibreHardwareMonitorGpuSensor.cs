using LibreHardwareMonitor.Hardware;
using MiniProToolkit.Core;

namespace MiniProToolkit.Hardware;

/// <summary>
/// Result of one optional GPU temperature pass. Every value is optional: the GPU
/// reading is display/telemetry data only and never replaces the EC CPU
/// temperature, the Energy Meter power or any fan-control input.
/// </summary>
public readonly record struct LhmGpuReading(
    double? GpuTemperatureC,
    string? SensorName,
    string? Status)
{
    public static LhmGpuReading NotStarted { get; } = new(null, null, "LHM GPU 尚未采样");
}

/// <summary>
/// Optional, read-only LibreHardwareMonitor reader for the GPU die temperature. It
/// uses its own <see cref="Computer"/> instance (independent from the CPU
/// cross-check reader), opens it lazily on a background thread and never throws:
/// any driver/permission/sensor failure degrades to an unavailable reading with a
/// short diagnostic status.
/// <para>
/// Only the GPU group is enabled. Motherboard, SuperIO, storage/NVMe, network,
/// memory, controller, battery, PSU and power-monitor groups stay disabled, so no
/// board or NVMe sensor can leak into the display field. The reader never installs
/// PawnIO, never writes to fans or EC registers and is not part of the fan-control,
/// 90 °C or Guardian path.
/// </para>
/// </summary>
public sealed class LibreHardwareMonitorGpuSensor : IDisposable
{
    private const int MaximumStatusLength = 160;

    /// <summary>
    /// Minimum spacing between two successful samples. GPU polling is more
    /// expensive than the CPU sensor pass and the temperature moves slowly, so a
    /// faster caller gets the cached reading instead of a new device update.
    /// </summary>
    public static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private Computer? _computer;
    private DateTimeOffset _nextAttemptUtc = LhmRetryPolicy.Immediate;
    private int _consecutiveFailures;
    private LhmGpuReading _lastReading = LhmGpuReading.NotStarted;
    private bool _disposed;

    public LhmGpuReading LastReading
    {
        get { lock (_sync) return _lastReading; }
    }

    /// <summary>
    /// Samples the GPU temperature. Call from a background thread; the first call
    /// opens the LHM computer. Repeated failures are cached and retried with a
    /// backoff, and a successful sample blocks the next update for
    /// <see cref="MinimumSampleInterval"/>.
    /// </summary>
    public LhmGpuReading Read()
    {
        try
        {
            return ReadCore();
        }
        catch (Exception ex)
        {
            // Last-resort guard: even a type-load or interop failure of the optional
            // LHM package must not reach the sampling loop or the UI.
            var failure = new LhmGpuReading(null, null, Describe("LHM GPU 不可用", ex));
            lock (_sync)
            {
                _consecutiveFailures++;
                _nextAttemptUtc = LhmRetryPolicy.Schedule(
                    DateTimeOffset.UtcNow,
                    LhmRetryPolicy.Backoff(LhmRetryPolicy.UpdateFailureBaseDelay, _consecutiveFailures));
                _lastReading = failure;
            }
            return failure;
        }
    }

    private LhmGpuReading ReadCore()
    {
        lock (_sync)
        {
            if (_disposed)
                return _lastReading;

            var now = DateTimeOffset.UtcNow;
            // Gate on every entry, not only before opening: the minimum sample
            // interval and the "no GPU sensor" backoff rely on this check.
            if (!LhmRetryPolicy.ShouldAttempt(now, _nextAttemptUtc))
                return _lastReading;

            if (_computer is null && !TryOpen(out var openStatus))
            {
                _consecutiveFailures++;
                _nextAttemptUtc = LhmRetryPolicy.Schedule(
                    now,
                    LhmRetryPolicy.Backoff(LhmRetryPolicy.OpenFailureBaseDelay, _consecutiveFailures));
                return _lastReading = new LhmGpuReading(null, null, openStatus);
            }

            return Sample(now);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            DropComputer();
            _lastReading = new LhmGpuReading(null, null, "LHM GPU 已关闭");
        }
    }

    private LhmGpuReading Sample(DateTimeOffset now)
    {
        if (_computer is not { } computer)
            return _lastReading;

        try
        {
            var (sensors, traversal) = CollectGpuSensors(computer);
            if (sensors.Count == 0)
            {
                _nextAttemptUtc = LhmRetryPolicy.Schedule(now, LhmRetryPolicy.NoCpuSensorDelay);
                return _lastReading = new LhmGpuReading(null, null, WithTraversalHint(
                    Sanitize("LHM 未发现 GPU 传感器（仅启用 GPU 组）"), traversal));
            }

            var selection = LhmGpuSensorSelector.SelectTemperature(sensors);
            if (!selection.IsSelected)
            {
                _nextAttemptUtc = LhmRetryPolicy.Schedule(now, LhmRetryPolicy.NoUsableSelectionDelay);
                return _lastReading = new LhmGpuReading(null, null, WithTraversalHint(DescribeNoSelection(selection), traversal));
            }

            _consecutiveFailures = 0;
            _nextAttemptUtc = LhmRetryPolicy.Schedule(now, MinimumSampleInterval);
            return _lastReading = new LhmGpuReading(
                selection.Value,
                selection.SensorName,
                WithTraversalHint(DescribeReading(selection), traversal));
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _nextAttemptUtc = LhmRetryPolicy.Schedule(
                now,
                LhmRetryPolicy.Backoff(LhmRetryPolicy.UpdateFailureBaseDelay, _consecutiveFailures));
            // Re-open on the next attempt: a failed update usually means the
            // driver handle or the sensor group is in a bad state.
            DropComputer();
            return _lastReading = new LhmGpuReading(null, null, Describe("LHM GPU 读取失败", ex));
        }
    }

    private bool TryOpen(out string status)
    {
        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                // GPU only: no CPU/MSR, no motherboard/SuperIO, no storage/NVMe,
                // no network, no memory, no controller, no battery, no PSU.
                IsCpuEnabled = false,
                IsGpuEnabled = true,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsMemoryEnabled = false,
                IsControllerEnabled = false,
                IsBatteryEnabled = false,
                IsPsuEnabled = false,
                IsPowerMonitorEnabled = false
            };
            computer.Open();
            _computer = computer;
            status = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            computer?.Close();
            status = Describe("LHM GPU 打开失败", ex);
            return false;
        }
    }

    private void DropComputer()
    {
        try { _computer?.Close(); }
        catch { /* best effort: the reader is optional and must never throw */ }
        _computer = null;
    }

    /// <summary>
    /// GPU-scoped sensor collection. The sub-hardware walk is iterative and bounded
    /// (reference-identity visited set, one 256-node budget for the whole pass, depth
    /// 8), so a vendor driver returning a cyclic or oversized graph cannot recurse
    /// forever or stall the background pass. Values found before the bound are kept.
    /// </summary>
    private static (List<LhmGpuSensorValue> Values, TreeWalkOutcome Traversal) CollectGpuSensors(Computer computer)
    {
        var values = new List<LhmGpuSensorValue>();
        var roots = new List<GpuTraversalNode>();
        foreach (var hardware in computer.Hardware)
        {
            // The GPU hardware type is the only reliable hardware-scope filter:
            // board, storage and network nodes never enter the candidate list.
            var vendor = MapVendor(hardware.HardwareType);
            if (vendor == LhmGpuVendor.Unknown)
                continue;
            // Each root keeps its own name/vendor; children inherit them.
            roots.Add(new GpuTraversalNode(hardware, hardware.Name, vendor));
        }

        var traversal = BoundedTreeWalk.Walk(
            roots,
            node => Children(node),
            (node, _) => CollectFrom(node, values));
        return (values, traversal);
    }

    private static IEnumerable<GpuTraversalNode> Children(GpuTraversalNode node)
    {
        foreach (var subHardware in node.Hardware.SubHardware)
            yield return new GpuTraversalNode(subHardware, node.RootName, node.Vendor);
    }

    private static void CollectFrom(GpuTraversalNode node, List<LhmGpuSensorValue> values)
    {
        var hardware = node.Hardware;
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            // The LHM sensor type is the only reliable unit information: "GPU Core"
            // exists as a temperature, a load and a clock sensor.
            var category = MapCategory(sensor.SensorType);
            if (category == LhmSensorCategory.Other)
                continue;
            values.Add(new LhmGpuSensorValue(node.RootName, node.Vendor, sensor.Name, sensor.Value, category));
        }
    }

    // Root name plus vendor, inherited by every child node of the bounded walk.
    private sealed class GpuTraversalNode
    {
        public GpuTraversalNode(IHardware hardware, string rootName, LhmGpuVendor vendor)
        {
            Hardware = hardware;
            RootName = rootName;
            Vendor = vendor;
        }

        public IHardware Hardware { get; }

        public string RootName { get; }

        public LhmGpuVendor Vendor { get; }
    }

    // A bounded walk that hit a limit is spelled out in the existing status text
    // instead of silently pretending the (possibly incomplete) sensor list is the
    // whole GPU. The reported temperature stays whatever the sensor really returned.
    private static string WithTraversalHint(string status, TreeWalkOutcome traversal)
    {
        if (!traversal.WasTruncated)
            return status;
        var cut = traversal.DepthLimitReached && traversal.NodeBudgetExhausted
            ? "节点与深度"
            : traversal.DepthLimitReached ? "深度" : "节点";
        return Sanitize($"{status}（LHM 硬件遍历已触及{cut}上限：{traversal.VisitedNodes} 个节点，读数可能不完整）");
    }

    private static LhmGpuVendor MapVendor(HardwareType hardwareType) => hardwareType switch
    {
        HardwareType.GpuNvidia => LhmGpuVendor.Nvidia,
        HardwareType.GpuAmd => LhmGpuVendor.Amd,
        HardwareType.GpuIntel => LhmGpuVendor.Intel,
        _ => LhmGpuVendor.Unknown
    };

    private static LhmSensorCategory MapCategory(SensorType sensorType) => sensorType switch
    {
        SensorType.Temperature => LhmSensorCategory.Temperature,
        _ => LhmSensorCategory.Other
    };

    // Hardware and sensor name plus unit, so a wrong pick is visible in the status
    // text during a manual comparison.
    private static string DescribeReading(LhmGpuSensorSelection selection)
    {
        var value = selection.Value?.ToString("0.0") ?? "--";
        var hardware = string.IsNullOrWhiteSpace(selection.HardwareName) ? "未知 GPU" : selection.HardwareName;
        return Sanitize($"LHM GPU 正常（{hardware} · {selection.SensorName ?? "未知"} {value}°C）");
    }

    private static string DescribeNoSelection(LhmGpuSensorSelection selection)
    {
        if (selection.State == LhmGpuSensorSelectionState.RejectedValue)
            return Sanitize("LHM GPU 温度已忽略：数值为 0 或超出合理范围");
        return Sanitize("LHM 无可用的 GPU Core / GPU Temperature 传感器（已按硬件类型与温度类别筛选）");
    }

    private static string Describe(string prefix, Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return Sanitize(string.IsNullOrWhiteSpace(message) ? prefix : $"{prefix}：{message}");
    }

    // Telemetry must not carry local paths or credentials, so every status string
    // goes through the shared redactor and a length cap.
    private static string Sanitize(string text)
    {
        var redacted = DiagnosticRedactor.Redact(text);
        return redacted.Length <= MaximumStatusLength
            ? redacted
            : redacted[..MaximumStatusLength] + "…";
    }
}
