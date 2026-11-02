using LibreHardwareMonitor.Hardware;
using MiniProToolkit.Core;

namespace MiniProToolkit.Hardware;

/// <summary>
/// Result of one optional LibreHardwareMonitor cross-check pass. Both values are
/// null-able: the LHM reader is a comparison source only and never replaces the
/// EC temperature or the Energy Meter power reading.
/// </summary>
public readonly record struct LhmCpuReading(
    double? CpuPackageTemperatureC,
    double? CpuPackagePowerWatts,
    string? Status)
{
    public static LhmCpuReading NotStarted { get; } = new(null, null, "LHM 尚未采样");
}

/// <summary>
/// Optional, read-only LibreHardwareMonitor reader for CPU package temperature and
/// package power. It is single-instance, lazily opened on a background thread and
/// never throws: any driver/permission/sensor failure degrades to an unavailable
/// reading with a short diagnostic status.
/// <para>
/// The reader only enables the CPU group (no GPU/NVAPI, no motherboard, no
/// storage). It never installs PawnIO, never touches MSR/MMIO, never writes to
/// fans or EC registers and is not part of the fan-control path.
/// </para>
/// </summary>
public sealed class LibreHardwareMonitorCpuSensor : IDisposable
{
    private const int MaximumStatusLength = 160;

    private readonly object _sync = new();
    private Computer? _computer;
    private DateTimeOffset _nextAttemptUtc = LhmRetryPolicy.Immediate;
    private int _consecutiveFailures;
    private LhmCpuReading _lastReading = LhmCpuReading.NotStarted;
    private bool _disposed;

    public LhmCpuReading LastReading
    {
        get { lock (_sync) return _lastReading; }
    }

