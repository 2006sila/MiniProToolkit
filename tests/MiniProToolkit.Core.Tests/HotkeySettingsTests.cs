using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// The global hotkeys are rebindable but never free-form: only whitelisted keys and modifier
/// combinations that are safe to own globally are accepted, conflicts are refused, and an old or
/// partial settings file keeps the shipped Ctrl+Alt+... defaults instead of losing hotkey control.
/// </summary>
public sealed class HotkeySettingsTests
{
    [Fact]
    public void DefaultsKeepThePreviouslyFixedCtrlAltCombinations()
    {
        Assert.Equal(
        [
            (HotkeyAction.SilenceMode, "Ctrl+Alt+1"),
            (HotkeyAction.BalanceMode, "Ctrl+Alt+2"),
            (HotkeyAction.PerformanceMode, "Ctrl+Alt+3"),
            (HotkeyAction.AutomaticMode, "Ctrl+Alt+A"),
            (HotkeyAction.FullSpeed, "Ctrl+Alt+F")
        ],
            HotkeySettings.Default.Bindings.Select(binding => (binding.Action, binding.Gesture.Describe())));

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeySettings.Default.GestureFor(HotkeyAction.FullSpeed).Modifiers);
        HotkeySettings.Default.Validate();
    }

    [Fact]
    public void ActionValuesStayStableBecauseTheyAreTheRegistrationIds()
    {
        Assert.Equal(0, (int)HotkeyAction.SilenceMode);
        Assert.Equal(1, (int)HotkeyAction.BalanceMode);
        Assert.Equal(2, (int)HotkeyAction.PerformanceMode);
        Assert.Equal(3, (int)HotkeyAction.AutomaticMode);
        Assert.Equal(4, (int)HotkeyAction.FullSpeed);
        Assert.Equal(5, Enum.GetValues<HotkeyAction>().Length);
    }

    [Fact]
    public void KeyCatalogIsWhitelistedAndMapsToVirualKeys()
    {
        Assert.Equal(48, HotkeyKeyCatalog.Keys.Count);
        Assert.Equal(HotkeyKeyCatalog.Keys.Count, HotkeyKeyCatalog.Keys.Distinct().Count());

        Assert.True(HotkeyKeyCatalog.TryGetVirtualKey("1", out var one));
        Assert.Equal(0x31u, one);
        Assert.True(HotkeyKeyCatalog.TryGetVirtualKey("0", out var zero));
        Assert.Equal(0x30u, zero);
        Assert.True(HotkeyKeyCatalog.TryGetVirtualKey("a", out var a));
        Assert.Equal(0x41u, a);
        Assert.True(HotkeyKeyCatalog.TryGetVirtualKey("Z", out var z));
        Assert.Equal(0x5Au, z);
        Assert.True(HotkeyKeyCatalog.TryGetVirtualKey("F1", out var f1));
        Assert.Equal(0x70u, f1);
        Assert.True(HotkeyKeyCatalog.TryGetVirtualKey("f12", out var f12));
        Assert.Equal(0x7Bu, f12);

        foreach (var rejected in new[] { "F13", "F0", "Tab", "Escape", "Delete", "Ctrl", "", "  ", null, "1x" })
        {
            Assert.False(HotkeyKeyCatalog.TryGetVirtualKey(rejected, out _), $"key '{rejected}' must not be whitelisted");
            Assert.Null(HotkeyKeyCatalog.Normalize(rejected));
        }
    }

    [Fact]
    public void GesturesWithoutAModifierOrWithShiftOnlyAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new HotkeyGesture(HotkeyModifiers.None, "1").Validate());
        Assert.Throws<ArgumentException>(() => new HotkeyGesture(HotkeyModifiers.Shift, "1").Validate());
        // Ctrl/Alt/Win are each enough on their own.
        new HotkeyGesture(HotkeyModifiers.Control, "1").Validate();
        new HotkeyGesture(HotkeyModifiers.Alt, "9").Validate();
        new HotkeyGesture(HotkeyModifiers.Windows, "F5").Validate();
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Q").Validate();
    }

    [Fact]
    public void ShellReservedCombinationsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new HotkeyGesture(HotkeyModifiers.Alt, "F4").Validate());
        Assert.Throws<ArgumentException>(() => new HotkeyGesture(HotkeyModifiers.Windows, "L").Validate());
        Assert.Throws<ArgumentException>(() => new HotkeyGesture(HotkeyModifiers.Windows, "D").Validate());
        // The same key with a different modifier set is not reserved.
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, "F4").Validate();
    }

    [Fact]
    public void DuplicateCombinationsAcrossActionsAreRejected()
    {
        var settings = HotkeySettings.Default
            .WithGesture(HotkeyAction.BalanceMode, HotkeyGesture.CtrlAlt("1"));
        var error = Assert.Throws<ArgumentException>(settings.Validate);
        Assert.Contains("静音模式", error.Message);
        Assert.Contains("均衡模式", error.Message);
        Assert.Contains("Ctrl+Alt+1", error.Message);
    }

    [Fact]
    public void MissingOrDuplicatedActionsAreRejected()
    {
        var missing = new HotkeySettings(HotkeySettings.Default.Bindings
            .Where(binding => binding.Action != HotkeyAction.FullSpeed)
            .ToArray());
        Assert.Throws<ArgumentException>(missing.Validate);

        var duplicated = new HotkeySettings(
        [
            .. HotkeySettings.Default.Bindings,
            new HotkeyBinding(HotkeyAction.FullSpeed, HotkeyGesture.CtrlAlt("F"))
        ]);
        Assert.Throws<ArgumentException>(duplicated.Validate);
    }

    [Fact]
    public void ModifierNamesAreParsedFromAliasesAndUnknownNamesAreRejected()
    {
        Assert.True(HotkeyGesture.TryParseModifiers("ctrl+alt", out var controlAlt));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, controlAlt);
        Assert.True(HotkeyGesture.TryParseModifiers("Control, Alt, Shift, Win", out var all));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows, all);
        Assert.True(HotkeyGesture.TryParseModifiers("windows", out var windows));
        Assert.Equal(HotkeyModifiers.Windows, windows);
        Assert.False(HotkeyGesture.TryParseModifiers("Ctrl+Foo", out _));
        Assert.False(HotkeyGesture.TryParseModifiers("", out _));
        Assert.False(HotkeyGesture.TryParseModifiers(null, out _));
        Assert.Equal("Control+Alt", HotkeyGesture.CtrlAlt("1").DescribeStorage());
    }

    [Fact]
    public void NativeValuesMatchTheWin32RegistrationContract()
    {
        Assert.Equal(0x0001u, (uint)HotkeyModifiers.Alt);
        Assert.Equal(0x0002u, (uint)HotkeyModifiers.Control);
        Assert.Equal(0x0004u, (uint)HotkeyModifiers.Shift);
        Assert.Equal(0x0008u, (uint)HotkeyModifiers.Windows);
        var gesture = HotkeyGesture.CtrlAlt("2");
        Assert.Equal(0x0003u, gesture.NativeModifiers);
        Assert.Equal(0x32u, gesture.VirtualKey);
        Assert.Equal("Ctrl+Alt+2", gesture.Describe());
    }

    [Fact]
    public void MissingFileUsesTheDefaultsWithoutAWarning()
    {
        var directory = NewDirectory();
        try
        {
            var settings = new HotkeySettingsStore(Path.Combine(directory, "hotkeys.json")).LoadOrDefault(out var warning);
            Assert.Null(warning);
            Assert.Equal(HotkeySettings.Default.Bindings.Select(binding => binding.Gesture.Describe()),
                settings.Bindings.Select(binding => binding.Gesture.Describe()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SaveAndLoadRoundTripsTheReboundGestures()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            var store = new HotkeySettingsStore(path);
            var expected = HotkeySettings.Default
                .WithGesture(HotkeyAction.SilenceMode, new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Q"))
                .WithGesture(HotkeyAction.FullSpeed, new HotkeyGesture(HotkeyModifiers.Windows, "f11"));
            expected.Validate();
            store.Save(expected);

            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal("Ctrl+Shift+Q", actual.GestureFor(HotkeyAction.SilenceMode).Describe());
            Assert.Equal("Win+F11", actual.GestureFor(HotkeyAction.FullSpeed).Describe());
            Assert.Equal(expected.Bindings.Select(binding => binding.Gesture.Describe()),
                actual.Bindings.Select(binding => binding.Gesture.Describe()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OldFileWithoutHotkeyFieldsKeepsEveryDefault()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            // A file written before rebinding existed (only a version, no bindings).
            File.WriteAllText(path, "{\"version\":1}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal(HotkeySettings.Default.Bindings.Select(binding => binding.Gesture.Describe()),
                settings.Bindings.Select(binding => binding.Gesture.Describe()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PartialFileKeepsTheConfiguredActionAndDefaultsTheRest()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":[{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"9\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal("Ctrl+Alt+9", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            Assert.Equal("Ctrl+Alt+2", settings.GestureFor(HotkeyAction.BalanceMode).Describe());
            Assert.Equal("Ctrl+Alt+F", settings.GestureFor(HotkeyAction.FullSpeed).Describe());
            settings.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnsupportedKeyOrModifierFallsBackPerActionInsteadOfResettingTheFile()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":[" +
                "{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Shift\",\"key\":\"Journal\"}," +
                "{\"action\":\"BalanceMode\",\"modifiers\":\"Hyper\",\"key\":\"2\"}," +
                "{\"action\":\"PerformanceMode\",\"modifiers\":\"Control\",\"key\":\"3\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            // The two unusable entries fall back to their default, the valid one survives.
            Assert.Equal("Ctrl+Alt+1", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            Assert.Equal("Ctrl+Alt+2", settings.GestureFor(HotkeyAction.BalanceMode).Describe());
            Assert.Equal("Ctrl+3", settings.GestureFor(HotkeyAction.PerformanceMode).Describe());
            settings.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DangerousEmptyCombinationInAFileFallsBackToTheDefault()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":[{\"action\":\"SilenceMode\",\"modifiers\":\"\",\"key\":\"1\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal("Ctrl+Alt+1", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            settings.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ConflictInAFileKeepsTheFirstActionAndDefaultsTheLaterOne()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":[" +
                "{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"7\"}," +
                "{\"action\":\"BalanceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"7\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Contains("冲突", warning);
            Assert.Equal("Ctrl+Alt+7", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            Assert.Equal("Ctrl+Alt+2", settings.GestureFor(HotkeyAction.BalanceMode).Describe());
            settings.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnknownActionsAndUnsupportedVersionsDowngradeSafely()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":[{\"action\":\"MakeCoffee\",\"modifiers\":\"Control+Alt\",\"key\":\"1\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var unknownWarning);
            Assert.NotNull(unknownWarning);
            Assert.Equal("Ctrl+Alt+1", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            settings.Validate();

            File.WriteAllText(path, "{\"version\":99,\"bindings\":[]}");
            var reset = new HotkeySettingsStore(path).LoadOrDefault(out var versionWarning);
            Assert.NotNull(versionWarning);
            Assert.Equal(HotkeySettings.Default.Bindings.Select(binding => binding.Gesture.Describe()),
                reset.Bindings.Select(binding => binding.Gesture.Describe()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SavingAnInvalidSettingIsRefusedInsteadOfPersisted()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            var store = new HotkeySettingsStore(path);
            var invalid = HotkeySettings.Default
                .WithGesture(HotkeyAction.BalanceMode, new HotkeyGesture(HotkeyModifiers.None, "2"));

            Assert.Throws<ArgumentException>(() => store.Save(invalid));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RegistrationSummaryIsPerActionAndLogSafe()
    {
        Assert.Null(HotkeyRegistrationReport.Summarize([]));

        var summary = HotkeyRegistrationReport.Summarize(
        [
            new HotkeyRegistrationFailure(HotkeyAction.BalanceMode, "Ctrl+Alt+2",
                HotkeyRegistrationReport.ReasonForWin32Error(HotkeyRegistrationReport.HotkeyAlreadyRegistered)),
            new HotkeyRegistrationFailure(HotkeyAction.FullSpeed, "Ctrl+Alt+F",
                HotkeyRegistrationReport.ReasonForWin32Error(5))
        ]);

        Assert.NotNull(summary);
        Assert.Contains("均衡模式", summary);
        Assert.Contains("Ctrl+Alt+2", summary);
        Assert.Contains("已被其他程序占用", summary);
        Assert.Contains("错误码 5", summary);
        // No path, executable name or command line may leak into the log line.
        Assert.DoesNotContain(":\\", summary);
        Assert.DoesNotContain("hotkeys.json", summary);
        Assert.Equal("已被其他程序占用", HotkeyRegistrationReport.ReasonForWin32Error(1409));
    }

    [Fact]
    public void SettingsDescriptionIsLimitedToActionLabelsAndGestures()
    {
        var description = HotkeySettings.Default.Describe();

        Assert.Equal("静音模式=Ctrl+Alt+1；均衡模式=Ctrl+Alt+2；性能模式=Ctrl+Alt+3；EC 自动=Ctrl+Alt+A；满速=Ctrl+Alt+F", description);
        Assert.DoesNotContain(":\\", description);
    }

    [Fact]
    public void FileWithoutAVersionFieldIsReadAsTheCurrentVersion()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            // Minimal document: no version, no modifiers field on other actions.
            File.WriteAllText(path, "{\"bindings\":[{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"9\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            // A missing version must not reset the file to the defaults.
            Assert.Equal("Ctrl+Alt+9", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            Assert.Equal("Ctrl+Alt+F", settings.GestureFor(HotkeyAction.FullSpeed).Describe());
            Assert.DoesNotContain("版本", warning ?? string.Empty);
            settings.Validate();

            // An entirely empty object is still a valid (default-only) document, not a version error.
            File.WriteAllText(path, "{}");
            var empty = new HotkeySettingsStore(path).LoadOrDefault(out _);
            Assert.Equal(HotkeySettings.Default.Bindings.Select(binding => binding.Gesture.Describe()),
                empty.Bindings.Select(binding => binding.Gesture.Describe()));
            empty.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FutureVersionResetsTheWholeFileSafely()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            File.WriteAllText(path,
                "{\"version\":2,\"bindings\":[{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"9\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Contains("版本", warning);
            Assert.Equal(HotkeySettings.Default.Bindings.Select(binding => binding.Gesture.Describe()),
                settings.Bindings.Select(binding => binding.Gesture.Describe()));
            settings.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OldCompleteVersionKeepsEveryConfiguredGesture()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            // Exactly what Save() writes for the shipped defaults (v1, every action present).
            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":[" +
                "{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"1\"}," +
                "{\"action\":\"BalanceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"2\"}," +
                "{\"action\":\"PerformanceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"3\"}," +
                "{\"action\":\"AutomaticMode\",\"modifiers\":\"Control+Alt\",\"key\":\"A\"}," +
                "{\"action\":\"FullSpeed\",\"modifiers\":\"Control+Alt\",\"key\":\"F\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.Null(warning);
            Assert.Equal(HotkeySettings.Default.Bindings.Select(binding => binding.Gesture.Describe()),
                settings.Bindings.Select(binding => binding.Gesture.Describe()));
            settings.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ActionOccupyingAnothersDefaultGivesTheLaterActionAFreeCtrlAltCombination()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            // 静音模式 takes 均衡模式's default Ctrl+Alt+2; the file must not be reset as a whole.
            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":[{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"2\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);
            var again = new HotkeySettingsStore(path).LoadOrDefault(out _);

            Assert.NotNull(warning);
            Assert.Equal("Ctrl+Alt+2", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            // Deterministic repair: the first free Ctrl+Alt key of the whitelist (digits, letters, F-keys).
            Assert.Equal("Ctrl+Alt+1", settings.GestureFor(HotkeyAction.BalanceMode).Describe());
            Assert.Equal("Ctrl+Alt+3", settings.GestureFor(HotkeyAction.PerformanceMode).Describe());
            Assert.Equal("Ctrl+Alt+A", settings.GestureFor(HotkeyAction.AutomaticMode).Describe());
            Assert.Equal("Ctrl+Alt+F", settings.GestureFor(HotkeyAction.FullSpeed).Describe());
            settings.Validate();
            Assert.Equal(settings.Bindings.Select(binding => binding.Gesture.Describe()),
                again.Bindings.Select(binding => binding.Gesture.Describe()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SeveralConflictsAreRepairedPerActionWithoutLosingTheFile()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":[" +
                "{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"2\"}," +
                "{\"action\":\"BalanceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"2\"}," +
                "{\"action\":\"PerformanceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"2\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal("Ctrl+Alt+2", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            Assert.Equal("Ctrl+Alt+1", settings.GestureFor(HotkeyAction.BalanceMode).Describe());
            Assert.Equal("Ctrl+Alt+3", settings.GestureFor(HotkeyAction.PerformanceMode).Describe());
            settings.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DuplicatedAndIllegalEntriesStillLoadSafely()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "hotkeys.json");
            File.WriteAllText(path,
                "{\"bindings\":[" +
                "{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"7\"}," +
                "{\"action\":\"SilenceMode\",\"modifiers\":\"Control+Alt\",\"key\":\"8\"}," +
                "{\"action\":\"BalanceMode\",\"modifiers\":\"Alt\",\"key\":\"F4\"}," +
                "{\"action\":\"PerformanceMode\",\"modifiers\":\"Shift\",\"key\":\"5\"}," +
                "{\"action\":\"AutomaticMode\",\"modifiers\":\"\",\"key\":\"5\"}," +
                "{\"action\":\"FullSpeed\",\"modifiers\":\"Control+Alt\",\"key\":\"Journal\"}]}");

            var settings = new HotkeySettingsStore(path).LoadOrDefault(out var warning);

            Assert.NotNull(warning);
            Assert.Equal("Ctrl+Alt+7", settings.GestureFor(HotkeyAction.SilenceMode).Describe());
            // Reserved, Shift-only, unmodified and unsupported keys all fall back per action.
            Assert.Equal("Ctrl+Alt+2", settings.GestureFor(HotkeyAction.BalanceMode).Describe());
            Assert.Equal("Ctrl+Alt+3", settings.GestureFor(HotkeyAction.PerformanceMode).Describe());
            Assert.Equal("Ctrl+Alt+A", settings.GestureFor(HotkeyAction.AutomaticMode).Describe());
            Assert.Equal("Ctrl+Alt+F", settings.GestureFor(HotkeyAction.FullSpeed).Describe());
            settings.Validate();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        return directory;
    }
}
