using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class ProfileAutomationProcessConditionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);

    private static ProfileAutomationRule Rule(
        string name,
        string profile,
        int priority = 1,
        ProfileAutomationTrigger trigger = ProfileAutomationTrigger.ProcessStarted,
        string? processName = "app",
        int debounce = 0,
        double threshold = 50,
        int start = 0,
        int end = 1440) =>
        new(name, true, priority, profile, trigger, threshold, start, end, debounce, processName);

    private static ProfileAutomationContext Context(
        IReadOnlySet<string>? startedNames = null,
        IReadOnlyList<ProfileAutomationProcessInfo>? startedDetails = null,
        IReadOnlySet<string>? exitedNames = null,
        IReadOnlyList<ProfileAutomationProcessInfo>? exitedDetails = null,
        bool? ac = null) =>
        new(ac, null, Now, startedNames, exitedNames, startedDetails, exitedDetails);

    private static IReadOnlySet<string> Names(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void LegacyJsonWithNameOnlyRuleLoadsAndStillMatches()
    {
        const string legacyJson = """
        {
          "version": 1,
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "legacy",
              "enabled": true,
              "priority": 5,
              "profileName": "Performance",
              "trigger": "ProcessStarted",
              "threshold": 0,
              "startMinute": 0,
              "endMinute": 1440,
              "debounceSeconds": 30,
              "processName": "notepad.exe"
            }
          ]
        }
        """;
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitAutomationTests", Guid.NewGuid().ToString("N"));
        var store = new ProfileAutomationStore(Path.Combine(directory, "automation.json"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(store.Path, legacyJson);
            var loaded = store.LoadOrDefault(["Performance"], out var warning);
            Assert.Null(warning);
            var rule = Assert.Single(loaded.Rules);
            Assert.Null(rule.ProcessPath);
            Assert.Null(rule.ProcessCommandLine);
            Assert.Null(rule.ProcessCommandLineRegex);
            Assert.True(ProfileAutomationEngine.Matches(rule, Context(startedNames: Names("notepad"))));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExecutablePathMatchIsCaseInsensitiveAndSeparatorNormalized()
    {
        var rule = Rule("path", "Performance") with { ProcessPath = @"C:\Program Files\App\App.exe" };
        var matching = Context(startedNames: Names("app"), startedDetails: [new("app", "c:/program files//app/APP.EXE")]);
        Assert.True(ProfileAutomationEngine.Matches(rule, matching));

        var wrapped = Context(startedNames: Names("app"), startedDetails: [new("app", " \"C:\\Program Files\\App\\App.exe\" ")]);
        Assert.True(ProfileAutomationEngine.Matches(rule, wrapped));

        var otherPath = Context(startedNames: Names("app"), startedDetails: [new("app", @"C:\Other\App.exe")]);
        var result = ProfileAutomationEngine.Explain(rule, otherPath);
        Assert.False(result.Matched);
        Assert.Equal(ProfileAutomationMatchFailure.ExecutablePathMismatch, result.Failure);
        Assert.False(result.IsSkip);
    }

    [Fact]
    public void UncExecutablePathMatchesWithNormalizedSeparators()
    {
        var rule = Rule("unc", "Performance") with { ProcessPath = @"\\server\share\App.exe" };
        var context = Context(startedNames: Names("app"), startedDetails: [new("app", @"//SERVER/share/app.exe")]);
        Assert.True(ProfileAutomationEngine.Matches(rule, context));
        rule.Validate(["Performance"]);
    }

    [Fact]
    public void CommandLineSubstringMatchIsCaseInsensitive()
    {
        var rule = Rule("cmd", "Performance") with { ProcessCommandLine = "PROFILE=Gaming" };
        var info = new ProfileAutomationProcessInfo("app", @"C:\A\app.exe", "app.exe --profile=gaming --wait");
        Assert.True(ProfileAutomationEngine.Matches(rule, Context(startedNames: Names("app"), startedDetails: [info])));

        var mismatch = ProfileAutomationEngine.Explain(rule, Context(
            startedNames: Names("app"),
            startedDetails: [new("app", @"C:\A\app.exe", "app.exe --profile=silent")]));
        Assert.Equal(ProfileAutomationMatchFailure.CommandLineMismatch, mismatch.Failure);
    }

    [Fact]
    public void CommandLineRegexMatchesAndInvalidRegexFallsBackWithoutThrowing()
    {
        var rule = Rule("regex", "Performance") with { ProcessCommandLineRegex = @"--profile=(gaming|pro)\b" };
        rule.Validate(["Performance"]);
        var info = new ProfileAutomationProcessInfo("app", @"C:\A\app.exe", "app.exe --profile=pro");
        Assert.True(ProfileAutomationEngine.Matches(rule, Context(startedNames: Names("app"), startedDetails: [info])));

        var invalid = Rule("broken", "Performance") with { ProcessCommandLineRegex = "[unclosed" };
        var context = Context(startedNames: Names("app"), startedDetails: [new("app", @"C:\A\app.exe", "app.exe --x")]);
        var result = ProfileAutomationEngine.Explain(invalid, context);
        Assert.False(result.Matched);
        Assert.Equal(ProfileAutomationMatchFailure.InvalidCommandLinePattern, result.Failure);
        Assert.True(result.IsSkip);
        Assert.False(ProfileAutomationEngine.Matches(invalid, context));

        var error = Assert.Throws<ArgumentException>(() => invalid.Validate(["Performance"]));
        Assert.DoesNotContain("[unclosed", error.Message);
    }

    [Fact]
    public void InvalidRegexInStoredSettingsFallsBackToDisabledWithSafeWarning()
    {
        const string json = """
        {
          "version": 1,
          "enabled": true,
          "paused": false,
          "rules": [
            {
              "name": "bad",
              "enabled": true,
              "priority": 1,
              "profileName": "Performance",
              "trigger": "ProcessStarted",
              "processName": "app",
              "processCommandLineRegex": "[unclosed"
            }
          ]
        }
        """;
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitAutomationTests", Guid.NewGuid().ToString("N"));
        var store = new ProfileAutomationStore(Path.Combine(directory, "automation.json"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(store.Path, json);
            var loaded = store.LoadOrDefault(["Performance"], out var warning);
            Assert.NotNull(warning);
            Assert.DoesNotContain("[unclosed", warning);
            Assert.False(loaded.Enabled);
            Assert.Empty(loaded.Rules);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void EmptyConditionsDoNotRestrictMatching()
    {
        var rule = Rule("empty", "Performance") with { ProcessPath = "   ", ProcessCommandLine = "", ProcessCommandLineRegex = null };
        rule.Validate(["Performance"]);
        Assert.False(ProfileAutomationProcessCondition.HasAnyProcessCondition(rule));
        Assert.True(ProfileAutomationEngine.Matches(rule, Context(startedNames: Names("app"))));
        Assert.True(ProfileAutomationEngine.Matches(rule, Context(startedNames: Names("app.exe"), startedDetails: [])));
    }

    [Fact]
    public void AccessDeniedProcessIsSkippedAndDiagnosticsNeverLeakCommandLine()
    {
        const string secret = "tok-9f3c-SECRET";
        var rule = Rule("denied", "Performance") with
        {
            ProcessPath = @"C:\Secret\app.exe",
            ProcessCommandLine = secret
        };
        var context = Context(startedNames: Names("app"), startedDetails: [ProfileAutomationProcessInfo.Denied("app")]);
        var result = ProfileAutomationEngine.Explain(rule, context);
        Assert.False(result.Matched);
        Assert.Equal(ProfileAutomationMatchFailure.AccessDenied, result.Failure);
        Assert.True(result.IsSkip);

        var skipped = ProfileAutomationEngine.ExplainSkippedProcessRules([rule], context);
        var diagnostic = Assert.Single(skipped);
        Assert.Equal("denied", diagnostic.RuleName);
        Assert.DoesNotContain(secret, diagnostic.Describe());
        Assert.DoesNotContain("Secret", diagnostic.Describe());

        var mismatch = ProfileAutomationEngine.Explain(rule, Context(
            startedNames: Names("app"),
            startedDetails: [new("app", @"C:\Secret\app.exe", "app.exe --other")]));
        Assert.Equal(ProfileAutomationMatchFailure.CommandLineMismatch, mismatch.Failure);
        var all = ProfileAutomationEngine.ExplainProcessRules([rule], Context(
            startedNames: Names("app"),
            startedDetails: [new("app", @"C:\Secret\app.exe", "app.exe --other")]));
        Assert.DoesNotContain(secret, Assert.Single(all).Describe());
        Assert.False(rule.SafeDescription.Contains(secret, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Secret", rule.SafeDescription);
    }

    [Fact]
    public void RulesWithoutInstanceDetailsAreSkippedInsteadOfGuessed()
    {
        var nameOnly = Rule("name-only", "Performance");
        Assert.True(ProfileAutomationEngine.Matches(nameOnly, Context(startedNames: Names("app"))));

        var conditional = Rule("conditional", "Performance") with { ProcessPath = @"C:\A\app.exe" };
        var result = ProfileAutomationEngine.Explain(conditional, Context(startedNames: Names("app")));
        Assert.False(result.Matched);
        Assert.Equal(ProfileAutomationMatchFailure.ProcessDetailsUnavailable, result.Failure);
        Assert.True(result.IsSkip);

        var unknownName = ProfileAutomationEngine.Explain(conditional, Context(startedNames: Names("other")));
        Assert.Equal(ProfileAutomationMatchFailure.ProcessNameMismatch, unknownName.Failure);
        Assert.False(unknownName.IsSkip);
    }

    [Fact]
    public void MissingPathOrCommandLineInInstanceDetailsIsReportedAsSkip()
    {
        var pathRule = Rule("path", "Performance") with { ProcessPath = @"C:\A\app.exe" };
        var noPath = ProfileAutomationEngine.Explain(pathRule, Context(startedNames: Names("app"), startedDetails: [new("app", null)]));
        Assert.Equal(ProfileAutomationMatchFailure.ExecutablePathUnavailable, noPath.Failure);
        Assert.True(noPath.IsSkip);

        var commandRule = Rule("cmd", "Performance") with { ProcessCommandLine = "gaming" };
        var noCommandLine = ProfileAutomationEngine.Explain(commandRule, Context(startedNames: Names("app"), startedDetails: [new("app", @"C:\A\app.exe", null)]));
        Assert.Equal(ProfileAutomationMatchFailure.CommandLineUnavailable, noCommandLine.Failure);
        Assert.True(noCommandLine.IsSkip);
    }

    [Fact]
    public void ValidationRejectsUnsafeOrAmbiguousProcessConditions()
    {
        var relative = Rule("relative", "Performance") with { ProcessPath = "app.exe" };
        Assert.Throws<ArgumentException>(() => relative.Validate(["Performance"]));

        var wildcard = Rule("wildcard", "Performance") with { ProcessPath = @"C:\A\*.exe" };
        Assert.Throws<ArgumentException>(() => wildcard.Validate(["Performance"]));

        var both = Rule("both", "Performance") with { ProcessCommandLine = "gaming", ProcessCommandLineRegex = "gam.*" };
        Assert.Throws<ArgumentException>(() => both.Validate(["Performance"]));

        var control = Rule("control", "Performance") with { ProcessCommandLine = "bad\u0001value" };
        Assert.Throws<ArgumentException>(() => control.Validate(["Performance"]));

        var nonProcessTrigger = Rule("ac", "Performance", 1, ProfileAutomationTrigger.AcPower) with { ProcessPath = @"C:\A\app.exe" };
        Assert.Throws<ArgumentException>(() => nonProcessTrigger.Validate(["Performance"]));

        var pathInName = Rule("path-name", "Performance") with { ProcessName = @"C:\A\app.exe" };
        Assert.Throws<ArgumentException>(() => pathInName.Validate(["Performance"]));
    }

    [Fact]
    public void TrackerCapturesInstanceDetailsForStartAndExitWithoutChangingTransitions()
    {
        var tracker = new ProfileAutomationProcessTracker();
        var context = new ProfileAutomationContext(null, null, Now);
        var running = new[] { new ProfileAutomationProcessInfo("app", @"C:\A\app.exe", "--profile=gaming") };

        var first = tracker.AddProcessInstances(context, ["app"], Array.Empty<ProfileAutomationProcessInfo>());
        Assert.Empty(first.StartedProcessDetails!);
        Assert.Null(first.StartedProcesses?.FirstOrDefault());

        var started = tracker.AddProcessInstances(context, ["app"], running);
        Assert.Contains("app", started.StartedProcesses!);
        var startedInfo = Assert.Single(started.StartedProcessDetails!);
        Assert.Equal(@"C:\A\app.exe", startedInfo.ExecutablePath);

        var exited = tracker.AddProcessInstances(context, ["app"], Array.Empty<ProfileAutomationProcessInfo>());
        Assert.Contains("app", exited.ExitedProcesses!);
        var exitedInfo = Assert.Single(exited.ExitedProcessDetails!);
        Assert.Equal(@"C:\A\app.exe", exitedInfo.ExecutablePath);
        Assert.Contains("app", exited.StartedProcesses!);
        Assert.Single(exited.StartedProcessDetails!); // start transitions stay until ClearTransitions, like the name set

        tracker.ClearTransitions();
        var cleared = tracker.AddProcessInstances(context, ["app"], Array.Empty<ProfileAutomationProcessInfo>());
        Assert.Empty(cleared.ExitedProcessDetails!);
    }

    [Fact]
    public void NameOnlyTrackerOverloadLeavesDetailSetsUnset()
    {
        var tracker = new ProfileAutomationProcessTracker();
        var context = new ProfileAutomationContext(null, null, Now);
        tracker.AddProcessStates(context, ["tool"], Array.Empty<string>());
        var started = tracker.AddProcessStates(context, ["tool"], ["tool"]);
        Assert.Contains("tool", started.StartedProcesses!);
        Assert.Null(started.StartedProcessDetails);
        Assert.Null(started.ExitedProcessDetails);
    }

    [Fact]
    public void PriorityAndProcessDebounceSemanticsAreUnchangedByConditions()
    {
        var conditional = Rule("conditional", "Performance", 20) with { ProcessPath = @"C:\A\app.exe" };
        var fallback = Rule("fallback", "Silent", 10);
        var settings = new ProfileAutomationSettings(true, false, [fallback, conditional]);
        var matching = Context(startedNames: Names("app"), startedDetails: [new("app", @"C:\A\app.exe", null)]);
        Assert.Equal("conditional", ProfileAutomationEngine.Select(settings, matching, Now, null)?.Name);

        var wrongPath = Context(startedNames: Names("app"), startedDetails: [new("app", @"C:\B\app.exe", null)]);
        Assert.Equal("fallback", ProfileAutomationEngine.Select(settings, wrongPath, Now, null)?.Name);

        var debouncer = new ProfileAutomationDebouncer();
        Assert.Equal("conditional", debouncer.Evaluate(settings, matching, Now, null)?.Name);
        Assert.Null(debouncer.Evaluate(settings, matching, Now.AddSeconds(5), null));
    }

    [Fact]
    public void StoreRoundTripsPathAndCommandLineConditions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitAutomationTests", Guid.NewGuid().ToString("N"));
        var store = new ProfileAutomationStore(Path.Combine(directory, "automation.json"));
        try
        {
            var expected = new ProfileAutomationSettings(true, false,
            [
                Rule("path", "Performance", 5) with { ProcessPath = @"C:\Program Files\App\App.exe" },
                Rule("cmd", "Silent", 4, ProfileAutomationTrigger.ProcessExited) with { ProcessCommandLineRegex = @"--profile=(gaming|pro)" }
            ]);
            store.Save(expected, ["Performance", "Silent"]);
            var loaded = store.LoadOrDefault(["Performance", "Silent"], out var warning);
            Assert.Null(warning);
            Assert.Equal(expected.Rules, loaded.Rules);
            Assert.Equal(@"C:\Program Files\App\App.exe", loaded.Rules[0].ProcessPath);
            Assert.Equal(@"--profile=(gaming|pro)", loaded.Rules[1].ProcessCommandLineRegex);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SafeDescriptionOnlyContainsRuleIdentityAndProcessName()
    {
        var rule = Rule("gaming", "Performance") with
        {
            ProcessPath = @"C:\Users\someone\Secret\app.exe",
            ProcessCommandLine = "--token=abc123"
        };
        var description = rule.SafeDescription;
        Assert.Contains("gaming", description);
        Assert.Contains("app", description);
        Assert.DoesNotContain("someone", description);
        Assert.DoesNotContain("abc123", description);
    }
}
