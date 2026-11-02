using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MiniProToolkit.Core;

public enum ProfileAutomationTrigger { AcPower, BatteryPower, CpuLoadAbove, CpuLoadBelow, TimeRange, ProcessStarted, ProcessExited }

/// <summary>Why a rule did not match. Carries no process path or command-line text, so it is safe to log.</summary>
public enum ProfileAutomationMatchFailure
{
    None,
    ConditionNotMet,
    ProcessNameMismatch,
    ProcessDetailsUnavailable,
    AccessDenied,
    ExecutablePathUnavailable,
    ExecutablePathMismatch,
    CommandLineUnavailable,
    CommandLineMismatch,
    InvalidCommandLinePattern
}

public sealed record ProfileAutomationMatchResult(bool Matched, ProfileAutomationMatchFailure Failure = ProfileAutomationMatchFailure.None)
{
    public static ProfileAutomationMatchResult Match { get; } = new(true);

    /// <summary>True when the rule could not be evaluated safely (unreadable process data or unusable pattern).</summary>
    public bool IsSkip => !Matched && Failure is (ProfileAutomationMatchFailure.ProcessDetailsUnavailable
        or ProfileAutomationMatchFailure.AccessDenied
        or ProfileAutomationMatchFailure.ExecutablePathUnavailable
        or ProfileAutomationMatchFailure.CommandLineUnavailable
        or ProfileAutomationMatchFailure.InvalidCommandLinePattern);
}

/// <summary>Log-safe explanation for one rule. Never contains process paths, command lines or patterns.</summary>
public sealed record ProfileAutomationMatchDiagnostic(string RuleName, ProfileAutomationMatchFailure Failure)
{
    public bool IsSkip => new ProfileAutomationMatchResult(false, Failure).IsSkip;

    public string Describe() => $"自动规则「{RuleName}」未生效：{Failure switch
    {
        ProfileAutomationMatchFailure.ProcessNameMismatch => "进程名不匹配",
        ProfileAutomationMatchFailure.ProcessDetailsUnavailable => "无法读取进程路径/命令行，规则已跳过",
        ProfileAutomationMatchFailure.AccessDenied => "进程访问被拒绝，规则已跳过",
        ProfileAutomationMatchFailure.ExecutablePathUnavailable => "进程路径不可用，规则已跳过",
        ProfileAutomationMatchFailure.ExecutablePathMismatch => "进程路径不匹配",
        ProfileAutomationMatchFailure.CommandLineUnavailable => "进程命令行不可用，规则已跳过",
        ProfileAutomationMatchFailure.CommandLineMismatch => "命令行不匹配",
        ProfileAutomationMatchFailure.InvalidCommandLinePattern => "命令行正则无效，规则已跳过",
        _ => "条件未满足"
    }}";
}

/// <summary>
/// One process instance observed by the caller. <see cref="CommandLine"/> stays in memory only: it must never be
/// written to logs, telemetry or diagnostic events.
/// </summary>
public sealed record ProfileAutomationProcessInfo(
    string Name,
    string? ExecutablePath = null,
    string? CommandLine = null,
    bool AccessDenied = false)
{
    public static ProfileAutomationProcessInfo Denied(string name) => new(name, null, null, true);
}

/// <summary>Normalization and validation helpers shared by rule validation, snapshots and matching.</summary>
public static class ProfileAutomationProcessCondition
{
    private const int MaxPathLength = 260;
    private const int MaxPatternLength = 512;
    private const int MaxCachedPatterns = 128;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly char[] InvalidPathCharacters = ['*', '?', '"', '<', '>', '|'];
    private static readonly char[] InvalidProcessNameCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
    private static readonly ConcurrentDictionary<string, RegexEntry> PatternCache = new(StringComparer.Ordinal);

    /// <summary>Trims and drops a trailing ".exe" so "notepad", "notepad.exe" and "NOTEPAD" compare equal.</summary>
    public static string NormalizeProcessName(string? name)
    {
        var normalized = (name ?? string.Empty).Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? normalized[..^4] : normalized;
    }

