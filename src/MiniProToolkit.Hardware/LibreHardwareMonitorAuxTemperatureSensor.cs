using LibreHardwareMonitor.Hardware;
using MiniProToolkit.Core;

namespace MiniProToolkit.Hardware;

public readonly record struct LhmAuxTemperatureReading(
    AuxTemperatureSelection Motherboard, AuxTemperatureSelection Storage, string Status)
{
    public static LhmAuxTemperatureReading Unknown(string status) =>
        new(AuxTemperatureSelection.Unknown, AuxTemperatureSelection.Unknown, status);
}

/// <summary>
/// Independent optional board/storage reader. Opens lazily on the background pass.
/// Only Temperature values are inspected; no Control, PWM, driver installation or elevation API is used.
/// Missing LHM/PawnIO permissions are an unknown reading, never an invented fallback to CPU/EC.
/// </summary>
public sealed class LibreHardwareMonitorAuxTemperatureSensor : IDisposable
{
    private readonly object _sync = new();
    private Computer? _computer;
    private bool _disposed;
    private int _failures;
    private DateTimeOffset _nextAttempt;
    private string? _boardId, _storageId;
    private LhmAuxTemperatureReading _last = LhmAuxTemperatureReading.Unknown("Unknown：尚未采样");

    public LhmAuxTemperatureReading LastReading { get { lock (_sync) return _last; } }

    public LhmAuxTemperatureReading Read()
    {
        // Open, Update and Close share the very same lock (including the exception path).
        lock (_sync)
        {
            if (_disposed || DateTimeOffset.UtcNow < _nextAttempt) return _last;
            try
            {
                EnsureOpen();
                var values = Collect(_computer!);
                var board = AuxTemperatureSelector.Select(values, AuxTemperatureKind.Motherboard, _boardId);
                var storage = AuxTemperatureSelector.Select(values, AuxTemperatureKind.Storage, _storageId);
                if (board.IsKnown) _boardId = board.SensorId;
                if (storage.IsKnown) _storageId = storage.SensorId;
                var complete = board.IsKnown && storage.IsKnown;
                var retry = AuxTemperatureRetryPolicy.AfterSample(board.IsKnown, storage.IsKnown, _failures);
                _failures = retry.Failures;
                _nextAttempt = DateTimeOffset.UtcNow + retry.Delay;
                return _last = new(board, storage, Sanitize(complete
                    ? "LHM 主板/存储只读正常"
                    : $"LHM 只读：主板 {(board.IsKnown ? "正常" : "Unknown")}；存储 {(storage.IsKnown ? "正常" : "Unknown")}（无合理读数不替代 CPU）"));
            }
            catch (Exception ex)
            {
                _failures = Math.Min(_failures + 1, 7);
                _nextAttempt = DateTimeOffset.UtcNow + AuxTemperatureRetryPolicy.FailureDelay(_failures);
                DropComputer();
                // Exception type only: no device model, serial number, path or credential in telemetry.
                return _last = LhmAuxTemperatureReading.Unknown(Sanitize($"Unknown：LHM 辅助温度不可用（{ex.GetType().Name}）"));
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DropComputer();
            _last = LhmAuxTemperatureReading.Unknown("Unknown：读取器已关闭");
        }
    }

    private void EnsureOpen()
    {
        if (_computer is not null) return;
        var computer = new Computer
        {
            IsMotherboardEnabled = true, IsStorageEnabled = true,
            IsCpuEnabled = false, IsGpuEnabled = false, IsNetworkEnabled = false,
            IsMemoryEnabled = false, IsControllerEnabled = false, IsBatteryEnabled = false,
            IsPsuEnabled = false, IsPowerMonitorEnabled = false
        };
        // Assign before opening so a partially opened instance is also closed on failure.
        _computer = computer;
        computer.Open();
    }

    private void DropComputer()
    {
        try { _computer?.Close(); }
        catch { /* optional data only */ }
        finally { _computer = null; }
    }

    private static List<AuxTemperatureCandidate> Collect(Computer computer)
    {
        var result = new List<AuxTemperatureCandidate>();
        var visited = new HashSet<IHardware>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(IHardware Node, AuxTemperatureKind Kind, string RootId, int Depth)>();
        foreach (var node in computer.Hardware)
        {
            var kind = Map(node.HardwareType);
            if (kind != AuxTemperatureKind.Unknown) pending.Push((node, kind, node.Identifier.ToString(), 0));
        }
        // Bound cycles, graph size and depth: no unbounded recursive subhardware walk.
        while (pending.Count > 0 && visited.Count < 256)
        {
            var (node, kind, rootId, depth) = pending.Pop();
            if (!visited.Add(node)) continue;
            var mapped = Map(node.HardwareType);
            if (mapped != kind) continue;
            node.Update();
            foreach (var sensor in node.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;
                result.Add(new(kind, rootId, sensor.Identifier.ToString(), sensor.Name,
                    sensor.Value, LhmSensorCategory.Temperature,
                    kind == AuxTemperatureKind.Storage && rootId.StartsWith("/nvme/", StringComparison.OrdinalIgnoreCase)));
            }
            if (depth >= 8) continue;
            foreach (var child in node.SubHardware)
            {
                if (pending.Count >= 256) break;
                pending.Push((child, kind, rootId, depth + 1));
            }
        }
        return result;
    }

    private static AuxTemperatureKind Map(HardwareType type) => type switch
    {
        HardwareType.Motherboard or HardwareType.SuperIO => AuxTemperatureKind.Motherboard,
        HardwareType.Storage => AuxTemperatureKind.Storage,
        _ => AuxTemperatureKind.Unknown
    };

    private static string Sanitize(string status)
    {
        var safe = DiagnosticRedactor.Redact(status);
        return safe.Length <= 160 ? safe : safe[..160];
    }
}
