using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// File-compatibility boundaries of <see cref="ProfileAutomationStore"/>: a missing version must stay
/// readable, a future version must still fall back, and one rule with an unknown trigger must not
/// discard the other rules or leak stored paths, command lines or patterns into the warning.
/// </summary>
public sealed class ProfileAutomationStoreCompatibilityTests
{
    private const string Secret = "tok-9f3c-SECRET";
    private static readonly string[] Profiles = ["Performance", "Silent"];

    private static string CreateStore(out ProfileAutomationStore store)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitAutomationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        store = new ProfileAutomationStore(Path.Combine(directory, "automation.json"));
        return directory;
    }

    private static ProfileAutomationSettings Load(ProfileAutomationStore store, string json, out string? warning)
    {
        File.WriteAllText(store.Path, json);
        return store.LoadOrDefault(Profiles, out warning);
    }

    [Fact]
    public void MissingVersionIsReadAsCurrentShapeAndKeepsRules()
    {
        const string json = """
        {
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "ac",
              "enabled": true,
              "priority": 3,
              "profileName": "Performance",
              "trigger": "AcPower"
            }
          ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            var loaded = Load(store, json, out var warning);

            Assert.Null(warning);
            Assert.True(loaded.Enabled);
            Assert.Equal("ac", Assert.Single(loaded.Rules).Name);
            Assert.IsType<ProfileAutomationRule>(loaded.Rules[0]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LegacyVersionOneFileStaysReadable()
    {
        const string json = """
        {
          "version": 1,
          "enabled": true,
          "paused": true,
          "rules": [
            {
              "name": "legacy",
              "enabled": true,
              "priority": 5,
              "profileName": "Silent",
              "trigger": "ProcessStarted",
              "processName": "notepad.exe"
            }
          ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            var loaded = Load(store, json, out var warning);

            Assert.Null(warning);
            Assert.True(loaded.Paused);
            Assert.Equal("notepad.exe", Assert.Single(loaded.Rules).ProcessName);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("0")]
    public void UnsupportedVersionsStillFallBackToDefault(string version)
    {
        var json = $$"""
        {
          "version": {{version}},
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "ac",
              "enabled": true,
              "priority": 1,
              "profileName": "Performance",
              "trigger": "AcPower"
            }
          ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            var loaded = Load(store, json, out var warning);

            Assert.NotNull(warning);
            Assert.False(loaded.Enabled);
            Assert.Empty(loaded.Rules);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnknownTriggerDropsOnlyThatRuleAndReportsSafeWarning()
    {
        const string json = """
        {
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "future",
              "enabled": true,
              "priority": 9,
              "profileName": "Performance",
              "trigger": "WindowFocused",
              "processPath": "C:\\Secret\\app.exe",
              "processCommandLine": "tok-9f3c-SECRET"
            },
            {
              "name": "ac",
              "enabled": true,
              "priority": 3,
              "profileName": "Performance",
              "trigger": "AcPower"
            },
            {
              "name": "silent",
              "enabled": true,
              "priority": 2,
              "profileName": "Silent",
              "trigger": "CpuLoadAbove",
              "threshold": 90
            }
          ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            var loaded = Load(store, json, out var warning);

            Assert.NotNull(warning);
            Assert.Equal(["ac", "silent"], loaded.Rules.Select(rule => rule.Name));
            Assert.DoesNotContain(Secret, warning);
            Assert.DoesNotContain("Secret", warning);
            Assert.DoesNotContain("WindowFocused", warning);
            Assert.Contains("future", warning);
            Assert.True(loaded.Enabled);

            var selected = ProfileAutomationEngine.Select(
                loaded,
                new ProfileAutomationContext(true, 95, DateTimeOffset.Now),
                DateTimeOffset.Now,
                currentProfile: null);
            Assert.Equal("ac", selected?.Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnknownTriggerDropsNumericTriggerToo()
    {
        const string json = """
        {
          "version": 1,
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "numeric",
              "enabled": true,
              "priority": 1,
              "profileName": "Performance",
              "trigger": 99
            },
            {
              "name": "ac",
              "enabled": true,
              "priority": 1,
              "profileName": "Performance",
              "trigger": "AcPower"
            }
          ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            var loaded = Load(store, json, out var warning);

            Assert.NotNull(warning);
            Assert.Equal("ac", Assert.Single(loaded.Rules).Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SkippingAnUnknownTriggerDoesNotWeakenValidationOfOtherRules()
    {
        // The dropped rule also carries an unusable pattern; it must never reach validation and its
        // pattern text must never appear in the warning. Validation itself stays strict.
        const string json = """
        {
          "version": 1,
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "future",
              "enabled": true,
              "priority": 1,
              "profileName": "Performance",
              "trigger": "WindowFocused",
              "processName": "app",
              "processCommandLineRegex": "[unclosed"
            },
            {
              "name": "ac",
              "enabled": true,
              "priority": 1,
              "profileName": "Performance",
              "trigger": "AcPower"
            }
          ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            var loaded = Load(store, json, out var warning);
            Assert.NotNull(warning);
            Assert.Equal("ac", Assert.Single(loaded.Rules).Name);
            Assert.DoesNotContain("[unclosed", warning);

            // A dangerous rule that is still evaluated keeps rejecting the whole file.
            const string dangerous = """
            {
              "version": 1,
              "enabled": true,
              "paused": false,
              "rules": [
                {
                  "name": "bad",
                  "enabled": true,
                  "priority": 1,
                  "profileName": "Performance",
                  "trigger": "ProcessStarted",
                  "processName": "app",
                  "processCommandLineRegex": "[unclosed"
                }
              ]
            }
            """;
            var fallback = Load(store, dangerous, out var fallbackWarning);
            Assert.NotNull(fallbackWarning);
            Assert.False(fallback.Enabled);
            Assert.Empty(fallback.Rules);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AllRulesWithUnknownTriggersLoadAsEmptyEnabledSettingsWithWarning()
    {
        const string json = """
        {
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "future",
              "enabled": true,
              "priority": 1,
              "profileName": "Performance",
              "trigger": "WindowFocused"
            }
          ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            var loaded = Load(store, json, out var warning);

            Assert.NotNull(warning);
            Assert.True(loaded.Enabled);
            Assert.Empty(loaded.Rules);
            Assert.Null(ProfileAutomationEngine.Select(
                loaded,
                new ProfileAutomationContext(true, 10, DateTimeOffset.Now),
                DateTimeOffset.Now,
                currentProfile: null));
        }
        finally { Directory.Delete(directory, true); }
    }
}
