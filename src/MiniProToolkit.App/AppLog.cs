using System.IO;

namespace MiniProToolkit.App;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniProToolkit",
        "miniprotoolkit.log");

    public static string Path => LogPath;

    public static void Write(string message, Exception? exception = null)
    {
        var details = $"{message}{(exception is null ? "" : ": " + exception.GetBaseException().Message)}";
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {details}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never block monitoring or shutdown.
        }
    }
}
