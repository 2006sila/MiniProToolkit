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
            Assert.Contains("\"version\": 4", File.ReadAllText(path));
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
}
