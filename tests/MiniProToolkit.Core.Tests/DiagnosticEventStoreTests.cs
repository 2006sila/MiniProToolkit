using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class DiagnosticEventStoreTests
{
    [Fact]
    public void AppendsReadsRecentAndSkipsMalformedLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniProToolkitDiagnosticTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "events.jsonl");
        try
        {
            var store = new DiagnosticEventStore(path);
            store.Append("Recovery", "automatic restore requested", "Guardian");
            store.Append("Error", "sensor unavailable");
            File.AppendAllText(path, "{broken\n");
            var events = store.ReadRecent();
            Assert.Equal(2, events.Count);
            Assert.Equal("Guardian", events[0].Source);
            Assert.Equal("Error", events[1].Category);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadRecent(0));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
