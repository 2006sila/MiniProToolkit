using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Source guards for the taskbar enumeration boundary: only the primary shell taskbar
/// (<c>Shell_TrayWnd</c>) may become an overlay host. <c>SHAppBarMessage(ABM_GETTASKBARPOS)</c>
/// answers for the primary appbar only, so enumerating a <c>Shell_SecondaryTrayWnd</c> handle and
/// asking it for the appbar rectangle returns the primary rectangle - the duplicated-overlay bug.
/// The filter is a class-name comparison; no secondary taskbar is mounted or reparented and the
/// primary placement API stays in place. These checks read source text only: they never call
/// Win32, construct a WPF window, touch a log file, start a GUI or need a multi-monitor desktop.
/// </summary>
public sealed class TaskbarEnumerationMarkupTests
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

    private static string ReadNativeMethods() => File.ReadAllText(FindAppSource("NativeMethods.cs"));

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

    private static string EnumerationBlock(string source) =>
        SourceBetween(source, "internal static IEnumerable<IntPtr> EnumerateTaskbars()",
            "internal static (AppBarEdge Edge, Rect Rect) GetTaskbarPosition(IntPtr taskbar)");

    [Fact]
    public void EnumerateTaskbarsAcceptsOnlyThePrimaryShellTaskbar()
    {
        var block = EnumerationBlock(ReadNativeMethods());

        // The single comparison is the whole filter: the primary class name only.
        Assert.Contains("if (className == \"Shell_TrayWnd\")", block);
        Assert.DoesNotContain("Shell_SecondaryTrayWnd", block);
        Assert.DoesNotContain("is \"Shell_TrayWnd\" or", block);
        Assert.Equal(1, CountOccurrences(block, "Shell_TrayWnd"));

        // EnumWindows is still the walk and every accepted handle is returned unchanged.
        Assert.Contains("EnumWindows(", block);
        Assert.Contains("result.Add(windowHandle);", block);
        Assert.Contains("return true;", block);
        Assert.Contains("return result;", block);
    }

    [Fact]
    public void EnumerationIsSideEffectFreeAndMountsNoSecondaryWindow()
    {
        var block = EnumerationBlock(ReadNativeMethods());

        // Enumeration may not create, show, reposition or reparent anything, and it may not already
        // probe appbar geometry - the placement API is only used later for the one accepted handle.
        Assert.DoesNotContain("SetParent", block);
        Assert.DoesNotContain("SHAppBarMessage", block);
        Assert.DoesNotContain("GetTaskbarPosition", block);
        Assert.DoesNotContain("SetWindowPos", block);
        Assert.DoesNotContain("ShowWindow", block);
        Assert.DoesNotContain("MonitorFrom", block);
        Assert.DoesNotContain("TaskbarStatusWindow", block);
        Assert.DoesNotContain("FloatingStatusWindow", block);
    }

    [Fact]
    public void PrimaryAppbarPlacementApiIsPreserved()
    {
        var native = ReadNativeMethods();
        Assert.Contains("internal const uint AbmGetTaskbarPos = 0x00000005;", native);

        // The one retained placement API is still ABM_GETTASKBARPOS on the passed handle, with the
        // existing empty-rect fallback for an unusable appbar (auto-hidden/restarting shell).
        var placement = SourceBetween(native,
            "internal static (AppBarEdge Edge, Rect Rect) GetTaskbarPosition(IntPtr taskbar)",
            "private static string? GetClassName");
        Assert.Contains("HWnd = taskbar", placement);
        Assert.Contains("SHAppBarMessage(AbmGetTaskbarPos, ref data)", placement);
        Assert.Contains("return ((AppBarEdge)data.Edge, data.Rect);", placement);
        Assert.Contains("return (AppBarEdge.Bottom, new Rect());", placement);
        Assert.Equal(1, CountOccurrences(native, "SHAppBarMessage(AbmGetTaskbarPos"));
    }

    [Fact]
    public void EnsureTaskbarsMountsOnlyTheEnumeratedHandles()
    {
        var source = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        var ensure = SourceBetween(source, "private void EnsureTaskbars()", "private void OpenTaskbarSettingsFromMenu()");

        // One strip per enumerated handle, one creation site, no secondary class name or
        // Explorer reparenting in the mount path: the enumeration filter is the only veto.
        Assert.Contains("foreach (var handle in handles)", ensure);
        Assert.Equal(1, CountOccurrences(source, "new TaskbarStatusWindow("));
        Assert.DoesNotContain("Shell_SecondaryTrayWnd", ensure);
        Assert.DoesNotContain("SetParent", ensure);
    }
}
