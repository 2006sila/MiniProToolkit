namespace MiniProToolkit.Core;

/// <summary>
/// Values copied from an <em>already sampled</em> snapshot for an OSD body. Every field is
/// optional and a missing value renders as <c>--</c>, so building an OSD can never trigger an
/// extra hardware read and never shows an empty placeholder line.
/// </summary>
public readonly record struct OsdSample(
    double? CpuTemperatureC = null,
    int? Fan1Rpm = null,
    int? TargetRpm = null,
    string? ControlState = null)
{
    public static OsdSample Empty => default;

    /// <summary>EC/control temperature; the same field used by fan control and the 90 °C limit.</summary>
    public string TemperatureText =>
        CpuTemperatureC is { } value && double.IsFinite(value) ? $"{value:0}°C" : "--";

    /// <summary>Two-line body: live values on the first line, target/control state on the second.</summary>
    public string Summary
    {
        get
        {
            var control = string.IsNullOrWhiteSpace(ControlState) ? "--" : ControlState.Trim();
            return $"CPU {TemperatureText} · 风扇 {FormatRpm(Fan1Rpm)}\n目标 {FormatRpm(TargetRpm)} · 控制 {control}";
        }
    }

    private static string FormatRpm(int? rpm) => rpm is { } value ? $"{value} RPM" : "--";
}

/// <summary>
/// Hardware-free OSD message factory for the control events the main window already raises.
/// Pure and non-throwing: no hardware access, no sampling, no I/O. Failure details are passed
/// through <see cref="DiagnosticRedactor"/> and length-bounded, so a raw exception message can
/// never put a local path, network path or credential into an OSD popup.
/// </summary>
public static class OsdEvents
{
    public const string TitleCurve = "安全曲线";
    public const string TitleGuardian = "Guardian";
    public const string TitleProtection = "温度保护";
    public const string TitleFanProtection = "风扇保护";
    public const string TitleFixedRpm = "固定 RPM";
    public const string TitleProfile = "方案";
    public const string TitleCommand = "操作";

    /// <summary>Upper bound for a redacted failure detail line.</summary>
    public const int MaxDetailLength = 120;

    private static readonly TimeSpan NotifyDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FaultDuration = TimeSpan.FromSeconds(6);

    public static OsdMessage CurveEnabled(OsdSample sample) =>
        WithSample(TitleCurve, "已启用安全曲线（Guardian 托管）", sample, OsdSeverity.Success, NotifyDuration, "osd-curve", icon: OsdIcon.Fan);

    public static OsdMessage CurveStopped(OsdSample sample) =>
        WithSample(TitleCurve, "已停止安全曲线，已请求 EC 自动", sample, OsdSeverity.Info, NotifyDuration, "osd-curve-stop", icon: OsdIcon.Fan);

    /// <summary>Enabling was refused (temperature, fault, missing Guardian, unconfirmed reading).</summary>
    public static OsdMessage CurveRejected(string? detail, OsdSample sample) =>
        WithSample(TitleCurve, "未启用安全曲线", sample, OsdSeverity.Warning, FaultDuration, "osd-curve-reject", detail, OsdIcon.Warning);

    public static OsdMessage CurveStopFailed(string? detail) =>
        WithDetail(TitleCurve, "停止安全曲线失败，请检查保护状态", detail, OsdSeverity.Error, FaultDuration, "osd-curve-fail", OsdIcon.Error);

    public static OsdMessage GuardianRecovered(OsdSample sample) =>
        WithSample(TitleGuardian, "已恢复 EC 自动（手动控制已停止）", sample, OsdSeverity.Success, NotifyDuration, "osd-guardian-ok", icon: OsdIcon.Shield);

    public static OsdMessage GuardianRecoveryFailed(string? detail, OsdSample sample) =>
        WithSample(TitleGuardian, "恢复 EC 自动失败，恢复仍待重试", sample, OsdSeverity.Error, FaultDuration, "osd-guardian-fail", detail, OsdIcon.Shield);

    /// <summary>90 °C emergency: the fixed-RPM path latched full speed instead of a normal target.</summary>
    public static OsdMessage EmergencyFullSpeed(double? temperatureC, OsdSample sample)
    {
        var temperature = temperatureC is { } value && double.IsFinite(value) ? $"{value:0}°C" : "--";
        return WithSample(TitleProtection, $"CPU 温度 {temperature} 已达 90°C 限值，已进入紧急满速", sample,
            OsdSeverity.Error, FaultDuration, "osd-emergency", icon: OsdIcon.Temperature);
    }

