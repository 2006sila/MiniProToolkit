using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Privacy and behaviour boundaries of <see cref="ProfileAutomationStatusBoard"/>: the visible rule status uses
/// only the closed <see cref="ProfileAutomationRuleOutcome"/> vocabulary plus counters, never a process path,
/// command line or command-line pattern, and it never changes what the engine matches.
/// </summary>
public sealed class ProfileAutomationStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);

    private const string RuleName = "gaming";
    private const string SecretPath = @"C:\synthetic\someone\SecretFolder\player.exe";
    private const string SecretCommandLine = "player.exe --token=tok-9f3c-SECRET";
    private const string SecretPattern = @"--token=(tok-\w+-SECRET|other)";

    private static ProfileAutomationRule ProcessRule(string name = RuleName, bool enabled = true) =>
        new(name, enabled, 10, "Performance", ProfileAutomationTrigger.ProcessStarted,
            ProcessName: "player",
            ProcessPath: SecretPath,
            ProcessCommandLineRegex: SecretPattern);

    private static ProfileAutomationContext Context(
        IReadOnlySet<string>? startedNames = null,
        IReadOnlyList<ProfileAutomationProcessInfo>? startedDetails = null) =>
        new(null, null, Now, startedNames, null, startedDetails, null);

    private static IReadOnlySet<string> Names(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<ProfileAutomationRuleStatus> Track(
        IReadOnlyList<ProfileAutomationRule> rules,
        ProfileAutomationContext? context = null)
    {
        var board = new ProfileAutomationStatusBoard();
        board.RecordEvaluations(rules, context ?? Context(), Now);
        return board.Snapshot();
    }

    private static IEnumerable<string> AllBoardText(ProfileAutomationStatusBoard board) =>
        board.Snapshot()
            .SelectMany(status => new[] { status.RuleName, status.Reason, status.Describe(), status.CompactSummary })
            .Append(board.DescribeSummary());

    [Fact]
    public void EveryEngineFailureMapsToOneClosedOutcome()
    {
        Assert.Equal(ProfileAutomationRuleOutcome.Applied, ProfileAutomationStatusBoard.Map(ProfileAutomationMatchResult.Match));

        var expected = new Dictionary<ProfileAutomationMatchFailure, ProfileAutomationRuleOutcome>
        {
            [ProfileAutomationMatchFailure.ConditionNotMet] = ProfileAutomationRuleOutcome.NoMatch,
            [ProfileAutomationMatchFailure.ProcessNameMismatch] = ProfileAutomationRuleOutcome.NoProcess,
            [ProfileAutomationMatchFailure.ProcessDetailsUnavailable] = ProfileAutomationRuleOutcome.Timeout,
            [ProfileAutomationMatchFailure.AccessDenied] = ProfileAutomationRuleOutcome.AccessDenied,
            [ProfileAutomationMatchFailure.ExecutablePathUnavailable] = ProfileAutomationRuleOutcome.Timeout,
            [ProfileAutomationMatchFailure.ExecutablePathMismatch] = ProfileAutomationRuleOutcome.NoMatch,
            [ProfileAutomationMatchFailure.CommandLineUnavailable] = ProfileAutomationRuleOutcome.Timeout,
            [ProfileAutomationMatchFailure.CommandLineMismatch] = ProfileAutomationRuleOutcome.NoMatch,
            [ProfileAutomationMatchFailure.InvalidCommandLinePattern] = ProfileAutomationRuleOutcome.InvalidCondition
        };
        // Every failure the engine can report is covered, so a new failure cannot silently lose its visible reason.
        Assert.Equal(Enum.GetValues<ProfileAutomationMatchFailure>().Length, expected.Count + 1);
        foreach (var (failure, outcome) in expected)
            Assert.Equal(outcome, ProfileAutomationStatusBoard.Map(new ProfileAutomationMatchResult(false, failure)));
    }

    [Fact]
    public void AccessDeniedEvaluationIsVisibleAsSkipWithCounters()
    {
        var board = new ProfileAutomationStatusBoard();
        var rule = ProcessRule();
        var context = Context(Names("player"), [ProfileAutomationProcessInfo.Denied("player")]);

        board.RecordEvaluation(rule, context, Now);
        board.RecordEvaluation(rule, context, Now.AddSeconds(5));

        var status = Assert.Single(board.Snapshot());
        Assert.Equal(RuleName, status.RuleName);
        Assert.Equal(ProfileAutomationRuleOutcome.AccessDenied, status.Outcome);
        Assert.True(status.IsSkip);
        Assert.Equal(2, status.EvaluationCount);
        Assert.Equal(2, status.SkipCount);
        Assert.Equal(0, status.MatchCount);
        Assert.Equal(Now.AddSeconds(5), status.LastEvaluatedAt);
        Assert.Null(status.LastMatchedAt);
        Assert.Contains("跳过", status.Describe());
        Assert.DoesNotContain("player.exe", status.Describe());
    }

    [Fact]
    public void MissingProcessInstanceIsReportedAsNoProcessWithoutLeakingTheProcessName()
    {
        var board = new ProfileAutomationStatusBoard();
        board.RecordEvaluation(ProcessRule(), Context(Names("other")), Now);

        var status = Assert.Single(board.Snapshot());
        Assert.Equal(ProfileAutomationRuleOutcome.NoProcess, status.Outcome);
        Assert.False(status.IsSkip);
        Assert.Equal(1, status.EvaluationCount);
        Assert.Equal(0, status.SkipCount);
        Assert.DoesNotContain("player", status.Describe());
        Assert.DoesNotContain("other", status.Describe());
    }

    [Fact]
    public void UnreadableCommandLineIsVisibleAsTimeoutSkip()
    {
        var board = new ProfileAutomationStatusBoard();
        var rule = ProcessRule() with { ProcessPath = null };
        board.RecordEvaluation(rule, Context(Names("player"), [new("player", @"C:\A\player.exe", null)]), Now);

        var status = Assert.Single(board.Snapshot());
        Assert.Equal(ProfileAutomationRuleOutcome.Timeout, status.Outcome);
        Assert.True(status.IsSkip);
        Assert.Equal(1, status.SkipCount);
    }

    [Fact]
    public void UnusablePatternIsInvalidConditionAndNeverEchoesThePattern()
    {
        var board = new ProfileAutomationStatusBoard();
        var rule = ProcessRule() with { ProcessPath = null, ProcessCommandLineRegex = "[unclosed-SECRET" };
        board.RecordEvaluation(rule, Context(Names("player"), [new("player", @"C:\A\player.exe", "player.exe --x")]), Now);

        var status = Assert.Single(board.Snapshot());
        Assert.Equal(ProfileAutomationRuleOutcome.InvalidCondition, status.Outcome);
        Assert.True(status.IsSkip);
        Assert.DoesNotContain("unclosed", status.Describe());

        var error = Assert.Throws<ArgumentException>(() => rule.Validate(["Performance"]));
        Assert.DoesNotContain("unclosed", error.Message);
    }

    [Fact]
    public void MatchedEvaluationIsVisibleAndAppliedRefinesTheSameTickWithoutDoubleCounting()
    {
        var board = new ProfileAutomationStatusBoard();
        var rule = ProcessRule();
        var matching = Context(Names("player"), [new("player", SecretPath, SecretCommandLine)]);

        board.RecordEvaluation(rule, matching, Now);
        var matched = Assert.Single(board.Snapshot());
        Assert.Equal(ProfileAutomationRuleOutcome.Applied, matched.Outcome);
        Assert.Equal(1, matched.EvaluationCount);
        Assert.Equal(1, matched.MatchCount);
        Assert.Equal(Now, matched.LastMatchedAt);

        board.RecordApplied(rule.Name, Now);
        var applied = Assert.Single(board.Snapshot());
        Assert.Equal(ProfileAutomationRuleOutcome.Applied, applied.Outcome);
        Assert.Equal(1, applied.EvaluationCount);
        Assert.Equal(1, applied.MatchCount);
        Assert.Equal(0, applied.SkipCount);
        Assert.Contains("命中", applied.Describe());
    }

    [Fact]
    public void RejectedRuleIsVisibleAsMatchedButNotApplied()
    {
        var board = new ProfileAutomationStatusBoard();
        var rule = ProcessRule();
        board.RecordEvaluation(rule, Context(Names("player"), [new("player", SecretPath, SecretCommandLine)]), Now);
        board.RecordRejected(rule.Name, Now.AddSeconds(5));

        var status = Assert.Single(board.Snapshot());
        Assert.Equal(ProfileAutomationRuleOutcome.Rejected, status.Outcome);
        Assert.False(status.IsSkip);
        Assert.Equal(1, status.RejectedCount);
        Assert.Equal(1, status.MatchCount);
        Assert.Contains("未应用", status.Describe());
    }

    [Fact]
    public void BoardTextNeverContainsPathCommandLineOrPattern()
    {
        var board = new ProfileAutomationStatusBoard();
        var rule = ProcessRule();
        board.RecordEvaluations(
        [
            rule,
            rule with { Name = "path-mismatch" },
            rule with { Name = "denied", ProcessCommandLine = SecretCommandLine, ProcessCommandLineRegex = null },
            rule with { Name = "unavailable", ProcessCommandLine = null, ProcessCommandLineRegex = null, ProcessPath = null },
            rule with { Name = "invalid", ProcessCommandLineRegex = "[unclosed-SECRET" }
        ], Context(Names("player"), [new("player", @"C:\Other\player.exe", SecretCommandLine)]), Now);

        var texts = AllBoardText(board).ToArray();
        Assert.Equal(5, board.Snapshot().Count);
        foreach (var text in texts)
        {
            Assert.DoesNotContain(SecretPath, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretCommandLine, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretPattern, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tok-9f3c-SECRET", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecretFolder", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--token", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unclosed", text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("自动规则 5 条", board.DescribeSummary());
    }

    [Fact]
    public void DisabledAndNullRulesAreNotTracked()
    {
        var board = new ProfileAutomationStatusBoard();
        board.RecordEvaluations([ProcessRule(enabled: false), null!, ProcessRule(name: "live")],
            Context(Names("player"), [ProfileAutomationProcessInfo.Denied("player")]), Now);

        var status = Assert.Single(board.Snapshot());
        Assert.Equal("live", status.RuleName);
        Assert.Equal(ProfileAutomationRuleOutcome.AccessDenied, status.Outcome);
    }

    [Fact]
    public void SnapshotIsNameOrderedAndCappedLikeTheSettingsFile()
    {
        var board = new ProfileAutomationStatusBoard();
        for (var i = ProfileAutomationStatusBoard.MaxTrackedRules; i >= 0; i--)
            board.RecordEvaluation($"rule-{i:000}", new ProfileAutomationMatchResult(false, ProfileAutomationMatchFailure.ConditionNotMet), Now);

        var snapshot = board.Snapshot();
        Assert.Equal(ProfileAutomationStatusBoard.MaxTrackedRules, snapshot.Count);
        var names = snapshot.Select(status => status.RuleName).ToArray();
        Assert.Equal(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), names);
        // The first rule recorded fits, the 101st is refused: the cap keeps the earliest tracked rules.
        Assert.Equal("rule-001", names[0]);
        Assert.NotNull(board.Find("rule-100"));
        Assert.Null(board.Find("rule-000"));
        Assert.Null(board.Find("  "));
        Assert.NotNull(board.Find("RULE-001")); // case-insensitive like rule names
    }

    [Fact]
    public void PruneFollowsRenamedOrRemovedRulesAndResetClearsTheBoard()
    {
        var board = new ProfileAutomationStatusBoard();
        board.RecordEvaluation("old", new ProfileAutomationMatchResult(false, ProfileAutomationMatchFailure.ConditionNotMet), Now);
        board.RecordEvaluation("kept", ProfileAutomationMatchResult.Match, Now);

        // A settings save that renamed or deleted a rule must not leave a status row for a rule that no longer exists.
        board.Prune([new("kept", true, 1, "Performance", ProfileAutomationTrigger.AcPower)]);
        Assert.Equal("kept", Assert.Single(board.Snapshot()).RuleName);

        board.Prune([]);
        Assert.Empty(board.Snapshot());
        Assert.Equal("尚无自动规则运行记录。", board.DescribeSummary());

        board.RecordEvaluation("kept", ProfileAutomationMatchResult.Match, Now);
        board.Reset();
        Assert.Empty(board.Snapshot());
    }

    [Fact]
    public void PruneDropsTheRowOfARuleThatIsStillListedButDisabled()
    {
        var rule = ProcessRule() with { ProcessPath = null, ProcessCommandLineRegex = null };
        var board = new ProfileAutomationStatusBoard();
        board.RecordEvaluations([rule], Context(Names("player")), Now);
        Assert.Equal(ProfileAutomationRuleOutcome.Applied, Assert.Single(board.Snapshot()).Outcome);

        // The sampling loop prunes against the rules it still evaluates, so disabling (not deleting) a rule drops
        // its stale row instead of leaving a "condition met" line for a rule the engine no longer looks at.
        board.Prune(new[] { rule with { Enabled = false } }.Where(candidate => candidate.Enabled));
        Assert.Empty(board.Snapshot());
        Assert.Equal("尚无自动规则运行记录。", board.DescribeSummary());
    }

    [Fact]
    public void EmptyOrUnsafeRuleNamesAreNotStored()
    {
        var board = new ProfileAutomationStatusBoard();
        board.RecordEvaluation("   ", ProfileAutomationMatchResult.Match, Now);
        board.RecordEvaluation(null, ProfileAutomationMatchResult.Match, Now);
        Assert.Empty(board.Snapshot());

        board.RecordEvaluation("bad\u0001name", ProfileAutomationMatchResult.Match, Now);
        var status = Assert.Single(board.Snapshot());
        Assert.Equal("badname", status.RuleName);
        Assert.DoesNotContain('\u0001', status.Describe());
    }

    [Fact]
    public void StatusIsRuntimeOnlySoStoredSettingsStayUnchangedAndLegacyFilesStillLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitAutomationTests", Guid.NewGuid().ToString("N"));
        var store = new ProfileAutomationStore(Path.Combine(directory, "automation.json"));
        try
        {
            Directory.CreateDirectory(directory);
            store.Save(new ProfileAutomationSettings(true, false, [ProcessRule()]), ["Performance"]);
            var json = File.ReadAllText(store.Path);
            // The board is runtime-only: no status field ever reaches the settings file.
            Assert.DoesNotContain("outcome", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("status", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("matchCount", json, StringComparison.OrdinalIgnoreCase);

            var loaded = store.LoadOrDefault(["Performance"], out var warning);
            Assert.Null(warning);
            // Loaded process rules only get names here, so the missing instance data is reported as Timeout.
            var current = Track(loaded.Rules, Context(Names("player")));
            Assert.Equal(ProfileAutomationRuleOutcome.Timeout, Assert.Single(current).Outcome);

            // A legacy file without version or status fields still loads and produces visible statuses.
            File.WriteAllText(store.Path, """
            {
              "enabled": true,
              "rules": [
                { "name": "legacy", "enabled": true, "priority": 3, "profileName": "Performance", "trigger": "AcPower" }
              ]
            }
            """);
            var legacy = store.LoadOrDefault(["Performance"], out warning);
            Assert.Null(warning);
            var status = Assert.Single(Track(legacy.Rules));
            Assert.Equal("legacy", status.RuleName);
            Assert.Equal(ProfileAutomationRuleOutcome.NoMatch, status.Outcome); // no AC state in the context: no match
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void StaticSummaryMatchesBoardSummaryForExternalSnapshots()
    {
        var board = new ProfileAutomationStatusBoard();
        board.RecordEvaluation("a", ProfileAutomationMatchResult.Match, Now);
        board.RecordEvaluation("b", new ProfileAutomationMatchResult(false, ProfileAutomationMatchFailure.AccessDenied), Now);
        board.RecordEvaluation("c", new ProfileAutomationMatchResult(false, ProfileAutomationMatchFailure.ConditionNotMet), Now);
        board.RecordRejected("d", Now);

        var summary = board.DescribeSummary();
        // The settings window only holds a snapshot, so both entry points must produce the same text.
        Assert.Equal(summary, ProfileAutomationStatusBoard.DescribeSummary(board.Snapshot()));
        Assert.Contains("最近命中 1", summary);
        Assert.Contains("跳过 1", summary);
        Assert.Contains("未命中 1", summary);
        Assert.Contains("未应用 1", summary);
        Assert.Equal("尚无自动规则运行记录。", ProfileAutomationStatusBoard.DescribeSummary([]));
    }

    [Fact]
    public void StatusRecordingDoesNotChangeEngineSelectionOrDebouncing()
    {
        var matching = ProcessRule() with { ProcessPath = null, ProcessCommandLineRegex = null };
        var fallback = new ProfileAutomationRule("fallback", true, 1, "Silent", ProfileAutomationTrigger.AcPower);
        var settings = new ProfileAutomationSettings(true, false, [fallback, matching]);
        var context = Context(Names("player")) with { AcPower = true };
        var board = new ProfileAutomationStatusBoard();

        var before = ProfileAutomationEngine.Select(settings, context, Now, null)?.Name;
        board.RecordEvaluations(settings.Rules, context, Now);
        var emitted = new ProfileAutomationDebouncer().Evaluate(settings, context, Now, null)?.Name;
        var after = ProfileAutomationEngine.Select(settings, context, Now, null)?.Name;

        Assert.Equal(RuleName, before);
        Assert.Equal(before, after);
        Assert.Equal(RuleName, emitted);
        Assert.Equal(2, board.Snapshot().Count);
    }
}