    /// <summary>Trims quotes/whitespace, unifies separators to '\' and collapses duplicates; comparison stays case-insensitive.</summary>
    public static string NormalizeExecutablePath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim().Trim('"').Trim().Replace('/', '\\');
        if (normalized.Length == 0) return normalized;
        var prefix = string.Empty;
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            prefix = @"\\";
            normalized = normalized[2..];
        }
        else if (normalized.StartsWith('\\'))
        {
            normalized = normalized[1..];
        }
        return prefix + string.Join('\\', normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Accepts drive-rooted and UNC paths only; relative paths are rejected by rule validation.</summary>
    public static bool IsRootedExecutablePath(string? path)
    {
        var normalized = NormalizeExecutablePath(path);
        if (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\') return true;
        return normalized.StartsWith(@"\\", StringComparison.Ordinal) && normalized.Length > 2;
    }

    /// <summary>Whitespace-only optional values behave like "no restriction".</summary>
    public static string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool HasAnyProcessCondition(ProfileAutomationRule rule) =>
        CleanOptional(rule.ProcessPath) is not null ||
        CleanOptional(rule.ProcessCommandLine) is not null ||
        CleanOptional(rule.ProcessCommandLineRegex) is not null;

    /// <summary>True when the pattern compiles; invalid patterns are remembered so repeated validation stays cheap.</summary>
    public static bool IsValidCommandLinePattern(string? pattern) => ResolvePattern(pattern).Pattern is not null;

    /// <summary>Matches a rule pattern case-insensitively. Returns false when the pattern is unusable or times out.</summary>
    public static bool TryMatchCommandLineRegex(string? pattern, string commandLine, out bool matched)
    {
        matched = false;
        var entry = ResolvePattern(pattern);
        if (entry.Pattern is null) return false;
        try
        {
            matched = entry.Pattern.IsMatch(commandLine);
            return true;
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static void Validate(ProfileAutomationRule rule)
    {
        ValidateProcessName(rule.ProcessName);
        var path = CleanOptional(rule.ProcessPath);
        if (path is not null)
        {
            var normalized = NormalizeExecutablePath(path);
            if (normalized.Length is < 4 or > MaxPathLength) throw new ArgumentException("进程路径条件长度无效。");
            if (!IsRootedExecutablePath(normalized)) throw new ArgumentException("进程路径条件必须是完整路径（例如 C:\\App\\app.exe）。");
            if (normalized.IndexOfAny(InvalidPathCharacters) >= 0 || normalized.Any(char.IsControl))
                throw new ArgumentException("进程路径条件包含无效字符。");
            if (normalized.EndsWith('\\') || normalized.EndsWith('.')) throw new ArgumentException("进程路径条件必须指向可执行文件。");
        }
        var substring = CleanOptional(rule.ProcessCommandLine);
        var regex = CleanOptional(rule.ProcessCommandLineRegex);
        if (substring is not null && regex is not null) throw new ArgumentException("命令行条件只能选择子串或正则之一。");
        if (substring is not null)
        {
            if (substring.Length > MaxPatternLength) throw new ArgumentException("命令行子串条件过长。");
            if (substring.Any(char.IsControl)) throw new ArgumentException("命令行子串条件不能包含控制字符。");
        }
        if (regex is not null)
        {
            if (regex.Length > MaxPatternLength) throw new ArgumentException("命令行正则条件过长。");
            if (regex.Any(char.IsControl)) throw new ArgumentException("命令行正则条件不能包含控制字符。");
            // The pattern itself is never echoed: it may contain user secrets.
            if (!IsValidCommandLinePattern(regex)) throw new ArgumentException("命令行正则无效。");
        }
    }

    private static void ValidateProcessName(string? processName)
    {
        var name = processName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.IndexOfAny(InvalidProcessNameCharacters) >= 0)
            throw new ArgumentException("进程触发器需要有效的进程名（仅名称，不含路径）。");
    }

    private readonly record struct RegexEntry(Regex? Pattern);

    private static RegexEntry ResolvePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPatternLength) return default;
        if (PatternCache.TryGetValue(pattern, out var cached)) return cached;
        RegexEntry entry;
        try
        {
            entry = new RegexEntry(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));
        }
        catch (ArgumentException) { entry = default; }
        if (PatternCache.Count < MaxCachedPatterns) PatternCache.TryAdd(pattern, entry);
        return entry;
    }
}

