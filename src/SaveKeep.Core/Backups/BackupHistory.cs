using System.Text.Json;
using SaveKeep.Core.Storage;

namespace SaveKeep.Core.Backups;

/// <summary>When a backup was first listed (server clock), and its size then.</summary>
public sealed record SeenBackup(DateTimeOffset FirstSeenAt, long? Size);

/// <summary>
/// Backups seen in earlier FTP searches, per game. Backup names and file dates come from the console's clock,
/// which can be wrong (a 3DS still set to 2011, an ftpd without file dates), so "which backup is new" is
/// decided by what appeared since the previous search, timed by the server.
/// </summary>
public sealed class BackupHistory
{
    private readonly string? _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _games;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed class Entry
    {
        public DateTimeOffset LastSearchAt { get; set; }
        public Dictionary<string, SeenBackup> Seen { get; set; } = [];
    }

    /// <param name="dataDir">Where <c>backup-history.json</c> lives; null keeps the history in memory only.</param>
    public BackupHistory(string? dataDir)
    {
        _path = dataDir is null ? null : Path.Combine(dataDir, "backup-history.json");
        _games = Load(_path);
    }

    public static string KeyFor(BackupTarget target) => $"{target.Platform}:{target.TitleId.ToUpperInvariant()}";
    public static string NameKey(BackupCandidate c) => $"{c.GameFolder}/{c.Name}";

    /// <summary>The state before this search, or null if this game was never searched.</summary>
    public Entry? Previous(string gameKey)
    {
        lock (_lock)
            return _games.TryGetValue(gameKey, out var e)
                ? new Entry { LastSearchAt = e.LastSearchAt, Seen = new Dictionary<string, SeenBackup>(e.Seen) }
                : null;
    }

    /// <summary>Records every backup listed in this search. Existing first-seen times are kept.</summary>
    public void Record(string gameKey, IEnumerable<BackupCandidate> listed, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_games.TryGetValue(gameKey, out var e)) _games[gameKey] = e = new Entry();
            foreach (var c in listed)
            {
                var key = NameKey(c);
                if (e.Seen.TryGetValue(key, out var old))
                    e.Seen[key] = old with { Size = c.Size ?? old.Size };
                else
                    e.Seen[key] = new SeenBackup(now, c.Size);
            }
            e.LastSearchAt = now;
            if (_path is not null) AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_games, Json));
        }
    }

    private static Dictionary<string, Entry> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { return []; } // a damaged history only costs the suggestion
    }
}
