using System.IO;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

internal static class AppLog
{
    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniProToolkit",
        "miniprotoolkit.log");

    // 2 MiB live file + 3 rotated archives, 32 KiB per entry. Rotation keeps the size work off
    // the per-line hot path: the normal append is a single AppendAllText, and a full live file
    // triggers a rename-once rotation instead of unbounded growth.
    private static readonly BoundedTextLog Log = new(
        LogPath,
        BoundedTextLog.DefaultMaximumFileBytes,
        BoundedTextLog.DefaultRetainedFiles,
        BoundedTextLog.DefaultMaximumEntryBytes);

    public static string Path => LogPath;

    public static void Write(string message, Exception? exception = null)
    {
        var details = $"{message}{(exception is null ? "" : ": " + exception.GetBaseException().Message)}";
        try
        {
            // Same line shape as before: "2026-11-02T17:03:04.5678901+08:00 message".
            Log.Append($"{DateTimeOffset.Now:O} {details}");
        }
        catch
        {
            // Logging must never block monitoring or shutdown.
        }
    }
}
