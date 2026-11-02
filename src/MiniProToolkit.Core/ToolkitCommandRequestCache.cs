namespace MiniProToolkit.Core;

/// <summary>What the command pipe server has to do with one parsed write request.</summary>
public enum ToolkitCommandDispatchDecisionKind
{
    /// <summary>This request id was never seen: the caller may execute the command once and then record the answer.</summary>
    Dispatch,

    /// <summary>The same request id already completed: answer from the cache and never execute the command again.</summary>
    Replay,

    /// <summary>
    /// The same request id was already dispatched but has no recorded answer yet (still running, or it timed out /
    /// failed before an answer existed). It must never be executed a second time; the caller fails closed.
    /// </summary>
    AlreadyInFlight
}

/// <summary>
/// Answer of one <see cref="ToolkitCommandRequestCache.Begin"/> call. <see cref="CachedAnswer"/> is the answer of the
/// first execution and is only set for <see cref="ToolkitCommandDispatchDecisionKind.Replay"/>.
/// </summary>
public sealed record ToolkitCommandDispatchDecision(
    ToolkitCommandDispatchDecisionKind Kind,
    ToolkitCommandIpcResponse? CachedAnswer = null)
{
    /// <summary>Only a first-seen request id may be executed.</summary>
    public bool ShouldExecute => Kind == ToolkitCommandDispatchDecisionKind.Dispatch;

    /// <summary>Shared verdict for a first-seen request id (no mutable state, safe to reuse).</summary>
    public static ToolkitCommandDispatchDecision Dispatch { get; } =
        new(ToolkitCommandDispatchDecisionKind.Dispatch);
}

/// <summary>
/// Bounded, in-memory, process-local cache that makes one write command idempotent for as long as a client may retry
/// it. The key is the pair (request id, action), so two different commands that happen to share an id stay independent
/// and the same command with a different id is executed independently.
/// <para>
/// Only the request id, the action token and the bounded answer (status, action echo, message, report) are kept: no
/// path, no command line and no file is involved, so a process restart clears the cache by construction. Capacity and
/// retention are hard bounds, so a client that keeps sending new ids cannot grow the process.
/// </para>
/// <para>
/// The in-flight window is what makes retries safe: an id is recorded <em>before</em> the command is dispatched, and a
/// duplicate that arrives while the first attempt is still running (or after it timed out without an answer) is
/// answered with "already received, do not retry" instead of being executed a second time. The retention window is
/// longer than the server's worst case for one write, so an unanswered attempt cannot be re-run by the same id.
/// </para>
/// </summary>
public sealed class ToolkitCommandRequestCache
{
    /// <summary>Maximum number of remembered requests. A retry uses the same id and therefore always fits.</summary>
    public const int DefaultCapacity = 32;

    /// <summary>
    /// How long one request id is remembered. Longer than the server's worst case for one write (40 s), so a duplicate
    /// sent right after a timeout is still recognized, and short enough that a manual retry with a fresh id is never
    /// blocked.
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromSeconds(60);

    private readonly int _capacity;
    private readonly long _retentionMilliseconds;
    private readonly Func<long> _now;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private readonly object _gate = new();

    /// <param name="capacity">Bounded number of remembered requests; must be positive.</param>
    /// <param name="retention">How long a request id is remembered; must be positive.</param>
    /// <param name="monotonicMilliseconds">
    /// Clock used for retention. Defaults to <see cref="Environment.TickCount64"/>; tests pass a counter so the
    /// retention window can be crossed without sleeping.
    /// </param>
    public ToolkitCommandRequestCache(int capacity = DefaultCapacity, TimeSpan? retention = null,
        Func<long>? monotonicMilliseconds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        var window = retention ?? DefaultRetention;
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "The retention window must be positive.");
        _capacity = capacity;
        _retentionMilliseconds = (long)Math.Ceiling(window.TotalMilliseconds);
        _now = monotonicMilliseconds ?? (static () => Environment.TickCount64);
    }

    /// <summary>Number of remembered requests (in-flight and completed). Never exceeds the configured capacity.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>
    /// Decides what to do with one request id before anything is executed. Records the id as in flight when it is
    /// first seen, so a duplicate can never be dispatched twice.
    /// </summary>
    public ToolkitCommandDispatchDecision Begin(string requestId, ToolkitCommandAction action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var key = KeyFor(requestId, action);
        lock (_gate)
        {
            var now = _now();
            PurgeExpired(now);
            if (_entries.TryGetValue(key, out var existing))
            {
                Touch(existing);
                if (existing.Answer is { } answer)
                {
                    // Sliding window: as long as a client keeps retrying the same id it keeps getting the same
                    // recorded answer instead of a second execution.
                    existing.ExpiresAt = now + _retentionMilliseconds;
                    return new ToolkitCommandDispatchDecision(ToolkitCommandDispatchDecisionKind.Replay, answer);
                }
                return new ToolkitCommandDispatchDecision(ToolkitCommandDispatchDecisionKind.AlreadyInFlight);
            }

            var created = new Entry(key) { ExpiresAt = now + _retentionMilliseconds };
            _entries.Add(key, created);
            created.Node = _recent.AddFirst(key);
            TrimToCapacity();
            return ToolkitCommandDispatchDecision.Dispatch;
        }
    }

    /// <summary>
    /// Records the answer of an executed request so every later duplicate is answered from the cache. Safe to call
    /// once per <see cref="Begin"/> that returned <see cref="ToolkitCommandDispatchDecisionKind.Dispatch"/>.
    /// </summary>
    public void Complete(string requestId, ToolkitCommandAction action, ToolkitCommandIpcResponse answer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(answer);
        var key = KeyFor(requestId, action);
        lock (_gate)
        {
            var now = _now();
            PurgeExpired(now);
            if (_entries.TryGetValue(key, out var entry))
            {
                Touch(entry);
            }
            else
            {
                entry = new Entry(key);
                _entries.Add(key, entry);
                entry.Node = _recent.AddFirst(key);
            }
            entry.Answer = answer;
            entry.ExpiresAt = now + _retentionMilliseconds;
            TrimToCapacity();
        }
    }

    /// <summary>Drops every remembered request. Only used by tests and by an explicit reset; a restart does the same.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recent.Clear();
        }
    }

    /// <summary>Cache key of one command: the request id plus the action token, never a value or a path.</summary>
    private static string KeyFor(string requestId, ToolkitCommandAction action) =>
        ToolkitCommandIpcProtocol.ActionToken(action) + "|" + requestId.Trim().ToLowerInvariant();

    private void Touch(Entry entry)
    {
        if (entry.Node is null)
            return;
        _recent.Remove(entry.Node);
        _recent.AddFirst(entry.Node);
    }

    private void PurgeExpired(long now)
    {
        List<string>? expired = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt > now)
                continue;
            (expired ??= []).Add(pair.Key);
        }
        if (expired is null)
            return;
        foreach (var key in expired)
            Remove(key);
    }

    /// <summary>Least-recently-used eviction keeps the cache bounded even for a client that only sends new ids.</summary>
    private void TrimToCapacity()
    {
        while (_entries.Count > _capacity && _recent.Last is { } oldest)
            Remove(oldest.Value);
    }

    private void Remove(string key)
    {
        if (!_entries.Remove(key, out var entry))
            return;
        if (entry.Node is not null)
            _recent.Remove(entry.Node);
    }

    private sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public LinkedListNode<string>? Node { get; set; }
        public ToolkitCommandIpcResponse? Answer { get; set; }
        public long ExpiresAt { get; set; }
    }
}
