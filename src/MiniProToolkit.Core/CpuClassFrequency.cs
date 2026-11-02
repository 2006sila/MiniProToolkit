using System.Buffers.Binary;
using System.Globalization;

namespace MiniProToolkit.Core;

public readonly record struct CpuTopologyKey(ushort Group, byte LogicalProcessorIndex);
public sealed record CpuClassProcessor(CpuTopologyKey Key, byte CoreIndex, byte EfficiencyClass);
public readonly record struct CpuClassFrequencySample(CpuTopologyKey Key, double? Mhz);

public enum CpuClassFrequencyState { Unavailable, NotApplicable, Available, Partial }

/// <summary>Statistics over logical processors, not physical cores. MHz is the Windows
/// Processor Frequency counter, not an APERF/MPERF effective-clock measurement.</summary>
public sealed record CpuClassFrequencyGroup(
    byte EfficiencyClass, CpuClassFrequencyState State, int ExpectedSampleCount, int SampleCount,
    double? AverageMhz, double? MinimumMhz, double? MaximumMhz, string? Status)
{
    public string Name => $"Windows 效率等级 {EfficiencyClass}";
}

public sealed record CpuClassFrequencyReading(
    CpuClassFrequencyState State, IReadOnlyList<CpuClassFrequencyGroup> Groups, string? Status)
{
    public static CpuClassFrequencyReading Unavailable(string status) =>
        new(CpuClassFrequencyState.Unavailable, Array.Empty<CpuClassFrequencyGroup>(), CpuClassFrequency.SanitizeStatus(status));
}

/// <summary>Pure CPU-set parser and aggregation. No native calls, counters or vendor inference.</summary>
public static class CpuClassFrequency
{
    public const int MaximumTopologyBytes = 1024 * 1024;
    private const int CpuSetRecordMinimumSize = 32;

    /// <summary>Parses SYSTEM_CPU_SET_INFORMATION's variable-sized records (Type 0).
    /// Unknown types are skipped only after their complete header and size are validated.
    /// Failure never exposes a partially parsed topology.</summary>
    public static bool TryParseTopology(ReadOnlySpan<byte> buffer,
        out IReadOnlyList<CpuClassProcessor> processors, out string? status)
    {
        processors = Array.Empty<CpuClassProcessor>();
        status = "CPU 拓扑缓冲无效。";
        if (buffer.IsEmpty || buffer.Length > MaximumTopologyBytes) return false;
        var result = new List<CpuClassProcessor>();
        var keys = new HashSet<CpuTopologyKey>();
        var ids = new HashSet<uint>();
        var coreClasses = new Dictionary<(ushort, byte), byte>();
        int offset = 0;
        while (offset < buffer.Length)
        {
            var record = buffer[offset..];
            if (record.Length < 8) return false;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            if (size < 8 || size > (uint)record.Length || (size & 7) != 0) return false;
            if (type == 0)
            {
                if (size < CpuSetRecordMinimumSize) return false;
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
                ushort group = BinaryPrimitives.ReadUInt16LittleEndian(record[12..]);
                byte logical = record[14], core = record[15], efficiency = record[18];
                var key = new CpuTopologyKey(group, logical);
                if (logical >= 64 || !keys.Add(key) || !ids.Add(id)) return false;
                if (coreClasses.TryGetValue((group, core), out var previous) && previous != efficiency)
                    return false;
                coreClasses[(group, core)] = efficiency;
                result.Add(new(key, core, efficiency));
            }
            offset += (int)size;
        }
        if (result.Count == 0) return false;
        processors = result.AsReadOnly();
        status = null;
        return true;
    }

