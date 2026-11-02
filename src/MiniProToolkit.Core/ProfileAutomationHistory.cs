using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniProToolkit.Core;

public enum ProfileAutomationExecutionOutcome { Applied, Rejected }
public enum ProfileAutomationExecutionReason { Applied, MissingProfile, Rejected, Exception }

/// <summary>Persisted allowlist only. Names and matching conditions must never be added to this shape.</summary>
public sealed record ProfileAutomationExecution(
    [property: JsonRequired] int Version,
    [property: JsonRequired] DateTimeOffset TimestampUtc,
    [property: JsonRequired] string RuleId,
    [property: JsonRequired] string ProfileId,
    [property: JsonRequired] ProfileAutomationExecutionOutcome Outcome,
    [property: JsonRequired] ProfileAutomationExecutionReason Reason,
    [property: JsonRequired] long DurationMilliseconds);

/// <summary>
/// Best-effort, bounded execution journal, not a per-tick matching log. Does not access hardware.
/// Anonymous IDs are stable hashes, not encryption; current display names belong exclusively to the UI.
/// </summary>
public sealed class ProfileAutomationHistory
{
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxBackups = 3;
    public const int MaxReadRecords = 500;
    public const long MaxDurationMilliseconds = 86_400_000;
    private const int MaxLineBytes = 2048;
    // Bounded process-wide lock pool also serializes separate instances targeting the same path.
    private static readonly object[] Gates = Enumerable.Range(0, 32).Select(_ => new object()).ToArray();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly object _gate;
    public string Path { get; }

    public ProfileAutomationHistory(string? path = null)
    {
        Path = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiniProToolkit", "history", "automation-executions.jsonl"));
        _gate = Gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path) % (uint)Gates.Length];
    }

    public static string AnonymousId(string? name) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes((name ?? string.Empty).Trim().ToUpperInvariant())))[..16].ToLowerInvariant();

    /// <summary>Returns false without exposing exception text or delaying the actual apply with a retry.</summary>
    public bool Record(string ruleName, string profileName, ProfileAutomationExecutionOutcome outcome,
        ProfileAutomationExecutionReason reason, TimeSpan duration, DateTimeOffset? timestamp = null)
    {
        try
        {
            var entry = new ProfileAutomationExecution(1, (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime(),
                AnonymousId(ruleName), AnonymousId(profileName), outcome, reason,
                (long)Math.Clamp(duration.TotalMilliseconds, 0, MaxDurationMilliseconds));
            if (!IsValid(entry)) return false;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Options) + "\n");
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length + bytes.Length > MaxFileBytes)
                {
                    for (var i = MaxBackups; i >= 1; i--)
                    {
                        var source = i == 1 ? Path : Path + "." + (i - 1);
                        if (File.Exists(source)) File.Move(source, Path + "." + i, true);
                    }
                }
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
            return true;
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { return false; }
    }

    /// <summary>
    /// Reads at most the last 500 physical lines across the active file and three backups, then sorts valid
    /// records by UTC descending. Reverse chunk reads bound memory and avoid scanning old file contents.
    /// Malformed/oversized lines, unsupported versions and enum values are ignored. Unknown fields are discarded.
    /// </summary>
    public IReadOnlyList<ProfileAutomationExecution> ReadRecent(int limit = MaxReadRecords, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 0, MaxReadRecords);
        if (limit == 0) return [];
        var entries = new List<ProfileAutomationExecution>();
        var remaining = MaxReadRecords;
        lock (_gate)
        {
            for (var backup = 0; backup <= MaxBackups && remaining > 0; backup++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = backup == 0 ? Path : Path + "." + backup;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    foreach (var line in ReadTailLines(stream, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        remaining--;
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            var root = document.RootElement;
                            // Reject permissive composite/numeric enum spellings and absent default outcomes.
                            if (root.ValueKind == JsonValueKind.Object
                                && root.TryGetProperty("outcome", out var outcome)
                                && outcome.ValueKind == JsonValueKind.String
                                && outcome.GetString() is "Applied" or "Rejected"
                                && root.TryGetProperty("reason", out var reason)
                                && reason.ValueKind == JsonValueKind.String
                                && reason.GetString() is "Applied" or "MissingProfile" or "Rejected" or "Exception")
                            {
                                var entry = root.Deserialize<ProfileAutomationExecution>(Options);
                                if (entry is not null && IsValid(entry)) entries.Add(entry);
                            }
                        }
                        catch (JsonException) { }
                        if (remaining <= 0) break;
                    }
                }
                catch (Exception ex) when (IsStorageFailure(ex)) { /* Missing or unavailable history is non-fatal. */ }
            }
        }
        return entries.OrderByDescending(entry => entry.TimestampUtc).Take(limit).ToArray();
    }

    /// <summary>Only anonymous validated fields are exported, never UI labels or unknown JSON properties.</summary>
    public static string ExportCsv(IEnumerable<ProfileAutomationExecution> entries)
    {
        var text = new StringBuilder("TimestampUtc,RuleId,ProfileId,Outcome,Reason,DurationMilliseconds\r\n");
        foreach (var entry in entries.Where(IsValid).Take(MaxReadRecords))
            text.Append(entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.RuleId).Append(',').Append(entry.ProfileId).Append(',')
                .Append(entry.Outcome).Append(',').Append(entry.Reason).Append(',')
                .Append(entry.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        return text.ToString();
    }

    private static bool IsStorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;

    private static bool IsValid(ProfileAutomationExecution entry) => entry.Version == 1
        && entry.TimestampUtc != default && entry.TimestampUtc.Offset == TimeSpan.Zero
        && ValidId(entry.RuleId) && ValidId(entry.ProfileId)
        && Enum.IsDefined(entry.Outcome) && Enum.IsDefined(entry.Reason)
        && (entry.Outcome == ProfileAutomationExecutionOutcome.Applied
            ? entry.Reason == ProfileAutomationExecutionReason.Applied
            : entry.Reason != ProfileAutomationExecutionReason.Applied)
        && entry.DurationMilliseconds is >= 0 and <= MaxDurationMilliseconds;

    private static bool ValidId(string? id) => id is { Length: 16 }
        && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IEnumerable<string> ReadTailLines(FileStream stream, CancellationToken token)
    {
        var position = stream.Length;
        var lowerBound = Math.Max(0, position - MaxFileBytes);
        var chunk = new byte[4096];
        var reversed = new List<byte>(MaxLineBytes);
        var oversized = false;
        var atEnd = true;
        while (position > lowerBound)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(chunk.Length, position - lowerBound);
            position -= count;
            stream.Position = position;
            stream.ReadExactly(chunk.AsSpan(0, count));
            for (var i = count - 1; i >= 0; i--)
            {
                if (chunk[i] == (byte)'\n')
                {
                    // A single terminal newline terminates the last record, not an extra empty row.
                    if (!atEnd) yield return Decode();
                    atEnd = false;
                    reversed.Clear(); oversized = false;
                }
                else
                {
                    atEnd = false;
                    if (reversed.Count < MaxLineBytes) reversed.Add(chunk[i]);
                    else oversized = true;
                }
            }
        }
        if (lowerBound == 0 && (reversed.Count > 0 || oversized)) yield return Decode();

        string Decode()
        {
            if (oversized) return string.Empty;
            var bytes = reversed.ToArray();
            Array.Reverse(bytes);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\r');
        }
    }
}