public sealed record ProfileAutomationRule(
    string Name,
    bool Enabled,
    int Priority,
    string ProfileName,
    ProfileAutomationTrigger Trigger,
    double Threshold = 0,
    int StartMinute = 0,
    int EndMinute = 1440,
    int DebounceSeconds = 30,
    string? ProcessName = null,
    string? ProcessPath = null,
    string? ProcessCommandLine = null,
    string? ProcessCommandLineRegex = null)
{
    /// <summary>Log-safe description. Never contains the executable path, command line or command-line pattern.</summary>
    public string SafeDescription => string.Join(" · ", new[] { Name, Trigger.ToString(), $"P{Priority}", ProfileName, ProcessName }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    public void Validate(IReadOnlyCollection<string>? profileNames = null)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64) throw new ArgumentException("自动规则名称无效。");
        if (string.IsNullOrWhiteSpace(ProfileName)) throw new ArgumentException("自动规则缺少方案。");
        if (Priority is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(Priority));
        if (!Enum.IsDefined(Trigger)) throw new ArgumentException("自动规则触发条件无效。");
        if (!double.IsFinite(Threshold) ||
            (Trigger is ProfileAutomationTrigger.CpuLoadAbove or ProfileAutomationTrigger.CpuLoadBelow && Threshold is < 0 or > 100))
            throw new ArgumentOutOfRangeException(nameof(Threshold));
        if (StartMinute is < 0 or > 1439 || EndMinute is < 0 or > 1440) throw new ArgumentOutOfRangeException(nameof(StartMinute));
        if (Trigger == ProfileAutomationTrigger.TimeRange && StartMinute == EndMinute) throw new ArgumentException("时间范围的开始和结束不能相同。");
        if (Trigger is ProfileAutomationTrigger.ProcessStarted or ProfileAutomationTrigger.ProcessExited)
        {
            ProfileAutomationProcessCondition.Validate(this);
        }
        else if (ProfileAutomationProcessCondition.HasAnyProcessCondition(this))
        {
            throw new ArgumentException("只有进程启动/退出规则可以使用进程路径或命令行条件。");
        }
        if (DebounceSeconds is < 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(DebounceSeconds));
        if (profileNames is not null && !profileNames.Contains(ProfileName, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("自动规则引用了不存在的方案。");
    }
}

public sealed record ProfileAutomationSettings(bool Enabled, bool Paused, IReadOnlyList<ProfileAutomationRule> Rules)
{
    public static ProfileAutomationSettings Default => new(false, false, []);
    public void Validate(IReadOnlyCollection<string>? names = null)
    {
        if (Rules is null || Rules.Count > 100) throw new ArgumentException("自动规则数量无效。");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            if (rule is null) throw new ArgumentException("自动规则不能为空。");
            rule.Validate(names);
            if (!keys.Add(rule.Name)) throw new ArgumentException("自动规则名称重复。");
        }
    }
}

