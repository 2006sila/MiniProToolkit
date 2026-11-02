namespace MiniProToolkit.Core;

/// <summary>
/// CapsLock / NumLock toggle state. This is a read-only mirror of the keyboard
/// toggle state: nothing in this type can change it.
/// </summary>
public sealed record SystemLockKeys(bool CapsLockOn, bool NumLockOn)
{
    public string CapsLockText => CapsLockOn ? "开" : "关";

    public string NumLockText => NumLockOn ? "开" : "关";
}

/// <summary>
/// One read-only built-in panel brightness reading. A percentage only exists when
/// the platform returned a plausible value, so the UI can show "未知" instead of a
/// fabricated number.
/// </summary>
public sealed record BrightnessReading(int? Percent, string? Status)
{
    /// <summary>Lowest plausible panel brightness percentage.</summary>
    public const int MinimumPercent = 0;

    /// <summary>Highest plausible panel brightness percentage.</summary>
    public const int MaximumPercent = 100;

    public const string OutOfRangeStatus = "内建屏幕亮度读数超出 0-100 范围，按不可用处理。";

    public const string MissingStatus = "内建屏幕亮度未返回读数。";

    public bool IsAvailable => Percent is not null;

    /// <summary>Unavailable reading: no percentage, only a short sanitized status.</summary>
    public static BrightnessReading Unavailable(string? status = null) => new(null, status ?? MissingStatus);

    /// <summary>
    /// Normalizes a raw percentage. Only 0-100 is accepted; a missing or implausible
    /// value degrades to <see cref="Unavailable"/> instead of being clamped, so a
    /// broken sensor can never look like a real brightness setting.
    /// </summary>
    public static BrightnessReading FromPercent(int? rawPercent)
    {
        if (rawPercent is not { } percent)
            return Unavailable();
        return percent is >= MinimumPercent and <= MaximumPercent
            ? new BrightnessReading(percent, null)
            : Unavailable(OutOfRangeStatus);
    }
}

/// <summary>
/// Default output endpoint volume: the CoreAudio master volume scalar (0-1) plus the
/// mute flag. Read-only; there is deliberately no setter anywhere in this type.
/// </summary>
public sealed record AudioVolumeReading(double? VolumeScalar, bool? Muted, string? Status)
{
    /// <summary>Lowest plausible master volume scalar.</summary>
    public const double MinimumScalar = 0d;

    /// <summary>Highest plausible master volume scalar.</summary>
    public const double MaximumScalar = 1d;

    public const string OutOfRangeStatus = "默认输出音量标量超出 0-1 范围，按不可用处理。";

    public const string IncompleteStatus = "默认输出音量或静音状态缺失，按不可用处理。";

    public bool IsAvailable => VolumeScalar is not null && Muted is not null;

    /// <summary>Rounded volume percentage, or null when the scalar is unusable.</summary>
    public int? Percent => VolumeScalar is { } scalar
        ? (int)Math.Round(scalar * 100d, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>Unavailable reading: neither a scalar nor a mute state.</summary>
    public static AudioVolumeReading Unavailable(string? status = null) => new(null, null, status ?? IncompleteStatus);

    /// <summary>
    /// Normalizes a raw master volume scalar and mute flag. Non-finite or out of
    /// range values and a missing mute flag degrade to <see cref="Unavailable"/>;
    /// a volume is never clamped into a fake plausible value.
    /// </summary>
    public static AudioVolumeReading FromScalar(double? scalar, bool? muted)
    {
        if (scalar is not { } rawScalar || muted is not { } isMuted)
            return Unavailable();
        if (!double.IsFinite(rawScalar) || rawScalar is < MinimumScalar or > MaximumScalar)
            return Unavailable(OutOfRangeStatus);
        return new AudioVolumeReading(rawScalar, isMuted, null);
    }
}

/// <summary>
/// Default input endpoint (microphone) mute state. Read-only: there is no setter.
/// </summary>
public sealed record AudioMuteReading(bool? Muted, string? Status)
{
    public const string MissingStatus = "默认输入设备未返回静音状态。";

    public bool IsAvailable => Muted is not null;

    /// <summary>Unavailable reading: the mute state is unknown, not assumed to be false.</summary>
    public static AudioMuteReading Unavailable(string? status = null) => new(null, status ?? MissingStatus);

