using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

/// <summary>
/// Best-effort process inventory for the profile automation engine. Only processes whose name is watched by a rule
/// are described, and the executable path / command line are read only when a rule actually uses them.
/// <para>
/// Privacy: command lines live in this instance's memory for matching only. They are never logged, persisted,
/// shown in the diagnostics window or attached to diagnostic events.
/// </para>
/// <para>
/// Safety: an unreadable process becomes <see cref="ProfileAutomationProcessInfo.Denied"/> or a missing value so the
/// engine skips the rule instead of guessing. A failure never throws out of a sampling pass.
/// </para>
/// </summary>
internal sealed class ProfileAutomationProcessSampler
{
    private const int MaxCachedCommandLines = 256;

    // Upper bound for one WMI command-line lookup. The inventory now runs off the UI thread, but a stuck
    // lookup would still delay the 5 s automation pass, so each process gets a short, fixed budget and a
    // timeout simply marks that process' command-line condition as unavailable.
    private static readonly TimeSpan CommandLineQueryTimeout = TimeSpan.FromMilliseconds(500);

    // Command lines cannot change for a live process, so one successful/failed lookup per (pid, start time)
    // is enough to keep both WMI traffic and repeated access-denied probing off the sampling path.
    private readonly Dictionary<(int ProcessId, long StartTimeTicks), string?> _commandLineCache = new();

    /// <summary>
    /// Describes every running process the rules watch. <paramref name="needsExecutablePath"/> and
    /// <paramref name="needsCommandLine"/> keep the expensive reads limited to the rules that use them.
    /// </summary>
    public IReadOnlyList<ProfileAutomationProcessInfo> Capture(
        IReadOnlyCollection<string> watchedNames,
        bool needsExecutablePath,
        bool needsCommandLine)
    {
        var wanted = (watchedNames ?? [])
            .Select(ProfileAutomationProcessCondition.NormalizeProcessName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];

        var captured = new List<ProfileAutomationProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (!wanted.Contains(ProfileAutomationProcessCondition.NormalizeProcessName(name))) continue;
                captured.Add(Describe(process, name, needsExecutablePath, needsCommandLine));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
            {
                // The process exited between enumeration and inspection, or it is protected: skip it.
            }
            finally
            {
                process.Dispose();
            }
        }
        return captured;
    }

    private ProfileAutomationProcessInfo Describe(Process process, string name, bool needsExecutablePath, bool needsCommandLine)
    {
        string? executablePath = null;
        if (needsExecutablePath)
        {
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch (Win32Exception)
            {
                // Access denied (or an incompatible protected process): report it as denied so the rule is skipped.
                return ProfileAutomationProcessInfo.Denied(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
            {
                executablePath = null;
            }
        }

        var commandLine = needsCommandLine ? TryGetCommandLine(process) : null;
        return new ProfileAutomationProcessInfo(name, executablePath, commandLine);
    }

    private string? TryGetCommandLine(Process process)
    {
        int processId;
        long startTimeTicks;
        try
        {
            processId = process.Id;
            startTimeTicks = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_commandLineCache.TryGetValue((processId, startTimeTicks), out var cached)) return cached;
        var commandLine = QueryCommandLine(processId);
        if (_commandLineCache.Count >= MaxCachedCommandLines) _commandLineCache.Clear();
        _commandLineCache[(processId, startTimeTicks)] = commandLine;
        return commandLine;
    }

    /// <summary>Reads the command line through WMI. Any failure yields null so the engine skips the rule.</summary>
    private static string? QueryCommandLine(int processId)
    {
        try
        {
            var query = $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId.ToString(CultureInfo.InvariantCulture)}";
            using var searcher = new ManagementObjectSearcher(query);
            // Bounded per process: a throttled or stuck WMI repository must not stretch a sampling pass.
            // A timeout surfaces as ManagementException and only makes this one condition unavailable.
            searcher.Options.Timeout = CommandLineQueryTimeout;
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    if (item["CommandLine"] is string text && text.Length > 0) return text;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // WMI missing, throttled or denied: the command-line condition is skipped, never guessed.
            return null;
        }
    }
}