    /// <summary>Accepts only the documented group,logical-index instance shape;
    /// never guesses from core names or accepts aggregate instances.</summary>
    public static bool TryParseInstance(string? instance, out CpuTopologyKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(instance)) return false;
        int comma = instance.IndexOf(',');
        if (comma <= 0 || comma == instance.Length - 1) return false;
        var groupText = instance.AsSpan(0, comma);
        var logicalText = instance.AsSpan(comma + 1);
        if (!ushort.TryParse(groupText, NumberStyles.None, CultureInfo.InvariantCulture, out var group) ||
            !byte.TryParse(logicalText, NumberStyles.None, CultureInfo.InvariantCulture, out var logical) || logical >= 64)
            return false;
        key = new(group, logical);
        return true;
    }

    public static CpuClassFrequencyReading Calculate(IReadOnlyList<CpuClassProcessor> topology,
        IEnumerable<CpuClassFrequencySample> samples, string? status = null, bool topologyConsistent = true)
    {
        string? cleanStatus = SanitizeStatus(status);
        if (topology.Count == 0) return CpuClassFrequencyReading.Unavailable(cleanStatus ?? "CPU 拓扑不可用。");
        var keys = new HashSet<CpuTopologyKey>();
        var cores = new Dictionary<(ushort, byte), byte>();
        foreach (var cpu in topology)
        {
            if (cpu.Key.LogicalProcessorIndex >= 64 || !keys.Add(cpu.Key) ||
                (cores.TryGetValue((cpu.Key.Group, cpu.CoreIndex), out var efficiency) && efficiency != cpu.EfficiencyClass))
                topologyConsistent = false;
            cores[(cpu.Key.Group, cpu.CoreIndex)] = cpu.EfficiencyClass;
        }
        var values = new Dictionary<CpuTopologyKey, double?>();
        foreach (var sample in samples)
        {
            if (!keys.Contains(sample.Key) || !values.TryAdd(sample.Key, ValidMhz(sample.Mhz)))
                topologyConsistent = false;
        }
        var classes = topology.GroupBy(p => p.EfficiencyClass).OrderBy(g => g.Key).ToArray();
        var groups = new List<CpuClassFrequencyGroup>();
        if (!topologyConsistent)
        {
            string mismatch = cleanStatus ?? "CPU 拓扑与计数器实例不一致，本次不可用。";
            foreach (var group in classes)
                groups.Add(new(group.Key, CpuClassFrequencyState.Unavailable, group.Count(), 0, null, null, null, mismatch));
            return new(CpuClassFrequencyState.Unavailable, groups.AsReadOnly(), mismatch);
        }
        if (classes.Length == 1)
        {
            const string homogeneous = "不适用：Windows 仅报告一个效率等级（同构），不推断 Intel P/E。";
            groups.Add(new(classes[0].Key, CpuClassFrequencyState.NotApplicable, topology.Count, 0, null, null, null, homogeneous));
            return new(CpuClassFrequencyState.NotApplicable, groups.AsReadOnly(), homogeneous);
        }
        foreach (var group in classes)
        {
            var valid = group.Select(p => values.GetValueOrDefault(p.Key)).OfType<double>().ToArray();
            var state = valid.Length == 0 ? CpuClassFrequencyState.Unavailable :
                valid.Length == group.Count() ? CpuClassFrequencyState.Available : CpuClassFrequencyState.Partial;
            groups.Add(new(group.Key, state, group.Count(), valid.Length,
                valid.Length == 0 ? null : valid.Average(), valid.Length == 0 ? null : valid.Min(),
                valid.Length == 0 ? null : valid.Max(), state == CpuClassFrequencyState.Available ? null :
                cleanStatus ?? "部分或全部逻辑处理器缺少有效 MHz 样本；未使用 0 填充。"));
        }
        var overall = groups.All(g => g.State == CpuClassFrequencyState.Available) ? CpuClassFrequencyState.Available :
            groups.All(g => g.State == CpuClassFrequencyState.Unavailable) ? CpuClassFrequencyState.Unavailable : CpuClassFrequencyState.Partial;
        return new(overall, groups.AsReadOnly(), overall == CpuClassFrequencyState.Available ? null :
            cleanStatus ?? "Windows 效率等级频率样本不完整。");
    }

    private static double? ValidMhz(double? value) => value is { } v && double.IsFinite(v) &&
        v >= CpuFrequencyNormalizer.MinimumPlausibleMhz && v <= CpuFrequencyNormalizer.MaximumPlausibleMhz ? v : null;

    public static string? SanitizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        string clean = DiagnosticRedactor.Redact(status).Replace('\r', ' ').Replace('\n', ' ');
        return clean.Length <= 160 ? clean : clean[..160] + "…";
    }
}
