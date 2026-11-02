using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MiniProToolkit.App;

internal static class NativeMethods
{
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const int WsPopup = unchecked((int)0x80000000);
    internal const int WsChild = 0x40000000;
    internal const int WsVisible = 0x10000000;
    internal const int WsClipChildren = 0x02000000;
    internal const int WsClipSiblings = 0x04000000;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExToolWindow = 0x00000080;
    internal const uint WmHotKey = 0x0312;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SwpFrameChanged = 0x0020;
    internal static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder imagePath,
        ref int imagePathLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// Confirms that a connected command pipe is served by a process with the same
    /// executable image as this client. Failure is deliberately fail-closed: a
    /// connected same-user pipe is not trusted merely because it has the expected name.
    /// </summary>
    internal static bool IsExpectedCommandPipeServer(SafePipeHandle pipe)
    {
        if (pipe is null || pipe.IsInvalid || pipe.IsClosed)
            return false;

        try
        {
            if (!GetNamedPipeServerProcessId(pipe, out var serverProcessId) || serverProcessId == 0)
                return false;

            var expectedPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(expectedPath))
                return false;

            var process = OpenProcess(ProcessQueryLimitedInformation, false, serverProcessId);
            if (process == IntPtr.Zero)
                return false;
            try
            {
                var buffer = new StringBuilder(1024);
                var length = buffer.Capacity;
                if (!QueryFullProcessImageName(process, 0, buffer, ref length))
                    return false;

                var actualPath = buffer.ToString(0, length);
                return string.Equals(
                    Path.GetFullPath(actualPath),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                _ = CloseHandle(process);
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(
        IntPtr parentHandle,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetParent(IntPtr childHandle, IntPtr parentHandle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetClientRect(IntPtr windowHandle, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr windowHandle, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ScreenToClient(IntPtr windowHandle, ref Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    /// <summary>
    /// Registers the shell's <c>TaskbarCreated</c> broadcast and returns its message token, or
    /// <c>0</c> when the registration is unavailable. The token is process-wide and constant
    /// until the process exits, so it is registered once per hook lifetime and never guessed.
    /// </summary>
    internal static int RegisterTaskbarCreatedMessage()
    {
        try { return unchecked((int)RegisterWindowMessage(Core.TaskbarRefreshPolicy.TaskbarCreatedMessageName)); }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

    internal const uint AbmGetTaskbarPos = 0x00000005;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const int MonitorDpiTypeEffective = 0;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    internal static double GetMonitorScale(IntPtr monitor)
    {
        try { return GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0; }
        catch (DllNotFoundException) { return 1.0; }
        catch (EntryPointNotFoundException) { return 1.0; }
    }

    internal enum AppBarEdge : uint
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    internal delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr windowHandle);

    internal static IEnumerable<IntPtr> EnumerateTaskbars()
    {
        var result = new List<IntPtr>();
        EnumWindows((windowHandle, _) =>
        {
            var className = GetClassName(windowHandle);
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                result.Add(windowHandle);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    internal static (AppBarEdge Edge, Rect Rect) GetTaskbarPosition(IntPtr taskbar)
    {
        var data = new AppBarData
        {
            CbSize = Marshal.SizeOf<AppBarData>(),
            HWnd = taskbar
        };
        if (SHAppBarMessage(AbmGetTaskbarPos, ref data) != IntPtr.Zero)
            return ((AppBarEdge)data.Edge, data.Rect);
        return (AppBarEdge.Bottom, new Rect());
    }

    private static string? GetClassName(IntPtr windowHandle)
    {
        var buffer = new System.Text.StringBuilder(256);
        return GetClassName(windowHandle, buffer, buffer.Capacity) == 0
            ? null
            : buffer.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr windowHandle, System.Text.StringBuilder className, int maxCount);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        internal int CbSize;
        internal IntPtr HWnd;
        internal uint CallbackMessage;
        internal uint Edge;
        internal Rect Rect;
        internal int Param;
    }
}
