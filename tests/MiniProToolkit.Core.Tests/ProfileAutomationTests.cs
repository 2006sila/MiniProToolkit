using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class ProfileAutomationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);
    private static ProfileAutomationRule Rule(string name, string profile, int priority, ProfileAutomationTrigger trigger, int debounce = 0, double threshold = 50, int start = 0, int end = 1440) =>
        new(name, true, priority, profile, trigger, threshold, start, end, debounce);

    [Fact]
    public void HighestPriorityMatchingRuleWinsAndStableNameBreaksTies()
    {
        var settings = new ProfileAutomationSettings(true, false,
        [
            Rule("b", "Balanced", 10, ProfileAutomationTrigger.AcPower),
            Rule("z", "Performance", 20, ProfileAutomationTrigger.AcPower),
            Rule("a", "Silent", 20, ProfileAutomationTrigger.AcPower)
        ]);
        var result = ProfileAutomationEngine.Select(settings, new(true, 10, Now), Now, null);
        Assert.Equal("a", result?.Name);
    }

    [Fact]
    public void PausedUnknownInputsAndCurrentHighestProfileDoNotSelectFallback()
    {
        var rules = new[]
        {
            Rule("top", "Already", 10, ProfileAutomationTrigger.AcPower),
            Rule("lower", "Other", 1, ProfileAutomationTrigger.AcPower)
        };
        Assert.Null(ProfileAutomationEngine.Select(new(true, true, rules), new(true, 80, Now), Now, null));
        Assert.Null(ProfileAutomationEngine.Select(new(true, false, rules), new(true, 80, Now), Now, "Already"));
        Assert.Null(ProfileAutomationEngine.Select(new(true, false, [Rule("ac", "x", 1, ProfileAutomationTrigger.AcPower)]), new(null, 80, Now), Now, null));
        Assert.Null(ProfileAutomationEngine.Select(new(true, false, [Rule("cpu", "x", 1, ProfileAutomationTrigger.CpuLoadAbove)]), new(true, null, Now), Now, null));
    }

    [Fact]
    public void TimeRangeSupportsMidnightWrap()
    {
        var overnight = Rule("night", "Quiet", 1, ProfileAutomationTrigger.TimeRange, start: 22 * 60, end: 6 * 60);
        Assert.True(ProfileAutomationEngine.Matches(overnight, new(null, null, new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero))));
        Assert.True(ProfileAutomationEngine.Matches(overnight, new(null, null, new DateTimeOffset(2026, 10, 31, 4, 0, 0, TimeSpan.Zero))));
        Assert.False(ProfileAutomationEngine.Matches(overnight, new(null, null, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero))));
        var untilMidnight = Rule("midnight", "Quiet", 1, ProfileAutomationTrigger.TimeRange, start: 22 * 60, end: 0);
        Assert.True(ProfileAutomationEngine.Matches(untilMidnight, new(null, null, new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.Zero))));
        Assert.False(ProfileAutomationEngine.Matches(untilMidnight, new(null, null, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void ProcessTrackerEmitsOnlyTransitionsAndDoesNotTreatStartupAsStart()
    {
        var tracker = new ProfileAutomationProcessTracker();
        var baseContext = new ProfileAutomationContext(null, null, Now);
        var first = tracker.AddProcessStates(baseContext, ["notepad.exe"], ["notepad"]);
        Assert.Null(first.StartedProcesses?.FirstOrDefault());
        var stillRunning = tracker.AddProcessStates(baseContext, ["notepad.exe"], ["notepad.exe"]);
        Assert.Null(stillRunning.StartedProcesses?.FirstOrDefault());
        var exited = tracker.AddProcessStates(baseContext, ["notepad.exe"], []);
        Assert.Contains("notepad", exited.ExitedProcesses!);
        var started = tracker.AddProcessStates(baseContext, ["notepad.exe"], ["notepad"]);
        Assert.Contains("notepad", started.StartedProcesses!);
        var repeated = tracker.AddProcessStates(baseContext, ["notepad.exe"], ["notepad"]);
        Assert.Contains("notepad", repeated.StartedProcesses!);
        tracker.ClearTransitions();
        var cleared = tracker.AddProcessStates(baseContext, ["notepad.exe"], ["notepad"]);
        Assert.Empty(cleared.StartedProcesses!);
    }

    [Fact]
    public void InitiallyAbsentProcessStartIsDetectedAndNotDebounced()
    {
        var tracker = new ProfileAutomationProcessTracker();
        var context = new ProfileAutomationContext(null, null, Now);
        tracker.AddProcessStates(context, ["tool"], []);
        var started = tracker.AddProcessStates(context, ["tool"], ["tool"]);
        var rule = Rule("tool-start", "Performance", 1, ProfileAutomationTrigger.ProcessStarted, debounce: 3600) with { ProcessName = "tool.exe" };
        var debouncer = new ProfileAutomationDebouncer();
        var settings = new ProfileAutomationSettings(true, false, [rule]);
        Assert.Equal("tool-start", debouncer.Evaluate(settings, started, Now, null)?.Name);
        Assert.Null(debouncer.Evaluate(settings, started, Now.AddSeconds(5), null));
    }

    [Fact]
    public void ProcessRulesMatchNormalizedNamesAndRejectPaths()
    {
        var started = Rule("start", "Performance", 1, ProfileAutomationTrigger.ProcessStarted) with { ProcessName = "notepad.exe" };
        var exited = Rule("exit", "Performance", 1, ProfileAutomationTrigger.ProcessExited) with { ProcessName = "notepad" };
        Assert.True(ProfileAutomationEngine.Matches(started, new(null, null, Now, new HashSet<string>(["notepad"]), null)));
        Assert.True(ProfileAutomationEngine.Matches(exited, new(null, null, Now, null, new HashSet<string>(["notepad.exe"]))));
        var invalid = started with { ProcessName = "C:\\Windows\\notepad.exe" };
        Assert.Throws<ArgumentException>(() => invalid.Validate(["Performance"]));
    }

    [Fact]
    public void DebouncerRequiresStableConditionAndEmitsOnlyOnce()
    {
        var rule = Rule("ac", "Performance", 1, ProfileAutomationTrigger.AcPower, debounce: 30);
        var settings = new ProfileAutomationSettings(true, false, [rule]);
        var context = new ProfileAutomationContext(true, 10, Now);
        var debouncer = new ProfileAutomationDebouncer();
        Assert.Null(debouncer.Evaluate(settings, context, Now, null));
        Assert.Null(debouncer.Evaluate(settings, context, Now.AddSeconds(29), null));
        Assert.Equal("ac", debouncer.Evaluate(settings, context, Now.AddSeconds(30), null)?.Name);
        Assert.Null(debouncer.Evaluate(settings, context, Now.AddSeconds(60), null));
        Assert.Null(debouncer.Evaluate(settings, context with { AcPower = false }, Now.AddSeconds(61), null));
        Assert.Null(debouncer.Evaluate(settings, context, Now.AddSeconds(62), null));
        Assert.Equal("ac", debouncer.Evaluate(settings, context, Now.AddSeconds(92), null)?.Name);
    }

    [Fact]
    public void RuleValidationRejectsUnknownProfileAndEmptyTimeWindow()
    {
        Assert.Throws<ArgumentException>(() => Rule("x", "missing", 0, ProfileAutomationTrigger.AcPower).Validate(["Present"]));
        Assert.Throws<ArgumentException>(() => Rule("x", "p", 0, ProfileAutomationTrigger.TimeRange, start: 10, end: 10).Validate(["p"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rule("x", "p", 0, ProfileAutomationTrigger.CpuLoadAbove, threshold: 101).Validate(["p"]));
    }

    [Fact]
    public void StoreRoundTripsAndCorruptFilesFallbackDisabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitAutomationTests", Guid.NewGuid().ToString("N"));
        var store = new ProfileAutomationStore(Path.Combine(directory, "automation.json"));
        try
        {
            var expected = new ProfileAutomationSettings(true, true, [Rule("ac", "Performance", 7, ProfileAutomationTrigger.AcPower)]);
            store.Save(expected, ["Performance"]);
            var loaded = store.LoadOrDefault(["Performance"], out var warning);
            Assert.Null(warning);
            Assert.Equal(expected.Enabled, loaded.Enabled);
            Assert.Equal(expected.Paused, loaded.Paused);
            Assert.Equal(expected.Rules, loaded.Rules);
            File.WriteAllText(store.Path, "{");
            var fallback = store.LoadOrDefault(["Performance"], out warning);
            Assert.NotNull(warning);
            Assert.False(fallback.Enabled);
            Assert.Empty(fallback.Rules);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
