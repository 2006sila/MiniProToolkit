using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class CpuClassFrequencyTelemetryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string OriginalCsvHeader = "Timestamp,CpuTemperatureC,Fan1Rpm,Fan2Rpm,RequestedTargetRpm,TargetRpm,TargetDuty,RpmError,RpmFeedbackState,FanFault,CpuUsagePercent,MemoryUsagePercent,CpuPackagePowerWatts,PowerMode,FanControlMode,Error,Event,LhmCpuPackageTemperatureC,LhmCpuPackagePowerWatts,LhmStatus,DisplayCpuTemperatureC,DisplayCpuPackagePowerWatts,DisplayCpuSource,DisplayTemperatureFromLhm,DisplayPowerFromLhm,DisplaySourceStatus,CpuFrequencyMhz,CpuFrequencyPercentOfBase,CpuFrequencyBaseMhz,CpuFrequencySource,CpuFrequencyStatus";

    private static HardwareSnapshot Snapshot() => new(
        DateTimeOffset.UtcNow, 63, 2400, 0, PowerMode.Balance, 20, 40,
        FanControlMode.FixedRpm, 45, null)
    {
        CpuPackagePowerWatts = 18,
        CpuFrequencyMhz = 3200,
        RequestedTargetRpm = 2500,
        TargetRpm = 2400,
        MotherboardTemperatureC = 37,
        StorageTemperatureC = 42,
        AuxTemperatureStatus = "aux intact"
    };

    private static CpuClassFrequencyReading Reading(CpuClassFrequencyState state) => new(state,
        new CpuClassFrequencyGroup[]
        {
            new(0, state, 4, state == CpuClassFrequencyState.Available ? 4 : 2, 2100, 2000, 2200, null),
            new(8, CpuClassFrequencyState.Available, 8, 8, 3500, 3400, 3600, null)
        }, state == CpuClassFrequencyState.Partial ? "样本不完整" : null);

    [Theory]
    [InlineData(CpuClassFrequencyState.Available)]
    [InlineData(CpuClassFrequencyState.Partial)]
    [InlineData(CpuClassFrequencyState.Unavailable)]
    [InlineData(CpuClassFrequencyState.NotApplicable)]
    public void FromSnapshotAndJsonRoundTripPreserveReading(CpuClassFrequencyState state)
    {
        var reading = state == CpuClassFrequencyState.Unavailable
            ? CpuClassFrequencyReading.Unavailable("本次不可用")
            : state == CpuClassFrequencyState.NotApplicable
                ? new(state, new[] { new CpuClassFrequencyGroup(2, state, 16, 0, null, null, null, "同构") }, "同构")
                : Reading(state);
        var snapshot = Snapshot() with { CpuClassFrequency = reading };
        var record = HardwareTelemetryRecord.FromSnapshot(snapshot);
        Assert.Same(reading, record.CpuClassFrequency);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        Assert.Contains("\"cpuClassFrequency\":", json);
        Assert.DoesNotContain("cpuClassFrequencyText", json);
        var restored = JsonSerializer.Deserialize<HardwareTelemetryRecord>(json, JsonOptions)!;
        Assert.Equal(state, restored.CpuClassFrequency!.State);
        Assert.Equal(reading.Status, restored.CpuClassFrequency.Status);
        Assert.Equal(reading.Groups.ToArray(), restored.CpuClassFrequency.Groups.ToArray());
        Assert.Equal(37, restored.MotherboardTemperatureC);
        Assert.Equal(42, restored.StorageTemperatureC);
        Assert.Equal("aux intact", restored.AuxTemperatureStatus);
    }

    [Fact]
    public void OptionalFieldAllowsOldJsonAndNullSnapshots()
    {
        Assert.Null(Snapshot().CpuClassFrequency);
        var record = JsonSerializer.Deserialize<HardwareTelemetryRecord>("{\"timestamp\":\"2026-01-01T00:00:00Z\",\"cpuTemperatureC\":52}", JsonOptions)!;
        Assert.Null(record.CpuClassFrequency);
        Assert.Contains("不可用", record.CpuClassFrequencyText);
        Assert.Equal(52, record.CpuTemperatureC);
    }

    [Fact]
    public void CsvHeaderAndAll31CellsStayByteIdenticalWithClassAndAuxData()
    {
        var original = HardwareTelemetryRecord.FromSnapshot(Snapshot());
        var added = HardwareTelemetryRecord.FromSnapshot(Snapshot() with { Timestamp = original.Timestamp, CpuClassFrequency = Reading(CpuClassFrequencyState.Partial) });
        Assert.Equal(OriginalCsvHeader, TelemetryRecorder.CsvHeader);
        Assert.Equal(31, TelemetryRecorder.CsvHeader.Split(',').Length);
        Assert.Equal(31, TelemetryRecorder.ToCsv(added).Split(',').Length);
        Assert.Equal(TelemetryRecorder.ToCsv(original), TelemetryRecorder.ToCsv(added));
        Assert.Equal(original.CpuTemperatureC, added.CpuTemperatureC);
        Assert.Equal(original.CpuPackagePowerWatts, added.CpuPackagePowerWatts);
        Assert.Equal(original.FanControlMode, added.FanControlMode);
        Assert.Equal(original.TargetRpm, added.TargetRpm);
        Assert.Equal(original.TargetDuty, added.TargetDuty);
    }

    [Fact]
    public void FormatterShowsWindowsClassesMhzAndValidExpectedSamples()
    {
        var text = HardwareTelemetryRecord.FormatCpuClassFrequency(Reading(CpuClassFrequencyState.Partial));
        Assert.Contains("Partial", text);
        Assert.Contains("Windows 效率等级 0: 2100 MHz", text);
        Assert.Contains("样本 2/4", text);
        Assert.Contains("Windows 效率等级 8: 3500 MHz", text);
        Assert.Contains("样本 8/8", text);
        Assert.Contains("样本不完整", text);
        Assert.DoesNotContain("Intel", text);
        Assert.Contains("有效", HardwareTelemetryRecord.FormatCpuClassFrequency(Reading(CpuClassFrequencyState.Available)));
        var homogeneous = new CpuClassFrequencyReading(CpuClassFrequencyState.NotApplicable,
            new[] { new CpuClassFrequencyGroup(0, CpuClassFrequencyState.NotApplicable, 16, 0, null, null, null, null) }, null);
        var homogeneousText = HardwareTelemetryRecord.FormatCpuClassFrequency(homogeneous);
        Assert.Contains("不适用（同构", homogeneousText);
        Assert.Contains("-- MHz", homogeneousText);
        Assert.Contains("0/16", homogeneousText);
        Assert.Contains("不可用", HardwareTelemetryRecord.FormatCpuClassFrequency(CpuClassFrequencyReading.Unavailable("本次失败")));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("[null]", false)]
    [InlineData("[null,{\"efficiencyClass\":8,\"state\":2,\"expectedSampleCount\":4,\"sampleCount\":4,\"averageMhz\":3500},null]", true)]
    public void MalformedNullableGroupsRoundTripWithoutBindingExceptions(string groupsJson, bool hasValidGroup)
    {
        var json = "{\"cpuClassFrequency\":{\"state\":2,\"groups\":" + groupsJson + "}}";
        var record = JsonSerializer.Deserialize<HardwareTelemetryRecord>(json, JsonOptions)!;
        var text = record.CpuClassFrequencyText;
        var restored = JsonSerializer.Deserialize<HardwareTelemetryRecord>(
            JsonSerializer.Serialize(record, JsonOptions), JsonOptions)!;
        Assert.Equal(text, restored.CpuClassFrequencyText);
        Assert.NotNull(restored.CpuClassFrequency);
        if (groupsJson == "null") Assert.Null(restored.CpuClassFrequency.Groups);
        else Assert.Contains(restored.CpuClassFrequency.Groups, group => group is null);
        if (hasValidGroup)
        {
            Assert.Contains("Windows 效率等级 8: 3500 MHz", text);
            Assert.Contains("样本 4/4", text);
        }
        else
        {
            Assert.StartsWith("不可用", text);
            Assert.DoesNotContain("MHz", text);
        }
    }

    [Fact]
    public void UnknownStateEnumsDegradeToUnavailableAfterJsonRoundTrip()
    {
        const string json = "{\"cpuClassFrequency\":{\"state\":999,\"groups\":[{\"efficiencyClass\":8,\"state\":-1,\"expectedSampleCount\":4,\"sampleCount\":4,\"averageMhz\":3500}]}}";
        var record = JsonSerializer.Deserialize<HardwareTelemetryRecord>(json, JsonOptions)!;
        var restored = JsonSerializer.Deserialize<HardwareTelemetryRecord>(JsonSerializer.Serialize(record, JsonOptions), JsonOptions)!;
        Assert.StartsWith("不可用", restored.CpuClassFrequencyText);
        Assert.Contains("-- MHz", restored.CpuClassFrequencyText);
        Assert.Contains("（不可用）", restored.CpuClassFrequencyText);
        Assert.DoesNotContain("3500", restored.CpuClassFrequencyText);
        Assert.DoesNotContain("999", restored.CpuClassFrequencyText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormatterBoundsInputBeforeFilteringNullGroups(bool nullPrefix)
    {
        var group = new CpuClassFrequencyGroup(0, CpuClassFrequencyState.Available, 1, 1, 2100, 2100, 2100, null);
        var groups = Enumerable.Repeat(nullPrefix ? null! : group, 256)
            .Append(group with { EfficiencyClass = 8, AverageMhz = 3500 }).ToArray();
        var record = HardwareTelemetryRecord.FromSnapshot(Snapshot() with
        {
            CpuClassFrequency = new(CpuClassFrequencyState.Available, groups, null)
        });
        var restored = JsonSerializer.Deserialize<HardwareTelemetryRecord>(JsonSerializer.Serialize(record, JsonOptions), JsonOptions)!;
        var text = restored.CpuClassFrequencyText;
        Assert.DoesNotContain("Windows 效率等级 8", text);
        Assert.DoesNotContain("3500", text);
        Assert.Contains("仅显示前 256 组", text);
        Assert.Equal(nullPrefix ? 0 : 256, text.Split("Windows 效率等级", StringSplitOptions.None).Length - 1);
        if (nullPrefix) Assert.StartsWith("不可用", text);
    }

    [Fact]
    public void FailedSampleCannotLeakPreviousSnapshotClassData()
    {
        var previous = Snapshot() with { CpuClassFrequency = Reading(CpuClassFrequencyState.Available) };
        var failed = previous with { CpuClassFrequency = CpuClassFrequencyReading.Unavailable("本次失败") };
        var record = HardwareTelemetryRecord.FromSnapshot(failed);
        Assert.Equal(CpuClassFrequencyState.Unavailable, record.CpuClassFrequency!.State);
        Assert.Empty(record.CpuClassFrequency.Groups);
        Assert.DoesNotContain("3500", record.CpuClassFrequencyText);
        Assert.Null(HardwareTelemetryRecord.FromSnapshot(HardwareSnapshot.ErrorSnapshot("failed pass")).CpuClassFrequency);
    }

    [Fact]
    public void AppWiringUsesBackgroundPassDemoGuardAndFreshLifecycleWithoutHardwareAccess()
    {
        // Read source only. These tests never reference/instantiate Hardware or open counters.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "MiniProToolkit.App", "MainWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "MiniProToolkit.App", "MainWindow.xaml.cs"));
        Assert.Contains("_cpuClassFrequencyReader = demo ? null : new CpuClassFrequencyReader();", source);
        var task = source.IndexOf("var state = await Task.Run(() =>", StringComparison.Ordinal);
        var attach = source.IndexOf("snapshot = AttachCpuClassFrequency(snapshot);", task, StringComparison.Ordinal);
        var publish = source.IndexOf("DisplaySnapshot(state.snapshot)", task, StringComparison.Ordinal);
        Assert.True(task >= 0 && attach > task && publish > attach);
        Assert.Contains("ReinitializeCpuClassFrequencyReader();", source[task..attach]);
        var method = source[source.IndexOf("private HardwareSnapshot AttachCpuClassFrequency(", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("private HardwareSnapshot AttachCpuFrequency(", StringComparison.Ordinal)];
        Assert.Contains("if (_demo) return snapshot with { CpuClassFrequency = null };", method);
        Assert.Contains("_cpuClassFrequencyReader?.Read()", method);
        Assert.Contains("return snapshot with { CpuClassFrequency = reading };", method);
        Assert.DoesNotContain(".LastReading", method);
        Assert.Contains("CpuClassFrequencyReading.Unavailable", method);
        var failedExit = source[source.IndexOf("_cpuFrequencyReinitializeRequested = true;", StringComparison.Ordinal)..];
        Assert.Contains("_cpuClassFrequencyReinitializeRequested = true;", failedExit[..300]);
        var resume = source[source.IndexOf("private async Task RunResumeRefreshAsync()", StringComparison.Ordinal)..];
        Assert.Contains("_cpuClassFrequencyReinitializeRequested = true;", resume[..resume.IndexOf("RefreshSnapshotAsync(", StringComparison.Ordinal)]);
        Assert.Contains("try { _cpuClassFrequencyReader?.Dispose(); }", source);
        Assert.Contains("finally { _cpuClassFrequencyReader = null; }", source);
    }
}
