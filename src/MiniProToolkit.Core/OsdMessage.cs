namespace MiniProToolkit.Core;

/// <summary>
/// Severity of a structured on-screen display message.
/// Numeric values are part of the public surface (callers may persist them); append only.
/// </summary>
public enum OsdSeverity
{
    /// <summary>Neutral informational message. Renders with the legacy OSD look.</summary>
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// Bounded built-in OSD icon choices. Values are part of the public surface; append only.
/// The view maps these values to fixed text glyphs and never loads caller-provided artwork.
/// </summary>
public enum OsdIcon
{
    /// <summary>No icon; preserves the legacy OSD layout.</summary>
    None = 0,
    /// <summary>Neutral information glyph.</summary>
    Info = 1,
    /// <summary>Successful action glyph.</summary>
    Success = 2,
    /// <summary>Warning glyph.</summary>
    Warning = 3,
    /// <summary>Error glyph.</summary>
    Error = 4,
    /// <summary>Temperature and thermal-protection glyph.</summary>
    Temperature = 5,
    /// <summary>Fan-control glyph.</summary>
    Fan = 6,
    /// <summary>Guardian and protection glyph.</summary>
    Shield = 7
}

/// <summary>
/// Structured OSD message: optional title, multi-line body, severity, display duration and a
/// bounded built-in icon.
/// Instances are plain data; use <see cref="OsdMessageNormalizer.TryNormalize"/> to obtain a
/// bounded, display-ready message.
/// </summary>
public sealed record OsdMessage(
    string Body,
    string? Title = null,
    OsdSeverity Severity = OsdSeverity.Info,
    TimeSpan? Duration = null,
    string? CoalesceKey = null)
{
    /// <summary>Fixed built-in icon choice; <see cref="OsdIcon.None"/> preserves the legacy layout.</summary>
    public OsdIcon Icon { get; init; } = OsdIcon.None;

    /// <summary>
    /// Backwards compatible factory for the legacy <c>ShowMessage(string)</c> path: the whole
    /// text becomes the body and renders exactly like the original OSD.
    /// </summary>
    public static OsdMessage FromText(string text) => new(text);
}

/// <summary>
/// Pure, non-throwing normalization for OSD messages. All limits are safety boundaries so a
/// misbehaving producer (sampling thread, automation) can never inflate the OSD window or
/// block on it for an unbounded time.
/// </summary>
public static class OsdMessageNormalizer
{
    public const double DefaultDurationSeconds = 2;
    public const double MinDurationSeconds = 1;
    public const double MaxDurationSeconds = 30;
    public const int MaxTitleLength = 80;
    public const int MaxBodyLength = 512;
    public const int MaxBodyLines = 6;

    /// <summary>
    /// Normalizes <paramref name="message"/> into a bounded message. Returns <c>false</c> when the
    /// message is null or has no visible body text; nothing is thrown.
    /// </summary>
    public static bool TryNormalize(OsdMessage? message, out OsdMessage normalized)
    {
        normalized = new OsdMessage(string.Empty, null, OsdSeverity.Info, TimeSpan.FromSeconds(DefaultDurationSeconds));
        if (message is null)
            return false;

        var body = NormalizeBody(message.Body);
        if (body.Length == 0)
            return false;

        var title = NormalizeTitle(message.Title);
        var severity = Enum.IsDefined(message.Severity) ? message.Severity : OsdSeverity.Info;
        var duration = NormalizeDuration(message.Duration);
        var key = NormalizeKey(message.CoalesceKey, title, body);
        var icon = Enum.IsDefined(message.Icon) ? message.Icon : OsdIcon.None;
        normalized = new OsdMessage(body, title, severity, duration, key) { Icon = icon };
        return true;
    }

    private static string NormalizeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var lines = new List<string>();
        foreach (var rawLine in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = StripControlCharacters(rawLine).Trim();
            if (line.Length > 0)
                lines.Add(line);
            if (lines.Count == MaxBodyLines)
                break;
        }

