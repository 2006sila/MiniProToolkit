using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class FanProfileTests
{
    [Fact]
    public void VersionOneMigrationKeepsAutomaticModeAndSaveBacksUpOriginal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var store = new FanProfileStore(Path.Combine(directory, "profiles.json"));
        try
        {
            store.Save([new("旧方案", PowerMode.Silence, FanCurveSettings.Default)]);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!;
            json["version"] = 1;
            var p = json["profiles"]![0]!.AsObject();
            p.Remove("fanMode"); p.Remove("fixedRpm");
            File.WriteAllText(store.Path, json.ToJsonString());
            var old = File.ReadAllText(store.Path);
            var migrated = Assert.Single(store.Load());
            Assert.Equal(ProfileFanMode.Automatic, migrated.FanMode);
            store.Save([migrated with { FanMode = ProfileFanMode.FixedRpm, FixedRpm = 3200 }]);
            Assert.Equal(old, File.ReadAllText(store.Path + ".bak"));
            var loaded = Assert.Single(store.Load());
            Assert.Equal(ProfileFanMode.FixedRpm, loaded.FanMode);
            Assert.Equal(3200, loaded.FixedRpm);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DuplicateNamesAndInvalidModesAreRejected()
    {
        var p = FanProfile.Defaults[0];
        Assert.Throws<ArgumentException>(() => FanProfileStore.ValidateCollection([p, p]));
        Assert.Throws<ArgumentException>(() => (p with { PowerMode = PowerMode.Unknown }).Validate());
        Assert.Throws<ArgumentException>(() => (p with { FanMode = (ProfileFanMode)99 }).Validate());
    }

    [Fact]
    public void SaveAndLoadProfilesPreservesModeAndRampDownPoints()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MiniProToolkitTests", Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(directory, "profiles.json");
        Directory.CreateDirectory(directory);
        var store = new FanProfileStore(path);
        var expected = new FanProfile("测试方案", PowerMode.Performance, new FanCurveSettings([
            new(45, 40, 42),
            new(85, 85, 80),
            new(90, 100, 86)
        ], MaximumDutyIncreasePerTick: 8, MaximumDutyDecreasePerTick: 3));
        try
        {
            store.Save([expected]);
            var actual = store.LoadOrDefault(out var warning);

            Assert.Null(warning);
            var profile = Assert.Single(actual);
            Assert.Equal(expected.Name, profile.Name);
            Assert.Equal(expected.PowerMode, profile.PowerMode);
            Assert.Equal(expected.FanCurve.Points, profile.FanCurve.Points);
            Assert.Equal(8, profile.FanCurve.MaximumDutyIncreasePerTick);
            Assert.Equal(3, profile.FanCurve.MaximumDutyDecreasePerTick);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