    /// <summary>Normalizes a raw mute flag; null stays unknown instead of becoming "未静音".</summary>
    public static AudioMuteReading FromMute(bool? muted) => muted is { } value
        ? new AudioMuteReading(value, null)
        : Unavailable();
}

/// <summary>
/// Result of one explicit system-control write. A control is successful only after
/// the platform readback matches the requested value; a write error or mismatch
/// must never be presented as success.
/// </summary>
public enum SystemControlWriteState
{
    Succeeded,
    Failed,
    Unconfirmed,
    Unavailable
}

public sealed record SystemControlWriteResult(
    string Control,
    SystemControlWriteState State,
    string Status,
    int? RequestedPercent = null,
    int? ConfirmedPercent = null,
    bool? RequestedMuted = null,
    bool? ConfirmedMuted = null)
{
    public bool IsSuccess => State == SystemControlWriteState.Succeeded;
}

/// <summary>Read-only availability result for an explicit control path.</summary>
public sealed record SystemControlAvailability(bool IsAvailable, string Status)
{
    public static SystemControlAvailability Unavailable(string status) => new(false, status);
    public static SystemControlAvailability Available(string status) => new(true, status);
}

public sealed record SystemControlAvailabilitySnapshot(
    SystemControlAvailability OutputVolume,
    SystemControlAvailability MicrophoneMute,
    SystemControlAvailability Brightness)
{
    public static SystemControlAvailabilitySnapshot Unavailable(string status) => new(
        SystemControlAvailability.Unavailable(status),
        SystemControlAvailability.Unavailable(status),
        SystemControlAvailability.Unavailable(status));
}

/// <summary>
/// Pure verification for write results. Hardware code supplies fake/readback values
/// here, so a mismatch and a missing readback are testable without COM or WMI calls.
/// </summary>
public static class SystemControlVerification
{
    public static SystemControlWriteResult VerifyOutputVolume(int requestedPercent, AudioVolumeReading readback)
    {
        if (requestedPercent is < BrightnessReading.MinimumPercent or > BrightnessReading.MaximumPercent)
            return new SystemControlWriteResult("output-volume", SystemControlWriteState.Failed,
                "目标输出音量超出 0-100 范围，未执行写入。", RequestedPercent: requestedPercent);
        // Do not compare rounded percentages: 42.49% must not confirm a 42% write.
        // The tolerance only covers float marshaling of the requested scalar.
        var normalized = AudioVolumeReading.FromScalar(readback.VolumeScalar, readback.Muted);
        if (!normalized.IsAvailable)
            return new SystemControlWriteResult("output-volume", SystemControlWriteState.Unconfirmed,
                "输出音量写入后的读回不可用，未确认成功。", RequestedPercent: requestedPercent);
        return Math.Abs(normalized.VolumeScalar!.Value - requestedPercent / 100d) <= 0.000001d
            ? new SystemControlWriteResult("output-volume", SystemControlWriteState.Succeeded,
                "输出音量写入完成并读回匹配。", requestedPercent, readback.Percent)
            : new SystemControlWriteResult("output-volume", SystemControlWriteState.Unconfirmed,
                "输出音量写入后的读回与目标不一致，未确认成功。", requestedPercent, readback.Percent);
    }

    public static SystemControlWriteResult VerifyMicrophoneMute(bool requestedMuted, AudioMuteReading readback)
    {
        if (!readback.IsAvailable)
            return new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Unconfirmed,
                "麦克风静音写入后的读回不可用，未确认成功。", RequestedMuted: requestedMuted);
        return readback.Muted == requestedMuted
            ? new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Succeeded,
                "麦克风静音写入完成并读回匹配。", RequestedMuted: requestedMuted, ConfirmedMuted: readback.Muted)
            : new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Unconfirmed,
                "麦克风静音写入后的读回与目标不一致，未确认成功。", RequestedMuted: requestedMuted, ConfirmedMuted: readback.Muted);
    }

    public static SystemControlWriteResult VerifyBrightness(int requestedPercent, BrightnessReading readback)
    {
        if (requestedPercent is < BrightnessReading.MinimumPercent or > BrightnessReading.MaximumPercent)
            return new SystemControlWriteResult("brightness", SystemControlWriteState.Failed,
                "目标屏幕亮度超出 0-100 范围，未执行写入。", RequestedPercent: requestedPercent);
        if (!BrightnessReading.FromPercent(readback.Percent).IsAvailable)
            return new SystemControlWriteResult("brightness", SystemControlWriteState.Unconfirmed,
                "屏幕亮度写入后的读回不可用，未确认成功。", RequestedPercent: requestedPercent);
        return readback.Percent == requestedPercent
            ? new SystemControlWriteResult("brightness", SystemControlWriteState.Succeeded,
                "屏幕亮度写入完成并读回匹配。", requestedPercent, readback.Percent)
            : new SystemControlWriteResult("brightness", SystemControlWriteState.Unconfirmed,
                "屏幕亮度写入后的读回与目标不一致，未确认成功。", requestedPercent, readback.Percent);
    }
}

