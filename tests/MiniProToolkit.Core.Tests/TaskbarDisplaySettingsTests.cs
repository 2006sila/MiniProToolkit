using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class TaskbarDisplaySettingsTests
{
    [Fact]
    public void SaveAndLoadPreservesOrderAndLayout()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        var store = new TaskbarDisplaySettingsStore(path);
        var expected = new TaskbarDisplaySettings(
            [TaskbarDisplayItem.TargetRpm, TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.WmiStatus],
            Compact: false,
            Vertical: true,
            RefreshIntervalSeconds: 3);
        try
        {
            store.Save(expected);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(expected.Items, actual.Items);
            Assert.Equal(expected.Compact, actual.Compact);
            Assert.Equal(expected.Vertical, actual.Vertical);
            Assert.Equal(expected.RefreshIntervalSeconds, actual.RefreshIntervalSeconds);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NumericEnumValuesRemainStableForPreviouslySavedSettings()
    {
        Assert.Equal(0, (int)TaskbarDisplayItem.CpuTemperature);
        Assert.Equal(1, (int)TaskbarDisplayItem.FanRpm);
        Assert.Equal(2, (int)TaskbarDisplayItem.TargetRpm);
        Assert.Equal(9, (int)TaskbarDisplayItem.WmiStatus);
        Assert.Equal(10, (int)TaskbarDisplayItem.CpuPackagePower);
        // New items are appended so every stored number keeps its meaning.
        Assert.Equal(11, (int)TaskbarDisplayItem.CpuFrequency);
        Assert.Equal(12, (int)TaskbarDisplayItem.GpuTemperature);
        Assert.Equal(12, Enum.GetValues<TaskbarDisplayItem>().Max(item => (int)item));
    }

    [Fact]
    public void DefaultItemsAreUnchangedByTheNewMetrics()
    {
        // Opt-in only: the default taskbar still shows temperature, RPM, package power and mode.
        Assert.Equal(
            [TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm, TaskbarDisplayItem.CpuPackagePower, TaskbarDisplayItem.PowerMode],
            TaskbarDisplaySettings.Default.Items);
        Assert.DoesNotContain(TaskbarDisplayItem.CpuFrequency, TaskbarDisplaySettings.Default.Items);
        // The read-only GPU item follows the same opt-in rule.
        Assert.DoesNotContain(TaskbarDisplayItem.GpuTemperature, TaskbarDisplaySettings.Default.Items);
    }

    [Fact]
    public void GpuItemIsAppendedAndCanBeSelectedWithoutTouchingOldSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new TaskbarDisplaySettingsStore(path);
            // A version-4 file saved before the GPU item existed must keep its exact list.
            File.WriteAllText(path, "{\"version\":4,\"items\":[\"CpuTemperature\",\"RpmFeedback\"],\"compact\":false,\"vertical\":false,\"refreshIntervalSeconds\":2}");
            var before = store.LoadOrDefault(out var beforeWarning);
            Assert.Null(beforeWarning);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.RpmFeedback], before.Items);

            // Opt-in through the existing settings list: order is preserved on reload.
            var expected = new TaskbarDisplaySettings(
                [TaskbarDisplayItem.GpuTemperature, TaskbarDisplayItem.CpuTemperature],
                Compact: true, Vertical: false, RefreshIntervalSeconds: 1);
            store.Save(expected);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(expected.Items, actual.Items);
            Assert.Contains("\"GpuTemperature\"", File.ReadAllText(path));
            // Still a read-only display list: a duplicate GPU item is rejected by Validate.
            Assert.Throws<ArgumentException>(() => new TaskbarDisplaySettings(
                [TaskbarDisplayItem.GpuTemperature, TaskbarDisplayItem.GpuTemperature],
                Compact: true, Vertical: false, RefreshIntervalSeconds: 1).Validate());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void VersionThreeSettingsKeepTheUsersItemListWithoutInjectingNewMetrics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            // A user who deliberately hid CPU package power must not get it back, and the
            // frequency item must stay hidden until the user picks it.
            File.WriteAllText(path, "{\"version\":3,\"items\":[\"CpuTemperature\",\"FanRpm\",\"PowerMode\"],\"compact\":true,\"vertical\":false,\"refreshIntervalSeconds\":1}");
            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);
            Assert.Null(warning);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm, TaskbarDisplayItem.PowerMode], actual.Items);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NewMetricItemsCanBeSelectedAndOrdered()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new TaskbarDisplaySettingsStore(path);
            var expected = new TaskbarDisplaySettings(
                [TaskbarDisplayItem.CpuFrequency, TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.CpuUsage],
                Compact: true, Vertical: false, RefreshIntervalSeconds: 2);
            store.Save(expected);

            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(expected.Items, actual.Items);
            Assert.Contains("\"version\": 6", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void VersionTwoSettingsMigrateByAddingCpuPower()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "{\"version\":2,\"items\":[\"CpuTemperature\",\"FanRpm\",\"PowerMode\"],\"compact\":true,\"vertical\":false,\"refreshIntervalSeconds\":1}");
            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);
            Assert.Null(warning);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm, TaskbarDisplayItem.CpuPackagePower, TaskbarDisplayItem.PowerMode], actual.Items);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DefaultBackgroundIsFullyTransparent()
    {
        // TrafficMonitor-like default: the status text is drawn directly on the taskbar pixels.
        Assert.Equal("#00000000", TaskbarDisplaySettings.Default.BackgroundColor);
        TaskbarDisplaySettings.Default.Validate();
    }

    [Fact]
    public void LegacyPositionFieldsAreIgnoredOnLoadAndDroppedOnSave()
    {
        // A taskbar.json written by the build that had a position anchor and an offset must load
        // without a warning and keep every other setting; the placement is fixed to the taskbar's
        // starting edge again, so the two retired fields are simply dropped on the next save.
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path,
                "{\"version\":4,\"items\":[\"CpuTemperature\",\"FanRpm\"],\"compact\":true,\"vertical\":false," +
                "\"refreshIntervalSeconds\":2,\"fontFamily\":\"Consolas\",\"fontSize\":14," +
                "\"textColor\":\"#FFFFFFFF\",\"backgroundColor\":\"#80202124\"," +
                "\"positionAnchor\":\"Center\",\"positionOffsetDip\":-75}");
            var store = new TaskbarDisplaySettingsStore(path);

            var loaded = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm], loaded.Items);
            Assert.True(loaded.Compact);
            Assert.False(loaded.Vertical);
            Assert.Equal(2, loaded.RefreshIntervalSeconds);
            Assert.Equal("Consolas", loaded.FontFamily);
            Assert.Equal(14, loaded.FontSize);
            Assert.Equal("#FFFFFFFF", loaded.TextColor);
            Assert.Equal("#80202124", loaded.BackgroundColor);
            loaded.Validate();

            store.Save(loaded);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("positionAnchor", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("positionOffsetDip", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Center", json);
            Assert.DoesNotContain("-75", json);
            // The schema version follows the current writer and every remaining setting is kept.
            Assert.Contains("\"version\": 6", json);
            Assert.Contains("\"compact\": true", json);
            Assert.Contains("\"Consolas\"", json);
            Assert.Contains("\"#80202124\"", json);
            var reloaded = store.LoadOrDefault(out var reloadWarning);
            Assert.Null(reloadWarning);
            Assert.Equal(loaded.Items, reloaded.Items);
            Assert.Equal(loaded.Compact, reloaded.Compact);
            Assert.Equal(loaded.Vertical, reloaded.Vertical);
            Assert.Equal(loaded.RefreshIntervalSeconds, reloaded.RefreshIntervalSeconds);
            Assert.Equal(loaded.BackgroundColor, reloaded.BackgroundColor);
            Assert.Equal(loaded.TextColor, reloaded.TextColor);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExplicitNullLegacyPositionFieldsAreIgnoredWithoutResettingTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            // A hand-edited file may spell the retired fields out as null; that is the same as absent.
            File.WriteAllText(path,
                "{\"version\":4,\"items\":[\"CpuTemperature\"],\"compact\":false,\"vertical\":true," +
                "\"refreshIntervalSeconds\":5,\"positionAnchor\":null,\"positionOffsetDip\":null}");
            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature], actual.Items);
            Assert.False(actual.Compact);
            Assert.True(actual.Vertical);
            Assert.Equal(5, actual.RefreshIntervalSeconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void StoredOpaqueBackgroundIsNeverReplacedByTheNewTransparentDefault()
    {
        // An existing installation that saved the old #202124 plate keeps it byte-for-byte; the
        // transparent default only applies to a file that never stored a background color.
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "{\"version\":4,\"items\":[\"CpuTemperature\"],\"compact\":true,\"vertical\":false,\"refreshIntervalSeconds\":1,\"backgroundColor\":\"#FF202124\"}");
            var legacyPlate = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var legacyWarning);
            Assert.Null(legacyWarning);
            Assert.Equal("#FF202124", legacyPlate.BackgroundColor);

            File.WriteAllText(path, "{\"version\":4,\"items\":[\"CpuTemperature\"],\"compact\":true,\"vertical\":false,\"refreshIntervalSeconds\":1}");
            var transparent = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var transparentWarning);
            Assert.Null(transparentWarning);
            Assert.Equal("#00000000", transparent.BackgroundColor);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SemiTransparentBackgroundRoundTripsAndStaysValidated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new TaskbarDisplaySettingsStore(path);
            var expected = new TaskbarDisplaySettings(
                [TaskbarDisplayItem.CpuTemperature], Compact: true, Vertical: false,
                RefreshIntervalSeconds: 1, BackgroundColor: "#80202124");
            store.Save(expected);

            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal("#80202124", actual.BackgroundColor);
            // A malformed color is still rejected, so the ARGB form keeps its existing rules.
            Assert.Throws<ArgumentException>(() => new TaskbarDisplaySettings(
                [TaskbarDisplayItem.CpuTemperature], Compact: true, Vertical: false,
                RefreshIntervalSeconds: 1, BackgroundColor: "#8012345").Validate());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("4")]
    public void NullItemsFallBackToDefaultWithWarning(string version)
    {
        // A JSON null item list must not escape the fallback filter as a NullReferenceException.
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, $"{{\"version\":{version},\"items\":null,\"compact\":false,\"vertical\":true,\"refreshIntervalSeconds\":5}}");

            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(TaskbarDisplaySettings.Default.Items, actual.Items);
            Assert.Equal(TaskbarDisplaySettings.Default.Compact, actual.Compact);
            Assert.Equal(TaskbarDisplaySettings.Default.Vertical, actual.Vertical);
            Assert.Equal(TaskbarDisplaySettings.Default.RefreshIntervalSeconds, actual.RefreshIntervalSeconds);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("\"RetiredMetricItem\"")]
    [InlineData("13")]
    public void UnknownItemNamesAreDroppedWithoutLosingTheOtherItems(string retiredItem)
    {
        // A settings file written by a build that shipped an extra (since removed) item must
        // keep every other choice and must never fail to load.
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "{\"version\":4,\"items\":[\"CpuTemperature\",\"FanRpm\"," + retiredItem +
                ",\"PowerMode\"],\"compact\":true,\"vertical\":false,\"refreshIntervalSeconds\":1}");

            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm, TaskbarDisplayItem.PowerMode], actual.Items);
            Assert.True(actual.Compact);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SettingsWithOnlyUnknownItemsFallBackToDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "{\"version\":4,\"items\":[\"RetiredMetricItem\"],\"compact\":true,\"vertical\":false,\"refreshIntervalSeconds\":1}");

            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(TaskbarDisplaySettings.Default.Items, actual.Items);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void InvalidSettingsFallBackToDefault()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{\"version\":1,\"items\":[\"CpuTemperature\",\"CpuTemperature\"],\"compact\":true,\"vertical\":false,\"refreshIntervalSeconds\":1}");
        try
        {
            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(TaskbarDisplaySettings.Default.Items, actual.Items);
            Assert.Equal(TaskbarDisplaySettings.Default.Compact, actual.Compact);
            Assert.Equal(TaskbarDisplaySettings.Default.Vertical, actual.Vertical);
            Assert.Equal(TaskbarDisplaySettings.Default.RefreshIntervalSeconds, actual.RefreshIntervalSeconds);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DefaultLayoutModeIsStackedPairs()
    {
        // A stays the default so an existing installation does not change its appearance.
        Assert.Equal(TaskbarLayoutMode.StackedPairs, TaskbarDisplaySettings.Default.LayoutMode);
        Assert.Equal("#00000000", TaskbarDisplaySettings.Default.BackgroundColor);
        TaskbarDisplaySettings.Default.Validate();
    }

    [Theory]
    [InlineData(TaskbarLayoutMode.StackedPairs)]
    [InlineData(TaskbarLayoutMode.Inline)]
    [InlineData(TaskbarLayoutMode.AdaptiveColumns)]
    [InlineData(TaskbarLayoutMode.LabelOverValue)]
    public void LayoutModeRoundTripsThroughTheSettingsFile(TaskbarLayoutMode mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new TaskbarDisplaySettingsStore(path);
            var expected = new TaskbarDisplaySettings(
                [TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.CpuUsage],
                Compact: true, Vertical: false, RefreshIntervalSeconds: 2, LayoutMode: mode);

            store.Save(expected);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(mode, actual.LayoutMode);
            // The string-enum form keeps the file readable and the version is the current schema.
            Assert.Contains("\"layoutMode\": \"" + mode, File.ReadAllText(path));
            Assert.Contains("\"version\": 6", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LegacyFileWithoutLayoutModeLoadsAsStackedPairsAndKeepsOtherSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            // A version-4 file written before the layout mode existed (with the retired position
            // fields still present) keeps every setting and gets the documented A default.
            File.WriteAllText(path,
                "{\"version\":4,\"items\":[\"CpuTemperature\",\"FanRpm\"],\"compact\":false,\"vertical\":false," +
                "\"refreshIntervalSeconds\":3,\"fontFamily\":\"Consolas\",\"fontSize\":14," +
                "\"textColor\":\"#FFFFFFFF\",\"backgroundColor\":\"#80202124\"," +
                "\"positionAnchor\":\"Center\",\"positionOffsetDip\":-75}");

            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(TaskbarLayoutMode.StackedPairs, actual.LayoutMode);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm], actual.Items);
            Assert.Equal("Consolas", actual.FontFamily);
            Assert.Equal(14, actual.FontSize);
            Assert.Equal("#FFFFFFFF", actual.TextColor);
            Assert.Equal("#80202124", actual.BackgroundColor);
            Assert.Equal(3, actual.RefreshIntervalSeconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("\"FutureLayoutMode\"")]
    [InlineData("99")]
    [InlineData("null")]
    public void UnknownOrInvalidLayoutModeFallsBackToStackedPairsWithoutResettingOtherSettings(string storedMode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path,
                "{\"version\":4,\"items\":[\"CpuTemperature\",\"CpuUsage\"],\"compact\":true,\"vertical\":false," +
                "\"refreshIntervalSeconds\":5,\"fontFamily\":\"Consolas\",\"fontSize\":14," +
                "\"textColor\":\"#FFFFFFFF\",\"backgroundColor\":\"#80202124\"," +
                "\"layoutMode\":" + storedMode + "}");

            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);

            // No reset warning: only the unusable layout field is ignored.
            Assert.Null(warning);
            Assert.Equal(TaskbarLayoutMode.StackedPairs, actual.LayoutMode);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.CpuUsage], actual.Items);
            Assert.True(actual.Compact);
            Assert.Equal(5, actual.RefreshIntervalSeconds);
            Assert.Equal("Consolas", actual.FontFamily);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UndefinedLayoutModeIsRejectedByValidate()
    {
        var settings = new TaskbarDisplaySettings(
            [TaskbarDisplayItem.CpuTemperature], Compact: true, Vertical: false, RefreshIntervalSeconds: 1,
            LayoutMode: (TaskbarLayoutMode)42);
        Assert.Throws<ArgumentException>(() => settings.Validate());
    }
    [Fact]
    public void VersionFourFileWithoutNudgeFieldsLoadsAsZeroOffsetAndSavesTheCurrentSchema()
    {
        // An existing installation never stored the bounded nudge, so it must load as 0/0 and keep
        // exactly the fixed placement it had before; the next save simply adds the two fields.
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path,
                "{\"version\":4,\"items\":[\"CpuTemperature\",\"CpuUsage\"],\"compact\":true,\"vertical\":false," +
                "\"refreshIntervalSeconds\":2,\"fontFamily\":\"Consolas\",\"fontSize\":14," +
                "\"textColor\":\"#FFFFFFFF\",\"backgroundColor\":\"#80202124\",\"layoutMode\":\"Inline\"}");
            var store = new TaskbarDisplaySettingsStore(path);

            var loaded = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(0, loaded.OffsetXDip);
            Assert.Equal(0, loaded.OffsetYDip);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.CpuUsage], loaded.Items);
            Assert.Equal(TaskbarLayoutMode.Inline, loaded.LayoutMode);
            Assert.Equal(14, loaded.FontSize);
            loaded.Validate();

            store.Save(loaded);
            var json = File.ReadAllText(path);
            Assert.Contains("\"version\": 6", json);
            Assert.Contains("\"offsetXDip\": 0", json);
            Assert.Contains("\"offsetYDip\": 0", json);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void BoundedNudgeRoundTripsThroughTheSettingsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new TaskbarDisplaySettingsStore(path);
            var expected = new TaskbarDisplaySettings(
                [TaskbarDisplayItem.CpuTemperature], Compact: true, Vertical: false,
                RefreshIntervalSeconds: 1, OffsetXDip: 100, OffsetYDip: -40);
            expected.Validate();

            store.Save(expected);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(100, actual.OffsetXDip);
            Assert.Equal(-40, actual.OffsetYDip);
            Assert.Equal(expected.Items, actual.Items);
            Assert.Equal(expected.Compact, actual.Compact);
            Assert.Equal(expected.Vertical, actual.Vertical);
            Assert.Equal(expected.RefreshIntervalSeconds, actual.RefreshIntervalSeconds);
            Assert.Equal(expected.TextColor, actual.TextColor);
            Assert.Equal(expected.BackgroundColor, actual.BackgroundColor);
            Assert.Equal(expected.LayoutMode, actual.LayoutMode);
            var json = File.ReadAllText(path);
            Assert.Contains("\"offsetXDip\": 100", json);
            Assert.Contains("\"offsetYDip\": -40", json);
            Assert.Contains("\"version\": 6", json);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(TaskbarDisplaySettings.MaximumOffsetDip + 1, 0)]
    [InlineData(TaskbarDisplaySettings.MinimumOffsetDip - 1, 0)]
    [InlineData(0, TaskbarDisplaySettings.MaximumOffsetDip + 1)]
    [InlineData(0, TaskbarDisplaySettings.MinimumOffsetDip - 1)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void NudgeOutsideTheBoundedRangeIsRejectedByValidate(int offsetX, int offsetY)
    {
        var settings = new TaskbarDisplaySettings(
            [TaskbarDisplayItem.CpuTemperature], Compact: true, Vertical: false, RefreshIntervalSeconds: 1,
            OffsetXDip: offsetX, OffsetYDip: offsetY);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Theory]
    [InlineData(TaskbarDisplaySettings.MinimumOffsetDip)]
    [InlineData(-8)]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(TaskbarDisplaySettings.MaximumOffsetDip)]
    public void NudgeBoundsAreInclusiveAndZeroIsTheDefault(int offset)
    {
        var settings = new TaskbarDisplaySettings(
            [TaskbarDisplayItem.CpuTemperature], Compact: true, Vertical: false, RefreshIntervalSeconds: 1,
            OffsetXDip: offset, OffsetYDip: -offset);
        settings.Validate();
        Assert.Equal(offset, settings.OffsetXDip);
        Assert.Equal(-offset, settings.OffsetYDip);
        Assert.Equal(0, TaskbarDisplaySettings.Default.OffsetXDip);
        Assert.Equal(0, TaskbarDisplaySettings.Default.OffsetYDip);
    }

    [Fact]
    public void LegacyPositionFieldsStayIgnoredWhileTheBoundedNudgeIsPreserved()
    {
        // A hand-edited/older file may still carry the retired anchor pair next to the new bounded
        // nudge: the anchor is ignored (and dropped on save) while the nudge and every other
        // setting survive.
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path,
                "{\"version\":5,\"items\":[\"CpuTemperature\",\"FanRpm\"],\"compact\":false,\"vertical\":false," +
                "\"refreshIntervalSeconds\":2,\"fontFamily\":\"Consolas\",\"fontSize\":14," +
                "\"textColor\":\"#FFFFFFFF\",\"backgroundColor\":\"#80202124\",\"layoutMode\":\"AdaptiveColumns\"," +
                "\"offsetXDip\":100,\"offsetYDip\":-40," +
                "\"positionAnchor\":\"Center\",\"positionOffsetDip\":-75}");
            var store = new TaskbarDisplaySettingsStore(path);

            var loaded = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(100, loaded.OffsetXDip);
            Assert.Equal(-40, loaded.OffsetYDip);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm], loaded.Items);
            Assert.Equal(TaskbarLayoutMode.AdaptiveColumns, loaded.LayoutMode);
            Assert.Equal(14, loaded.FontSize);
            Assert.Equal("#80202124", loaded.BackgroundColor);
            loaded.Validate();

            store.Save(loaded);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("positionAnchor", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("positionOffsetDip", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Center", json);
            Assert.DoesNotContain("-75", json);
            Assert.Contains("\"offsetXDip\": 100", json);
            Assert.Contains("\"offsetYDip\": -40", json);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TaskbarDisplayIsEnabledByDefault()
    {
        // The dialog's 显示任务栏状态 switch defaults to on, so a fresh installation keeps the strip
        // it always had; only an explicit save of false hides it.
        Assert.True(TaskbarDisplaySettings.Default.Enabled);
        TaskbarDisplaySettings.Default.Validate();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    public void LegacyFileWithoutEnabledFieldLoadsAsEnabled(string version)
    {
        // Schema 6 appended the on/off switch. Every older file has no "enabled" member, which must
        // load as true (the strip that installation already showed) without a reset warning.
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path,
                "{\"version\":" + version + ",\"items\":[\"CpuTemperature\",\"FanRpm\"],\"compact\":false,\"vertical\":false,\"refreshIntervalSeconds\":1}");
            var actual = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.True(actual.Enabled);
            // The item list keeps its own version rule (v1/v2 still get CPU package power injected).
            Assert.Contains(TaskbarDisplayItem.CpuTemperature, actual.Items);
            Assert.Contains(TaskbarDisplayItem.FanRpm, actual.Items);
            Assert.False(actual.Compact);
            Assert.False(actual.Vertical);
            Assert.Equal(1, actual.RefreshIntervalSeconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DisabledTaskbarRoundTripsAsSchemaSixAndKeepsEveryOtherSetting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new TaskbarDisplaySettingsStore(path);
            var expected = new TaskbarDisplaySettings(
                [TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm], Compact: true, Vertical: false,
                RefreshIntervalSeconds: 2, LayoutMode: TaskbarLayoutMode.AdaptiveColumns,
                OffsetXDip: 8, OffsetYDip: -8, Enabled: false);
            expected.Validate();

            store.Save(expected);
            var json = File.ReadAllText(path);
            Assert.Contains("\"version\": 6", json);
            Assert.Contains("\"enabled\": false", json);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.False(actual.Enabled);
            Assert.Equal(expected.Items, actual.Items);
            Assert.Equal(expected.Compact, actual.Compact);
            Assert.Equal(expected.Vertical, actual.Vertical);
            Assert.Equal(expected.RefreshIntervalSeconds, actual.RefreshIntervalSeconds);
            Assert.Equal(expected.LayoutMode, actual.LayoutMode);
            Assert.Equal(expected.OffsetXDip, actual.OffsetXDip);
            Assert.Equal(expected.OffsetYDip, actual.OffsetYDip);

            // Re-enabling is the same single field, not a new file.
            store.Save(expected with { Enabled = true });
            Assert.Contains("\"enabled\": true", File.ReadAllText(path));
            var reenabled = store.LoadOrDefault(out var reenabledWarning);
            Assert.Null(reenabledWarning);
            Assert.True(reenabled.Enabled);
            Assert.Equal(expected.Items, reenabled.Items);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExplicitEnabledValueInTheFileIsHonoured()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "taskbar.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path,
                "{\"version\":6,\"items\":[\"CpuTemperature\"],\"compact\":true,\"vertical\":false," +
                "\"refreshIntervalSeconds\":1,\"enabled\":false}");
            var disabled = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var disabledWarning);
            Assert.Null(disabledWarning);
            Assert.False(disabled.Enabled);

            File.WriteAllText(path,
                "{\"version\":6,\"items\":[\"CpuTemperature\"],\"compact\":true,\"vertical\":false," +
                "\"refreshIntervalSeconds\":1,\"enabled\":true}");
            var enabled = new TaskbarDisplaySettingsStore(path).LoadOrDefault(out var enabledWarning);
            Assert.Null(enabledWarning);
            Assert.True(enabled.Enabled);
        }
        finally { Directory.Delete(directory, true); }
    }
}
