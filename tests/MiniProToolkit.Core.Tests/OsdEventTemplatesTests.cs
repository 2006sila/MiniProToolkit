using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class OsdEventTemplatesTests
{
    private static OsdMessage[] FactoryMessages() =>
    [
        OsdEvents.CurveEnabled(default), OsdEvents.CurveStopped(default),
        OsdEvents.CurveRejected("拒绝原因", default), OsdEvents.CurveStopFailed("失败原因"),
        OsdEvents.GuardianRecovered(default), OsdEvents.GuardianRecoveryFailed("恢复失败", default),
        OsdEvents.EmergencyFullSpeed(95, default), OsdEvents.CurveEmergencyFullSpeed(95, 100, default),
        OsdEvents.FanProtection("保护原因", default), OsdEvents.FixedRpmFault("闭环故障", default),
        OsdEvents.FixedRpmRecovered(default), OsdEvents.CommandSucceeded("已完成", default),
        OsdEvents.CommandFailed("请求", "错误"), OsdEvents.ProfileApplied("均衡", "摘要", default),
        OsdEvents.ProfileFailed("应用失败", default), OsdEvents.ProfilePreview("均衡", "摘要")
    ];

    private static string NewPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkit.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "osd.json");
    }

    [Fact]
    public void CatalogExactlyMatchesExistingFactoryKeys()
    {
        Assert.Equal(FactoryMessages().Select(m => m.CoalesceKey).Order(), OsdEventTemplates.Events.Select(e => e.Key).Order());
        Assert.Equal(16, OsdEventTemplates.Events.Select(e => e.Key).Distinct().Count());
        Assert.Null(OsdEventTemplates.Find("OSD-command"));
        Assert.Null(OsdEventTemplates.Find("osd-command "));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"enabled\":false}")]
    [InlineData("{\"version\":2,\"theme\":\"Dark\"}")]
    [InlineData("{\"version\":3,\"eventTemplates\":null}")]
    public void LegacyAndMissingFieldsHaveNoOverrides(string json)
    {
        var path = NewPath();
        File.WriteAllText(path, json);
        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);
        Assert.Null(preferences.EventTemplates);
        Assert.Null(warning);
        Assert.Null(new OsdPreferences(true).EventTemplates);
    }

    [Fact]
    public void MissingTemplateFieldsUseCompatibleDefaultsAndSafeOverridesPersist()
    {
        var path = NewPath();
        File.WriteAllText(path, """
            {"version":3,"eventTemplates":{"osd-command":{},"osd-profile-ok":{"title":"方案标题"},
              "osd-emergency":{"title":"一切成功","showOriginalBody":false,"icon":2,
                "additionalText":"检查保护状态","durationSeconds":1}}}
            """);
        var store = new OsdPreferencesStore(path);
        var preferences = store.LoadOrDefault(out var warning);
        Assert.NotNull(warning);
        Assert.False(preferences.EventTemplates!.ContainsKey("osd-command"));
        Assert.True(preferences.EventTemplates["osd-profile-ok"].ShowOriginalBody);
        var safety = preferences.EventTemplates["osd-emergency"];
        Assert.Null(safety.Title);
        Assert.Null(safety.Icon);
        Assert.True(safety.ShowOriginalBody);
        store.Save(preferences);
        var reloaded = store.LoadOrDefault(out warning);
        Assert.Null(warning);
        Assert.Equal(safety, reloaded.EventTemplates!["osd-emergency"]);
        var source = OsdEvents.EmergencyFullSpeed(95, default);
        var applied = OsdEventTemplates.Apply(source, reloaded.EventTemplates);
        Assert.Contains("90°C", applied.Body);
        Assert.StartsWith(source.Body, applied.Body);
        Assert.Equal(source.Title, applied.Title);
        Assert.Equal(source.Duration, applied.Duration);
    }

    [Fact]
    public void NoOverrideReturnsExactOriginalForAllFactoriesAndLegacyText()
    {
        foreach (var message in FactoryMessages().Append(OsdMessage.FromText("旧提示")))
        {
            Assert.Same(message, OsdEventTemplates.Apply(message, null));
            Assert.Same(message, OsdEventTemplates.Apply(message, new Dictionary<string, OsdEventTemplate>()));
        }
    }

    [Fact]
    public void FullTemplateRoundTripsAndCanBeResetWithoutLosingOtherPreferences()
    {
        var store = new OsdPreferencesStore(NewPath());
        var template = new OsdEventTemplate("自定义标题", false, "静态说明", OsdIcon.Info, 9);
        var appearance = new OsdAppearance(FontFamily: "Consolas");
        store.Save(new OsdPreferences(false, OsdThemePreset.Dark, appearance,
            new Dictionary<string, OsdEventTemplate> { ["osd-command"] = template }));
        var preferences = store.LoadOrDefault(out var warning);
        Assert.Null(warning);
        Assert.False(preferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Dark, preferences.Theme);
        Assert.Equal(appearance, preferences.Appearance);
        Assert.Equal(template, preferences.EventTemplates!["osd-command"]);
        var source = OsdEvents.CommandSucceeded("原内容", default);
        var applied = OsdEventTemplates.Apply(source, preferences.EventTemplates);
        Assert.Equal("自定义标题", applied.Title);
        Assert.Equal("自定义说明：静态说明", applied.Body);
        Assert.Equal(OsdIcon.Info, applied.Icon);
        Assert.Equal(TimeSpan.FromSeconds(9), applied.Duration);
        Assert.Equal(source.CoalesceKey, applied.CoalesceKey);
        Assert.Equal(source.Severity, applied.Severity);
        store.Save(preferences with { EventTemplates = null });
        Assert.Null(store.LoadOrDefault(out warning).EventTemplates);
        Assert.Null(warning);
    }

    [Fact]
    public void InvalidEntriesAreIsolatedAndKeysAreAllowlistedOnLoadAndSave()
    {
        var path = NewPath();
        File.WriteAllText(path, """
            {"version":3,"enabled":false,"theme":"Light","eventTemplates":{
              "not-an-event":{"title":"禁止"}, "OSD-command":{"title":"禁止"},
              "osd-command":{"title":"有效","icon":999,"durationSeconds":99},
              "osd-profile-ok":{"showOriginalBody":"invalid"}, "osd-fan-fault":false}}
            """);
        var store = new OsdPreferencesStore(path);
        var preferences = store.LoadOrDefault(out var warning);
        Assert.NotNull(warning);
        Assert.False(preferences.IsEnabled);
        Assert.Equal(OsdThemePreset.Light, preferences.Theme);
        Assert.Single(preferences.EventTemplates!);
        Assert.Null(preferences.EventTemplates!["osd-command"].Icon);
        Assert.Equal(30, preferences.EventTemplates["osd-command"].DurationSeconds);
        store.Save(preferences with { EventTemplates = new Dictionary<string, OsdEventTemplate>
        {
            ["evil"] = new("bad"), ["osd-command"] = new("valid")
        }});
        Assert.Single(store.LoadOrDefault(out warning).EventTemplates!);
        Assert.Null(warning);
        var unknown = new OsdMessage("正文", CoalesceKey: "evil");
        Assert.Same(unknown, OsdEventTemplates.Apply(unknown,
            new Dictionary<string, OsdEventTemplate> { ["evil"] = new("bad") }));
    }

    [Fact]
    public void AllSafetyEventsPreserveOriginalBodyTitleIconSeverityAndMinimumDurationEvenAfterNormalization()
    {
        foreach (var source in FactoryMessages().Where(m => OsdEventTemplates.Find(m.CoalesceKey)!.IsSafetyEvent))
        {
            var applied = OsdEventTemplates.Apply(source, new Dictionary<string, OsdEventTemplate>
            {
                [source.CoalesceKey!] = new("一切成功", false, "测试说明", OsdIcon.Success, 1)
            });
            Assert.Equal(source.Title, applied.Title);
            Assert.Equal(source.Icon, applied.Icon);
            Assert.Equal(source.Severity, applied.Severity);
            Assert.Equal(source.CoalesceKey, applied.CoalesceKey);
            Assert.Equal(source.Duration, applied.Duration);
            Assert.StartsWith(source.Body + "\n自定义说明：", applied.Body);
            Assert.True(OsdMessageNormalizer.TryNormalize(applied, out var normalized));
            Assert.Contains(source.Body, normalized.Body);
        }
    }

    [Theory]
    [InlineData(OsdSeverity.Warning)]
    [InlineData(OsdSeverity.Error)]
    public void SeverityAlsoLocksNormallyNonSafetyKey(OsdSeverity severity)
    {
        var source = new OsdMessage("不能丢失的失败正文", "错误", severity, TimeSpan.FromSeconds(6), "osd-command") { Icon = OsdIcon.Error };
        var applied = OsdEventTemplates.Apply(source, new Dictionary<string, OsdEventTemplate>
        { ["osd-command"] = new("成功", false, null, OsdIcon.Success, 1) });
        Assert.Equal(source, applied);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    [InlineData(0.2, 1d)]
    [InlineData(100, 30d)]
    public void DurationAndIconAreBounded(double input, double? expected)
    {
        var clean = OsdEventTemplates.Sanitize("osd-command", new(Icon: (OsdIcon)999, DurationSeconds: input));
        Assert.Equal(expected, clean.DurationSeconds);
        Assert.Null(clean.Icon);
        foreach (var icon in Enum.GetValues<OsdIcon>())
            Assert.Equal(icon, OsdEventTemplates.Sanitize("osd-command", new(Icon: icon)).Icon);
    }

    [Fact]
    public void UserTextIsBoundedControlFreeAndRedactedAndNeverInterpreted()
    {
        var clean = OsdEventTemplates.Sanitize("osd-command", new(
            "\u0000\u202E标题 password=super-secret", false,
            "C:\\Users\\private\\secret.txt\n\t", OsdIcon.None, 2));
        Assert.DoesNotContain("super-secret", clean.Title!);
        Assert.DoesNotContain("private", clean.AdditionalText!);
        Assert.DoesNotContain('\u202E', clean.Title!);
        Assert.DoesNotContain('\u0000', clean.Title!);
        Assert.DoesNotContain('\n', clean.AdditionalText!);
        var longText = OsdEventTemplates.Sanitize("osd-command", new(new string('a', 3000), true, new string('b', 3000)));
        Assert.Equal(80, longText.Title!.Length);
        Assert.Equal(120, longText.AdditionalText!.Length);
        var literal = "{sample} $(script) https://invalid.example";
        var source = OsdEvents.CommandSucceeded("原文", default);
        var result = OsdEventTemplates.Apply(source, new Dictionary<string, OsdEventTemplate>
        { ["osd-command"] = new(AdditionalText: literal) });
        Assert.Contains(DiagnosticRedactor.Redact(literal), result.Body);
    }

    [Fact]
    public void TemplateCannotHideEventByMakingBodyEmpty()
    {
        var source = OsdEvents.CommandSucceeded("完成", default);
        var result = OsdEventTemplates.Apply(source, new Dictionary<string, OsdEventTemplate>
        { ["osd-command"] = new(ShowOriginalBody: false) });
        Assert.Equal(source.Body, result.Body);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"wrong\"")]
    public void InvalidTemplateContainerKeepsOtherPreferences(string container)
    {
        var path = NewPath();
        File.WriteAllText(path, "{\"enabled\":false,\"eventTemplates\":" + container + "}");
        var preferences = new OsdPreferencesStore(path).LoadOrDefault(out var warning);
        Assert.False(preferences.IsEnabled);
        Assert.Null(preferences.EventTemplates);
        Assert.NotNull(warning);
    }
}