/// <summary>
/// Snapshot of the read-only system indicators the diagnostics backend can collect
/// without touching EC, fan or power-mode code. Every field is optional on purpose:
/// an unavailable indicator is reported as unknown with a status, never as 0/false.
/// </summary>
public sealed record SystemIndicatorSnapshot(
    SystemLockKeys LockKeys,
    BrightnessReading Brightness,
    AudioVolumeReading OutputVolume,
    AudioMuteReading Microphone)
{
    /// <summary>All-unavailable snapshot used when a caller refuses to probe at all (for example demo mode).</summary>
    public static SystemIndicatorSnapshot Unavailable(string? status = null) => new(
        new SystemLockKeys(false, false),
        BrightnessReading.Unavailable(status),
        AudioVolumeReading.Unavailable(status),
        AudioMuteReading.Unavailable(status));

    /// <summary>Ready-to-display lines, one per indicator, already sanitized and marked read-only.</summary>
    public IReadOnlyList<string> Lines() =>
    [
        SystemIndicatorText.LockKeys(LockKeys),
        SystemIndicatorText.Brightness(Brightness),
        SystemIndicatorText.OutputVolume(OutputVolume),
        SystemIndicatorText.Microphone(Microphone)
    ];
}

/// <summary>
/// Pure formatting/normalization helpers for the read-only system indicators. All
/// text states that the value is read-only and never opened for writing, and every
/// status coming from a platform call is redacted and truncated before display so a
/// device name or local path cannot leak into logs or the diagnostics window.
/// </summary>
public static class SystemIndicatorText
{
    /// <summary>Suffix that tells the reader nothing here is writable.</summary>
    public const string ReadOnlyMark = "只读，未开放写入";

    /// <summary>Placeholder used when a value is unavailable, so 0/false is never implied.</summary>
    public const string UnknownMark = "未知";

    private const int MaximumStatusLength = 160;

    public static string LockKeys(SystemLockKeys keys) =>
        $"CapsLock：{keys.CapsLockText} · NumLock：{keys.NumLockText}（只读）";

    public static string Brightness(BrightnessReading reading) => WithMark(
        reading.Percent is { } percent
            ? $"屏幕亮度：{percent}%"
            : $"屏幕亮度：{UnknownMark} · {StatusText(reading.Status)}");

    public static string OutputVolume(AudioVolumeReading reading) => WithMark(OutputBody(reading));

    public static string Microphone(AudioMuteReading reading) => WithMark(MicrophoneBody(reading));

    /// <summary>Output volume and microphone mute on one line, for a single capability row.</summary>
    public static string Audio(AudioVolumeReading output, AudioMuteReading microphone) =>
        WithMark($"{OutputBody(output)}；{MicrophoneBody(microphone)}");

    private static string OutputBody(AudioVolumeReading reading) =>
        reading.Percent is { } percent && reading.Muted is { } muted
            ? $"默认输出音量：{percent}%（{MuteText(muted)}）"
            : $"默认输出音量：{UnknownMark} · {StatusText(reading.Status)}";

    private static string MicrophoneBody(AudioMuteReading reading) =>
        reading.Muted is { } muted
            ? $"默认输入麦克风：{MuteText(muted)}"
            : $"默认输入麦克风：{UnknownMark} · {StatusText(reading.Status)}";

    private static string MuteText(bool muted) => muted ? "已静音" : "未静音";

    private static string StatusText(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "读取不可用" : Sanitize(status);

    private static string WithMark(string body) => $"{body}（{ReadOnlyMark}）";

    private static string Sanitize(string text)
    {
        var redacted = DiagnosticRedactor.Redact(text.Trim());
        return redacted.Length <= MaximumStatusLength
            ? redacted
            : redacted[..MaximumStatusLength] + "…";
    }
}