public sealed class ProfileAutomationStore
{
    private const int CurrentVersion = 1;
    private const int MaxReportedRuleNames = 5;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly HashSet<string> KnownTriggerNames = Enum.GetNames<ProfileAutomationTrigger>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    public ProfileAutomationStore(string? path = null) => Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniProToolkit", "automation.json");
    public string Path { get; }
    public ProfileAutomationSettings LoadOrDefault(IReadOnlyCollection<string> names, out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path)) return ProfileAutomationSettings.Default;
            if (JsonNode.Parse(File.ReadAllText(Path)) is not JsonObject root) throw new InvalidDataException("自动规则文件结构无效。");
            // Only a trigger this build does not know is dropped rule-by-rule; every other
            // validation problem still rejects the whole file, so no unsafe rule is ever loaded.
            // The reason is reported without echoing any stored path, command line or pattern.
            var skipped = RemoveUnsupportedTriggers(root);
            var doc = root.Deserialize<Document>(Options) ?? throw new InvalidDataException("自动规则文件为空。");
            // A missing version means the file predates version stamping: read it as the current
            // shape. Older supported versions stay readable, future ones still fall back.
            if (doc.Version is { } version && version is < 1 or > CurrentVersion) throw new InvalidDataException("不支持的自动规则版本。");
            var settings = new ProfileAutomationSettings(doc.Enabled, doc.Paused, doc.Rules);
            // Structural validation runs first and without the profile list, so every other problem
            // (bad priority, duplicate name, unusable condition, bad trigger payload...) keeps rejecting
            // the whole file even when an orphaned rule is present. Only a rule that is valid apart from
            // referencing a deleted profile is excluded, and only that single rule.
            settings.Validate();
            var kept = new List<ProfileAutomationRule>(settings.Rules.Count);
            var orphans = new List<ProfileAutomationRule>();
            foreach (var rule in settings.Rules)
            {
                // A null profile list means "do not check profiles" (same as rule validation): every rule
                // is kept, so this path can never turn into a silent mass-drop.
                if (names is null || names.Contains(rule.ProfileName, StringComparer.OrdinalIgnoreCase)) kept.Add(rule);
                else orphans.Add(rule);
            }
            if (orphans.Count == 0)
            {
                settings.Validate(names);
                if (skipped.Count > 0) warning = DescribeSkippedTriggers(skipped);
                return settings;
            }
            // Deleting one profile must never hand control to the next matching rule, so the surviving
            // rules are returned with every field (and Paused) intact but automation forced off until the
            // user reviews the file. Nothing is written back here: the stored file stays untouched.
            var orphanNames = orphans.Select(rule => SanitizeName(rule.Name)).ToArray();
            warning = skipped.Count > 0
                ? DescribeSkippedTriggersAndOrphanProfiles(skipped, orphanNames, kept.Count)
                : DescribeOrphanProfiles(orphanNames, kept.Count);
            return new ProfileAutomationSettings(false, settings.Paused, kept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { warning = $"自动规则已回退默认：{ex.Message}"; return ProfileAutomationSettings.Default; }
    }

    /// <summary>
    /// Drops rules whose stored trigger name/number is not a <see cref="ProfileAutomationTrigger"/> this build
    /// understands, so one rule written by a newer build cannot discard every other rule. Returns the safe
    /// display names of the dropped rules (never a path, command line or pattern).
    /// </summary>
    private static List<string> RemoveUnsupportedTriggers(JsonObject root)
    {
        var skipped = new List<string>();
        if (FindProperty(root, "rules") is not JsonArray rules) return skipped;
        // OfType keeps the JsonObject narrowing all the way into Select: a fresh cast there would
        // let nullable analysis (and a future refactor) treat the rule as possibly null.
        var unsupported = rules
            .OfType<JsonObject>()
            .Where(IsUnsupportedTrigger)
            .Select(rule => (Node: (JsonNode)rule, Name: SafeRuleName(rule)))
            .ToArray();
        foreach (var (node, name) in unsupported)
        {
            rules.Remove(node);
            skipped.Add(name);
        }
        return skipped;
    }

    private static bool IsUnsupportedTrigger(JsonObject rule)
    {
        var trigger = FindProperty(rule, "trigger");
        if (trigger is null) return false; // absent trigger keeps the previous default-value behavior
        if (trigger is not JsonValue value) return true;
        if (value.TryGetValue<string>(out var name)) return !KnownTriggerNames.Contains(name);
        if (value.TryGetValue<int>(out var number)) return !Enum.IsDefined((ProfileAutomationTrigger)number);
        return true;
    }

    private static JsonNode? FindProperty(JsonObject obj, string name)
    {
        foreach (var property in obj)
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string SafeRuleName(JsonObject rule) => SanitizeName(
        FindProperty(rule, "name") is JsonValue value && value.TryGetValue<string>(out var name) ? name : null);

    /// <summary>Rule display names only: control characters are stripped and the text is truncated, so a stored
    /// name can never break or bloat a log line. Never applied to paths, command lines or patterns.</summary>
    private static string SanitizeName(string? text)
    {
        var safe = new string((text ?? string.Empty).Trim().Where(c => !char.IsControl(c)).Take(64).ToArray());
        return safe.Length == 0 ? "(未命名)" : safe;
    }

    private static string DescribeSkippedTriggers(IReadOnlyList<string> names) =>
        $"自动规则已跳过 {names.Count} 条触发条件不受支持的规则（{ListNames(names)}），其余规则已正常加载。";

    private static string DescribeOrphanProfiles(IReadOnlyList<string> names, int keptCount) =>
        $"自动规则已跳过 {names.Count} 条引用不存在方案的规则（{ListNames(names)}），其余 {keptCount} 条规则已保留，但自动切换已关闭，请复核后重新启用。";

    private static string DescribeSkippedTriggersAndOrphanProfiles(
        IReadOnlyList<string> skippedTriggers, IReadOnlyList<string> orphanNames, int keptCount) =>
        $"自动规则已跳过 {skippedTriggers.Count} 条触发条件不受支持的规则（{ListNames(skippedTriggers)}）和 {orphanNames.Count} 条引用不存在方案的规则（{ListNames(orphanNames)}），其余 {keptCount} 条规则已保留，但自动切换已关闭，请复核后重新启用。";

    private static string ListNames(IReadOnlyList<string> names)
    {
        var listed = string.Join("、", names.Take(MaxReportedRuleNames));
        var more = names.Count > MaxReportedRuleNames ? $" 等 {names.Count} 条" : string.Empty;
        return listed + more;
    }
    public void Save(ProfileAutomationSettings settings, IReadOnlyCollection<string> names)
    {
        settings.Validate(names);
        var directory = System.IO.Path.GetDirectoryName(Path)!; Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(CurrentVersion, settings.Enabled, settings.Paused, settings.Rules.ToList()), Options));
            if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true);
            File.Move(temporary, Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record Document(int? Version, bool Enabled, bool Paused, List<ProfileAutomationRule> Rules);
}

