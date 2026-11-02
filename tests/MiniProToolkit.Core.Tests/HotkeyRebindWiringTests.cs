using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Source guard for the production wiring of the hotkey rebind: the main window must go through the
/// transactional helper, so the new registrations are live before the file is written and a failed
/// registration can never drop the previous hotkeys. Source text only: no WPF window, no real global
/// hotkey, no settings file.
/// </summary>
public sealed class HotkeyRebindWiringTests
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

    private static string SourceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker {endMarker} after {startMarker}");
        return source[start..end];
    }

    [Fact]
    public void TaskbarSettingsSaveAppliesHotkeysThroughTheTransaction()
    {
        var handler = SourceBetween(ReadMainWindow(), "private void TaskbarSettingsClick(",
            "private void DisplaySourceSettingsClick(");

        var runIndex = handler.IndexOf("HotkeyRebindTransaction.Run(", StringComparison.Ordinal);
        var saveIndex = handler.IndexOf("_hotkeySettingsStore.Save(hotkeys)", StringComparison.Ordinal);
        Assert.True(runIndex >= 0, "The hotkey save must go through HotkeyRebindTransaction.");
        // The store write is inside the transaction's save delegate: registration comes first.
        Assert.True(saveIndex > runIndex, "The hotkey file must only be written by the rebind transaction.");

        // New gestures are applied/verified and the previous settings are the restore target.
        Assert.Contains("() => ActivateHotkeySettings(hotkeys),", handler);
        Assert.Contains("IsHotkeysFullyRegistered,", handler);
        Assert.Contains("() => ActivateHotkeySettings(previousHotkeys)", handler);
        Assert.Contains("var previousHotkeys = _hotkeySettings;", handler);

        // Both failure paths are reported with the transaction's own message, never as success.
        Assert.Contains("if (rebind.Committed)", handler);
        Assert.Contains("rebind.DescribeForLog()", handler);
        Assert.Contains("rebind.DescribeProblem()", handler);
        Assert.Contains("AppLog.Write(\"hotkey settings save failed\", saveFailure);", handler);
        Assert.Contains("AppLog.Write(\"hotkey registration restore failed\", restoreFailure);", handler);
    }

    [Fact]
    public void RegistrationStillReturnsPerActionFailuresAndKeepsTheStartupDegradation()
    {
        var source = ReadMainWindow();

        // The startup path keeps per-action degradation: one occupied combination only costs its own
        // action, and the deferred-handle branch still leaves registration for the later retry.
        Assert.Contains("SourceInitialized += (_, _) => RegisterHotkeys();", source);
        Assert.Contains("private List<HotkeyRegistrationFailure> RegisterHotkeys()", source);
        Assert.Contains("global hotkeys: window handle unavailable, registration deferred", source);

        // Activating keeps the old release-then-register order but never releases the window hook:
        // the taskbar/shell notifications stay attached.
        var activate = SourceBetween(source, "private List<HotkeyRegistrationFailure> ActivateHotkeySettings(",
            "/// <summary>Releases only the live hotkey registrations");
        Assert.Contains("UnregisterHotkeyRegistrations();", activate);
        Assert.Contains("_hotkeySettings = settings;", activate);
        Assert.Contains("return RegisterHotkeys();", activate);
        Assert.DoesNotContain("ReleaseWindowHook", activate);
        Assert.DoesNotContain("UnregisterHotkeys()", activate);
    }
}
