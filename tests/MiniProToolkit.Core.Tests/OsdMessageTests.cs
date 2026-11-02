using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class OsdMessageTests
{
    [Fact]
    public void LegacyTextKeepsWholeTextAndDefaults()
    {
        var ok = OsdMessageNormalizer.TryNormalize(OsdMessage.FromText("MiniProToolkit：已切换静音模式"), out var message);

        Assert.True(ok);
        Assert.Equal("MiniProToolkit：已切换静音模式", message.Body);
        Assert.Null(message.Title);
        Assert.Equal(OsdSeverity.Info, message.Severity);
        Assert.Equal(OsdIcon.None, message.Icon);
        Assert.Equal(TimeSpan.FromSeconds(2), message.Duration!.Value);
    }

    [Fact]
    public void StructuredMessageKeepsTitleBodySeverityAndDuration()
    {
        var ok = OsdMessageNormalizer.TryNormalize(
            new OsdMessage("目标 2800 RPM\n实际 2730 RPM", "固定 RPM", OsdSeverity.Warning, TimeSpan.FromSeconds(5)),
            out var message);

        Assert.True(ok);
        Assert.Equal("固定 RPM", message.Title);
        Assert.Equal("目标 2800 RPM\n实际 2730 RPM", message.Body);
        Assert.Equal(OsdSeverity.Warning, message.Severity);
        Assert.Equal(TimeSpan.FromSeconds(5), message.Duration!.Value);
    }

    [Fact]
    public void ExistingPositionalAndJsonMessagesRemainCompatible()
    {
        var positional = new OsdMessage("legacy body", "legacy title", OsdSeverity.Warning,
            TimeSpan.FromSeconds(5), "legacy-key");

        Assert.Equal(OsdIcon.None, positional.Icon);
        var (body, title, severity, duration, coalesceKey) = positional;
        Assert.Equal(("legacy body", "legacy title", OsdSeverity.Warning, TimeSpan.FromSeconds(5), "legacy-key"),
            (body, title, severity, duration, coalesceKey));
        Assert.True(OsdMessageNormalizer.TryNormalize(positional, out var normalized));
        Assert.Equal("legacy-key", normalized.CoalesceKey);
        Assert.Equal(OsdIcon.None, normalized.Icon);

        var oldJson = "{\"Body\":\"legacy body\",\"Title\":\"legacy title\",\"Severity\":2,\"Duration\":\"00:00:05\",\"CoalesceKey\":\"legacy-key\"}";
        var deserialized = JsonSerializer.Deserialize<OsdMessage>(oldJson);

        Assert.NotNull(deserialized);
        Assert.Equal("legacy body", deserialized!.Body);
        Assert.Equal(OsdSeverity.Warning, deserialized.Severity);
        Assert.Equal(OsdIcon.None, deserialized.Icon);
    }

    [Fact]
    public void UndefinedIconFallsBackToNone()
    {
        var ok = OsdMessageNormalizer.TryNormalize(
            new OsdMessage("温度 62 °C") { Icon = (OsdIcon)99 }, out var message);

        Assert.True(ok);
        Assert.Equal(OsdIcon.None, message.Icon);
    }

    [Fact]
    public void BodyLinesAreTrimmedDroppedAndCapped()
    {
        var body = string.Join("\r\n", Enumerable.Range(1, 10).Select(index => $"  第{index}行  ")) + "\r\n\r\n   ";
        var ok = OsdMessageNormalizer.TryNormalize(new OsdMessage(body), out var message);

        Assert.True(ok);
        var lines = message.Body.Split('\n');
        Assert.Equal(OsdMessageNormalizer.MaxBodyLines, lines.Length);
        Assert.Equal("第1行", lines[0]);
        Assert.Equal("第6行", lines[^1]);
        Assert.DoesNotContain('\r', message.Body);
    }

    [Fact]
    public void BodyIsTruncatedToTheSafetyLimit()
    {
        var ok = OsdMessageNormalizer.TryNormalize(new OsdMessage(new string('x', 5000)), out var message);

        Assert.True(ok);
        Assert.Equal(OsdMessageNormalizer.MaxBodyLength, message.Body.Length);
        Assert.EndsWith("…", message.Body);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(0.0, 2)]
    [InlineData(-3.0, 2)]
    [InlineData(0.2, 1)]
    [InlineData(7.5, 7.5)]
    [InlineData(120.0, 30)]
    public void DurationIsClampedToTheSafetyWindow(double? seconds, double expected)
    {
        TimeSpan? duration = seconds is { } value ? TimeSpan.FromSeconds(value) : null;
        var ok = OsdMessageNormalizer.TryNormalize(new OsdMessage("温度 62 °C", Duration: duration), out var message);

        Assert.True(ok);
        Assert.Equal(expected, message.Duration!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void EmptyOrMissingBodyIsRejected()
    {
        Assert.False(OsdMessageNormalizer.TryNormalize(null, out _));
        Assert.False(OsdMessageNormalizer.TryNormalize(OsdMessage.FromText("   "), out _));
        Assert.False(OsdMessageNormalizer.TryNormalize(new OsdMessage("", "只有标题"), out _));
        Assert.False(OsdMessageNormalizer.TryNormalize(OsdMessage.FromText("\r\n\r\n"), out _));
    }

    [Fact]
    public void UndefinedSeverityFallsBackToInfo()
    {
        var ok = OsdMessageNormalizer.TryNormalize(new OsdMessage("温度 62 °C", Severity: (OsdSeverity)99), out var message);

        Assert.True(ok);
        Assert.Equal(OsdSeverity.Info, message.Severity);
    }

    [Fact]
    public void CoalesceKeyUsesTitleOrLegacyPrefix()
    {
        OsdMessageNormalizer.TryNormalize(OsdMessage.FromText("微型主机：温度 88 °C"), out var legacy);
        OsdMessageNormalizer.TryNormalize(OsdMessage.FromText("微型主机：风扇 3200 RPM"), out var legacySibling);
        OsdMessageNormalizer.TryNormalize(new OsdMessage("目标 2800 RPM\n实际 2730 RPM", "固定 RPM"), out var structured);
        OsdMessageNormalizer.TryNormalize(new OsdMessage("任意正文", "固定 RPM", CoalesceKey: "rpm-fixed"), out var explicitKey);

        Assert.Equal("微型主机", legacy.CoalesceKey);
        Assert.Equal(legacy.CoalesceKey, legacySibling.CoalesceKey);
        Assert.Equal("固定 RPM", structured.CoalesceKey);
        Assert.Equal("rpm-fixed", explicitKey.CoalesceKey);
    }

    [Fact]
    public void QueueCoalescesTheLastWaitingMessageWithTheSameKey()
    {
        var queue = new OsdMessageQueue();
        OsdMessageNormalizer.TryNormalize(OsdMessage.FromText("风扇：目标 2800 RPM"), out var first);
        OsdMessageNormalizer.TryNormalize(OsdMessage.FromText("风扇：目标 2730 RPM"), out var second);

        Assert.Equal(OsdEnqueueResult.Queued, queue.Enqueue(first));
        Assert.Equal(OsdEnqueueResult.Coalesced, queue.Enqueue(second));

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out var dequeued));
        Assert.Equal(second.Body, dequeued.Body);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void QueueKeepsDistinctMessagesInOrderAndTrimsFromTheFront()
    {
        var queue = new OsdMessageQueue(capacity: 50);
        for (var index = 0; index < 60; index++)
            queue.Enqueue(new OsdMessage($"消息 {index}", CoalesceKey: $"key-{index}"));

        Assert.Equal(50, queue.Count);
        Assert.Equal(10, queue.DroppedCount);
        Assert.True(queue.TryDequeue(out var oldest));
        Assert.Equal("消息 10", oldest.Body);
    }
}