public sealed record ProfileAutomationContext(
    bool? AcPower,
    double? CpuLoadPercent,
    DateTimeOffset LocalTime,
    IReadOnlySet<string>? StartedProcesses = null,
    IReadOnlySet<string>? ExitedProcesses = null,
    IReadOnlyList<ProfileAutomationProcessInfo>? StartedProcessDetails = null,
    IReadOnlyList<ProfileAutomationProcessInfo>? ExitedProcessDetails = null);

public static class ProfileAutomationEngine
{
    public static ProfileAutomationRule? Select(ProfileAutomationSettings settings, ProfileAutomationContext context, DateTimeOffset now, string? currentProfile)
    {
        if (!settings.Enabled || settings.Paused) return null;
        var selected = settings.Rules.Where(r => r.Enabled && Matches(r, context))
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return selected is not null && !string.Equals(selected.ProfileName, currentProfile, StringComparison.OrdinalIgnoreCase) ? selected : null;
    }

    public static bool Matches(ProfileAutomationRule rule, ProfileAutomationContext context) => Explain(rule, context).Matched;

    /// <summary>Evaluates a rule and reports a log-safe failure code; never exposes process paths or command lines.</summary>
    public static ProfileAutomationMatchResult Explain(ProfileAutomationRule rule, ProfileAutomationContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);
        return rule.Trigger switch
        {
            ProfileAutomationTrigger.ProcessStarted => ExplainProcess(context.StartedProcesses, context.StartedProcessDetails, rule),
            ProfileAutomationTrigger.ProcessExited => ExplainProcess(context.ExitedProcesses, context.ExitedProcessDetails, rule),
            _ => MatchesCondition(rule, context) ? ProfileAutomationMatchResult.Match : new ProfileAutomationMatchResult(false, ProfileAutomationMatchFailure.ConditionNotMet)
        };
    }

    /// <summary>Diagnostics for every enabled process rule that did not match; filter with <see cref="ProfileAutomationMatchDiagnostic.IsSkip"/> before logging.</summary>
    public static IReadOnlyList<ProfileAutomationMatchDiagnostic> ExplainProcessRules(IEnumerable<ProfileAutomationRule> rules, ProfileAutomationContext context)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);
        var diagnostics = new List<ProfileAutomationMatchDiagnostic>();
        foreach (var rule in rules)
        {
            if (rule is null || !rule.Enabled || rule.Trigger is not (ProfileAutomationTrigger.ProcessStarted or ProfileAutomationTrigger.ProcessExited)) continue;
            var result = Explain(rule, context);
            if (!result.Matched) diagnostics.Add(new ProfileAutomationMatchDiagnostic(rule.Name, result.Failure));
        }
        return diagnostics;
    }

    /// <summary>Only the diagnostics caused by unreadable process data or unusable patterns.</summary>
    public static IReadOnlyList<ProfileAutomationMatchDiagnostic> ExplainSkippedProcessRules(IEnumerable<ProfileAutomationRule> rules, ProfileAutomationContext context) =>
        ExplainProcessRules(rules, context).Where(diagnostic => diagnostic.IsSkip).ToArray();

    private static bool MatchesCondition(ProfileAutomationRule rule, ProfileAutomationContext context) => rule.Trigger switch
    {
        ProfileAutomationTrigger.AcPower => context.AcPower == true,
        ProfileAutomationTrigger.BatteryPower => context.AcPower == false,
        ProfileAutomationTrigger.CpuLoadAbove => context.CpuLoadPercent is { } high && high >= rule.Threshold,
        ProfileAutomationTrigger.CpuLoadBelow => context.CpuLoadPercent is { } low && low <= rule.Threshold,
        ProfileAutomationTrigger.TimeRange => rule.StartMinute <= rule.EndMinute
            ? context.LocalTime.Hour * 60 + context.LocalTime.Minute >= rule.StartMinute && context.LocalTime.Hour * 60 + context.LocalTime.Minute < rule.EndMinute
            : context.LocalTime.Hour * 60 + context.LocalTime.Minute >= rule.StartMinute || context.LocalTime.Hour * 60 + context.LocalTime.Minute < rule.EndMinute,
        _ => false
    };

    private static ProfileAutomationMatchResult ExplainProcess(
        IReadOnlySet<string>? names,
        IReadOnlyList<ProfileAutomationProcessInfo>? details,
        ProfileAutomationRule rule)
    {
        var requested = ProfileAutomationProcessCondition.CleanOptional(rule.ProcessName);
        if (requested is null) return new ProfileAutomationMatchResult(false, ProfileAutomationMatchFailure.ProcessNameMismatch);
        var normalizedName = ProfileAutomationProcessCondition.NormalizeProcessName(requested);
        var nameMatches = names is not null && names.Any(name =>
            string.Equals(ProfileAutomationProcessCondition.NormalizeProcessName(name), normalizedName, StringComparison.OrdinalIgnoreCase));

        if (!ProfileAutomationProcessCondition.HasAnyProcessCondition(rule))
            return nameMatches ? ProfileAutomationMatchResult.Match : new ProfileAutomationMatchResult(false, ProfileAutomationMatchFailure.ProcessNameMismatch);

        // Extra conditions need instance data. Without it the rule is skipped instead of guessed.
        if (details is null)
            return new ProfileAutomationMatchResult(false, nameMatches ? ProfileAutomationMatchFailure.ProcessDetailsUnavailable : ProfileAutomationMatchFailure.ProcessNameMismatch);

        var pathCondition = ProfileAutomationProcessCondition.CleanOptional(rule.ProcessPath) is { } rawPath
            ? ProfileAutomationProcessCondition.NormalizeExecutablePath(rawPath)
            : null;
        var substring = ProfileAutomationProcessCondition.CleanOptional(rule.ProcessCommandLine);
        var pattern = ProfileAutomationProcessCondition.CleanOptional(rule.ProcessCommandLineRegex);
        var considered = false;
        var failure = ProfileAutomationMatchFailure.None;
        foreach (var info in details)
        {
            if (info is null || !string.Equals(
                    ProfileAutomationProcessCondition.NormalizeProcessName(info.Name), normalizedName, StringComparison.OrdinalIgnoreCase))
                continue;
            considered = true;
            var candidate = ExplainDetail(info, pathCondition, substring, pattern);
            if (candidate == ProfileAutomationMatchFailure.None) return ProfileAutomationMatchResult.Match;
            failure = Prefer(failure, candidate);
        }
        if (!considered)
            return new ProfileAutomationMatchResult(false, nameMatches ? ProfileAutomationMatchFailure.ProcessDetailsUnavailable : ProfileAutomationMatchFailure.ProcessNameMismatch);
        return new ProfileAutomationMatchResult(false, failure == ProfileAutomationMatchFailure.None ? ProfileAutomationMatchFailure.ProcessNameMismatch : failure);
    }

    private static ProfileAutomationMatchFailure ExplainDetail(
        ProfileAutomationProcessInfo info,
        string? normalizedPath,
        string? commandLineSubstring,
        string? commandLinePattern)
    {
        if (info.AccessDenied) return ProfileAutomationMatchFailure.AccessDenied;
        if (normalizedPath is not null)
        {
            if (string.IsNullOrWhiteSpace(info.ExecutablePath)) return ProfileAutomationMatchFailure.ExecutablePathUnavailable;
            if (!string.Equals(
                    ProfileAutomationProcessCondition.NormalizeExecutablePath(info.ExecutablePath), normalizedPath, StringComparison.OrdinalIgnoreCase))
                return ProfileAutomationMatchFailure.ExecutablePathMismatch;
        }
        if (commandLineSubstring is null && commandLinePattern is null) return ProfileAutomationMatchFailure.None;
        if (info.CommandLine is null) return ProfileAutomationMatchFailure.CommandLineUnavailable;
        if (commandLineSubstring is not null)
            return info.CommandLine.Contains(commandLineSubstring, StringComparison.OrdinalIgnoreCase)
                ? ProfileAutomationMatchFailure.None
                : ProfileAutomationMatchFailure.CommandLineMismatch;
        return ProfileAutomationProcessCondition.TryMatchCommandLineRegex(commandLinePattern, info.CommandLine, out var matched)
            ? matched ? ProfileAutomationMatchFailure.None : ProfileAutomationMatchFailure.CommandLineMismatch
            : ProfileAutomationMatchFailure.InvalidCommandLinePattern;
    }

    private static ProfileAutomationMatchFailure Prefer(ProfileAutomationMatchFailure current, ProfileAutomationMatchFailure candidate) =>
        Rank(candidate) > Rank(current) ? candidate : current;

    private static int Rank(ProfileAutomationMatchFailure failure) => failure switch
    {
        ProfileAutomationMatchFailure.InvalidCommandLinePattern => 6,
        ProfileAutomationMatchFailure.AccessDenied => 5,
        ProfileAutomationMatchFailure.ProcessDetailsUnavailable => 4,
        ProfileAutomationMatchFailure.ExecutablePathUnavailable => 3,
        ProfileAutomationMatchFailure.CommandLineUnavailable => 2,
        ProfileAutomationMatchFailure.ExecutablePathMismatch => 1,
        ProfileAutomationMatchFailure.CommandLineMismatch => 1,
        _ => 0
    };
}

