using System.Text.RegularExpressions;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Static source guards for the hard WMI bounds on the brightness and capability-probe paths:
/// every brightness query must go through the single bounded searcher factory, the power-switch
/// bind must carry an <c>ObjectGetOptions</c> timeout and run on its own bounded worker, and the
/// diagnostics capability button must drop repeat clicks while a probe is running.
/// Deliberately no WMI call, no window construction and no hardware access: a wedged provider
/// cannot be simulated on a build machine, so these checks only prove the wiring is still there.
/// </summary>
public sealed class HardwareWmiTimeoutTests
{
    [Fact]
    public void EveryBrightnessQueryGoesThroughTheBoundedSearcherFactory()
    {
        var source = ReadSource("MiniProToolkit.Hardware", "BrightnessReader.cs");

        // One factory only: an inline searcher would silently fall back to an unbounded query.
        Assert.Single(Regex.Matches(source, @"new\s+ManagementObjectSearcher\s*\("));
        Assert.Matches(@"private static readonly TimeSpan WmiQueryTimeout = TimeSpan\.FromSeconds\(2\);", source);
        Assert.Contains("searcher.Options.Timeout = WmiQueryTimeout;", source);

        // All four brightness query sites (read, control probe, connection check, method count)
        // must go through that factory.
        Assert.Equal(4, Regex.Matches(source, @"using var searcher = CreateSearcher\(").Count);
        Assert.Contains("CreateSearcher(BrightnessQuery)", source);
        Assert.Contains("CreateSearcher(ConnectionQuery)", source);
        Assert.Equal(2, Regex.Matches(source, @"CreateSearcher\(MethodsQuery\)").Count);

        // The abandoned-wait problem is only solved if the worker wait stays bounded too.
        Assert.Matches(@"OperationTimeout = TimeSpan\.FromSeconds\(3\);", source);
        Assert.Contains("SharedWorker.Execute(", source);
    }

    [Fact]
    public void PowerSwitchBindCarriesAnObjectGetTimeoutAndRunsOnABoundedWorker()
    {
        var source = ReadSource("MiniProToolkit.Hardware", "HardwareCapabilityProbe.cs");

        Assert.Matches(@"WmiObjectTimeout = TimeSpan\.FromSeconds\(2\);", source);

        // Exactly one ManagementObject bind, and it must not pass a null options argument.
        Assert.Single(Regex.Matches(source, @"new\s+ManagementObject\s*\("));
        Assert.DoesNotMatch(@"new\s+ManagementObject\s*\([^;]*?,\s*null\s*\)", source);
        Assert.Contains("new ObjectGetOptions { Timeout = WmiObjectTimeout }", source);

        // GetMethodParameters has no timeout overload, so the whole probe must own a worker slot
        // with a finite budget instead of blocking the caller forever.
        Assert.Matches(@"PowerSwitchProbeTimeout = TimeSpan\.FromSeconds\(10\);", source);
        Assert.Contains("SingleFlightOperationWorker PowerSwitchWorker", source);
        Assert.Matches(@"PowerSwitchWorker\.Execute\(\s*ProbePowerSwitchCore,", source);

        // The bind is what the timeout option applies to; it must still be performed.
        Assert.Contains("instance.Get();", source);
    }

    [Fact]
    public void CapabilityButtonDropsRepeatClicksAndAlwaysReleasesTheFlag()
    {
        var source = ReadSource("MiniProToolkit.App", "DiagnosticsWindow.xaml.cs");
        Assert.Matches(@"private int _capabilitiesBusy;", source);

        var handler = SourceBetween(source,
            "private async void ProbeCapabilitiesClick",
            "private async void ProbeSystemControlsClick");

        var guard = handler.IndexOf("Interlocked.CompareExchange(ref _capabilitiesBusy, 1, 0)", StringComparison.Ordinal);
        var work = handler.IndexOf("await Task.Run(() => new HardwareCapabilityProbe().Probe())", StringComparison.Ordinal);
        var release = handler.IndexOf("Volatile.Write(ref _capabilitiesBusy, 0);", StringComparison.Ordinal);
        Assert.True(guard >= 0, "Repeat clicks must be rejected through the busy flag.");
        Assert.True(work > guard, "The busy flag must be taken before the WMI probe starts.");
        Assert.True(release > work, "The busy flag must only be released after the probe returns.");
        Assert.Contains("finally", handler);
        Assert.Contains("ProbeCapabilitiesButton.IsEnabled = false;", handler);
        Assert.Contains("ProbeCapabilitiesButton.IsEnabled = true;", handler);

        // The system-control path owns its own busy flag and must not be coupled to this one.
        var controlHandler = SourceBetween(source,
            "private async void ProbeSystemControlsClick",
            "private async void SetOutputVolumeClick");
        Assert.DoesNotContain("_capabilitiesBusy", controlHandler);
        Assert.Contains("_systemControlBusy", controlHandler);

        var markup = File.ReadAllText(FindSource("MiniProToolkit.App", "DiagnosticsWindow.xaml"));
        Assert.Contains("x:Name=\"ProbeCapabilitiesButton\"", markup);
    }

    private static string ReadSource(string project, string fileName) =>
        File.ReadAllText(FindSource(project, fileName));

    private static string SourceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker {endMarker} after {startMarker}");
        return source[start..end];
    }

    private static string FindSource(string projectName, string fileName)
    {
        // Start at the test output directory; work in any checkout without a user-specific path.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests",
                "MiniProToolkit.Core.Tests.csproj");
            var candidate = Path.Combine(directory.FullName, "src", projectName, fileName);
            if (File.Exists(project) && File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"Could not locate {projectName} source {fileName} above the test directory.");
    }
}