        var text = string.Join("\n", lines);
        return text.Length <= MaxBodyLength ? text : text[..(MaxBodyLength - 1)] + "…";
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var builder = new System.Text.StringBuilder(title.Length);
        var lastWasSpace = false;
        foreach (var character in StripControlCharacters(title))
        {
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && !lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var text = builder.ToString().Trim();
        if (text.Length == 0)
            return null;
        return text.Length <= MaxTitleLength ? text : text[..MaxTitleLength];
    }

    private static TimeSpan NormalizeDuration(TimeSpan? duration)
    {
        var seconds = duration?.TotalSeconds ?? DefaultDurationSeconds;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            seconds = DefaultDurationSeconds;
        seconds = Math.Clamp(seconds, MinDurationSeconds, MaxDurationSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static string NormalizeKey(string? coalesceKey, string? title, string body)
    {
        var explicitKey = string.IsNullOrWhiteSpace(coalesceKey) ? null : StripControlCharacters(coalesceKey).Trim();
        if (!string.IsNullOrEmpty(explicitKey))
            return explicitKey.Length <= OsdMessageKeys.MaxKeyLength
                ? explicitKey
                : explicitKey[..OsdMessageKeys.MaxKeyLength];
        return OsdMessageKeys.Resolve(title, body);
    }

    private static string StripControlCharacters(string value) =>
        new(value.Where(character => !char.IsControl(character)).ToArray());
}

/// <summary>
/// Coalescing key rules. Kept identical to the first-version OSD behaviour ("标题：正文" collapses
/// on the prefix before the separator) so existing callers keep their merge semantics.
/// </summary>
public static class OsdMessageKeys
{
    public const int MaxKeyLength = 64;

    /// <summary>Key used to merge repeated messages. An explicit <see cref="OsdMessage.CoalesceKey"/> wins.</summary>
    public static string Resolve(OsdMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.IsNullOrWhiteSpace(message.CoalesceKey))
            return Truncate(message.CoalesceKey.Trim());
        return Resolve(message.Title, message.Body);
    }

    internal static string Resolve(string? title, string body)
    {
        if (!string.IsNullOrWhiteSpace(title))
            return Truncate(title.Trim());

        var text = (body ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        // Legacy separators kept, plus newline so a multi-line body merges on its first line.
        var separator = text.IndexOfAny(['：', ':', ' ', '\n', '\t']);
        return Truncate(separator > 0 ? text[..separator] : text);
    }

    private static string Truncate(string value) =>
        value.Length <= MaxKeyLength ? value : value[..MaxKeyLength];
}

/// <summary>Outcome of <see cref="OsdMessageQueue.Enqueue"/>.</summary>
public enum OsdEnqueueResult
{
    /// <summary>The message was appended at the end of the queue.</summary>
    Queued = 0,
    /// <summary>The message replaced an identical-key message that was still waiting at the end.</summary>
    Coalesced = 1
}

/// <summary>
/// Bounded FIFO queue with the first-version coalescing rule: a new message replaces the last
/// still-waiting message when both resolve to the same coalescing key, and the oldest entries are
/// dropped once the capacity is exceeded.
/// </summary>
public sealed class OsdMessageQueue
{
    public const int DefaultCapacity = 50;

    private readonly LinkedList<OsdMessage> _pending = new();
    private long _dropped;

    public OsdMessageQueue(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "OSD queue capacity must be positive.");
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _pending.Count;

    public bool IsEmpty => _pending.Count == 0;

    /// <summary>Number of messages discarded because the queue was full.</summary>
    public long DroppedCount => _dropped;

    public OsdEnqueueResult Enqueue(OsdMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = OsdEnqueueResult.Queued;
        if (_pending.Last is { } last && OsdMessageKeys.Resolve(last.Value) == OsdMessageKeys.Resolve(message))
        {
            _pending.RemoveLast();
            result = OsdEnqueueResult.Coalesced;
        }

        _pending.AddLast(message);
        while (_pending.Count > Capacity)
        {
            _pending.RemoveFirst();
            _dropped++;
        }

        return result;
    }

    public bool TryDequeue(out OsdMessage message)
    {
        if (_pending.First is { } first)
        {
            message = first.Value;
            _pending.RemoveFirst();
            return true;
        }

        message = null!;
        return false;
    }

    public void Clear() => _pending.Clear();
}
