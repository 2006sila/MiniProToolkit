using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Pins the independent desktop floating window contract: disabled by default, stored in its own
/// file with its own schema, tolerant per-field fallback for a hand-edited/unusable field and every
/// value round-tripping without touching the taskbar settings. All file work happens under the
/// process temp directory; no real <c>%LOCALAPPDATA%</c> file is written or read.
/// </summary>
public sealed class FloatingStatusSettingsTests
{
    private static (FloatingStatusSettingsStore Store, string Directory) CreateStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        System.IO.Directory.CreateDirectory(directory);
        return (new FloatingStatusSettingsStore(Path.Combine(directory, "floating-status.json")), directory);
    }

    [Fact]
    public void DefaultIsDisabledAndIndependentFromTheTaskbar()
    {
        var settings = FloatingStatusSettings.Default;
        settings.Validate();

        Assert.False(settings.Enabled);
        Assert.Equal(TaskbarLayoutMode.StackedPairs, settings.LayoutMode);
        Assert.True(settings.Compact);
        Assert.True(settings.Topmost);
        Assert.False(settings.IsLocked);
        Assert.Null(settings.MonitorDeviceName);
        Assert.Null(settings.LeftDip);
        Assert.Null(settings.TopDip);
        // Second-phase interaction switches: click-through is opt-in, edge snapping is on.
        Assert.False(settings.ClickThrough);
        Assert.True(settings.SnapToEdges);
        // Only the four static headline metrics; no control action is reachable from the card.
        Assert.Equal(
            [TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm, TaskbarDisplayItem.CpuPackagePower, TaskbarDisplayItem.PowerMode],
            settings.Items);
        Assert.Equal(12d, settings.FontSize);
        Assert.Equal("Segoe UI", settings.FontFamily);
    }

    /// <summary>
    /// Field-by-field default comparison: the record's list property is compared by reference by
    /// the generated equality, so two fresh <c>Default</c> instances are never record-equal.
    /// </summary>
    private static void AssertDefault(FloatingStatusSettings actual)
    {
        var expected = FloatingStatusSettings.Default;
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.LayoutMode, actual.LayoutMode);
        Assert.Equal(expected.Compact, actual.Compact);
        Assert.Equal(expected.FontFamily, actual.FontFamily);
        Assert.Equal(expected.FontSize, actual.FontSize);
        Assert.Equal(expected.TextColor, actual.TextColor);
        Assert.Equal(expected.BackgroundColor, actual.BackgroundColor);
        Assert.Equal(expected.Topmost, actual.Topmost);
        Assert.Equal(expected.IsLocked, actual.IsLocked);
        Assert.Null(actual.MonitorDeviceName);
        Assert.Null(actual.LeftDip);
        Assert.Null(actual.TopDip);
        Assert.Equal(expected.ClickThrough, actual.ClickThrough);
        Assert.Equal(expected.SnapToEdges, actual.SnapToEdges);
    }

    [Fact]
    public void FloatingStoreUsesItsOwnFileNextToTheTaskbarFile()
    {
        var floating = new FloatingStatusSettingsStore();
        var taskbar = new TaskbarDisplaySettingsStore();

        Assert.NotEqual(taskbar.Path, floating.Path);
        Assert.EndsWith("floating-status.json", floating.Path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("taskbar.json", taskbar.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetDirectoryName(taskbar.Path), Path.GetDirectoryName(floating.Path));
    }

    [Fact]
    public void SaveAndLoadRoundTripsEveryIndependentValue()
    {
        var (store, directory) = CreateStore();
        var expected = new FloatingStatusSettings(
            Enabled: true,
            Items: [TaskbarDisplayItem.GpuTemperature, TaskbarDisplayItem.CpuUsage, TaskbarDisplayItem.PowerMode],
            LayoutMode: TaskbarLayoutMode.Inline,
            Compact: false,
            FontFamily: "Consolas",
            FontSize: 15,
            TextColor: "#FFF0F3F6",
            BackgroundColor: "#80202124",
            Topmost: false,
            IsLocked: true,
            MonitorDeviceName: @"\\.\DISPLAY2",
            LeftDip: -120.5,
            TopDip: 33.3,
            ClickThrough: true,
            SnapToEdges: false);
        try
        {
            store.Save(expected);
            var json = File.ReadAllText(store.Path);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(expected.Enabled, actual.Enabled);
            Assert.Equal(expected.Items, actual.Items);
            Assert.Equal(expected.LayoutMode, actual.LayoutMode);
            Assert.Equal(expected.Compact, actual.Compact);
            Assert.Equal(expected.FontFamily, actual.FontFamily);
            Assert.Equal(expected.FontSize, actual.FontSize);
            Assert.Equal(expected.TextColor, actual.TextColor);
            Assert.Equal(expected.BackgroundColor, actual.BackgroundColor);
            Assert.Equal(expected.Topmost, actual.Topmost);
            Assert.Equal(expected.IsLocked, actual.IsLocked);
            Assert.Equal(expected.MonitorDeviceName, actual.MonitorDeviceName);
            Assert.Equal(expected.LeftDip, actual.LeftDip);
            Assert.Equal(expected.TopDip, actual.TopDip);
            Assert.True(actual.ClickThrough);
            Assert.False(actual.SnapToEdges);
            Assert.Contains("\"clickThrough\": true", json);
            Assert.Contains("\"snapToEdges\": false", json);
            Assert.Contains("\"version\": 1", json);
            Assert.Contains("\"Inline\"", json);
            Assert.DoesNotContain("taskbar", json, StringComparison.OrdinalIgnoreCase);
            // The taskbar file is a different file and is not created by a floating save.
            Assert.False(File.Exists(Path.Combine(directory, "taskbar.json")));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingFileReturnsDefaultWithoutWarning()
    {
        var (store, directory) = CreateStore();
        try
        {
            var actual = store.LoadOrDefault(out var warning);
            Assert.Null(warning);
            AssertDefault(actual);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("{\"version\":99,\"items\":[\"CpuTemperature\"]}")]
    [InlineData("{\"version\":1,\"items\":null}")]
    [InlineData("{\"version\":1,\"items\":[\"RetiredMetric\"]}")]
    public void StructurallyUnusableFileFallsBackToDefaultWithWarning(string content)
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path, content);

            var actual = store.LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            AssertDefault(actual);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExplicitNullLayoutModeIsTreatedAsAbsentWithoutAWarning()
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path,
                "{\"version\":1,\"items\":[\"CpuTemperature\"],\"layoutMode\":null,\"fontSize\":14}");
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(TaskbarLayoutMode.StackedPairs, actual.LayoutMode);
            Assert.Equal(14d, actual.FontSize);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnknownItemIsDroppedWithoutLosingTheOtherFields()
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path,
                "{\"version\":1,\"enabled\":true,\"items\":[\"CpuTemperature\",\"RetiredMetric\",\"PowerMode\"]," +
                "\"layoutMode\":\"LabelOverValue\",\"compact\":false,\"fontFamily\":\"Consolas\",\"fontSize\":14," +
                "\"textColor\":\"#FFFFFFFF\",\"backgroundColor\":\"#80202124\",\"topmost\":false,\"isLocked\":true}");
            var actual = store.LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.True(actual.Enabled);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.PowerMode], actual.Items);
            Assert.Equal(TaskbarLayoutMode.LabelOverValue, actual.LayoutMode);
            Assert.False(actual.Compact);
            Assert.Equal("Consolas", actual.FontFamily);
            Assert.Equal(14d, actual.FontSize);
            Assert.Equal("#80202124", actual.BackgroundColor);
            Assert.False(actual.Topmost);
            Assert.True(actual.IsLocked);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("\"FutureLayoutMode\"")]
    [InlineData("99")]
    public void UnusableLayoutModeFallsBackToStackedPairsAndKeepsEverythingElse(string stored)
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path,
                "{\"version\":1,\"enabled\":true,\"items\":[\"CpuTemperature\",\"CpuUsage\"]," +
                "\"layoutMode\":" + stored + ",\"compact\":false,\"fontFamily\":\"Consolas\",\"fontSize\":14," +
                "\"textColor\":\"#FFFFFFFF\",\"backgroundColor\":\"#80202124\"}");
            var actual = store.LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(TaskbarLayoutMode.StackedPairs, actual.LayoutMode);
            Assert.True(actual.Enabled);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.CpuUsage], actual.Items);
            Assert.False(actual.Compact);
            Assert.Equal("Consolas", actual.FontFamily);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnusableFontSizeAndColoursFallBackPerFieldWithoutResettingTheFile()
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path,
                "{\"version\":1,\"enabled\":true,\"items\":[\"CpuTemperature\"],\"layoutMode\":\"Inline\"," +
                "\"compact\":true,\"fontFamily\":\"Consolas\",\"fontSize\":999," +
                "\"textColor\":\"not-a-colour\",\"backgroundColor\":\"#12345\",\"topmost\":false," +
                "\"monitorDeviceName\":\"\\\\\\\\.\\\\DISPLAY3\",\"leftDip\":40,\"topDip\":50}");
            var actual = store.LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.True(actual.Enabled);
            Assert.Equal(TaskbarLayoutMode.Inline, actual.LayoutMode);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature], actual.Items);
            Assert.Equal("Consolas", actual.FontFamily);
            Assert.Equal(FloatingStatusSettings.Default.FontSize, actual.FontSize);
            Assert.Equal(FloatingStatusSettings.Default.TextColor, actual.TextColor);
            Assert.Equal(FloatingStatusSettings.Default.BackgroundColor, actual.BackgroundColor);
            Assert.False(actual.Topmost);
            Assert.Equal(40d, actual.LeftDip);
            Assert.Equal(50d, actual.TopDip);
            actual.Validate();
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void HalfStoredPositionFallsBackToTheDefaultCornerWithAWarning()
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path,
                "{\"version\":1,\"items\":[\"CpuTemperature\"],\"leftDip\":40,\"topDip\":null}");
            var actual = store.LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Null(actual.LeftDip);
            Assert.Null(actual.TopDip);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature], actual.Items);
            actual.Validate();
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void LegacyFileWithoutNewerFieldsLoadsWithDocumentedDefaults()
    {
        var (store, directory) = CreateStore();
        try
        {
            // Only the original minimal shape: every field added later must default, not reset.
            File.WriteAllText(store.Path, "{\"version\":1,\"items\":[\"FanRpm\"]}");
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.False(actual.Enabled);
            Assert.True(actual.Compact);
            Assert.True(actual.Topmost);
            Assert.False(actual.IsLocked);
            Assert.Equal(FloatingStatusSettings.Default.FontFamily, actual.FontFamily);
            Assert.Equal(FloatingStatusSettings.Default.BackgroundColor, actual.BackgroundColor);
            Assert.Null(actual.LeftDip);
            Assert.Null(actual.TopDip);
            // The two appended switches did not exist in that file: documented defaults, no warning.
            Assert.False(actual.ClickThrough);
            Assert.True(actual.SnapToEdges);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void InteractionSwitchesSurviveADisabledCard()
    {
        var (store, directory) = CreateStore();
        try
        {
            // Enabled=false must not quietly drop the other preferences: they apply again next time.
            store.Save(FloatingStatusSettings.Default with { Enabled = false, ClickThrough = true, SnapToEdges = false });

            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.False(actual.Enabled);
            Assert.True(actual.ClickThrough);
            Assert.False(actual.SnapToEdges);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExplicitNullInteractionFlagsAreTreatedAsAbsent()
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path,
                "{\"version\":1,\"items\":[\"CpuTemperature\"],\"clickThrough\":null,\"snapToEdges\":null}");

            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.False(actual.ClickThrough);
            Assert.True(actual.SnapToEdges);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("\"yes\"", "true")]
    [InlineData("1", "true")]
    [InlineData("false", "\"sometimes\"")]
    [InlineData("false", "0")]
    public void MalformedInteractionFlagFallsBackForThatFieldOnly(string clickThrough, string snapToEdges)
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path,
                "{\"version\":1,\"enabled\":true,\"items\":[\"CpuTemperature\"],\"layoutMode\":\"Inline\"," +
                "\"fontSize\":14,\"clickThrough\":" + clickThrough + ",\"snapToEdges\":" + snapToEdges + "}");

            var actual = store.LoadOrDefault(out var warning);

            // A wrong *type* for one of the two appended switches falls back for that switch only;
            // every other field keeps the store's original rule.
            Assert.NotNull(warning);
            Assert.True(actual.Enabled);
            Assert.Equal(14d, actual.FontSize);
            Assert.Equal(TaskbarLayoutMode.Inline, actual.LayoutMode);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature], actual.Items);
            Assert.False(actual.ClickThrough);
            Assert.True(actual.SnapToEdges);
            actual.Validate();
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnknownFutureFieldIsSkippedAndNeverResetsTheFile()
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path,
                "{\"version\":1,\"items\":[\"CpuTemperature\"],\"enabled\":true,\"futureField\":{\"a\":1}," +
                "\"anotherFuture\":[1,2,3]}");
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.True(actual.Enabled);
            Assert.Equal([TaskbarDisplayItem.CpuTemperature], actual.Items);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void DuplicateItemsAreDroppedInFirstSeenOrderWithoutResettingTheFile()
    {
        var (store, directory) = CreateStore();
        try
        {
            // A hand-edited/copied file repeated two items. Before this rule the duplicate reached
            // Validate(), threw and reset the WHOLE file to defaults.
            File.WriteAllText(store.Path,
                "{\"version\":1,\"enabled\":true,\"items\":[\"FanRpm\",\"CpuTemperature\",\"FanRpm\",\"PowerMode\",\"CpuTemperature\"]," +
                "\"layoutMode\":\"Inline\",\"compact\":false,\"fontFamily\":\"Consolas\",\"fontSize\":14}");

            var actual = store.LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Contains("重复显示项", warning);
            Assert.Equal(
                [TaskbarDisplayItem.FanRpm, TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.PowerMode],
                actual.Items);
            // Every other field is still the saved value, not the default.
            Assert.True(actual.Enabled);
            Assert.Equal(TaskbarLayoutMode.Inline, actual.LayoutMode);
            Assert.False(actual.Compact);
            Assert.Equal("Consolas", actual.FontFamily);
            Assert.Equal(14d, actual.FontSize);
            actual.Validate();
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void FileWithOnlyDuplicatedItemsKeepsOneItemInsteadOfResetting()
    {
        var (store, directory) = CreateStore();
        try
        {
            File.WriteAllText(store.Path, "{\"version\":1,\"items\":[\"CpuUsage\",\"CpuUsage\"],\"fontSize\":16}");

            var actual = store.LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal([TaskbarDisplayItem.CpuUsage], actual.Items);
            Assert.Equal(16d, actual.FontSize);
            actual.Validate();
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void SaveReplacesTheFileAtomicallyAndLeavesNoTemporaryFile()
    {
        var (store, directory) = CreateStore();
        try
        {
            store.Save(FloatingStatusSettings.Default with { Enabled = true, LeftDip = 12, TopDip = 24 });
            store.Save(FloatingStatusSettings.Default with { Enabled = false, LeftDip = 13, TopDip = 25 });

            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.False(actual.Enabled);
            Assert.Equal(13d, actual.LeftDip);
            Assert.Equal(25d, actual.TopDip);
            Assert.Empty(System.IO.Directory.GetFiles(directory, "*.tmp"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ValidateRejectsInvalidValues()
    {
        var valid = FloatingStatusSettings.Default;
        Assert.Throws<ArgumentException>(() => (valid with { Items = [] }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (valid with { Items = [TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.CpuTemperature] }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (valid with { Items = [(TaskbarDisplayItem)99] }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { LayoutMode = (TaskbarLayoutMode)42 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { FontSize = 7 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { FontSize = 33 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { FontSize = double.NaN }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { FontFamily = "  " }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { TextColor = "red" }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { BackgroundColor = "#1234" }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { MonitorDeviceName = new string('x', 201) }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { LeftDip = 10 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { LeftDip = FloatingStatusSettings.MaximumPositionDip + 1, TopDip = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { LeftDip = 0, TopDip = double.PositiveInfinity }).Validate());
        (valid with { LeftDip = 0, TopDip = 0 }).Validate();
    }

    [Fact]
    public void FloatingSaveNeverWritesTheTaskbarFile()
    {
        var (store, directory) = CreateStore();
        try
        {
            // A taskbar file already exists with a user value: the floating save must leave it alone.
            var taskbarPath = Path.Combine(directory, "taskbar.json");
            File.WriteAllText(taskbarPath, "{\"version\":5,\"items\":[\"FanRpm\"],\"compact\":true,\"vertical\":false,\"refreshIntervalSeconds\":1}");
            var before = File.ReadAllText(taskbarPath);

            store.Save(FloatingStatusSettings.Default with { Enabled = true });

            Assert.Equal(before, File.ReadAllText(taskbarPath));
            Assert.True(File.Exists(store.Path));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }
}
