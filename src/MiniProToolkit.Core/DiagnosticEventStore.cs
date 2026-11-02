using System.Text;
using System.Text.Json;

namespace MiniProToolkit.Core;

public sealed record DiagnosticEventRecord(DateTimeOffset Timestamp, string Category, string Message, string Source);

/// <summary>Append-only, bounded local journal for control and recovery diagnostics.</summary>
public sealed class DiagnosticEventStore
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int RetainedFiles = 4;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly object Sync = new();
    public DiagnosticEventStore(string? path = null) => Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniProToolkit", "history", "diagnostic-events.jsonl");
    public string Path { get; }

    public void Append(string category, string message, string source = "MiniProToolkit")
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("事件类别不能为空。", nameof(category));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("事件内容不能为空。", nameof(message));
        var record = new DiagnosticEventRecord(DateTimeOffset.UtcNow, category.Trim(), message.Trim(), source);
        lock (Sync)
        {
            var directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("诊断事件路径无效。");
            Directory.CreateDirectory(directory);
            if (File.Exists(Path) && new FileInfo(Path).Length >= MaxFileBytes) Rotate();
            File.AppendAllText(Path, JsonSerializer.Serialize(record, Options) + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public IReadOnlyList<DiagnosticEventRecord> ReadRecent(int maximum = 200)
    {
        if (maximum is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (Sync)
        {
            var events = new List<DiagnosticEventRecord>();
            foreach (var file in Enumerable.Range(1, RetainedFiles).Reverse().Select(i => $"{Path}.{i}").Append(Path))
            {
                if (!File.Exists(file)) continue;
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        try { if (JsonSerializer.Deserialize<DiagnosticEventRecord>(line, Options) is { } item) events.Add(item); }
                        catch (JsonException) { }
                    }
                }
                catch (IOException) { }
            }
            return events.OrderByDescending(e => e.Timestamp).Take(maximum).OrderBy(e => e.Timestamp).ToArray();
        }
    }

    private void Rotate()
    {
        var oldest = $"{Path}.{RetainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = RetainedFiles - 1; i >= 1; i--)
        {
            var source = $"{Path}.{i}";
            if (File.Exists(source)) File.Move(source, $"{Path}.{i + 1}", true);
        }
        File.Move(Path, $"{Path}.1", true);
    }
}
