using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed record HardwareTelemetryRecord(
    DateTimeOffset Timestamp,
    double? CpuTemperatureC,
    int? Fan1Rpm,
    int? Fan2Rpm,
    int? RequestedTargetRpm,
    int? TargetRpm,
    int? TargetDuty,
    int? RpmError,
    FanRpmFeedbackState RpmFeedbackState,
    string? FanFault,
    int? CpuUsagePercent,
    int? MemoryUsagePercent,
    double? CpuPackagePowerWatts,
    PowerMode PowerMode,
    FanControlMode FanControlMode,
    string? Error,
    string? Event)
{
    // Optional LibreHardwareMonitor CPU cross-check. The existing
    // CpuTemperatureC (EC) and CpuPackagePowerWatts (Energy Meter) keep their
    // original meaning; these fields are additive comparison data.
    public double? LhmCpuPackageTemperatureC { get; init; }
    public double? LhmCpuPackagePowerWatts { get; init; }
    public string? LhmStatus { get; init; }

    // Source of the user-visible CPU temperature/power for this record. The original
    // CpuTemperatureC (EC) and CpuPackagePowerWatts (Energy Meter) fields keep their
    // meaning, and the per-metric flags show whether each displayed value actually
    // came from LHM or fell back to the system source.
    public CpuDisplaySource DisplayCpuSource { get; init; } = CpuDisplaySource.EcEnergyMeter;
    public double? DisplayCpuTemperatureC { get; init; }
    public double? DisplayCpuPackagePowerWatts { get; init; }
    public bool DisplayTemperatureFromLhm { get; init; }
    public bool DisplayPowerFromLhm { get; init; }
    public string? DisplaySourceStatus { get; init; }

    // Read-only CPU clock. Every field is additive and optional: old JSONL rows deserialize
    // with nulls, and the CSV columns are appended after the display-source block so the
    // existing archive-on-mismatch rule still keeps old files intact.
    public double? CpuFrequencyMhz { get; init; }
    public double? CpuFrequencyPercentOfBase { get; init; }
    public double? CpuFrequencyBaseMhz { get; init; }
    public CpuFrequencySource CpuFrequencySource { get; init; } = CpuFrequencySource.Unavailable;
    public string? CpuFrequencyStatus { get; init; }

    // JSON-only read-only Windows efficiency-class frequencies. Old rows default to null;
    // CSV remains exactly the existing 31 columns and this data never drives EC control.
    public CpuClassFrequencyReading? CpuClassFrequency { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string CpuClassFrequencyText => FormatCpuClassFrequency(CpuClassFrequency);

    public static string FormatCpuClassFrequency(CpuClassFrequencyReading? reading)
    {
        if (reading is null) return "不可用（无类别频率记录）";
        static string StateText(CpuClassFrequencyState state) => state switch
        {
            CpuClassFrequencyState.Available => "有效",
            CpuClassFrequencyState.Partial => "Partial（部分有效）",
            CpuClassFrequencyState.NotApplicable => "不适用（同构，Windows 仅报告一个效率等级）",
            _ => "不可用"
        };
        // Historical JSON may contain null collections/elements despite the non-null model.
        // Bound before filtering so even a long run of null elements cannot cause unbounded work.
        var groups = reading.Groups?.Take(256).OfType<CpuClassFrequencyGroup>().ToArray()
            ?? Array.Empty<CpuClassFrequencyGroup>();
        var detail = string.Join("；", groups.Select(group =>
            $"{group.Name}: {(Enum.IsDefined(group.State) && group.AverageMhz is { } mhz && double.IsFinite(mhz) ? mhz.ToString("0", CultureInfo.InvariantCulture) : "--")} MHz，样本 {group.SampleCount}/{group.ExpectedSampleCount}（{StateText(group.State)}）"));
        var status = MiniProToolkit.Core.CpuClassFrequency.SanitizeStatus(reading.Status);
        var state = groups.Length == 0 ? CpuClassFrequencyState.Unavailable : reading.State;
        return StateText(state) + (detail.Length == 0 ? string.Empty : $" · {detail}") +
            (reading.Groups?.Count > 256 ? " · 仅显示前 256 组（历史数据超限）" : string.Empty) +
            (status is null ? string.Empty : $" · {status}");
    }

    // Curve status summary of the pass that produced this row. Both fields are JSON-only and
    // additive: the CSV header/columns are unchanged (so no schema archive is triggered) and
    // older JSONL rows deserialize with nulls. CurveStatus is a
    // <see cref="FanCurveStatusKind"/> name, CurveReason the Core-provided level/lease reason;
    // neither carries paths, command lines or adapter/network details.
    public string? CurveStatus { get; init; }
    public string? CurveReason { get; init; }

    // Explicit curve tracking is JSON-only and separate from the fixed-RPM fields above.
    // Missing decisions and legacy rows retain nulls; CSV stays at 31 columns.
    public int? CurveTargetRpm { get; init; }
    public int? CurveActualRpm { get; init; }
    public int? CurveRpmError { get; init; }
    public string? CurveTrackingState { get; init; }

    // Optional read-only GPU die temperature (JSON-only, additive). Old JSONL rows
    // deserialize with nulls and the CSV header/columns stay unchanged, so no archive
    // is triggered. GpuSensorName names the LHM sensor, GpuSensorStatus the redacted
    // reader state; none of these fields is an input to fan control.
    public double? GpuTemperatureC { get; init; }
    public string? GpuSensorName { get; init; }
    public string? GpuSensorStatus { get; init; }

    // Optional board/storage values are JSON-only. CSV schema and CPU/EC semantics stay unchanged.
    public double? MotherboardTemperatureC { get; init; }
    public string? MotherboardTemperatureSensorId { get; init; }
    public string? MotherboardTemperatureSensorName { get; init; }
    public double? StorageTemperatureC { get; init; }
    public string? StorageTemperatureSensorId { get; init; }
    public string? StorageTemperatureSensorName { get; init; }
    public string? AuxTemperatureStatus { get; init; }

    /// <summary>Displayed temperature; old records without display fields fall back to the EC value.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? EffectiveDisplayCpuTemperatureC => DisplayCpuTemperatureC ?? CpuTemperatureC;

    /// <summary>Displayed package power; old records without display fields fall back to the Energy Meter value.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? EffectiveDisplayCpuPackagePowerWatts => DisplayCpuPackagePowerWatts ?? CpuPackagePowerWatts;

    public static HardwareTelemetryRecord FromSnapshot(
        HardwareSnapshot snapshot,
        string? eventName = null,
        string? curveStatus = null,
        string? curveReason = null,
        FanCurveDecision? curveDecision = null) => new(
            snapshot.Timestamp,
            snapshot.CpuTemperatureC,
            snapshot.Fan1Rpm,
            snapshot.Fan2Rpm,
            snapshot.RequestedTargetRpm,
            snapshot.TargetRpm,
            snapshot.TargetDuty,
            snapshot.RpmError,
            snapshot.RpmFeedbackState,
            snapshot.FanFault,
            snapshot.CpuUsagePercent,
            snapshot.MemoryUsagePercent,
            snapshot.CpuPackagePowerWatts,
            snapshot.PowerMode,
            snapshot.FanControlMode,
            snapshot.Error,
            eventName)
        {
            LhmCpuPackageTemperatureC = snapshot.LhmCpuPackageTemperatureC,
            LhmCpuPackagePowerWatts = snapshot.LhmCpuPackagePowerWatts,
            LhmStatus = snapshot.LhmStatus,
            DisplayCpuSource = snapshot.DisplayCpuSource,
            DisplayCpuTemperatureC = snapshot.DisplayCpuTemperatureC,
            DisplayCpuPackagePowerWatts = snapshot.DisplayCpuPackagePowerWatts,
            DisplayTemperatureFromLhm = snapshot.DisplayTemperatureFromLhm,
            DisplayPowerFromLhm = snapshot.DisplayPowerFromLhm,
            DisplaySourceStatus = snapshot.DisplaySourceStatus,
            CpuFrequencyMhz = snapshot.CpuFrequencyMhz,
            CpuFrequencyPercentOfBase = snapshot.CpuFrequencyPercentOfBase,
            CpuFrequencyBaseMhz = snapshot.CpuFrequencyBaseMhz,
            CpuFrequencySource = snapshot.CpuFrequencySource,
            CpuFrequencyStatus = snapshot.CpuFrequencyStatus,
            CpuClassFrequency = snapshot.CpuClassFrequency,
            CurveStatus = curveStatus,
            CurveReason = curveReason,
            CurveTargetRpm = curveDecision?.TargetRpm,
            CurveActualRpm = curveDecision?.ActualRpm,
            CurveRpmError = curveDecision?.RpmError,
            CurveTrackingState = curveDecision is { TrackingState: not FanCurveRpmTrackingState.Disabled }
                ? curveDecision.TrackingState.ToString() : null,
            GpuTemperatureC = snapshot.GpuTemperatureC,
            GpuSensorName = snapshot.GpuSensorName,
            GpuSensorStatus = snapshot.GpuSensorStatus,
            MotherboardTemperatureC = snapshot.MotherboardTemperatureC,
            MotherboardTemperatureSensorId = snapshot.MotherboardTemperatureSensorId,
            MotherboardTemperatureSensorName = snapshot.MotherboardTemperatureSensorName,
            StorageTemperatureC = snapshot.StorageTemperatureC,
            StorageTemperatureSensorId = snapshot.StorageTemperatureSensorId,
            StorageTemperatureSensorName = snapshot.StorageTemperatureSensorName,
            AuxTemperatureStatus = snapshot.AuxTemperatureStatus
        };
}

public sealed class TelemetryRecorder : IDisposable
{
    public const string CsvHeader = "Timestamp,CpuTemperatureC,Fan1Rpm,Fan2Rpm,RequestedTargetRpm,TargetRpm,TargetDuty,RpmError,RpmFeedbackState,FanFault,CpuUsagePercent,MemoryUsagePercent,CpuPackagePowerWatts,PowerMode,FanControlMode,Error,Event,LhmCpuPackageTemperatureC,LhmCpuPackagePowerWatts,LhmStatus,DisplayCpuTemperatureC,DisplayCpuPackagePowerWatts,DisplayCpuSource,DisplayTemperatureFromLhm,DisplayPowerFromLhm,DisplaySourceStatus,CpuFrequencyMhz,CpuFrequencyPercentOfBase,CpuFrequencyBaseMhz,CpuFrequencySource,CpuFrequencyStatus";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = TelemetryMaintenance.Gate;
    private TelemetryRetentionSettings _retention = new();
    private DateTimeOffset _nextSettingsRefresh;
    public string? RetentionWarning { get; private set; }

    /// <summary>
    /// False when the on-disk retention settings could not be read/validated, so the writer runs on
    /// fail-closed defaults. While false no archive is ever deleted and none is renamed: the
    /// periodic cleanup plan is skipped and an append that would need rotation is refused
    /// (<see cref="Append"/>) instead of dropping the tail archive.
    /// </summary>
    public bool RetentionTrusted { get; private set; } = true;
    private readonly string _jsonlPath;
    private readonly string _csvPath;
    private bool _disposed;

    // Stamp of the CSV content this instance last validated (or wrote). The header line is
    // only read again when the current on-disk stamp differs, so steady-state appends do no
    // extra read. Any external write, append, truncate, delete or rename changes at least one
    // component and forces a fresh header check; a first append always checks. Length+mtime
    // cannot detect a deliberate same-stamp replacement, which is a separate threat from the
    // path/link checks that still run on every append.
    private CsvStamp? _verifiedCsvStamp;
    private int _csvHeaderReads;
    private readonly Action<string, string> _appendCsvLine;

    private readonly record struct CsvStamp(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc)
    {
        public static CsvStamp From(FileInfo info) => new(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }

    public TelemetryRecorder(string? directory = null)
        : this(directory, static (path, line) => File.AppendAllText(path, line, Encoding.UTF8))
    {
    }

    /// <summary>
    /// Test seam for the CSV append itself: lets a test inject an external delete/truncate/replace
    /// exactly between the pre-append check and the write without relying on timing races.
    /// </summary>
    internal TelemetryRecorder(string? directory, Action<string, string> appendCsvLine)
    {
        ArgumentNullException.ThrowIfNull(appendCsvLine);
        Directory = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiniProToolkit",
            "history");
        _jsonlPath = System.IO.Path.Combine(Directory, "telemetry.jsonl");
        _csvPath = System.IO.Path.Combine(Directory, "telemetry.csv");
        _appendCsvLine = appendCsvLine;
    }

    /// <summary>Number of CSV header lines actually read back from disk (diagnostics/tests only).</summary>
    internal int CsvHeaderValidationCount => _csvHeaderReads;

    public string Directory { get; }
    public string JsonlPath => _jsonlPath;
    public string CsvPath => _csvPath;

    public void Append(HardwareTelemetryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            System.IO.Directory.CreateDirectory(Directory);
            if (!TelemetryMaintenance.IsSafeDirectory(Directory)) throw new IOException("历史目录不能是链接。");
            foreach (var path in new[] { _jsonlPath, _csvPath })
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("历史文件不能是链接。");
            if (DateTimeOffset.UtcNow >= _nextSettingsRefresh)
            {
                var store = new TelemetryRetentionSettingsStore(Directory);
                _retention = store.Load(out var warning, out var trusted);
                RetentionWarning = warning;
                RetentionTrusted = trusted;
                _nextSettingsRefresh = DateTimeOffset.UtcNow.AddSeconds(60);
                // Fail closed: the fallback defaults are not the user's policy, so running the
                // cleanup plan on them would delete archives the user still wants (P0 data loss).
                if (trusted)
                    TelemetryMaintenance.Execute(TelemetryMaintenance.Plan(Directory, _retention, DateTimeOffset.UtcNow));
            }
            // Untrusted policy: never rotate (which deletes the tail archive) and never delete via
            // the cleanup plan. Refuse the write with an actionable error instead, leaving all
            // files untouched and keeping the growth bounded by the size cap.
            if (!RetentionTrusted) EnsureRotationIsAllowed();
            var csvInfo = EnsureCsvSchema();
            RotateIfNeeded(_jsonlPath);
            var csvRotated = RotateIfNeeded(_csvPath, csvInfo);
            // Rotation moves the validated file away: the new CSV must get its header before
            // the first data row, and the cached stamp must never describe the old file.
            if (csvRotated) WriteCsvHeader();
            try
            {
                File.AppendAllText(_jsonlPath,
                    JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // A partial write leaves the stamped state unknown; the next append revalidates.
                _verifiedCsvStamp = null;
                throw;
            }
            // The CSV can be externally deleted, truncated, replaced or rotated between the
            // schema check above and this write. Re-check right before appending so a headerless
            // row is never produced on the normal path, then authenticate the post-write length
            // so a race that still happens can never leave a poisoned (always-matching) stamp.
            var expectedCsvLength = PrepareCsvAppend();
            var csvLine = ToCsv(record) + Environment.NewLine;
            var appendedBytes = Encoding.UTF8.GetByteCount(csvLine);
            try
            {
                _appendCsvLine(_csvPath, csvLine);
            }
            catch
            {
                _verifiedCsvStamp = null;
                throw;
            }
            AuthenticateCsvStamp(expectedCsvLength, appendedBytes);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private bool RotateIfNeeded(string path, FileInfo? knownInfo = null)
    {
        var maximumBytes = _retention.MaximumFileBytes;
        var retainedFiles = _retention.RetainedArchiveCount;
        var info = knownInfo ?? new FileInfo(path);
        if (!info.Exists || info.Length < maximumBytes)
            return false;

        // Refuse link targets, including archive destinations used by rotation.
        for (var index = 1; index <= retainedFiles; index++)
            if (File.Exists($"{path}.{index}") && (File.GetAttributes($"{path}.{index}") & FileAttributes.ReparsePoint) != 0)
                throw new IOException("历史归档不能是链接。");
        var oldest = $"{path}.{retainedFiles}";
        if (File.Exists(oldest))
            File.Delete(oldest);
        for (var index = retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            if (File.Exists(source))
                File.Move(source, $"{path}.{index + 1}", true);
        }
        File.Move(path, $"{path}.1", true);
        if (string.Equals(path, _csvPath, StringComparison.OrdinalIgnoreCase)) _verifiedCsvStamp = null;
        return true;
    }

    /// <summary>
    /// Fail-closed guard for an untrusted retention policy. Rotation is the only writer path that
    /// deletes an archive (<c>.N</c> is dropped while the ring shifts), so with an unreadable
    /// configuration the append is refused instead of silently deleting history: the active files
    /// are left byte-for-byte untouched and the telemetry write stops at the size cap (bounded, no
    /// unbounded growth) until the user re-saves a valid retention setting.
    /// </summary>
    private void EnsureRotationIsAllowed()
    {
        foreach (var path in new[] { _jsonlPath, _csvPath })
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < _retention.MaximumFileBytes) continue;
            throw new IOException(
                $"历史保留配置不可用（{TelemetryRetentionSettingsStore.FileName} 无法读取或无效），" +
                $"而 {Path.GetFileName(path)} 已达到 {_retention.MaximumFileBytes} 字节上限；为避免删除归档，本次遥测写入已跳过，" +
                "文件未做任何改动。请打开历史窗口确认并重新保存保留设置，保存后自动恢复写入。");
        }
    }

    /// <summary>
    /// Validates the CSV header, reusing one <see cref="FileInfo"/> for the size check and the
    /// cache decision. Returns the current file info (null when a header was just written) so
    /// the caller can reuse it for the size-based rotation check instead of re-statting.
    /// </summary>
    private FileInfo? EnsureCsvSchema()
    {
        var info = new FileInfo(_csvPath);
        if (!info.Exists || info.Length == 0)
        {
            WriteCsvHeader();
            return null;
        }
        var stamp = CsvStamp.From(info);
        if (_verifiedCsvStamp == stamp) return info;
        string? header;
        try
        {
            _csvHeaderReads++;
            using var reader = new StreamReader(_csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            header = reader.ReadLine();
        }
        catch
        {
            _verifiedCsvStamp = null;
            throw;
        }
        if (header == CsvHeader)
        {
            _verifiedCsvStamp = stamp;
            return info;
        }
        // Keep incompatible history intact. Rotation remains size-based for files of this schema.
        _verifiedCsvStamp = null;
        var archive = $"{_csvPath}.schema-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.bak";
        File.Move(_csvPath, archive);
        WriteCsvHeader();
        return null;
    }

    private void WriteCsvHeader()
    {
        try
        {
            File.WriteAllText(_csvPath,
                CsvHeader + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            _verifiedCsvStamp = null;
            throw;
        }
        UpdateCsvStamp();
    }

    private void UpdateCsvStamp()
    {
        try
        {
            var info = new FileInfo(_csvPath);
            _verifiedCsvStamp = info.Exists ? CsvStamp.From(info) : null;
        }
        catch
        {
            _verifiedCsvStamp = null;
        }
    }

    /// <summary>
    /// Re-checks the CSV immediately before the data row is appended and returns the file length
    /// the row is expected to be appended to. A file that vanished or was truncated gets its
    /// header restored; a file whose stamp no longer matches the validated one is revalidated
    /// (which may archive an externally replaced, incompatible header).
    /// </summary>
    private long PrepareCsvAppend()
    {
        var info = new FileInfo(_csvPath);
        if (info.Exists && info.Length > 0)
        {
            if (_verifiedCsvStamp == CsvStamp.From(info)) return info.Length;
            // External append/rewrite since validation: validate this exact content again.
            EnsureCsvSchema();
            info = new FileInfo(_csvPath);
            if (info.Exists && info.Length > 0) return info.Length;
        }
        WriteCsvHeader();
        return new FileInfo(_csvPath).Length;
    }

    /// <summary>
    /// Only trusts the post-write stamp when the file grew by exactly the appended UTF-8 bytes
    /// on top of the length that was valid before the write. Anything else (external delete that
    /// let AppendAllText create a headerless file, truncate, concurrent append, BOM on recreate)
    /// invalidates the cache so the next append re-reads the header instead of staying poisoned.
    /// </summary>
    private void AuthenticateCsvStamp(long expectedLength, int appendedBytes)
    {
        try
        {
            var info = new FileInfo(_csvPath);
            _verifiedCsvStamp = info.Exists && info.Length == expectedLength + appendedBytes
                ? CsvStamp.From(info)
                : null;
        }
        catch
        {
            _verifiedCsvStamp = null;
        }
    }

    public static string ToCsv(HardwareTelemetryRecord record) => string.Join(",",
        Csv(record.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
        Csv(record.CpuTemperatureC?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.Fan1Rpm?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.Fan2Rpm?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.RequestedTargetRpm?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.TargetRpm?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.TargetDuty?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.RpmError?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.RpmFeedbackState.ToString()),
        Csv(record.FanFault),
        Csv(record.CpuUsagePercent?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.MemoryUsagePercent?.ToString(CultureInfo.InvariantCulture)),
        Csv(record.CpuPackagePowerWatts?.ToString("0.0", CultureInfo.InvariantCulture)),
        Csv(record.PowerMode.ToString()),
        Csv(record.FanControlMode.ToString()),
        Csv(record.Error),
        Csv(record.Event),
        Csv(record.LhmCpuPackageTemperatureC?.ToString("0.0", CultureInfo.InvariantCulture)),
        Csv(record.LhmCpuPackagePowerWatts?.ToString("0.0", CultureInfo.InvariantCulture)),
        Csv(record.LhmStatus),
        Csv(record.DisplayCpuTemperatureC?.ToString("0.0", CultureInfo.InvariantCulture)),
        Csv(record.DisplayCpuPackagePowerWatts?.ToString("0.0", CultureInfo.InvariantCulture)),
        Csv(record.DisplayCpuSource.ToString()),
        Csv(record.DisplayTemperatureFromLhm.ToString(CultureInfo.InvariantCulture)),
        Csv(record.DisplayPowerFromLhm.ToString(CultureInfo.InvariantCulture)),
        Csv(record.DisplaySourceStatus),
        Csv(record.CpuFrequencyMhz?.ToString("0", CultureInfo.InvariantCulture)),
        Csv(record.CpuFrequencyPercentOfBase?.ToString("0.0", CultureInfo.InvariantCulture)),
        Csv(record.CpuFrequencyBaseMhz?.ToString("0", CultureInfo.InvariantCulture)),
        Csv(record.CpuFrequencySource.ToString()),
        Csv(record.CpuFrequencyStatus));

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }
}
