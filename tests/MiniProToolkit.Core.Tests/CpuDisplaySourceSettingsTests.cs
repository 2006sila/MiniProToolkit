using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class CpuDisplaySourceSettingsTests
{
    private static string NewSettingsPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "display-source.json");
    }

    [Fact]
    public void DefaultIsCompatibleEcAndEnergyMeter()
    {
        Assert.Equal(CpuDisplaySource.EcEnergyMeter, CpuDisplaySourceSettings.Default.Source);
        Assert.False(CpuDisplaySourceSettings.Default.UsesLhm);
        Assert.Equal(0, (int)CpuDisplaySource.EcEnergyMeter);
        Assert.Equal(1, (int)CpuDisplaySource.LibreHardwareMonitor);
    }

    [Fact]
    public void MissingFileUsesCompatibleDefaultWithoutWarning()
    {
        var path = NewSettingsPath();
        try
        {
            var settings = new CpuDisplaySourceSettingsStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(CpuDisplaySource.EcEnergyMeter, settings.Source);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void OldJsonWithoutSourceFieldKeepsCompatibleDefault()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{\"version\":1}");
            var settings = new CpuDisplaySourceSettingsStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(CpuDisplaySource.EcEnergyMeter, settings.Source);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void MissingVersionJsonIsReadAsCurrentSchema()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{\"source\":\"LibreHardwareMonitor\"}");
            var settings = new CpuDisplaySourceSettingsStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(CpuDisplaySource.LibreHardwareMonitor, settings.Source);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void MissingVersionWithoutSourceKeepsCompatibleDefault()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{}");
            var settings = new CpuDisplaySourceSettingsStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(CpuDisplaySource.EcEnergyMeter, settings.Source);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void UnsupportedVersionStillFallsBackWithWarning()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{\"version\":2,\"source\":\"LibreHardwareMonitor\"}");
            var settings = new CpuDisplaySourceSettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(CpuDisplaySource.EcEnergyMeter, settings.Source);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void UnknownEnumNameFallsBackToDefaultWithWarning()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{\"version\":1,\"source\":\"GpuOnly\"}");
            var settings = new CpuDisplaySourceSettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(CpuDisplaySource.EcEnergyMeter, settings.Source);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void UnknownEnumNumberFallsBackToDefaultWithWarning()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{\"version\":1,\"source\":42}");
            var settings = new CpuDisplaySourceSettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(CpuDisplaySource.EcEnergyMeter, settings.Source);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void CorruptJsonFallsBackToDefaultWithoutThrowing()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{\"version\":1,\"source\":");
            var settings = new CpuDisplaySourceSettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(CpuDisplaySource.EcEnergyMeter, settings.Source);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void SaveAndLoadRoundTripsLhmSelection()
    {
        var path = NewSettingsPath();
        try
        {
            var store = new CpuDisplaySourceSettingsStore(path);
            store.Save(new CpuDisplaySourceSettings(CpuDisplaySource.LibreHardwareMonitor));

            var settings = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(CpuDisplaySource.LibreHardwareMonitor, settings.Source);
            Assert.True(settings.UsesLhm);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void ValidateRejectsUndefinedEnumValue()
    {
        var settings = new CpuDisplaySourceSettings((CpuDisplaySource)99);
        Assert.Throws<ArgumentException>(settings.Validate);
    }
}

public sealed class CpuDisplaySourceSelectorTests
{
    private static HardwareSnapshot Snapshot(
        double? ecTemperature = 52,
        double? energyMeterPower = 12.5,
        double? lhmTemperature = 58.3,
        double? lhmPower = 15.4) => new(
            DateTimeOffset.UtcNow,
            ecTemperature,
            2180,
            0,
            PowerMode.Balance,
            25,
            40,
            FanControlMode.FirmwareAutomatic,
            null,
            null)
        {
            CpuPackagePowerWatts = energyMeterPower,
            LhmCpuPackageTemperatureC = lhmTemperature,
            LhmCpuPackagePowerWatts = lhmPower,
            LhmStatus = "LHM 正常"
        };

    [Fact]
    public void EcSourceIgnoresLhmValuesAndReportsNoFallback()
    {
        var displayed = CpuDisplaySourceSelector.Apply(Snapshot(), CpuDisplaySource.EcEnergyMeter);

        Assert.Equal(52, displayed.DisplayCpuTemperatureC);
        Assert.Equal(12.5, displayed.DisplayCpuPackagePowerWatts);
        Assert.False(displayed.DisplayTemperatureFromLhm);
        Assert.False(displayed.DisplayPowerFromLhm);
        Assert.Equal(CpuDisplaySource.EcEnergyMeter, displayed.DisplayCpuSource);
        Assert.Null(displayed.DisplaySourceStatus);
    }

    [Fact]
    public void LhmSourceUsesBothValuesWhenAvailable()
    {
        var displayed = CpuDisplaySourceSelector.Apply(Snapshot(), CpuDisplaySource.LibreHardwareMonitor);

        Assert.Equal(58.3, displayed.DisplayCpuTemperatureC);
        Assert.Equal(15.4, displayed.DisplayCpuPackagePowerWatts);
        Assert.True(displayed.DisplayTemperatureFromLhm);
        Assert.True(displayed.DisplayPowerFromLhm);
        Assert.Equal(CpuDisplaySource.LibreHardwareMonitor, displayed.DisplayCpuSource);
        Assert.Null(displayed.DisplaySourceStatus);
    }

    [Fact]
    public void MissingLhmTemperatureFallsBackTemperatureOnly()
    {
        var displayed = CpuDisplaySourceSelector.Apply(
            Snapshot(lhmTemperature: null), CpuDisplaySource.LibreHardwareMonitor);

        Assert.Equal(52, displayed.DisplayCpuTemperatureC);
        Assert.False(displayed.DisplayTemperatureFromLhm);
        Assert.Equal(15.4, displayed.DisplayCpuPackagePowerWatts);
        Assert.True(displayed.DisplayPowerFromLhm);
        Assert.Equal(CpuDisplaySourceSelector.TemperatureFallbackStatus, displayed.DisplaySourceStatus);
    }

    [Fact]
    public void MissingLhmPowerFallsBackPowerOnly()
    {
        var displayed = CpuDisplaySourceSelector.Apply(
            Snapshot(lhmPower: null), CpuDisplaySource.LibreHardwareMonitor);

        Assert.Equal(58.3, displayed.DisplayCpuTemperatureC);
        Assert.True(displayed.DisplayTemperatureFromLhm);
        Assert.Equal(12.5, displayed.DisplayCpuPackagePowerWatts);
        Assert.False(displayed.DisplayPowerFromLhm);
        Assert.Equal(CpuDisplaySourceSelector.PowerFallbackStatus, displayed.DisplaySourceStatus);
    }

    [Fact]
    public void PlaceholderZeroValuesFallBackBothMetricsWithStatus()
    {
        // LibreHardwareMonitor reports 0.0 °C / 0 W for sensors it cannot read
        // without its driver, so those must never become the primary values.
        var displayed = CpuDisplaySourceSelector.Apply(
            Snapshot(lhmTemperature: 0, lhmPower: 0), CpuDisplaySource.LibreHardwareMonitor);

        Assert.Equal(52, displayed.DisplayCpuTemperatureC);
        Assert.Equal(12.5, displayed.DisplayCpuPackagePowerWatts);
        Assert.False(displayed.DisplayTemperatureFromLhm);
        Assert.False(displayed.DisplayPowerFromLhm);
        Assert.Equal(CpuDisplaySourceSelector.FullFallbackStatus, displayed.DisplaySourceStatus);
    }

    [Fact]
    public void ApplyKeepsEcControlFieldsAndEnergyMeterPower()
    {
        var displayed = CpuDisplaySourceSelector.Apply(
            Snapshot(ecTemperature: 52, energyMeterPower: 12.5, lhmTemperature: 95, lhmPower: 60),
            CpuDisplaySource.LibreHardwareMonitor);

        Assert.Equal(52, displayed.CpuTemperatureC);
        Assert.Equal(12.5, displayed.CpuPackagePowerWatts);
        Assert.Equal(95, displayed.DisplayCpuTemperatureC);
        Assert.Equal(60, displayed.DisplayCpuPackagePowerWatts);
    }

    [Fact]
    public void CurveAndEmergencyLimitStillReadEcTemperature()
    {
        var displayed = CpuDisplaySourceSelector.Apply(
            Snapshot(ecTemperature: 52, lhmTemperature: 95), CpuDisplaySource.LibreHardwareMonitor);
        var coolDecision = new FanCurveEngine(FanCurveSettings.Default).Evaluate(displayed);

        Assert.Equal(FanCurveAction.SetDuty, coolDecision.Action);
        Assert.NotEqual(100, coolDecision.DutyPercent);

        var hotEc = CpuDisplaySourceSelector.Apply(
            Snapshot(ecTemperature: 95, lhmTemperature: 50), CpuDisplaySource.LibreHardwareMonitor);
        var emergencyDecision = new FanCurveEngine(FanCurveSettings.Default).Evaluate(hotEc);

        Assert.Equal(100, emergencyDecision.DutyPercent);
    }

    [Fact]
    public void TelemetryKeepsOriginalsDisplayValuesAndPerMetricSource()
    {
        var displayed = CpuDisplaySourceSelector.Apply(
            Snapshot(lhmPower: null), CpuDisplaySource.LibreHardwareMonitor);
        var record = HardwareTelemetryRecord.FromSnapshot(displayed);

        Assert.Equal(52, record.CpuTemperatureC);
        Assert.Equal(12.5, record.CpuPackagePowerWatts);
        Assert.Equal(58.3, record.DisplayCpuTemperatureC);
        Assert.Equal(12.5, record.DisplayCpuPackagePowerWatts);
        Assert.True(record.DisplayTemperatureFromLhm);
        Assert.False(record.DisplayPowerFromLhm);
        Assert.Equal(CpuDisplaySource.LibreHardwareMonitor, record.DisplayCpuSource);
        Assert.Equal(CpuDisplaySourceSelector.PowerFallbackStatus, record.DisplaySourceStatus);
    }

    [Fact]
    public void ApplyIsIdempotentForBothSources()
    {
        var lhmOnce = CpuDisplaySourceSelector.Apply(Snapshot(), CpuDisplaySource.LibreHardwareMonitor);
        var lhmTwice = CpuDisplaySourceSelector.Apply(lhmOnce, CpuDisplaySource.LibreHardwareMonitor);
        Assert.Equal(lhmOnce, lhmTwice);

        var ecOnce = CpuDisplaySourceSelector.Apply(Snapshot(), CpuDisplaySource.EcEnergyMeter);
        var ecTwice = CpuDisplaySourceSelector.Apply(ecOnce, CpuDisplaySource.EcEnergyMeter);
        Assert.Equal(ecOnce, ecTwice);
    }

    [Fact]
    public void ReprojectRebuildsDisplayFieldsFromOriginalValues()
    {
        var lhmRecord = HardwareTelemetryRecord.FromSnapshot(
            CpuDisplaySourceSelector.Apply(Snapshot(), CpuDisplaySource.LibreHardwareMonitor));

        var ecRecord = CpuDisplaySourceSelector.Reproject(lhmRecord, CpuDisplaySource.EcEnergyMeter);

        Assert.Equal(52, ecRecord.DisplayCpuTemperatureC);
        Assert.Equal(12.5, ecRecord.DisplayCpuPackagePowerWatts);
        Assert.False(ecRecord.DisplayTemperatureFromLhm);
        Assert.False(ecRecord.DisplayPowerFromLhm);
        Assert.Equal(CpuDisplaySource.EcEnergyMeter, ecRecord.DisplayCpuSource);
        Assert.Null(ecRecord.DisplaySourceStatus);
        // Originals are never touched by a re-projection.
        Assert.Equal(52, ecRecord.CpuTemperatureC);
        Assert.Equal(12.5, ecRecord.CpuPackagePowerWatts);
        Assert.Equal(58.3, ecRecord.LhmCpuPackageTemperatureC);
        Assert.Equal(15.4, ecRecord.LhmCpuPackagePowerWatts);

        var backToLhm = CpuDisplaySourceSelector.Reproject(ecRecord, CpuDisplaySource.LibreHardwareMonitor);
        Assert.Equal(lhmRecord, backToLhm);
    }

    [Fact]
    public void ReprojectKeepsPerMetricFallbackStatus()
    {
        var lhmRecord = HardwareTelemetryRecord.FromSnapshot(
            CpuDisplaySourceSelector.Apply(Snapshot(lhmPower: null), CpuDisplaySource.LibreHardwareMonitor));

        var reprojected = CpuDisplaySourceSelector.Reproject(lhmRecord, CpuDisplaySource.LibreHardwareMonitor);

        Assert.Equal(lhmRecord, reprojected);
        Assert.Equal(CpuDisplaySourceSelector.PowerFallbackStatus, reprojected.DisplaySourceStatus);
        Assert.True(reprojected.DisplayTemperatureFromLhm);
        Assert.False(reprojected.DisplayPowerFromLhm);
    }

    [Fact]
    public void CsvAppendsDisplayColumnsAfterExistingCrossCheckColumns()
    {
        var displayed = CpuDisplaySourceSelector.Apply(Snapshot(), CpuDisplaySource.LibreHardwareMonitor);
        var record = HardwareTelemetryRecord.FromSnapshot(displayed);

        var header = TelemetryRecorder.CsvHeader.Split(',');
        var values = TelemetryRecorder.ToCsv(record).Split(',');

        Assert.Equal("CpuTemperatureC", header[1]);
        Assert.Equal("CpuPackagePowerWatts", header[12]);
        Assert.Equal("LhmCpuPackageTemperatureC", header[17]);
        Assert.Equal("DisplayCpuTemperatureC", header[20]);
        Assert.Equal("DisplaySourceStatus", header[25]);
        Assert.Equal(header.Length, values.Length);
        Assert.Equal("58.3", values[20]);
        Assert.Equal("15.4", values[21]);
        Assert.Equal("LibreHardwareMonitor", values[22]);
        Assert.Equal("True", values[23]);
        Assert.Equal("True", values[24]);
        Assert.Equal(string.Empty, values[25]);
    }

    [Fact]
    public void LegacyTelemetryWithoutDisplayFieldsFallsBackToOriginals()
    {
        var legacy = new HardwareTelemetryRecord(
            DateTimeOffset.UtcNow, 52, 2180, 0, null, null, null, null,
            FanRpmFeedbackState.Disabled, null, 25, 40, 12.5,
            PowerMode.Balance, FanControlMode.FirmwareAutomatic, null, null);

        Assert.Null(legacy.DisplayCpuTemperatureC);
        Assert.Equal(52, legacy.EffectiveDisplayCpuTemperatureC);
        Assert.Equal(12.5, legacy.EffectiveDisplayCpuPackagePowerWatts);
        Assert.Equal(CpuDisplaySource.EcEnergyMeter, legacy.DisplayCpuSource);
    }
}
