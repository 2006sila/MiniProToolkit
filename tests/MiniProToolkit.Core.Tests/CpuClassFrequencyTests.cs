using System.Buffers.Binary;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class CpuClassFrequencyTests
{
    private static byte[] Record(uint id, ushort group, byte logical, byte core, byte efficiency, int size = 32)
    {
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)size);
        if (size >= 32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), id);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), group);
            bytes[14] = logical;
            bytes[15] = core;
            bytes[18] = efficiency;
        }
        return bytes;
    }

    private static CpuClassProcessor Cpu(ushort group, byte logical, byte efficiency, byte? core = null) =>
        new(new(group, logical), core ?? logical, efficiency);
    private static CpuClassFrequencySample Sample(ushort group, byte logical, double? mhz) => new(new(group, logical), mhz);

    [Fact]
    public void ParsesExactKeysAcrossGroupsAndPreservesCoreAndEfficiency()
    {
        byte[] bytes = [.. Record(100, 0, 0, 3, 0), .. Record(101, 1, 0, 7, 9)];
        Assert.True(CpuClassFrequency.TryParseTopology(bytes, out var topology, out var status));
        Assert.Null(status);
        Assert.Equal(new CpuClassProcessor(new(0, 0), 3, 0), topology[0]);
        Assert.Equal(new CpuClassProcessor(new(1, 0), 7, 9), topology[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(40)]
    [InlineData(uint.MaxValue)]
    public void RejectsInvalidOrTruncatedRecordSizes(uint size)
    {
        var bytes = Record(1, 0, 0, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, size);
        Assert.False(CpuClassFrequency.TryParseTopology(bytes, out var topology, out _));
        Assert.Empty(topology);
    }

    [Fact]
    public void RejectsEmptyTruncatedOversizedAndTrailingBuffersWithoutPartialTopology()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[7],
                     new byte[CpuClassFrequency.MaximumTopologyBytes + 1],
                     Record(1, 0, 0, 0, 0)[..31],
                     Record(1, 0, 0, 0, 0).Concat(new byte[1]).ToArray() })
        {
            Assert.False(CpuClassFrequency.TryParseTopology(bytes, out var topology, out _));
            Assert.Empty(topology);
        }
    }

    [Fact]
    public void SkipsValidatedUnknownTypesAndAcceptsExtendedCpuSetRecords()
    {
        var unknown = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(unknown, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(4), 77);
        Assert.True(CpuClassFrequency.TryParseTopology([.. unknown, .. Record(1, 0, 0, 0, 5, 40)], out var topology, out _));
        Assert.Single(topology);
        Assert.False(CpuClassFrequency.TryParseTopology(unknown, out _, out _));
        BinaryPrimitives.WriteUInt32LittleEndian(unknown, 16);
        Assert.False(CpuClassFrequency.TryParseTopology(unknown, out _, out _));
    }

    [Fact]
    public void RejectsDuplicateKeysIdsIllegalLogicalIndexAndConflictingCoreClasses()
    {
        foreach (var bytes in new[] {
            Record(1, 0, 0, 0, 0).Concat(Record(2, 0, 0, 0, 0)).ToArray(),
            Record(1, 0, 0, 0, 0).Concat(Record(1, 1, 0, 0, 0)).ToArray(),
            Record(1, 0, 64, 0, 0),
            Record(1, 0, 0, 0, 0).Concat(Record(2, 0, 1, 0, 1)).ToArray() })
            Assert.False(CpuClassFrequency.TryParseTopology(bytes, out _, out _));
    }

    [Theory]
    [InlineData("0,0", 0, 0)]
    [InlineData("1,0", 1, 0)]
    [InlineData("65535,63", 65535, 63)]
    public void MapsOnlyExactGroupLogicalInstances(string name, int group, int logical)
    {
        Assert.True(CpuClassFrequency.TryParseInstance(name, out var key));
        Assert.Equal(new CpuTopologyKey((ushort)group, (byte)logical), key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("0,_Total")]
    [InlineData("core0")]
    [InlineData("0,0#1")]
    [InlineData("0,0,1")]
    [InlineData(" 0,0")]
    [InlineData("0,+1")]
    [InlineData("0,64")]
    [InlineData("65536,0")]
    public void RejectsGuessedAggregateOrMalformedInstanceNames(string? name) =>
        Assert.False(CpuClassFrequency.TryParseInstance(name, out _));

    [Fact]
    public void HomogeneousCpuIsExplicitlyNotApplicableNeverIntelPE()
    {
        var reading = CpuClassFrequency.Calculate([Cpu(0, 0, 0), Cpu(0, 1, 0)], [Sample(0, 0, 3500)]);
        Assert.Equal(CpuClassFrequencyState.NotApplicable, reading.State);
        Assert.Contains("同构", reading.Status);
        Assert.Equal("Windows 效率等级 0", Assert.Single(reading.Groups).Name);
        Assert.Null(reading.Groups[0].AverageMhz);
    }

    [Fact]
    public void HeterogeneousClassesUseWindowsNamesAndLogicalSampleMeansAcrossGroups()
    {
        var reading = CpuClassFrequency.Calculate(
            [Cpu(0, 0, 9), Cpu(1, 0, 9), Cpu(1, 1, 0)],
            [Sample(1, 1, 2000), Sample(1, 0, 4000.5), Sample(0, 0, 3000.5)]);
        Assert.Equal(CpuClassFrequencyState.Available, reading.State);
        Assert.Equal("Windows 效率等级 0", reading.Groups[0].Name);
        var group = reading.Groups[1];
        Assert.Equal("Windows 效率等级 9", group.Name);
        Assert.Equal(2, group.SampleCount);
        Assert.Equal(2, group.ExpectedSampleCount);
        Assert.Equal(3500.5, group.AverageMhz);
        Assert.Equal(3000.5, group.MinimumMhz);
        Assert.Equal(4000.5, group.MaximumMhz);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(99d)]
    [InlineData(10001d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidAndMissingSamplesProducePartialOrUnavailableNeverZero(double? invalid)
    {
        var reading = CpuClassFrequency.Calculate(
            [Cpu(0, 0, 0), Cpu(0, 1, 0), Cpu(1, 0, 1)],
            [Sample(0, 0, 3000), Sample(0, 1, invalid)]);
        Assert.Equal(CpuClassFrequencyState.Partial, reading.State);
        Assert.Equal(CpuClassFrequencyState.Partial, reading.Groups[0].State);
        Assert.Equal(1, reading.Groups[0].SampleCount);
        Assert.Equal(3000d, reading.Groups[0].AverageMhz);
        Assert.Equal(CpuClassFrequencyState.Unavailable, reading.Groups[1].State);
        Assert.Null(reading.Groups[1].AverageMhz);
        Assert.Null(reading.Groups[1].MinimumMhz);
        Assert.Null(reading.Groups[1].MaximumMhz);
    }

    [Fact]
    public void MissingSampleWithinClassDoesNotBiasMeanWithZero()
    {
        var reading = CpuClassFrequency.Calculate(
            [Cpu(0, 0, 0, 0), Cpu(0, 1, 0, 0), Cpu(1, 0, 1)],
            [Sample(0, 0, 100), Sample(1, 0, 10000)]);
        Assert.Equal(CpuClassFrequencyState.Partial, reading.State);
        Assert.Equal(2, reading.Groups[0].ExpectedSampleCount);
        Assert.Equal(1, reading.Groups[0].SampleCount);
        Assert.Equal(100d, reading.Groups[0].AverageMhz);
        Assert.Equal(10000d, reading.Groups[1].AverageMhz);
        Assert.Equal(CpuClassFrequencyState.Available, reading.Groups[1].State);
    }

    [Fact]
    public void MissingAllCountersLeavesEachHeterogeneousGroupUnavailable()
    {
        var reading = CpuClassFrequency.Calculate([Cpu(0, 0, 0), Cpu(1, 0, 1)], []);
        Assert.Equal(CpuClassFrequencyState.Unavailable, reading.State);
        Assert.All(reading.Groups, g => { Assert.Null(g.AverageMhz); Assert.Equal(0, g.SampleCount); });
    }

    [Fact]
    public void DuplicateUnknownOrInconsistentTopologyInvalidatesAllGroups()
    {
        CpuClassProcessor[] topology = [Cpu(0, 0, 0), Cpu(1, 0, 1)];
        foreach (var samples in new[] {
            new[] { Sample(0, 0, 3000), Sample(0, 0, 4000) },
            new[] { Sample(2, 0, 3000) } })
        {
            var reading = CpuClassFrequency.Calculate(topology, samples);
            Assert.Equal(CpuClassFrequencyState.Unavailable, reading.State);
            Assert.All(reading.Groups, g => Assert.Null(g.AverageMhz));
        }
        Assert.Equal(CpuClassFrequencyState.Unavailable,
            CpuClassFrequency.Calculate(topology, [], topologyConsistent: false).State);
        Assert.Equal(CpuClassFrequencyState.Unavailable,
            CpuClassFrequency.Calculate([Cpu(0, 0, 0), Cpu(0, 0, 1)], []).State);
        Assert.Equal(CpuClassFrequencyState.Unavailable, CpuClassFrequency.Calculate([], []).State);
    }

    [Fact]
    public void StatusIsRedactedSingleLineAndBounded()
    {
        var reading = CpuClassFrequency.Calculate([Cpu(0, 0, 0), Cpu(1, 0, 1)], [],
            "token=abc; C:\\Users\\private\\file\r\n" + new string('x', 300));
        Assert.DoesNotContain("abc", reading.Status);
        Assert.DoesNotContain("private", reading.Status);
        Assert.DoesNotContain("\n", reading.Status);
        Assert.True(reading.Status!.Length <= 161);
        Assert.All(reading.Groups, group => Assert.Equal(reading.Status, group.Status));
    }
}
