using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniProToolkit.Core;

namespace MiniProToolkit.Hardware;

/// <summary>
/// Optional, read-only Windows efficiency-class frequency reader. Uses exact
/// Processor Information\Processor Frequency group,logical-index instances.
/// Does not use nominal MHz, vendor names, instance-name core heuristics or P/E labels.
/// The absolute counter is reported MHz, not a hardware effective-clock measurement.
/// Call from a background sampling thread; no native/counter access occurs in the constructor.
/// </summary>
public sealed class CpuClassFrequencyReader : IDisposable
{
    /// <summary>Resource bound, not a truncation rule. Above 256 logical processors
    /// all groups are unavailable; no subset is presented as the whole CPU.</summary>
    public const int MaximumCounters = 256;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TopologyRefreshInterval = TimeSpan.FromSeconds(60);
    private const string Category = "Processor Information";
    private const string CounterName = "Processor Frequency";
    private readonly object _sync = new();
    private readonly Dictionary<CpuTopologyKey, PerformanceCounter> _counters = new();
    private IReadOnlyList<CpuClassProcessor> _topology = Array.Empty<CpuClassProcessor>();
    private CpuClassFrequencyReading _lastReading = CpuClassFrequencyReading.Unavailable("CPU 效率等级频率尚未采样。");
    private TimeSpan? _lastRead;
    private TimeSpan _nextInitialization;
    private bool _initialized;
    private bool _consistent = true;
    private bool _disposed;
    private string? _status;

    public CpuClassFrequencyReading LastReading { get { lock (_sync) return _lastReading; } }

    /// <summary>Serialized, at most once per second. Failures retry after 30 seconds,
    /// successful topology/counter sets refresh after 60 seconds. Dispose is terminal.</summary>
    public CpuClassFrequencyReading Read()
    {
        lock (_sync)
        {
            if (_disposed) return _lastReading;
            var now = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
            if (_lastRead is { } last && now - last < SampleInterval) return _lastReading;
            _lastRead = now;
            try
            {
                if (!_initialized || now >= _nextInitialization) Initialize(now);
                if (!_consistent)
                    return _lastReading = CpuClassFrequency.Calculate(_topology, [], _status, topologyConsistent: false);
                var samples = new List<CpuClassFrequencySample>(_counters.Count);
                foreach (var pair in _counters.ToArray())
                {
                    try
                    {
                        // PERF raw absolute MHz: unlike a rate counter this needs no
                        // fabricated baseline and no global nominal-clock conversion.
                        samples.Add(new(pair.Key, pair.Value.NextValue()));
                    }
                    catch (Exception)
                    {
                        SafeDispose(pair.Value);
                        _counters.Remove(pair.Key);
                        _status = "部分 CPU 频率计数器读取失败；将在退避后重试。";
                        _nextInitialization = Min(_nextInitialization, now + RetryInterval);
                    }
                }
                _lastReading = CpuClassFrequency.Calculate(_topology, samples, _status);
                if (_lastReading.State is CpuClassFrequencyState.Partial or CpuClassFrequencyState.Unavailable)
                    _nextInitialization = Min(_nextInitialization, now + RetryInterval);
                return _lastReading;
            }
            catch (Exception)
            {
                // Do not expose raw exception text (paths, machine names or secrets).
                DropCounters();
                _initialized = true;
                _nextInitialization = now + RetryInterval;
                _status = "CPU 效率等级频率读取失败；将在退避后重试。";
                return _lastReading = CpuClassFrequency.Calculate(_topology, [], _status);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DropCounters();
            _topology = Array.Empty<CpuClassProcessor>();
            _lastReading = CpuClassFrequencyReading.Unavailable("CPU 效率等级频率读取已关闭。");
        }
    }

    private void Initialize(TimeSpan now)
    {
        DropCounters();
        _topology = Array.Empty<CpuClassProcessor>();
        _consistent = true;
        _status = null;
        _initialized = true;
        _nextInitialization = now + RetryInterval;
        if (!OperatingSystem.IsWindows() || !TryReadTopology(out _topology))
        {
            _status = "Windows CPU Set 拓扑不可用或缓冲无效；将在退避后重试。";
            return;
        }
        if (_topology.Select(p => p.EfficiencyClass).Distinct().Count() == 1)
        {
            _nextInitialization = now + TopologyRefreshInterval;
            return; // Homogeneous topology needs no counters, even above 256 LPs.
        }
        if (_topology.Count > MaximumCounters)
        {
            _consistent = false;
            _status = "CPU 超过 256 个逻辑处理器的计数器资源上限；不截断或估算。";
            return;
        }
        var keys = _topology.Select(p => p.Key).ToHashSet();
        var category = new PerformanceCounterCategory(Category);
        var instances = new Dictionary<CpuTopologyKey, string>();
        foreach (string instance in category.GetInstanceNames())
        {
            if (!CpuClassFrequency.TryParseInstance(instance, out var key)) continue;
            if (!keys.Contains(key) || !instances.TryAdd(key, instance))
            {
                _consistent = false;
                _status = "CPU 拓扑与计数器实例不一致。";
                return;
            }
        }
        foreach (var pair in instances)
        {
            try
            {
                _counters.Add(pair.Key, new PerformanceCounter(Category, CounterName, pair.Value, readOnly: true));
            }
            catch (Exception)
            {
                _status = "部分 CPU 频率计数器不可用；将在退避后重试。";
            }
        }
        if (_counters.Count == _topology.Count)
            _nextInitialization = now + TopologyRefreshInterval;
        else
            _status = "缺少部分或全部 Processor Frequency 实例；不使用替代值。";
    }

    private static bool TryReadTopology(out IReadOnlyList<CpuClassProcessor> topology)
    {
        topology = Array.Empty<CpuClassProcessor>();
        const int insufficientBuffer = 122;
        bool success = GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint required, IntPtr.Zero, 0);
        if (success || Marshal.GetLastWin32Error() != insufficientBuffer) return false;
        // Bound both allocation and retries: topology can change between query and fill.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (required < 8 || required > CpuClassFrequency.MaximumTopologyBytes) return false;
            uint capacity = required;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)capacity));
            try
            {
                success = GetSystemCpuSetInformation(buffer, capacity, out required, IntPtr.Zero, 0);
                int error = Marshal.GetLastWin32Error();
                if (!success)
                {
                    if (error == insufficientBuffer && required > capacity) continue;
                    return false;
                }
                if (required == 0 || required > capacity) return false;
                var bytes = new byte[checked((int)required)];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return CpuClassFrequency.TryParseTopology(bytes, out topology, out _);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }

    private void DropCounters()
    {
        foreach (var counter in _counters.Values) SafeDispose(counter);
        _counters.Clear();
    }

    private static void SafeDispose(PerformanceCounter counter)
    {
        try { counter.Dispose(); } catch { /* Optional reader: cleanup is best effort. */ }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength,
        out uint returnedLength, IntPtr process, uint flags);
}
