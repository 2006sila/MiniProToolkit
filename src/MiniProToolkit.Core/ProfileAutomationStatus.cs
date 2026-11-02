using System.Globalization;

namespace MiniProToolkit.Core;

/// <summary>
/// Fixed, presentation-safe reason codes for <see cref="ProfileAutomationRuleStatus"/>. The set is deliberately
/// coarse and closed: a status may be shown in the settings window, copied into diagnostics or written to the app
/// log, so it never carries a process path, a command line or a command-line pattern.
/// </summary>
public enum ProfileAutomationRuleOutcome
{
    /// <summary>No process instance matching the rule's name was observed.</summary>
    NoProcess,
    /// <summary>Process data could not be read because access was denied.</summary>
    AccessDenied,
    /// <summary>Process data could not be read (unavailable or a bounded lookup timed out).</summary>
    Timeout,
    /// <summary>The rule's own condition is unusable (for example an unusable command-line pattern).</summary>
    InvalidCondition,
    /// <summary>The condition was evaluated but did not hold (power, CPU load, time window, path or command line).</summary>
    NoMatch,
    /// <summary>The rule's condition was met.</summary>
    Applied,
    /// <summary>The rule was met but its profile could not be applied (missing profile or a rejected apply).</summary>
    Rejected
}

/// <summary>
/// Runtime-only status of one automation rule. Created from <see cref="ProfileAutomationStatusBoard"/> and never
/// persisted: it holds only a rule name, a closed reason code, counters and timestamps.
/// </summary>
public sealed record ProfileAutomationRuleStatus(
    string RuleName,
    ProfileAutomationRuleOutcome Outcome,
    int EvaluationCount = 0,
    int MatchCount = 0,
    int SkipCount = 0,
    int RejectedCount = 0,
    DateTimeOffset? LastEvaluatedAt = null,
    DateTimeOffset? LastMatchedAt = null)
{
    /// <summary>True when the last evaluation had to skip the rule because its process data was unusable.</summary>
    public bool IsSkip => Outcome is ProfileAutomationRuleOutcome.AccessDenied
        or ProfileAutomationRuleOutcome.Timeout
        or ProfileAutomationRuleOutcome.InvalidCondition;

    /// <summary>Fixed short phrase for the reason code; never derived from stored rule text.</summary>
    public string Reason => Outcome switch
    {
        ProfileAutomationRuleOutcome.NoProcess => "未发现触发进程",
        ProfileAutomationRuleOutcome.AccessDenied => "进程访问被拒绝，已跳过",
        ProfileAutomationRuleOutcome.Timeout => "进程数据读取不可用，已跳过",
        ProfileAutomationRuleOutcome.InvalidCondition => "规则条件无效，已跳过",
        ProfileAutomationRuleOutcome.NoMatch => "条件未满足",
        ProfileAutomationRuleOutcome.Applied => "条件已命中",
        ProfileAutomationRuleOutcome.Rejected => "命中但未能应用",
        _ => "状态未知"
    };

    /// <summary>One-line, log/UI-safe summary: rule name, reason code, counters and the last evaluation time.</summary>
    public string Describe()
    {
        var text = $"规则「{CleanRuleName(RuleName) ?? "(未命名)"}」{Reason} · 命中 {MatchCount} · 跳过 {SkipCount}";
        if (RejectedCount > 0) text += $" · 未应用 {RejectedCount}";
        if (LastEvaluatedAt is { } at) text += $" · 最近评估 {FormatTime(at)}";
        return text;
    }

    /// <summary>Compact list label: reason code plus matched/skipped counters.</summary>
    public string CompactSummary => $"{Reason} · 命中 {MatchCount} · 跳过 {SkipCount}";

    /// <summary>Strips control characters and truncates, so a rule name can never break a log line.</summary>
    internal static string? CleanRuleName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        var safe = new string(trimmed.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (safe.Length == 0) return null;
        return safe.Length > 64 ? safe[..64] : safe;
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// Pure in-memory board that answers "which rule matched or was skipped, and why" for the settings window.
/// <para>
/// It is only fed with <see cref="ProfileAutomationMatchResult"/> values (plus apply/reject callbacks), so it can
/// never store a process path, a command line or a command-line pattern. Nothing here is persisted, and the board
/// never changes rule matching, sampling or the automation control chain.
/// </para>
/// </summary>
public sealed class ProfileAutomationStatusBoard
{
    /// <summary>Same upper bound as the settings file, so a corrupt caller cannot grow the board without limit.</summary>
    public const int MaxTrackedRules = 100;

    private readonly object _gate = new();
    private readonly Dictionary<string, ProfileAutomationRuleStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Snapshot ordered by rule name so the settings list stays stable between ticks.</summary>
    public IReadOnlyList<ProfileAutomationRuleStatus> Snapshot()
    {
        lock (_gate)
            return _statuses.Values.OrderBy(status => status.RuleName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ProfileAutomationRuleStatus? Find(string? ruleName)
    {
        var name = ProfileAutomationRuleStatus.CleanRuleName(ruleName);
        if (name is null) return null;
        lock (_gate) return _statuses.TryGetValue(name, out var status) ? status : null;
    }

    /// <summary>Records this tick's evaluation of one rule. Disabled rules keep no status, so stale rows disappear.</summary>
    public void RecordEvaluation(ProfileAutomationRule rule, ProfileAutomationContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);
        if (!rule.Enabled) return;
        RecordEvaluation(rule.Name, ProfileAutomationEngine.Explain(rule, context), now);
    }

    /// <summary>
    /// Records one evaluation result. Only the closed reason code is stored, never the matched details; the
    /// counters are driven exclusively by this method so <see cref="RecordApplied"/> and
    /// <see cref="RecordRejected"/> can refine the same tick without double counting.
    /// </summary>
    public void RecordEvaluation(string? ruleName, ProfileAutomationMatchResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        var matched = result.Matched;
        var skipped = !matched && result.IsSkip;
        Update(ruleName, Map(result), now, evaluationDelta: 1, matchDelta: matched ? 1 : 0,
            skipDelta: skipped ? 1 : 0, rejectDelta: 0, stampMatched: matched);
    }

    /// <summary>
    /// Refines the latest evaluation of a rule to "matched and applied by the automation chain". Adds no
    /// counters, so pairing it with <see cref="RecordEvaluation"/> for the same tick stays accurate.
    /// </summary>
    public void RecordApplied(string? ruleName, DateTimeOffset now) =>
        Update(ruleName, ProfileAutomationRuleOutcome.Applied, now, evaluationDelta: 0, matchDelta: 0,
            skipDelta: 0, rejectDelta: 0, stampMatched: true);

    /// <summary>Refines the latest evaluation to "matched but not applied" (missing profile or a rejected apply).</summary>
    public void RecordRejected(string? ruleName, DateTimeOffset now) =>
        Update(ruleName, ProfileAutomationRuleOutcome.Rejected, now, evaluationDelta: 0, matchDelta: 0,
            skipDelta: 0, rejectDelta: 1, stampMatched: false);

    /// <summary>Records the tick for every enabled rule; a disabled or null rule is skipped, never guessed.</summary>
    public void RecordEvaluations(IEnumerable<ProfileAutomationRule> rules, ProfileAutomationContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);
        foreach (var rule in rules)
        {
            if (rule is null) continue;
            RecordEvaluation(rule, context, now);
        }
    }

    /// <summary>
    /// Drops statuses whose rule no longer exists (renamed or deleted rule, older settings file), so the window
    /// never shows a row for a rule the user cannot see.
    /// </summary>
    public void Prune(IEnumerable<ProfileAutomationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var keep = rules
            .Where(rule => rule is not null)
            .Select(rule => ProfileAutomationRuleStatus.CleanRuleName(rule.Name))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var stale in _statuses.Keys.Where(name => !keep.Contains(name)).ToArray())
                _statuses.Remove(stale);
    }

    public void Reset()
    {
        lock (_gate) _statuses.Clear();
    }

    /// <summary>One-line summary of the whole board; safe to show in the window or a diagnostic event.</summary>
    public string DescribeSummary() => DescribeSummary(Snapshot());

    /// <summary>Same one-line summary for a snapshot held outside the board (settings window, diagnostics).</summary>
    public static string DescribeSummary(IEnumerable<ProfileAutomationRuleStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var list = statuses.Where(status => status is not null).ToArray();
        if (list.Length == 0) return "尚无自动规则运行记录。";
        var applied = list.Count(status => status.Outcome == ProfileAutomationRuleOutcome.Applied);
        var skipped = list.Count(status => status.IsSkip);
        var rejected = list.Count(status => status.Outcome == ProfileAutomationRuleOutcome.Rejected);
        var unmatched = list.Length - applied - skipped - rejected;
        var text = $"自动规则 {list.Length} 条：最近命中 {applied} · 跳过 {skipped} · 未命中 {unmatched}";
        if (rejected > 0) text += $" · 未应用 {rejected}";
        return text + "。";
    }

    /// <summary>
    /// Maps an engine match result onto the closed status vocabulary. Unreadable process data becomes
    /// <see cref="ProfileAutomationRuleOutcome.Timeout"/> (the sampler reports unreadable/timeout the same way),
    /// an unusable pattern becomes <see cref="ProfileAutomationRuleOutcome.InvalidCondition"/> and a name that was
    /// never seen becomes <see cref="ProfileAutomationRuleOutcome.NoProcess"/>.
    /// </summary>
    public static ProfileAutomationRuleOutcome Map(ProfileAutomationMatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Matched) return ProfileAutomationRuleOutcome.Applied;
        return result.Failure switch
        {
            ProfileAutomationMatchFailure.ProcessNameMismatch => ProfileAutomationRuleOutcome.NoProcess,
            ProfileAutomationMatchFailure.ProcessDetailsUnavailable => ProfileAutomationRuleOutcome.Timeout,
            ProfileAutomationMatchFailure.AccessDenied => ProfileAutomationRuleOutcome.AccessDenied,
            ProfileAutomationMatchFailure.ExecutablePathUnavailable => ProfileAutomationRuleOutcome.Timeout,
            ProfileAutomationMatchFailure.CommandLineUnavailable => ProfileAutomationRuleOutcome.Timeout,
            ProfileAutomationMatchFailure.InvalidCommandLinePattern => ProfileAutomationRuleOutcome.InvalidCondition,
            _ => ProfileAutomationRuleOutcome.NoMatch
        };
    }

    private void Update(
        string? ruleName,
        ProfileAutomationRuleOutcome outcome,
        DateTimeOffset now,
        int evaluationDelta,
        int matchDelta,
        int skipDelta,
        int rejectDelta,
        bool stampMatched)
    {
        var name = ProfileAutomationRuleStatus.CleanRuleName(ruleName);
        if (name is null) return;
        lock (_gate)
        {
            if (!_statuses.TryGetValue(name, out var current))
            {
                if (_statuses.Count >= MaxTrackedRules) return;
                current = new ProfileAutomationRuleStatus(name, outcome);
            }
            _statuses[name] = current with
            {
                Outcome = outcome,
                EvaluationCount = current.EvaluationCount + evaluationDelta,
                MatchCount = current.MatchCount + matchDelta,
                SkipCount = current.SkipCount + skipDelta,
                RejectedCount = current.RejectedCount + rejectDelta,
                LastEvaluatedAt = evaluationDelta > 0 ? now : current.LastEvaluatedAt,
                LastMatchedAt = stampMatched ? now : current.LastMatchedAt
            };
        }
    }
}
