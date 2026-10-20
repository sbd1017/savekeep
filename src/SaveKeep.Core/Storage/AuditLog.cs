using System.Text;
using System.Text.Json;

namespace SaveKeep.Core.Storage;

public sealed record AuditEntry(DateTimeOffset At, string Event, string Message, object? Detail = null);

/// <summary>Append-only JSON-lines log, flushed to disk on every entry.</summary>
public sealed class AuditLog
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public AuditLog(string dataDir, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "audit.jsonl");
        _clock = clock ?? TimeProvider.System;
    }

    public string FilePath => _path;

    public void Write(string evt, string message, object? detail = null)
    {
        var line = JsonSerializer.Serialize(new AuditEntry(_clock.GetUtcNow(), evt, message, detail), Json) + "\n";
        lock (_lock)
        {
            using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(Encoding.UTF8.GetBytes(line));
            fs.Flush(flushToDisk: true);
        }
    }

    /// <summary>The last <paramref name="count"/> entries, newest first. Damaged lines are skipped.</summary>
    public IReadOnlyList<JsonElement> Tail(int count)
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            var list = new List<JsonElement>();
            foreach (var line in File.ReadLines(_path).TakeLast(count).Reverse())
            {
                try { list.Add(JsonDocument.Parse(line).RootElement.Clone()); }
                catch (JsonException) { }
            }
            return list;
        }
    }
}
