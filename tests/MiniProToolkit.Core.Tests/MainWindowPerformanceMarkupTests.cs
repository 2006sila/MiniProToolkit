using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Source and policy guards for the main-window idle-CPU work: the trend chart must not be
/// rebuilt while it cannot be seen, the legend must not be re-created per tick, the Guardian
/// verdict must come from the sampling pass instead of a second SCM query, the telemetry write
/// must leave the UI thread while staying inside the sampling gate, and taskbar discovery must be
/// throttled without delaying the event-driven paths. These checks read source text only: they
/// never construct the WPF window, open a log file, touch hardware or start a GUI.
/// </summary>
public sealed class MainWindowPerformanceMarkupTests
{
    private static string FindAppSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests", "MiniProToolkit.Core.Tests.csproj");
            var candidate = Path.Combine(directory.FullName, "src", "MiniProToolkit.App", fileName);
            if (File.Exists(project) && File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"Could not locate the App source {fileName} above the test directory.");
    }

    private static string ReadMainWindow() => File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));

    private static string ReadPresentation() => File.ReadAllText(FindAppSource("MainWindow.Presentation.cs"));

    private static string SourceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker {endMarker} after {startMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string SamplingPass(string source) =>
        SourceBetween(source, "private async Task<bool> RefreshSnapshotAsync(", "private void TelemetryChartSizeChanged(");

    [Fact]
    public void HiddenOrMinimizedWindowSkipsTheChartAndRepaintsOnRestore()
    {
        var source = ReadMainWindow();
        var chart = SourceBetween(source, "private void UpdateTelemetryChart()", "private void EnsureTelemetryChartShapes()");

        // The visibility guard comes first and only marks the redraw as owed.
        var guard = chart.IndexOf("if (!IsVisible || WindowState == WindowState.Minimized)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "UpdateTelemetryChart must contain a visibility/WindowState guard.");
        Assert.Contains("_telemetryChartDirty = true;", chart);
        Assert.DoesNotContain("Children.", chart);

        // No per-tick canvas rebuild: the shapes are created exactly once and cleared exactly once.
        Assert.DoesNotContain("Children.Clear", chart);
        Assert.Equal(1, CountOccurrences(source, "TelemetryChart.Children.Clear();"));
        Assert.Equal(1, CountOccurrences(source, "new System.Windows.Shapes.Polyline"));
        Assert.Contains("if (_telemetryChartShapesReady || TelemetryChart is null)", source);
        var shapes = SourceBetween(source, "private void EnsureTelemetryChartShapes()", "private System.Windows.Shapes.Line CreateChartGuide()");
        Assert.Contains("TelemetryChart.Children.Clear();", shapes);
        Assert.Equal(3, CountOccurrences(shapes, "CreateChartGuide()"));
        Assert.Equal(5, CountOccurrences(shapes, "CreateChartPolyline("));

        // Points are replaced as a whole (frozen) collection instead of per-point notifications.
        Assert.Contains("ReplaceChartPoints(_telemetryChartTemperature, temperature);", chart);
        Assert.DoesNotContain(".Points.Add(", chart);
        var replace = SourceBetween(source, "private static void ReplaceChartPoints(", "private void OnChartVisibilityChanged(");
        Assert.Contains("points.Freeze();", replace);
        Assert.Contains("polyline.Points = points;", replace);

        // Subscribed once, guarded against teardown, and it replays the skipped redraw.
        Assert.Equal(1, CountOccurrences(source, "IsVisibleChanged += OnChartVisibilityChanged;"));
        var restore = SourceBetween(source, "private void OnChartVisibilityChanged(", "private static double Normalize(");
        Assert.Contains("if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)", restore);
        Assert.Contains("!_telemetryChartDirty", restore);
        Assert.Contains("UpdateTelemetryChart();", restore);

        // The restore of a minimized window repaints on the spot instead of waiting for the next tick.
        var stateChanged = SourceBetween(source, "private void OnStateChanged(", "private void RequestExit()");
        Assert.Contains("WindowState == WindowState.Minimized", stateChanged);
        Assert.Contains("if (_telemetryChartDirty && IsVisible)", stateChanged);
        Assert.Contains("UpdateTelemetryChart();", stateChanged);
        Assert.Contains("if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)", stateChanged);
        Assert.True(stateChanged.IndexOf("if (_telemetryChartDirty && IsVisible)", StringComparison.Ordinal) >
            stateChanged.IndexOf("WindowState == WindowState.Minimized", StringComparison.Ordinal),
            "The restore repaint must sit on the non-minimized path.");

        // The bounded history window itself is unchanged (120 records / 2 minutes).
        var pass = SamplingPass(source);
        Assert.Contains("_recentTelemetry.Count > 120", pass);
        Assert.Contains("TimeSpan.FromMinutes(2)", pass);
    }

    [Fact]
    public void TelemetryWriteLeavesTheUiThreadButStaysInsideTheSamplingGate()
    {
        var source = ReadMainWindow();
        var pass = SamplingPass(source);
        var append = pass.IndexOf("_telemetry.Append(telemetryRecord)", StringComparison.Ordinal);
        Assert.True(append > 0, "The pass must still append exactly the sampled record.");
        Assert.Equal(1, CountOccurrences(pass, "_telemetry.Append(telemetryRecord)"));

        // The append is inside a Task.Run that is awaited before the pass returns.
        var taskRun = pass.LastIndexOf("await Task.Run(() =>", append, StringComparison.Ordinal);
        Assert.True(taskRun >= 0 && append - taskRun < 300, "The append must run inside an awaited Task.Run.");
        Assert.True(pass.IndexOf("return true;", StringComparison.Ordinal) > append,
            "The pass must not return before the append completed (ordering + exit semantics).");

        // It is the last UI/IO step: the frame's snapshot already reached every surface.
        Assert.True(pass.IndexOf("UpdateTelemetryChart();", StringComparison.Ordinal) < append);
        Assert.True(pass.IndexOf("UpdateGuardianStatus(state.guardianRunning);", StringComparison.Ordinal) < append);
        Assert.True(pass.IndexOf("UpdateTrayStatus(state.snapshot, state.guardianRunning);", StringComparison.Ordinal) < append);
        Assert.True(pass.IndexOf("taskbar.UpdateSnapshot(state.snapshot, state.guardianRunning, safeManual, safeRpmOrCurve,",
            StringComparison.Ordinal) < append);
        Assert.True(pass.IndexOf("floatingCard.UpdateSnapshot(state.snapshot, state.guardianRunning, cardSafeManual, cardSafeRpmOrCurve,",
            StringComparison.Ordinal) < append);

        // Gate and failure handling are unchanged: the append still runs under the pass's gate and
        // a failed write is still logged instead of failing the pass.
        Assert.Contains("await _gate.WaitAsync(waitForGate ? ResumeRefreshGateTimeout : TimeSpan.Zero)", pass);
        Assert.True(pass.IndexOf("_gate.Release();", StringComparison.Ordinal) > append);
        Assert.Contains("AppLog.Write(\"telemetry append failed\", ex);", pass);
        Assert.Contains("await _gate.WaitAsync(ExitGateTimeout)", source);
    }

    [Fact]
    public void PeriodicPassReusesTheGuardianVerdictInsteadOfQueryingTheServiceManager()
    {
        var source = ReadMainWindow();
        var pass = SamplingPass(source);

        Assert.Contains("UpdateGuardianStatus(state.guardianRunning);", pass);
        Assert.DoesNotContain("UpdateGuardianStatus();", pass);
        // Exactly one SCM query per pass, and it already runs on the background thread.
        Assert.Equal(1, CountOccurrences(pass, "GuardianAvailability.IsRunning()"));
        Assert.True(pass.IndexOf("GuardianAvailability.IsRunning()", StringComparison.Ordinal) <
            pass.IndexOf("DisplaySnapshot(state.snapshot)", StringComparison.Ordinal));

        // The startup/click safety re-checks are untouched: the no-arg overload still queries.
        Assert.Contains("private void UpdateGuardianStatus() => UpdateGuardianStatus(GuardianAvailability.IsRunning());", source);
        Assert.True(CountOccurrences(source, "GuardianAvailability.IsRunning()") >= 6,
            "Lease/click safety re-checks must still query Guardian directly.");
        // Guardian/lease decisions are still the sampled pass verdict.
        Assert.Contains("var guardianRunning = _demo || GuardianAvailability.IsRunning();", source);
        Assert.Contains("FanCurveLeasePolicy.Evaluate(", source);
    }

    [Fact]
    public void TaskbarDiscoveryIsThrottledWhileNotificationsStayImmediate()
    {
        var source = ReadMainWindow();
        var pass = SamplingPass(source);

        Assert.Contains("private static readonly TimeSpan TaskbarDiscoveryInterval = TimeSpan.FromSeconds(30);", source);
        var policy = SourceBetween(source, "private bool ShouldRediscoverTaskbars(long timestamp, long frequency) =>",
            "private void EnsureTaskbars()");
        Assert.Contains("_taskbarWindows.Count == 0", policy);
        Assert.Contains("!window.IsNativeWindowAlive", policy);
        Assert.Contains("_taskbarDiscovery.TryAcquireInterval(timestamp, frequency, TaskbarDiscoveryInterval)", policy);

        // The 1 Hz pass only re-enumerates behind the throttle; existing windows are refreshed anyway.
        Assert.Equal(1, CountOccurrences(pass, "EnsureTaskbars();"));
        Assert.True(pass.IndexOf("ShouldRediscoverTaskbars(taskbarTimestamp", StringComparison.Ordinal) <
            pass.IndexOf("EnsureTaskbars();", StringComparison.Ordinal));
        Assert.Contains("taskbar.TryAttachToTaskbar();", pass);
        Assert.Contains("taskbar.UpdateSnapshot(state.snapshot, state.guardianRunning, safeManual, safeRpmOrCurve,", pass);

        // Event-driven refreshes still enumerate immediately (shell restart remounts, others clamp).
        var refresh = SourceBetween(source, "private void RunTaskbarRefresh(TaskbarRefreshTrigger trigger)",
            "private void RecordDiagnosticEvent(string message)");
        Assert.Contains("if (RediscoversTaskbars(trigger))", refresh);
        Assert.Contains("EnsureTaskbars();", refresh);
        Assert.Contains("taskbar.TryAttachToTaskbar();", refresh);
        // ... and they record the discovery time so the next periodic tick does not repeat the walk.
        var discover = SourceBetween(source, "private void EnsureTaskbars()", "private void OpenTaskbarSettingsFromMenu()");
        Assert.Contains("_taskbarDiscovery.MarkRefreshed(System.Diagnostics.Stopwatch.GetTimestamp());", discover);

        // Behaviour of the throttle this wiring relies on: first tick discovers, the next 29 s of
        // ticks do not, an event may enumerate again, and the fallback still fires afterwards.
        var discovery = new TaskbarRefreshScheduler();
        var interval = TimeSpan.FromSeconds(30);
        const long frequency = 1_000;
        Assert.True(discovery.TryAcquireInterval(0, frequency, interval));
        Assert.False(discovery.TryAcquireInterval(1_000, frequency, interval));
        Assert.False(discovery.TryAcquireInterval(29_999, frequency, interval));
        discovery.MarkRefreshed(2_000);
        Assert.False(discovery.TryAcquireInterval(31_000, frequency, interval));
        Assert.True(discovery.TryAcquireInterval(32_000, frequency, interval));
    }

    [Fact]
    public void TaskbarWindowsCreatedBetweenPassesAreFilledWithoutAnExtraSamplingTick()
    {
        var source = ReadMainWindow();
        var pass = SamplingPass(source);
        var ensure = SourceBetween(source, "private void EnsureTaskbars()", "private void OpenTaskbarSettingsFromMenu()");

        // The immediate paint reuses the newest kept sample and the pass's gate helper: no second
        // sample, no hardware read and no new timer is added to the creation path.
        Assert.Contains("ComputeTaskbarFanGates(state.snapshot)", pass);
        Assert.Contains("ComputeTaskbarFanGates(snapshot)", ensure);
        Assert.Contains("if (_latestSnapshot is { } snapshot)", ensure);
        Assert.Contains("window.UpdateSnapshot(snapshot, _demo || _lastGuardianRunning,", ensure);
        Assert.DoesNotContain("RefreshSnapshotAsync", ensure);
        Assert.DoesNotContain("Task.Run(", ensure);
        Assert.DoesNotContain("_timer", ensure);

        // The creation and the shared preview settings happen before the immediate paint, and the
        // discovery bookkeeping still records exactly one enumeration per EnsureTaskbars call.
        var created = ensure.IndexOf("new TaskbarStatusWindow(_taskbarPreviewSettings ?? _taskbarSettings, handle);", StringComparison.Ordinal);
        var shown = ensure.IndexOf("window.Show();", StringComparison.Ordinal);
        var painted = ensure.IndexOf("window.UpdateSnapshot(", StringComparison.Ordinal);
        Assert.True(created >= 0 && shown > created && painted > shown,
            "A new strip must take the preview settings, be shown, then receive the kept sample.");
        Assert.Equal(1, CountOccurrences(ensure, "_taskbarDiscovery.MarkRefreshed("));

        // The 1 Hz pass and the user refresh interval are untouched: one EnsureTaskbars per tick
        // behind the throttle, and the same interval gate.
        Assert.Equal(1, CountOccurrences(pass, "EnsureTaskbars();"));
        Assert.Contains("TimeSpan.FromSeconds(_taskbarSettings.RefreshIntervalSeconds)", pass);
        Assert.Contains("TaskbarDiscoveryInterval = TimeSpan.FromSeconds(30);", source);
    }

    [Fact]
    public void TrendLegendIsOnlyRebuiltWhenTheSourceSentenceChanges()
    {
        var presentation = ReadPresentation();
        Assert.Contains("private string? _trendLegendSignature;", presentation);
        var legend = SourceBetween(presentation, "private void SetTrendLegend(string sourceDescription)",
            "private void AddLegendRun(");
        var cacheCheck = legend.IndexOf("string.Equals(_trendLegendSignature, sourceDescription, StringComparison.Ordinal)",
            StringComparison.Ordinal);
        Assert.True(cacheCheck >= 0, "SetTrendLegend must compare the cached signature.");
        Assert.True(cacheCheck < legend.IndexOf("Inlines.Clear();", StringComparison.Ordinal),
            "The cache check must run before the Inlines are cleared.");
        Assert.Contains("_trendLegendSignature = sourceDescription;", legend);
        // The same five colour-coded legend entries are still built for a new description.
        Assert.Equal(5, CountOccurrences(legend, "AddLegendRun("));
    }

    [Fact]
    public void TaskbarNotificationsThatCanAddTaskbarsReEnumerateWhileDpiOnlyRepositions()
    {
        var source = ReadMainWindow();
        // Decision: shell restart, monitor layout and taskbar settings re-enumerate; DPI only moves.
        var decision = SourceBetween(source, "private static bool RediscoversTaskbars(TaskbarRefreshTrigger trigger) =>",
            "private void RunTaskbarRefresh(TaskbarRefreshTrigger trigger)");
        Assert.Contains("TaskbarRefreshTrigger.ShellRecreated", decision);
        Assert.Contains("TaskbarRefreshTrigger.DisplayChanged", decision);
        Assert.Contains("TaskbarRefreshTrigger.TaskbarSettingChanged", decision);
        Assert.DoesNotContain("TaskbarRefreshTrigger.DpiChanged", decision);

        var refresh = SourceBetween(source, "private void RunTaskbarRefresh(TaskbarRefreshTrigger trigger)",
            "private void RecordDiagnosticEvent(string message)");
        Assert.Contains("if (RediscoversTaskbars(trigger))", refresh);
        Assert.Contains("EnsureTaskbars();", refresh);
        Assert.Contains("taskbar.TryAttachToTaskbar();", refresh);
        Assert.DoesNotContain("TaskbarRefreshTrigger.DpiChanged", refresh);

        // Coalescing and the interval slot are unchanged: still one queued refresh per burst.
        Assert.Contains("if (!_taskbarRefresh.TryQueue())", source);
        Assert.Contains("_taskbarRefresh.MarkRefreshed(System.Diagnostics.Stopwatch.GetTimestamp());", refresh);
        Assert.Contains("_taskbarRefresh.Complete();", refresh);

        // Core keeps its narrower "the mounted windows are dead" meaning, and every one of these
        // notifications still bypasses the user's periodic refresh interval.
        Assert.True(TaskbarRefreshPolicy.RemountsTaskbarWindows(TaskbarRefreshTrigger.ShellRecreated));
        Assert.False(TaskbarRefreshPolicy.RemountsTaskbarWindows(TaskbarRefreshTrigger.DisplayChanged));
        Assert.False(TaskbarRefreshPolicy.RemountsTaskbarWindows(TaskbarRefreshTrigger.TaskbarSettingChanged));
        Assert.False(TaskbarRefreshPolicy.RemountsTaskbarWindows(TaskbarRefreshTrigger.DpiChanged));
        foreach (var trigger in Enum.GetValues<TaskbarRefreshTrigger>())
            Assert.True(TaskbarRefreshPolicy.BypassesRefreshInterval(trigger));
    }
}