/// <summary>Tracks process start/exit transitions without persisting process inventory or command lines.</summary>
public sealed class ProfileAutomationProcessTracker
{
    private readonly Dictionary<string, bool> _wasRunning = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exited = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ProfileAutomationProcessInfo>> _lastKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ProfileAutomationProcessInfo>> _startedDetails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ProfileAutomationProcessInfo>> _exitedDetails = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        _wasRunning.Clear();
        _started.Clear();
        _exited.Clear();
        _lastKnown.Clear();
        _startedDetails.Clear();
        _exitedDetails.Clear();
    }

    public void ClearTransitions()
    {
        _started.Clear();
        _exited.Clear();
        _startedDetails.Clear();
        _exitedDetails.Clear();
    }

    /// <summary>Name-only tracking kept for callers that cannot read executable paths or command lines.</summary>
    public ProfileAutomationContext AddProcessStates(ProfileAutomationContext context, IEnumerable<string> watchedNames, IEnumerable<string> runningNames) =>
        AddProcessStates(context, watchedNames, runningNames.Select(static name => new ProfileAutomationProcessInfo(name)).ToArray(), includeDetails: false);

    /// <summary>
    /// Instance-aware tracking used by path/command-line conditions. Details stay in memory only. Kept as a separate
    /// method name so existing name-only call sites never become overload-ambiguous.
    /// </summary>
    public ProfileAutomationContext AddProcessInstances(ProfileAutomationContext context, IEnumerable<string> watchedNames, IReadOnlyList<ProfileAutomationProcessInfo> runningProcesses) =>
        AddProcessStates(context, watchedNames, runningProcesses, includeDetails: true);

    private ProfileAutomationContext AddProcessStates(
        ProfileAutomationContext context,
        IEnumerable<string> watchedNames,
        IReadOnlyList<ProfileAutomationProcessInfo> runningProcesses,
        bool includeDetails)
    {
        var watched = watchedNames.Select(ProfileAutomationProcessCondition.NormalizeProcessName)
            .Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = new Dictionary<string, List<ProfileAutomationProcessInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in runningProcesses)
        {
            if (info is null) continue;
            var name = ProfileAutomationProcessCondition.NormalizeProcessName(info.Name);
            if (name.Length == 0) continue;
            if (!running.TryGetValue(name, out var instances)) running[name] = instances = [];
            instances.Add(info);
        }
        foreach (var stale in _wasRunning.Keys.Where(name => !watched.Contains(name)).ToArray())
        {
            _wasRunning.Remove(stale);
            _started.Remove(stale);
            _exited.Remove(stale);
            _lastKnown.Remove(stale);
            _startedDetails.Remove(stale);
            _exitedDetails.Remove(stale);
        }
        var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in watched)
        {
            var isRunning = running.ContainsKey(name);
            if (_wasRunning.TryGetValue(name, out var wasRunning))
            {
                if (!wasRunning && isRunning)
                {
                    started.Add(name);
                    _started.Add(name);
                    _startedDetails[name] = running[name].ToList();
                }
                if (wasRunning && !isRunning)
                {
                    exited.Add(name);
                    _exited.Add(name);
                    _exitedDetails[name] = _lastKnown.TryGetValue(name, out var previous) ? previous.ToList() : [];
                }
            }
            _wasRunning[name] = isRunning;
            if (isRunning) _lastKnown[name] = running[name].ToList();
            else _lastKnown.Remove(name);
        }
        started.UnionWith(_started);
        exited.UnionWith(_exited);
        if (!includeDetails) return context with { StartedProcesses = started, ExitedProcesses = exited };

        var startedDetails = new List<ProfileAutomationProcessInfo>();
        foreach (var name in started)
        {
            if (_startedDetails.TryGetValue(name, out var captured)) startedDetails.AddRange(captured);
            else if (_lastKnown.TryGetValue(name, out var current)) startedDetails.AddRange(current);
        }
        var exitedDetails = new List<ProfileAutomationProcessInfo>();
        foreach (var name in exited)
            if (_exitedDetails.TryGetValue(name, out var captured)) exitedDetails.AddRange(captured);

        return context with
        {
            StartedProcesses = started,
            ExitedProcesses = exited,
            StartedProcessDetails = startedDetails,
            ExitedProcessDetails = exitedDetails
        };
    }
}

/// <summary>Requires a condition to remain selected for its debounce duration and emits it once.</summary>
public sealed class ProfileAutomationDebouncer
{
    private string? _candidateName;
    private DateTimeOffset _candidateSince;
    private bool _emitted;

    public ProfileAutomationRule? Evaluate(ProfileAutomationSettings settings, ProfileAutomationContext context, DateTimeOffset now, string? currentProfile)
    {
        if (now < _candidateSince && _candidateName is not null) Reset();
        settings.Validate();
        var rule = ProfileAutomationEngine.Select(settings, context, now, currentProfile);
        if (rule is null)
        {
            Reset();
            return null;
        }
        if (!string.Equals(rule.Name, _candidateName, StringComparison.OrdinalIgnoreCase))
        {
            _candidateName = rule.Name;
            _candidateSince = now;
            _emitted = false;
        }
        var debounce = rule.Trigger is ProfileAutomationTrigger.ProcessStarted or ProfileAutomationTrigger.ProcessExited
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(rule.DebounceSeconds);
        if (_emitted || now - _candidateSince < debounce) return null;
        _emitted = true;
        return rule;
    }

    public void Reset() { _candidateName = null; _candidateSince = default; _emitted = false; }
}
