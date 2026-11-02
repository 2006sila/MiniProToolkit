using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class OsdNotificationsTests
{
    private static readonly OsdSample Sample = new(62.4, 2730, 2800, "固定 RPM 闭环");

    [Fact]
    public void SampleRendersMissingValuesAsPlaceholders()
    {
        Assert.Equal("--", OsdSample.Empty.TemperatureText);
        var summary = OsdSample.Empty.Summary;
        Assert.Contains("CPU --", summary);
        Assert.Contains("风扇 --", summary);
        Assert.Contains("目标 --", summary);
        Assert.Contains("控制 --", summary);
        Assert.Equal(2, summary.Split('\n').Length);
    }

    [Fact]
    public void SampleIncludesTemperatureActualAndTargetRpm()
    {
        var summary = Sample.Summary;
        Assert.Contains("CPU 62°C", summary);
        Assert.Contains("风扇 2730 RPM", summary);
        Assert.Contains("目标 2800 RPM", summary);
        Assert.Contains("控制 固定 RPM 闭环", summary);
    }

    [Fact]
    public void EveryEventNormalizesAndKeepsSeverity()
    {
        var messages = new (OsdMessage Message, OsdSeverity ExpectedSeverity, OsdIcon ExpectedIcon)[]
        {
            (OsdEvents.CurveEnabled(Sample), OsdSeverity.Success, OsdIcon.Fan),
            (OsdEvents.CurveStopped(Sample), OsdSeverity.Info, OsdIcon.Fan),
            (OsdEvents.CurveRejected("温度达到 90°C", Sample), OsdSeverity.Warning, OsdIcon.Warning),
            (OsdEvents.CurveStopFailed("EC 写入失败"), OsdSeverity.Error, OsdIcon.Error),
            (OsdEvents.GuardianRecovered(Sample), OsdSeverity.Success, OsdIcon.Shield),
            (OsdEvents.GuardianRecoveryFailed("EC 自动恢复失败", Sample), OsdSeverity.Error, OsdIcon.Shield),
            (OsdEvents.EmergencyFullSpeed(91.2, Sample), OsdSeverity.Error, OsdIcon.Temperature),
            (OsdEvents.CurveEmergencyFullSpeed(91.2, 100, Sample), OsdSeverity.Error, OsdIcon.Temperature),
            (OsdEvents.FanProtection("固定 RPM 反馈异常", Sample), OsdSeverity.Error, OsdIcon.Fan),
            (OsdEvents.FixedRpmFault("闭环已停止", Sample), OsdSeverity.Error, OsdIcon.Fan),
            (OsdEvents.FixedRpmRecovered(Sample), OsdSeverity.Success, OsdIcon.Fan),
            (OsdEvents.CommandSucceeded("静音模式", Sample), OsdSeverity.Success, OsdIcon.Success),
            (OsdEvents.CommandFailed("性能模式", "WMI 调用失败"), OsdSeverity.Error, OsdIcon.Error),
            (OsdEvents.ProfileApplied("均衡", "模式：均衡", Sample), OsdSeverity.Success, OsdIcon.Success),
            (OsdEvents.ProfileFailed("Guardian 未运行", Sample), OsdSeverity.Warning, OsdIcon.Warning),
            (OsdEvents.ProfilePreview("性能", "模式：性能"), OsdSeverity.Info, OsdIcon.Info)
        };

        foreach (var (message, expected, expectedIcon) in messages)
        {
            Assert.True(OsdMessageNormalizer.TryNormalize(message, out var normalized));
            Assert.Equal(expected, normalized.Severity);
            Assert.Equal(expectedIcon, normalized.Icon);
            Assert.False(string.IsNullOrWhiteSpace(normalized.Title));
            Assert.False(string.IsNullOrWhiteSpace(normalized.Body));
            // Nothing may exceed the display bounds, even with a long detail line.
            Assert.True(normalized.Body.Length <= OsdMessageNormalizer.MaxBodyLength);
            Assert.True(normalized.Body.Split('\n').Length <= OsdMessageNormalizer.MaxBodyLines);
        }
    }

    [Fact]
    public void EventsCarryDistinctCoalesceKeys()
    {
        OsdMessageNormalizer.TryNormalize(OsdEvents.CurveEnabled(Sample), out var enabled);
        OsdMessageNormalizer.TryNormalize(OsdEvents.CurveStopped(Sample), out var stopped);
        OsdMessageNormalizer.TryNormalize(OsdEvents.GuardianRecovered(Sample), out var recovered);
        OsdMessageNormalizer.TryNormalize(OsdEvents.FixedRpmFault("故障", Sample), out var fault);
        OsdMessageNormalizer.TryNormalize(OsdEvents.FixedRpmRecovered(Sample), out var fixedOk);
        OsdMessageNormalizer.TryNormalize(OsdEvents.CommandSucceeded("静音模式", Sample), out var command);
        OsdMessageNormalizer.TryNormalize(OsdEvents.EmergencyFullSpeed(91, Sample), out var fixedEmergency);
        OsdMessageNormalizer.TryNormalize(OsdEvents.CurveEmergencyFullSpeed(91, 100, Sample), out var curveEmergency);

        var keys = new[] { enabled, stopped, recovered, fault, fixedOk, command, fixedEmergency, curveEmergency }
            .Select(message => message.CoalesceKey)
            .ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CurveEmergencyNeverReusesTheFixedRpmEmergencyKey()
    {
        OsdMessageNormalizer.TryNormalize(OsdEvents.EmergencyFullSpeed(91, Sample), out var fixedEmergency);
        OsdMessageNormalizer.TryNormalize(OsdEvents.CurveEmergencyFullSpeed(91, 100, Sample), out var curveEmergency);

        Assert.NotEqual(fixedEmergency.CoalesceKey, curveEmergency.CoalesceKey);
        Assert.Equal(OsdEvents.TitleCurve, curveEmergency.Title);
        Assert.Contains("曲线", curveEmergency.Body);
        Assert.Contains("90°C", curveEmergency.Body);
        Assert.Contains("100%", curveEmergency.Body);
        // It states the curve path is not the fixed-RPM latch, so the two are never confused.
        Assert.Contains("固定 RPM 紧急锁存", curveEmergency.Body);
        Assert.DoesNotContain("固定 RPM 紧急锁存", fixedEmergency.Body);

        // Two curve emergencies coalesce with each other (one logical event, refreshed in place).
        var queue = new OsdMessageQueue();
        queue.Enqueue(curveEmergency);
        OsdMessageNormalizer.TryNormalize(OsdEvents.CurveEmergencyFullSpeed(93, 100, Sample), out var again);
        Assert.Equal(OsdEnqueueResult.Coalesced, queue.Enqueue(again));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void RepeatedEventSharesOneCoalesceKey()
    {
        OsdMessageNormalizer.TryNormalize(OsdEvents.CurveEnabled(new OsdSample(50, 2000)), out var first);
        OsdMessageNormalizer.TryNormalize(OsdEvents.CurveEnabled(new OsdSample(70, 3100)), out var second);

        Assert.Equal(first.CoalesceKey, second.CoalesceKey);
        var queue = new OsdMessageQueue();
        queue.Enqueue(first);
        Assert.Equal(OsdEnqueueResult.Coalesced, queue.Enqueue(second));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void FailureDetailsAreRedactedAndBounded()
    {
        var message = OsdEvents.GuardianRecoveryFailed(
            @"EC 写入失败 C:\Users\someone\AppData\Local\MiniProToolkit\curve.json 以及 " + new string('x', 400),
            Sample);

        Assert.DoesNotContain("C:\\Users", message.Body);
        Assert.Contains("[LOCAL_PATH]", message.Body);
        Assert.True(OsdEvents.SafeText(new string('y', 400))!.Length <= OsdEvents.MaxDetailLength);
    }

    [Fact]
    public void SafeTextDropsEmptyInput()
    {
        Assert.Null(OsdEvents.SafeText(null));
        Assert.Null(OsdEvents.SafeText("   "));
        Assert.Null(OsdEvents.SafeText("\r\n"));
    }

    [Fact]
    public void EmergencyMessageExplainsTheLimitWithoutAnExtraReading()
    {
        var message = OsdEvents.EmergencyFullSpeed(null, OsdSample.Empty);

        Assert.Equal(OsdEvents.TitleProtection, message.Title);
        Assert.Contains("90°C", message.Body);
        Assert.Contains("CPU --", message.Body);
        Assert.Equal(OsdSeverity.Error, message.Severity);
    }
}
