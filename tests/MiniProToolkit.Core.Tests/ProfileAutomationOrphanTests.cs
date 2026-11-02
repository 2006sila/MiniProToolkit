using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Loading behavior when a stored rule references a profile that no longer exists. One deleted profile
/// must not discard the other legal rules, but it must also never let the next matching rule take over:
/// the surviving rules come back with automation forced off until the user reviews them. Every other
/// kind of invalid configuration keeps rejecting the whole file.
/// </summary>
public sealed class ProfileAutomationOrphanTests
{
    private static readonly string[] Profiles = ["Performance", "Silent"];
    private static ProfileAutomationContext AcContext => new(true, 10, DateTimeOffset.Now);

    private static string CreateStore(out ProfileAutomationStore store)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitAutomationOrphanTests", Guid.NewGuid().ToString("N"));
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
    public void DeletedProfileDropsOnlyThatRuleAndKeepsAutomationOffWithoutTouchingTheFile()
    {
        const string json = """
        {
          "version": 1,
          "enabled": true,
          "paused": true,
          "rules": [
            {
              "name": "orphan-a",
              "enabled": true,
              "priority": 50,
              "profileName": "Deleted",
              "trigger": "ProcessStarted",
              "processName": "app",
              "processPath": "C:\\Secret\\app.exe",
              "processCommandLine": "tok-9f3c-SECRET",
              "debounceSeconds": 5
            },
            {
              "name": "keep-b",
              "enabled": true,
              "priority": 1,
              "profileName": "Performance",
              "trigger": "AcPower",
              "debounceSeconds": 15
            }
          ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            File.WriteAllText(store.Path, json);
            var bytes = File.ReadAllBytes(store.Path);

            var loaded = store.LoadOrDefault(Profiles, out var warning);

            // The legal rule survives untouched, but nothing may be selected while the file still
            // references a deleted profile.
            var kept = Assert.Single(loaded.Rules);
            Assert.Equal("keep-b", kept.Name);
            Assert.Equal("Performance", kept.ProfileName);
            Assert.Equal(1, kept.Priority);
            Assert.Equal(ProfileAutomationTrigger.AcPower, kept.Trigger);
            Assert.Equal(15, kept.DebounceSeconds);
            Assert.True(kept.Enabled);
            Assert.False(loaded.Enabled);
            Assert.True(loaded.Paused);
            Assert.Null(ProfileAutomationEngine.Select(loaded, AcContext, DateTimeOffset.Now, currentProfile: null));

            Assert.NotNull(warning);
            Assert.Contains("orphan-a", warning);
            Assert.Contains("1 条引用不存在方案", warning);
            Assert.Contains("自动切换已关闭", warning);
            // Paths, command lines and process names never reach the warning.
            Assert.DoesNotContain("Secret", warning);
            Assert.DoesNotContain("SECRET", warning);
            Assert.DoesNotContain("app.exe", warning);

            // Loading never rewrites the stored file.
            Assert.Equal(bytes, File.ReadAllBytes(store.Path));

            // Saving is still validated against the known profiles, and a settings object that no
            // longer carries the orphan can be saved explicitly by the user later.
            var saved = new ProfileAutomationSettings(true, false, loaded.Rules);
            store.Save(saved, Profiles);
            var reloaded = store.LoadOrDefault(Profiles, out var reloadWarning);
            Assert.Null(reloadWarning);
            Assert.True(reloaded.Enabled);
            Assert.Equal("keep-b", Assert.Single(reloaded.Rules).Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OnlyOrphanRulesLoadAsEmptyDisabledSettingsWithWarning()
    {
        const string json = """
        {
          "version": 1,
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "orphan-only",
              "enabled": true,
              "priority": 3,
              "profileName": "Deleted",
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
            Assert.Contains("orphan-only", warning);
            Assert.False(loaded.Enabled);
            Assert.False(loaded.Paused);
            Assert.Empty(loaded.Rules);
            Assert.Null(ProfileAutomationEngine.Select(loaded, AcContext, DateTimeOffset.Now, currentProfile: null));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnknownTriggerAndOrphanProfileWarningsAreMerged()
    {
        const string json = """
        {
          "version": 1,
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "future-rule",
              "enabled": true,
              "priority": 9,
              "profileName": "Performance",
              "trigger": "WindowFocused"
            },
            {
              "name": "orphan-a",
              "enabled": true,
              "priority": 5,
              "profileName": "Deleted",
              "trigger": "BatteryPower"
            },
            {
              "name": "keep-b",
              "enabled": true,
              "priority": 1,
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

            Assert.Equal("keep-b", Assert.Single(loaded.Rules).Name);
            Assert.False(loaded.Enabled);
            Assert.NotNull(warning);
            Assert.Contains("future-rule", warning);
            Assert.Contains("orphan-a", warning);
            Assert.Contains("1 条触发条件不受支持", warning);
            Assert.Contains("1 条引用不存在方案", warning);
            Assert.Contains("自动切换已关闭", warning);
            Assert.DoesNotContain("WindowFocused", warning);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("priority")]
    [InlineData("duplicate")]
    [InlineData("badcondition")]
    public void OrphanRuleDoesNotWeakenAnyOtherValidation(string problem)
    {
        var orphan = """
            {
              "name": "orphan-a",
              "enabled": true,
              "priority": 5,
              "profileName": "Deleted",
              "trigger": "AcPower"
            }
        """;
        var broken = problem switch
        {
            "priority" => """
                {
                  "name": "keep-b",
                  "enabled": true,
                  "priority": 99999,
                  "profileName": "Performance",
                  "trigger": "AcPower"
                }
                """,
            "duplicate" => """
                {
                  "name": "orphan-a",
                  "enabled": true,
                  "priority": 1,
                  "profileName": "Performance",
                  "trigger": "AcPower"
                }
                """,
            _ => """
                {
                  "name": "keep-b",
                  "enabled": true,
                  "priority": 1,
                  "profileName": "Performance",
                  "trigger": "ProcessStarted",
                  "processName": "app",
                  "processCommandLineRegex": "[unclosed"
                }
                """
        };
        var json = $$"""
        {
          "version": 1,
          "enabled": true,
          "paused": false,
          "rules": [ {{orphan}}, {{broken}} ]
        }
        """;
        var directory = CreateStore(out var store);
        try
        {
            var loaded = Load(store, json, out var warning);

            Assert.NotNull(warning);
            Assert.Contains("回退默认", warning);
            Assert.False(loaded.Enabled);
            Assert.Empty(loaded.Rules);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ValidConfigurationKeepsEveryValueUnchanged()
    {
        const string json = """
        {
          "version": 1,
          "enabled": true,
          "paused": true,
          "rules": [
            {
              "name": "ac",
              "enabled": false,
              "priority": 7,
              "profileName": "performance",
              "trigger": "AcPower",
              "debounceSeconds": 12
            },
            {
              "name": "silent",
              "enabled": true,
              "priority": 3,
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

            Assert.Null(warning);
            Assert.True(loaded.Enabled);
            Assert.True(loaded.Paused);
            Assert.Equal(["ac", "silent"], loaded.Rules.Select(rule => rule.Name));
            Assert.Equal(["performance", "Silent"], loaded.Rules.Select(rule => rule.ProfileName));
            Assert.Equal([false, true], loaded.Rules.Select(rule => rule.Enabled));
            Assert.Equal([7, 3], loaded.Rules.Select(rule => rule.Priority));
            Assert.Equal([12, 30], loaded.Rules.Select(rule => rule.DebounceSeconds));
            Assert.Equal(90, loaded.Rules[1].Threshold);
            Assert.Null(ProfileAutomationEngine.Select(loaded, AcContext, DateTimeOffset.Now, currentProfile: null));
        }
        finally { Directory.Delete(directory, true); }
    }
}