    /// <summary>
    /// Curve 90 °C emergency: the curve path itself commands 100 % duty. Deliberately a separate
    /// event and coalesce key from <see cref="EmergencyFullSpeed"/>: it announces the curve
    /// threshold (which follows the temperature back down) and never claims the fixed-RPM
    /// emergency latch latched. <paramref name="temperatureC"/> is the already sampled EC value.
    /// </summary>
    public static OsdMessage CurveEmergencyFullSpeed(double? temperatureC, int? dutyPercent, OsdSample sample)
    {
        var temperature = temperatureC is { } value && double.IsFinite(value) ? $"{value:0}°C" : "--";
        var duty = dutyPercent is { } percent ? $"{percent}%" : "100%";
        return WithSample(TitleCurve,
            $"CPU {temperature} 达到曲线 90°C 紧急阈值，曲线请求 {duty} duty（与固定 RPM 紧急锁存无关）",
            sample, OsdSeverity.Error, FaultDuration, "osd-curve-emergency", icon: OsdIcon.Temperature);
    }

    public static OsdMessage FanProtection(string? fault, OsdSample sample) =>
        WithSample(TitleFanProtection, "检测到风扇保护故障", sample, OsdSeverity.Error, FaultDuration, "osd-fan-fault", fault, OsdIcon.Fan);

    public static OsdMessage FixedRpmFault(string? detail, OsdSample sample) =>
        WithSample(TitleFixedRpm, "闭环已停止并请求 EC 自动，请先显式恢复", sample, OsdSeverity.Error, FaultDuration,
            "osd-fixed-fault", detail, OsdIcon.Fan);

    public static OsdMessage FixedRpmRecovered(OsdSample sample) =>
        WithSample(TitleFixedRpm, "风扇保护已解除，状态恢复", sample, OsdSeverity.Success, NotifyDuration, "osd-fixed-ok", icon: OsdIcon.Fan);

    public static OsdMessage CommandSucceeded(string label, OsdSample sample) =>
        WithSample(TitleCommand, SafeText(label) ?? "操作完成", sample, OsdSeverity.Success, NotifyDuration, "osd-command", icon: OsdIcon.Success);

    public static OsdMessage CommandFailed(string label, string? detail) =>
        WithDetail(TitleCommand, $"{SafeText(label) ?? "操作"}失败", detail, OsdSeverity.Error, FaultDuration, "osd-command-fail", OsdIcon.Error);

    public static OsdMessage ProfileApplied(string profileName, string? summary, OsdSample sample) =>
        WithSample(TitleProfile, $"已应用 {SafeText(profileName) ?? "方案"}" + FormatSummary(summary), sample,
            OsdSeverity.Success, NotifyDuration, "osd-profile-ok", icon: OsdIcon.Success);

    public static OsdMessage ProfileFailed(string? detail, OsdSample sample) =>
        WithSample(TitleProfile, "方案未应用", sample, OsdSeverity.Warning, FaultDuration, "osd-profile-fail", detail, OsdIcon.Warning);

    /// <summary>Demo mode preview: nothing was written to hardware.</summary>
    public static OsdMessage ProfilePreview(string profileName, string? summary) =>
        WithDetail(TitleProfile, $"演示预览（未访问硬件）：{SafeText(profileName) ?? "方案"}" + FormatSummary(summary),
            null, OsdSeverity.Info, NotifyDuration, "osd-profile-preview", OsdIcon.Info);

    /// <summary>
    /// Redacts paths/credentials and bounds a caller-provided detail. Returns <c>null</c> for
    /// empty input so a message never grows an empty line. Non-throwing.
    /// </summary>
    public static string? SafeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            var redacted = DiagnosticRedactor.Redact(text).Trim();
            if (redacted.Length == 0)
                return null;
            return redacted.Length <= MaxDetailLength ? redacted : redacted[..(MaxDetailLength - 1)] + "…";
        }
        catch (Exception)
        {
            // A redaction failure must not hide the event; keep a neutral, non-sensitive line.
            return "详情见日志";
        }
    }

    private static string FormatSummary(string? summary) =>
        string.IsNullOrWhiteSpace(summary) ? string.Empty : $" · {SafeText(summary)}";

    private static OsdMessage WithSample(string title, string headline, OsdSample sample, OsdSeverity severity,
        TimeSpan duration, string key, string? detail = null, OsdIcon icon = OsdIcon.None)
    {
        var safe = SafeText(detail);
        var body = safe is null
            ? $"{headline}\n{sample.Summary}"
            : $"{headline}\n{safe}\n{sample.Summary}";
        return new OsdMessage(body, title, severity, duration, key) { Icon = icon };
    }

    private static OsdMessage WithDetail(string title, string headline, string? detail, OsdSeverity severity,
        TimeSpan duration, string key, OsdIcon icon = OsdIcon.None)
    {
        var safe = SafeText(detail);
        return new OsdMessage(safe is null ? headline : $"{headline}\n{safe}", title, severity, duration, key) { Icon = icon };
    }
}
