using System.Text;

namespace MiniProToolkit.Core;

/// <summary>
/// Bounded, line-oriented UTF-8 text log. Appends are serialized per instance, the live file is
/// rotated by byte size and old files are rotated by rename only (never read into memory). A
/// single oversized entry is truncated on a UTF-8 character boundary and marked, so a huge
/// exception message cannot grow the log past its configured cap.
/// </summary>
public sealed class BoundedTextLog
{
    public const int DefaultMaximumFileBytes = 2 * 1024 * 1024;
    public const int DefaultRetainedFiles = 3;
    public const int DefaultMaximumEntryBytes = 32 * 1024;

    // Configuration bounds keep a caller from turning the log into an unbounded file or an
    // effectively untruncated entry while still allowing small temp-directory tests.
    private const int MinimumFileBytes = 1024;
    private const int MaximumFileBytesLimit = 64 * 1024 * 1024;
    private const int MaximumRetainedFilesLimit = 16;
    private const int MinimumEntryBytes = 256;
    private const int MaximumEntryBytesLimit = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _sync = new();
    private readonly int _maximumFileBytes;
    private readonly int _retainedFiles;
    private readonly int _maximumEntryBytes;

    public BoundedTextLog(string path, int maximumFileBytes = DefaultMaximumFileBytes,
        int retainedFiles = DefaultRetainedFiles, int maximumEntryBytes = DefaultMaximumEntryBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("日志路径不能为空。", nameof(path));
        if (maximumFileBytes is < MinimumFileBytes or > MaximumFileBytesLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (retainedFiles is < 1 or > MaximumRetainedFilesLimit)
            throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        if (maximumEntryBytes is < MinimumEntryBytes or > MaximumEntryBytesLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumEntryBytes));
        if (maximumEntryBytes > maximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumEntryBytes), "单条上限不能大于文件上限。");
        Path = System.IO.Path.GetFullPath(path);
        _maximumFileBytes = maximumFileBytes;
        _retainedFiles = retainedFiles;
        _maximumEntryBytes = maximumEntryBytes;
    }

    public string Path { get; }

    /// <summary>Appends one entry. Throws only argument/IO errors; callers such as AppLog swallow them.</summary>
    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entry = BoundEntry(text);
        var entryBytes = Utf8.GetByteCount(entry);
        lock (_sync)
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var info = new FileInfo(Path);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("日志文件不能是链接。");
            // A pre-existing oversized file (from before this cap) is rotated once, not trimmed.
            if (info.Exists && info.Length > 0 && info.Length + entryBytes > _maximumFileBytes)
                Rotate();
            File.AppendAllText(Path, entry, Utf8);
        }
    }

    private string BoundEntry(string text)
    {
        var line = text + Environment.NewLine;
        if (Utf8.GetByteCount(line) <= _maximumEntryBytes) return line;
        var marker = $"…[日志条目超过 {_maximumEntryBytes} 字节，已截断]{Environment.NewLine}";
        var budget = Math.Max(0, _maximumEntryBytes - Utf8.GetByteCount(marker));
        return TruncateUtf8Boundary(text, budget) + marker;
    }

    /// <summary>Largest prefix of <paramref name="text"/> whose UTF-8 encoding fits in <paramref name="maximumBytes"/>.</summary>
    internal static string TruncateUtf8Boundary(string text, int maximumBytes)
    {
        if (maximumBytes <= 0) return string.Empty;
        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.Utf8SequenceLength;
            if (bytes + length > maximumBytes) break;
            bytes += length;
            chars += rune.Utf16SequenceLength;
        }
        return chars == text.Length ? text : text[..chars];
    }

    private void Rotate()
    {
        var oldest = $"{Path}.{_retainedFiles}";
        if (File.Exists(oldest))
        {
            if ((File.GetAttributes(oldest) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("日志归档不能是链接。");
            File.Delete(oldest);
        }
        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{Path}.{index}";
            if (!File.Exists(source)) continue;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("日志归档不能是链接。");
            File.Move(source, $"{Path}.{index + 1}", true);
        }
        File.Move(Path, $"{Path}.1", true);
    }
}
