using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using MiniProToolkit.Core;

namespace MiniProToolkit.Hardware;

public sealed class PowerSwitchHardware : IDisposable, IFanControlTarget
{
    public const int MinimumSafeManualDuty = 100;
    public const int MinimumCurveDuty = 40;

    private const string NamespacePath = @"root\WMI";
    private const string InstanceName = @"ACPI\PNP0C14\IP3POWERSWITCH_0";
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _cpuPowerCounter;
    private bool _cpuPowerCounterUnavailable;
    private readonly object _sync = new();
    private ManagementObject? _instance;
    private FixedRpmControlSession _fixedRpm;
    private FanControlMode _fanControlMode = FanControlMode.Unknown;
    private FanRpmFeedbackState _rpmFeedbackState = FanRpmFeedbackState.Disabled;
    private int? _rpmError;
    private string? _fanFault;
    private int? _targetDuty;
    private int? _requestedTargetRpm;
    private int? _targetRpm;
    private bool _restoreRequired;
    private bool _disposed;

    public PowerSwitchHardware(FanCalibration? calibration = null)
    {
        var resolved = calibration ?? FanCalibration.Default;
        resolved.ValidateForFixedRpm();
        _fixedRpm = CreateFixedRpmSession(resolved);
    }

    // Configuration-only operation: does not invoke WMI or replace the hardware instance.
    public void UpdateCalibration(FanCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        calibration.ValidateForFixedRpm();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_restoreRequired || _fixedRpm.IsActive || _fixedRpm.RecoveryRequired)
                throw new InvalidOperationException("请先恢复 EC 自动，再应用标定。");
            _fixedRpm = CreateFixedRpmSession(calibration);
        }
    }

    public HardwareSnapshot ReadSnapshot()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sampledAt = DateTimeOffset.Now;
            var cpu = ReadCpuUsagePercent();
            var memory = GetMemoryUsagePercent();
            var cpuPower = ReadCpuPackagePowerWatts();
            try
            {
                var temperature = ReadTemperatureC();
                using var fanResult = Invoke("GetFanControl", new Dictionary<string, object>
                {
                    ["FanNumber"] = (byte)1
                });
                var packed = Convert.ToUInt32(fanResult["FanDuty"]
                    ?? throw new InvalidOperationException("Fan RPM is unavailable."));
                var fan1Rpm = (int)(packed & 0xFFFF);
                var fan2Rpm = (int)(packed >> 16);
                using var modeResult = Invoke("GetPowerMode");
                var modeValue = Convert.ToByte(modeResult["CurrentPowerMode"] ?? 255);
                return new HardwareSnapshot(
                    sampledAt,
                    temperature,
                    fan1Rpm,
                    fan2Rpm,
                    ToPowerMode(modeValue),
                    cpu,
                    memory,
                    _fanControlMode,
                    _targetDuty,
                    null)
                {
                    RequestedTargetRpm = _requestedTargetRpm,
                    TargetRpm = _targetRpm,
                    RpmError = _rpmError,
                    RpmFeedbackState = _rpmFeedbackState,
                    FanFault = _fanFault,
                    CpuPackagePowerWatts = cpuPower
                };
            }
            catch (Exception ex)
            {
                _instance?.Dispose();
                _instance = null;
                return HardwareSnapshot.ErrorSnapshot(ex.GetBaseException().Message) with
                {
                    CpuUsagePercent = cpu,
                    MemoryUsagePercent = memory,
                    FanControlMode = _fanControlMode,
                    TargetDuty = _targetDuty,
                    RequestedTargetRpm = _requestedTargetRpm,
                    TargetRpm = _targetRpm,
                    RpmError = _rpmError,
                    RpmFeedbackState = _rpmFeedbackState,
                    FanFault = _fanFault,
                    CpuPackagePowerWatts = cpuPower
                };
            }
        }
    }

    public void SetPowerMode(PowerMode mode)
    {
        if (mode is not (PowerMode.Balance or PowerMode.Performance or PowerMode.Silence))
            throw new ArgumentOutOfRangeException(nameof(mode));

        lock (_sync)
        {
            var clock = Stopwatch.StartNew();
            long? setterStatus = null;
            // Keep _sync through the write, reads and waits. Native calls cannot be
            // cancelled; the verifier checks their elapsed time after they return.
            PowerModeWriteVerification.Verify(
                mode,
                write: () =>
                {
                    using var setResult = Invoke("SetPowerMode", new Dictionary<string, object>
                    {
                        ["PowerMode"] = (byte)mode
                    });
                    setterStatus = ReadSetterStatus(setResult);
                },
                read: () =>
                {
                    using var verifyResult = Invoke("GetPowerMode");
                    return PowerModeWriteVerification.DecodeCurrentPowerMode(verifyResult["CurrentPowerMode"]);
                },
                elapsed: () => clock.Elapsed,
                wait: Thread.Sleep,
                getSetterStatus: () => setterStatus);
        }

        // ResultStatus is optional metadata, not an interpreted success/failure code.
        // Do not retain provider objects or invent a ReturnValue not supplied by WMI.
        static long? ReadSetterStatus(ManagementBaseObject result)
        {
            try
            {
                return result.Properties["ResultStatus"]?.Value switch
                {
                    byte value => value,
                    sbyte value => value,
                    short value => value,
                    ushort value => value,
                    int value => value,
                    uint value => value,
                    long value => value,
                    ulong value when value <= long.MaxValue => (long)value,
                    _ => null
                };
            }
            catch (Exception)
            {
                // Missing/unreadable optional status must not mask verification.
                return null;
            }
        }
    }

    public void SetFanDuty(int duty)
    {
        if (duty is < MinimumSafeManualDuty or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(duty),
                "Only full speed is available until thermal protection and Guardian are implemented.");

        SetDutyCore(duty, FanControlMode.ManualDuty, null);
    }

    public void SetCurveDuty(int dutyPercent)
    {
        if (dutyPercent is < MinimumCurveDuty or > 100)
            throw new ArgumentOutOfRangeException(nameof(dutyPercent),
                $"Curve duty must be between {MinimumCurveDuty} and 100 percent.");

        SetDutyCore(dutyPercent, FanControlMode.Curve, null);
    }

    public FanRpmTargetResult SetFixedRpm(int requestedRpm)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { return _fixedRpm.Start(requestedRpm); }
            finally { SyncFeedbackState(); }
        }
    }

    private FixedRpmControlSession CreateFixedRpmSession(FanCalibration calibration) => new(
        calibration,
        duty => SetDutyCore(duty, FanControlMode.FixedRpm, _fixedRpm.TargetRpm),
        RestoreAutomaticCore);

    public bool IsEmergencyFullSpeed
    {
        get { lock (_sync) return _fixedRpm.IsEmergencyFullSpeed; }
    }

    public bool IsRecoveryPending
    {
        get { lock (_sync) return _fixedRpm.RecoveryRequired && !_fixedRpm.IsActive && !_fixedRpm.IsEmergencyFullSpeed; }
    }

    public bool RecoveryRequired
    {
        get { lock (_sync) return _restoreRequired || _fixedRpm.RecoveryRequired; }
    }

    // Called explicitly by the control loop. ReadSnapshot itself never writes fan control.
    public HardwareSnapshot ApplyFixedRpmControl(HardwareSnapshot snapshot, bool guardianRunning, bool leaseValid = true)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _fixedRpm.Tick(snapshot, guardianRunning, leaseValid);
            SyncFeedbackState();
            return snapshot with
            {
                FanControlMode = _fanControlMode,
                TargetDuty = _targetDuty,
                RequestedTargetRpm = _requestedTargetRpm,
                TargetRpm = _targetRpm,
                RpmError = _rpmError,
                RpmFeedbackState = _rpmFeedbackState,
                FanFault = _fanFault
            };
        }
    }

    private void SyncFeedbackState()
    {
        _requestedTargetRpm = _fixedRpm.RequestedTargetRpm;
        _targetRpm = _fixedRpm.TargetRpm;
        _rpmError = _fixedRpm.ErrorRpm;
        _rpmFeedbackState = _fixedRpm.State;
        _fanFault = _fixedRpm.Fault;
    }

    private void SetDutyCore(int duty, FanControlMode mode, int? targetRpm)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _restoreRequired = true;
            using var result = Invoke("SetFanControl", new Dictionary<string, object>
            {
                ["FanNumber"] = (byte)1,
                ["FanDuty"] = (byte)duty
            });
            _fanControlMode = mode;
            _targetDuty = duty;
            _targetRpm = targetRpm;
            if (mode != FanControlMode.FixedRpm)
            {
                _fixedRpm.Stop();
                SyncFeedbackState();
            }
        }
    }

    public void RestoreAutomatic()
    {
        lock (_sync)
        {
            try { _fixedRpm.RestoreAutomatic(); }
            finally { SyncFeedbackState(); }
        }
    }

    private void RestoreAutomaticCore()
    {
        lock (_sync)
        {
            using var result = Invoke("SetFanControl", new Dictionary<string, object>
            {
                ["FanNumber"] = (byte)1,
                ["FanDuty"] = (byte)101
            });
            // The firmware reports RPM, not its control mode; this is a request state.
            using var verifyRead = Invoke("GetFanControl", new Dictionary<string, object>
            {
                ["FanNumber"] = (byte)1
            });
            _ = Convert.ToUInt32(verifyRead["FanDuty"]
                ?? throw new InvalidOperationException("Fan RPM is unavailable after automatic request."));
            _fanControlMode = FanControlMode.FirmwareAutomatic;
            _targetDuty = null;
            _requestedTargetRpm = null;
            _targetRpm = null;
            _restoreRequired = false;
        }
    }

    public void SetFullSpeed(bool enabled)
    {
        if (enabled)
            SetFanDuty(100);
        else
            RestoreAutomatic();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            try
            {
                if (_restoreRequired || _fixedRpm.RecoveryRequired)
                    RestoreAutomatic();
            }
            catch
            {
                // Shutdown must still release WMI and counter resources.
            }
            finally
            {
                _instance?.Dispose();
                _instance = null;
                _cpuCounter?.Dispose();
                _cpuPowerCounter?.Dispose();
                _disposed = true;
            }
        }
    }

    private int ReadTemperatureC()
    {
        using var result = Invoke("GetHwTemp", new Dictionary<string, object>
        {
            ["HwTempType"] = (byte)1
        });
        var raw = Convert.ToUInt32(result["Temp"] ?? uint.MaxValue);
        if (raw is 0x7FFFFFFF or uint.MaxValue || (raw & 0xFF) is 0 or > 125)
            throw new InvalidOperationException("EC temperature is unavailable.");
        return (int)(raw & 0xFF);
    }

    private ManagementBaseObject Invoke(string method, IDictionary<string, object>? arguments = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _instance ??= new ManagementObject(
            NamespacePath,
            $"PowerSwitchInterface.InstanceName='{InstanceName}'",
            null);
        using var parameters = arguments is null
            ? null
            : CreateParameters(method, arguments);
        return _instance.InvokeMethod(method, parameters, null)
            ?? throw new InvalidOperationException($"WMI method {method} returned no result.");
    }

    private ManagementBaseObject CreateParameters(string method, IDictionary<string, object> arguments)
    {
        var parameters = _instance!.GetMethodParameters(method);
        foreach (var argument in arguments)
        {
            if (parameters.Properties[argument.Key] is null)
                throw new InvalidOperationException($"WMI method {method} has no parameter {argument.Key}.");
            parameters[argument.Key] = argument.Value;
        }
        return parameters;
    }

    private static PowerMode ToPowerMode(byte value) => value switch
    {
        0 => PowerMode.Balance,
        1 => PowerMode.Performance,
        2 => PowerMode.Silence,
        _ => PowerMode.Unknown
    };

    private int? ReadCpuUsagePercent()
    {
        try
        {
            if (_cpuCounter is null)
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _ = _cpuCounter.NextValue();
                return null;
            }
            return ClampPercent(_cpuCounter.NextValue());
        }
        catch
        {
            _cpuCounter?.Dispose();
            _cpuCounter = null;
            return null;
        }
    }

    private double? ReadCpuPackagePowerWatts()
    {
        if (_cpuPowerCounterUnavailable)
            return null;

        try
        {
            if (_cpuPowerCounter is null)
            {
                var category = new PerformanceCounterCategory("Energy Meter");
                var package = category.GetInstanceNames()
                    .FirstOrDefault(name => name.Contains("_PKG", StringComparison.OrdinalIgnoreCase))
                    ?? category.GetInstanceNames()
                        .FirstOrDefault(name => name.Contains("Package", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(package))
                {
                    _cpuPowerCounterUnavailable = true;
                    return null;
                }

                _cpuPowerCounter = new PerformanceCounter(
                    "Energy Meter", "Power", package, readOnly: true);
            }

            // Windows Energy Meter exposes Power in milliwatts. Keep the
            // conversion here so every UI/history consumer receives watts.
            var milliwatts = _cpuPowerCounter.NextValue();
            if (!float.IsFinite(milliwatts) || milliwatts <= 0 || milliwatts > 1000 * 1000)
                return null;
            return Math.Round(milliwatts / 1000.0, 1);
        }
        catch
        {
            _cpuPowerCounter?.Dispose();
            _cpuPowerCounter = null;
            _cpuPowerCounterUnavailable = true;
            return null;
        }
    }

    private static int? GetMemoryUsagePercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysicalMemory == 0)
            return null;
        return ClampPercent(status.MemoryLoad);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysicalMemory;
        internal ulong AvailablePhysicalMemory;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    private static int ClampPercent(float value) => Math.Clamp((int)Math.Round(value), 0, 100);
    private static int ClampPercent(double value) => Math.Clamp((int)Math.Round(value), 0, 100);
}
