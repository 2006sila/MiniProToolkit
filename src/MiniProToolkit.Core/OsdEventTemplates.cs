using System.Text.Json;

namespace MiniProToolkit.Core;

/// <summary>Plain display data only. Null title/icon/duration follows the event factory.</summary>
public sealed record OsdEventTemplate(
    string? Title = null,
    bool ShowOriginalBody = true,
    string? AdditionalText = null,
    OsdIcon? Icon = null,
    double? DurationSeconds = null);

public sealed record OsdEventDefinition(string Key, string DisplayName, bool IsSafetyEvent)
{
    public override string ToString() => DisplayName;
}

/// <summary>Fixed event-key allowlist; never resolves names, resources, URIs or executable content.</summary>
public static class OsdEventTemplates
{
    public const int MaxTitleLength = 80;
    public const int MaxAdditionalTextLength = 120;
    public static IReadOnlyList<OsdEventDefinition> Events { get; } = Array.AsReadOnly(new[]
    {
        new OsdEventDefinition("osd-curve", "启用安全曲线", true),
        new OsdEventDefinition("osd-curve-stop", "停止安全曲线", true),
        new OsdEventDefinition("osd-curve-reject", "安全曲线拒绝启用", true),
        new OsdEventDefinition("osd-curve-fail", "安全曲线停止失败", true),
        new OsdEventDefinition("osd-guardian-ok", "Guardian 恢复", true),
        new OsdEventDefinition("osd-guardian-fail", "Guardian 恢复失败", true),
        new OsdEventDefinition("osd-emergency", "90°C 紧急满速", true),
        new OsdEventDefinition("osd-curve-emergency", "曲线 90°C 紧急满速", true),
        new OsdEventDefinition("osd-fan-fault", "风扇保护故障", true),
        new OsdEventDefinition("osd-fixed-fault", "固定 RPM 故障", true),
        new OsdEventDefinition("osd-fixed-ok", "固定 RPM 保护解除", true),
        new OsdEventDefinition("osd-command", "操作成功", false),
        new OsdEventDefinition("osd-command-fail", "操作失败", true),
        new OsdEventDefinition("osd-profile-ok", "方案应用成功", false),
        new OsdEventDefinition("osd-profile-fail", "方案应用失败", true),
        new OsdEventDefinition("osd-profile-preview", "方案演示预览", true)
    });

    public static OsdEventDefinition? Find(string? key) => Events.FirstOrDefault(item => item.Key == key);

    /// <summary>Single-line, bounded, redacted user text. Nothing is interpreted.</summary>
    public static string? SafeText(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Bound work before redaction as well as the final result. Remove format controls (bidi etc.).
        var bounded = text[..Math.Min(text.Length, 2048)];
        var plain = new string(bounded.Where(c => !char.IsControl(c) &&
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
        try
        {
            var safe = DiagnosticRedactor.Redact(plain).Trim();
            return safe.Length == 0 ? null : safe[..Math.Min(safe.Length, Math.Max(0, limit))];
        }
        catch (Exception)
        {
            // A display-only template must not fault the event producer or expose unredacted text.
            return null;
        }
    }

    public static OsdEventTemplate Sanitize(string key, OsdEventTemplate template)
    {
        var safety = Find(key)?.IsSafetyEvent == true;
        var seconds = template.DurationSeconds;
        if (seconds is { } value)
            seconds = double.IsFinite(value) && value > 0
                ? Math.Clamp(value, OsdMessageNormalizer.MinDurationSeconds, OsdMessageNormalizer.MaxDurationSeconds)
                : null;
        return new OsdEventTemplate(
            safety ? null : SafeText(template.Title, MaxTitleLength),
            safety || template.ShowOriginalBody,
            SafeText(template.AdditionalText, MaxAdditionalTextLength),
            !safety && template.Icon is { } icon && Enum.IsDefined(icon) ? icon : null,
            seconds);
    }

    public static Dictionary<string, OsdEventTemplate>? SanitizeOverrides(
        IReadOnlyDictionary<string, OsdEventTemplate>? templates)
    {
        if (templates is null) return null;
        var result = new Dictionary<string, OsdEventTemplate>(StringComparer.Ordinal);
        foreach (var entry in templates)
            if (Find(entry.Key) is not null && entry.Value is not null)
            {
                var template = Sanitize(entry.Key, entry.Value);
                if (template != new OsdEventTemplate()) result[entry.Key] = template;
            }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Absent/unknown overrides return the original object, preserving legacy rendering.</summary>
    public static OsdMessage Apply(OsdMessage message, IReadOnlyDictionary<string, OsdEventTemplate>? templates)
    {
        var definition = Find(message.CoalesceKey);
        if (definition is null || templates is null || !templates.TryGetValue(definition.Key, out var raw) || raw is null)
            return message;
        var template = Sanitize(definition.Key, raw);
        // Warning/error severity is an additional fail-safe even for a normally informational key.
        var safety = definition.IsSafetyEvent || message.Severity is OsdSeverity.Warning or OsdSeverity.Error;
        var body = safety || template.ShowOriginalBody ? message.Body : string.Empty;
        if (template.AdditionalText is { } extra)
            body += (body.Length == 0 ? string.Empty : "\n") + "自定义说明：" + extra;
        // A template cannot remove the event altogether.
        if (string.IsNullOrWhiteSpace(body)) body = message.Body;
        var seconds = template.DurationSeconds;
        if (safety && seconds is { } duration)
            seconds = Math.Max(duration, message.Duration?.TotalSeconds ?? OsdMessageNormalizer.DefaultDurationSeconds);
        return message with
        {
            Title = safety ? message.Title : template.Title ?? message.Title,
            Body = body,
            Icon = safety ? message.Icon : template.Icon ?? message.Icon,
            Duration = seconds is { } value ? TimeSpan.FromSeconds(value) : message.Duration
        };
    }

    internal static Dictionary<string, OsdEventTemplate>? Load(JsonElement? element, List<string> notes)
    {
        if (element is null || element.Value.ValueKind == JsonValueKind.Null) return null;
        if (element.Value.ValueKind != JsonValueKind.Object)
        {
            notes.Add("OSD 事件模板必须是事件 key 映射，已忽略");
            return null;
        }
        var result = new Dictionary<string, OsdEventTemplate>(StringComparer.Ordinal);
        foreach (var property in element.Value.EnumerateObject())
        {
            if (Find(property.Name) is null || property.Value.ValueKind != JsonValueKind.Object)
            {
                notes.Add("未知或无效的 OSD 事件模板已忽略");
                continue;
            }
            try
            {
                var template = property.Value.Deserialize<OsdEventTemplate>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (template is null) continue;
                var clean = Sanitize(property.Name, template);
                if (clean != template) notes.Add("OSD 事件模板已规范化（文本、安全字段或显示边界）");
                result[property.Name] = clean;
            }
            catch (JsonException)
            {
                notes.Add("无效的 OSD 事件模板已忽略");
            }
        }
        return SanitizeOverrides(result);
    }
}