    /// <summary>
    /// Samples the CPU sensors. Call from a background thread; the first call opens
    /// the LHM computer. Repeated failures are cached and retried with a backoff so
    /// a missing driver does not cause an expensive re-initialization every second.
    /// The same gate also applies when the computer is already open but no usable
    /// CPU sensor was found, so a sensor-less machine is not updated on every tick.
    /// </summary>
    public LhmCpuReading Read()
    {
        try
        {
            return ReadCore();
        }
        catch (Exception ex)
        {
            // Last-resort guard: even a type-load or interop failure of the optional
            // LHM package must not reach the sampling loop or the UI. A backoff is
            // still scheduled so such a failure is not retried on every tick.
            var failure = new LhmCpuReading(null, null, Describe("LHM 对照不可用", ex));
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

    private LhmCpuReading ReadCore()
    {
        lock (_sync)
        {
            if (_disposed)
                return _lastReading;

            var now = DateTimeOffset.UtcNow;
            // Gate on every entry, not only before opening: "no CPU sensor" and
            // "no usable selection" passes keep the computer open and rely on this
            // check to honour their backoff.
            if (!LhmRetryPolicy.ShouldAttempt(now, _nextAttemptUtc))
                return _lastReading;

            if (_computer is null && !TryOpen(out var openStatus))
            {
                _consecutiveFailures++;
                _nextAttemptUtc = LhmRetryPolicy.Schedule(
                    now,
                    LhmRetryPolicy.Backoff(LhmRetryPolicy.OpenFailureBaseDelay, _consecutiveFailures));
                return _lastReading = new LhmCpuReading(null, null, openStatus);
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
            _lastReading = new LhmCpuReading(null, null, "LHM 已关闭");
        }
    }

    private LhmCpuReading Sample(DateTimeOffset now)
    {
        if (_computer is not { } computer)
            return _lastReading;

        try
        {
            var (sensors, traversal) = CollectCpuSensors(computer);
            if (sensors.Count == 0)
            {
                _nextAttemptUtc = LhmRetryPolicy.Schedule(now, LhmRetryPolicy.NoCpuSensorDelay);
                return _lastReading = new LhmCpuReading(null, null, WithTraversalHint(DescribeNoCpuSensor(), traversal));
            }

            var temperature = LhmSensorSelector.SelectTemperature(sensors);
            var power = LhmSensorSelector.SelectPower(sensors);
            if (!temperature.IsSelected && !power.IsSelected)
            {
                _nextAttemptUtc = LhmRetryPolicy.Schedule(now, LhmRetryPolicy.NoUsableSelectionDelay);
                return _lastReading = new LhmCpuReading(null, null, WithTraversalHint(DescribeNoSelection(temperature, power), traversal));
            }

            _consecutiveFailures = 0;
            _nextAttemptUtc = LhmRetryPolicy.Immediate;
            return _lastReading = new LhmCpuReading(
                temperature.Value,
                power.Value,
                WithTraversalHint(DescribeReading(temperature, power), traversal));
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
            return _lastReading = new LhmCpuReading(null, null, Describe("LHM 读取失败", ex));
        }
    }

    private bool TryOpen(out string status)
    {
        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                // CPU only: no GPU/NVAPI, no motherboard, no storage, no network.
                IsCpuEnabled = true,
                IsGpuEnabled = false,
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
            status = Describe("LHM 打开失败", ex);
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
    /// CPU-scoped sensor collection. The sub-hardware walk is iterative and bounded
    /// (reference-identity visited set, one 256-node budget for the whole pass, depth
    /// 8) so a broken driver cannot recurse forever or keep a sample busy for long.
    /// A truncated walk still returns the values it found; the caller only adds a
    /// status hint, so a partial read never becomes a fabricated 0 °C / 0 W value.
    /// </summary>
    private static (List<LhmSensorValue> Values, TreeWalkOutcome Traversal) CollectCpuSensors(Computer computer)
    {
        var values = new List<LhmSensorValue>();
        var roots = new List<IHardware>();
        foreach (var hardware in computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Cpu)
                roots.Add(hardware);
        }

        var traversal = BoundedTreeWalk.Walk(
            roots,
            hardware => hardware.SubHardware,
            (hardware, _) => CollectFrom(hardware, values));
        return (values, traversal);
    }

    private static void CollectFrom(IHardware hardware, List<LhmSensorValue> values)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            // The LHM sensor type is the only reliable unit information: names such
            // as "Package" exist for both temperatures and wattages.
            var category = MapCategory(sensor.SensorType);
            if (category == LhmSensorCategory.Other)
                continue;
            values.Add(new LhmSensorValue(sensor.Name, sensor.Value, category));
        }
    }

    // A bounded walk that hit a limit is spelled out in the existing status text
    // instead of silently pretending the (possibly incomplete) sensor list is the
    // whole CPU. The numbers stay whatever the sensors really returned.
    private static string WithTraversalHint(string status, TreeWalkOutcome traversal)
    {
        if (!traversal.WasTruncated)
            return status;
        var cut = traversal.DepthLimitReached && traversal.NodeBudgetExhausted
            ? "节点与深度"
            : traversal.DepthLimitReached ? "深度" : "节点";
        return Sanitize($"{status}（LHM 硬件遍历已触及{cut}上限：{traversal.VisitedNodes} 个节点，读数可能不完整）");
    }

    private static LhmSensorCategory MapCategory(SensorType sensorType) => sensorType switch
    {
        SensorType.Temperature => LhmSensorCategory.Temperature,
        SensorType.Power => LhmSensorCategory.Power,
        _ => LhmSensorCategory.Other
    };

    private static string DescribeReading(LhmSensorSelection temperature, LhmSensorSelection power)
    {
        if (temperature.IsSelected && power.IsSelected)
            return Sanitize($"LHM 正常（温度 {Describe(temperature, "°C")}，功耗 {Describe(power, " W")}）");
        if (temperature.IsSelected)
            return Sanitize($"LHM 仅温度可用（{Describe(temperature, "°C")}，未找到 CPU Package 功耗传感器）");
        return Sanitize($"LHM 仅功耗可用（{Describe(power, " W")}，未找到 CPU Package 温度传感器）");
    }

    // Sensor name plus unit, so a wrong pick (温度被当成功耗) is visible in the
    // status text during a manual 60-second comparison.
    private static string Describe(LhmSensorSelection selection, string unit)
    {
        var value = selection.Value?.ToString("0.0") ?? "--";
        return $"{selection.SensorName ?? "未知"} {value}{unit}";
    }

    private static string DescribeNoCpuSensor()
    {
        var pawnIo = PawnIoHint();
        return Sanitize($"LHM 未发现 CPU 传感器{pawnIo}");
    }

    private static string DescribeNoSelection(LhmSensorSelection temperature, LhmSensorSelection power)
    {
        var reasons = new List<string>();
        if (temperature.State == LhmSensorSelectionState.RejectedValue)
            reasons.Add("温度值无效");
        if (power.State == LhmSensorSelectionState.RejectedValue)
            reasons.Add("功耗值无效");
        if (reasons.Count == 0)
            return Sanitize($"LHM 无可用的 CPU Package 传感器（已按温度/功耗类别筛选）{PawnIoHint()}");
        // 0 °C / 0 W is what a driver-less LHM returns for sensors it cannot read, so
        // the reason is spelled out instead of showing a pseudo-healthy value.
        return Sanitize($"LHM CPU 传感器已忽略：{string.Join("、", reasons)}（数值为 0 或超出合理范围）{PawnIoHint()}");
    }

    private static string PawnIoHint()
    {
        try
        {
            return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled
                ? string.Empty
                : "（可选驱动 PawnIO 未安装，本程序不会安装）";
        }
        catch
        {
            return string.Empty;
        }
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
