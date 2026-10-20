using System.Text.Json;
using System.Text.RegularExpressions;
using PKHeX.Core;
using SaveKeep.Core.Games;
using SaveKeep.Core.Saves;
using SaveKeep.Core.Storage;
using SaveKeep.Core.Util;

namespace SaveKeep.Core.Vault;

/// <summary>Where a save in the vault came from.</summary>
public static class VaultSources
{
    /// <summary>Uploaded through the web page.</summary>
    public const string Upload = "upload";
    /// <summary>Copied from the console over read-only FTP.</summary>
    public const string FtpFetch = "ftp-fetch";
    /// <summary>Sent by JKSV on the Switch to the WebDAV receiver.</summary>
    public const string JksvWebDav = "jksv-webdav";
}

public static class VaultEventKinds
{
    /// <summary>An identical save arrived again.</summary>
    public const string Seen = "seen";
    public const string Pinned = "pinned";
    public const string Unpinned = "unpinned";
}

/// <summary>Points at one vault entry.</summary>
public sealed record VaultRef(string Game, string Id)
{
    public override string ToString() => $"{Game}/{Id}";
}

/// <summary>The description of a stored save (<c>vault.json</c>), written once together with the save.</summary>
public sealed record VaultRecord
{
    public required string Id { get; init; }
    /// <summary>Folder name under <c>vault/</c>, from the save's game version (e.g. <c>sw</c>, <c>us</c>).</summary>
    public required string Game { get; init; }
    public required string Edition { get; init; }
    /// <summary>Switch, 3DS or Other. Decides which restore steps apply (JKSV or Checkpoint).</summary>
    public required string Platform { get; init; }
    public string? TitleId { get; init; }
    public required TrainerSummary Trainer { get; init; }
    public required int BoxPokemon { get; init; }
    public required int PartyCount { get; init; }
    public required string SaveType { get; init; }
    public required string Sha256 { get; init; }
    public required int Size { get; init; }
    /// <summary>How the save first reached the vault (see <see cref="VaultSources"/>).</summary>
    public required string Source { get; init; }
    public string? SourceDetail { get; init; }
    /// <summary>The server's clock when the save was stored. Console clocks are never used for ordering.</summary>
    public required DateTimeOffset ArchivedAt { get; init; }
    /// <summary>SHA-256 of the JKSV zip the save came in, kept next to it as <c>original.zip</c>.</summary>
    public string? OriginalZipSha256 { get; init; }
}

/// <summary>Something that happened to an entry after it was stored, kept as its own write-once file.</summary>
public sealed record VaultEvent(DateTimeOffset At, string Kind, string? Source = null, string? Detail = null);

/// <summary>An entry as listed: the record, every later event, its size on disk, and whether it is pinned.</summary>
public sealed record VaultEntry(VaultRecord Record, IReadOnlyList<VaultEvent> Events, long Bytes, bool HasOriginalZip)
{
    public VaultRef Ref => new(Record.Game, Record.Id);

    public IReadOnlyList<string> Sources =>
        [Record.Source, .. Events.Where(e => e.Kind == VaultEventKinds.Seen && e.Source is not null).Select(e => e.Source!).Distinct().Where(s => s != Record.Source)];

    /// <summary>The latest pin or unpin decides. A pinned entry is never pruned.</summary>
    public VaultEvent? Pin => Events.LastOrDefault(e => e.Kind is VaultEventKinds.Pinned or VaultEventKinds.Unpinned) is { Kind: VaultEventKinds.Pinned } p ? p : null;
    public bool Pinned => Pin is not null;
}

public sealed record VaultAddResult(VaultEntry Entry, bool Added, IReadOnlyList<VaultRef> Pruned);

public sealed record VaultOptions
{
    public int KeepPerGame { get; init; } = 20;
    public long MaxTotalBytes { get; init; } = 1024L * 1024 * 1024;
}

/// <summary>
/// Write-once store of saves: <c>vault/&lt;game&gt;/&lt;utc-stamp&gt;-&lt;sha8&gt;/</c> holding <c>main</c>,
/// <c>vault.json</c>, optionally the JKSV <c>original.zip</c>, and <c>events/*.json</c>. No file is ever changed;
/// later facts (seen again, pinned, unpinned) are new files. Saves are de-duplicated by the SHA-256 of <c>main</c>.
/// </summary>
public sealed partial class SaveVault
{
    public const string MainFileName = "main";
    public const string RecordFileName = "vault.json";
    public const string OriginalZipFileName = "original.zip";
    public const int MaxNoteLength = 200;
    private const string EventsDir = "events";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly AuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();

    public string Root { get; }
    public VaultOptions Options { get; }
    public DateTimeOffset Now => _clock.GetUtcNow();

    public SaveVault(string dataDir, AuditLog audit, VaultOptions? options = null, TimeProvider? clock = null)
    {
        Root = Path.GetFullPath(Path.Combine(dataDir, "vault"));
        Directory.CreateDirectory(Root);
        _audit = audit;
        Options = options ?? new VaultOptions();
        _clock = clock ?? TimeProvider.System;
    }

    [GeneratedRegex("^[a-z0-9]{1,16}$")]
    private static partial Regex GamePattern();
    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$")]
    private static partial Regex IdPattern();

    public static bool IsValidRef(string? game, string? id) =>
        game is not null && id is not null && GamePattern().IsMatch(game) && IdPattern().IsMatch(id);

    // ---------------------------------------------------------------- adding

    /// <summary>
    /// Stores a save, or records that an identical one (same SHA-256 of <c>main</c>) was seen again.
    /// <paramref name="game"/>, if given, is the game the file must belong to. Throws <see cref="InvalidDataException"/>
    /// if the file is not a save.
    /// </summary>
    public VaultAddResult Add(byte[] main, string source, string? detail, KnownGame? game = null, byte[]? originalZip = null)
    {
        var sav = game is null ? SaveReader.Read(main) : SaveReader.ReadAs(main, game);
        var sha = Hex.Sha256(main);
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            if (FindBySha(sha) is { } existing)
            {
                WriteEvent(existing.Ref, new VaultEvent(now, VaultEventKinds.Seen, source, detail));
                _audit.Write("vault-seen", $"{existing.Ref}: identical save from {source} (sha256 {sha[..12]}); not stored twice", new { game = existing.Record.Game, id = existing.Record.Id, source, detail });
                return new VaultAddResult(Load(existing.Ref)!, false, []);
            }

            var key = GameKey(sav);
            var id = UniqueId(key, now, sha);
            var dir = EntryDir(new VaultRef(key, id));
            var (platform, titleId, edition) = SaveReader.Describe(sav);
            var record = new VaultRecord
            {
                Id = id, Game = key, Edition = edition, Platform = platform, TitleId = titleId,
                Trainer = TrainerSummary.From(sav), BoxPokemon = SaveReader.CountBoxPokemon(sav), PartyCount = sav.PartyCount,
                SaveType = sav.GetType().Name, Sha256 = sha, Size = main.Length, Source = source, SourceDetail = detail, ArchivedAt = now,
                OriginalZipSha256 = originalZip is null ? null : Hex.Sha256(originalZip),
            };
            // Staged in a hidden folder and renamed into place, so a half-written entry is never listed.
            var staging = Path.Combine(Path.GetDirectoryName(dir)!, $".incoming-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            AtomicFile.WriteNew(Path.Combine(staging, MainFileName), main);
            if (originalZip is not null) AtomicFile.WriteNew(Path.Combine(staging, OriginalZipFileName), originalZip);
            AtomicFile.WriteNew(Path.Combine(staging, RecordFileName), JsonSerializer.SerializeToUtf8Bytes(record, Json));
            Directory.Move(staging, dir);
            _audit.Write("vault-add", $"{key}/{id}: {record.Edition} {record.SaveType}, trainer {record.Trainer.Name} ({record.Trainer.DisplayTid}), {record.BoxPokemon} Pokémon, from {source}",
                new { game = key, id, sha256 = sha, record.Size, source, detail, originalZip = record.OriginalZipSha256 });

            var pruned = PruneLocked(keep: new VaultRef(key, id));
            return new VaultAddResult(Load(new VaultRef(key, id))!, true, pruned);
        }
    }

    // ---------------------------------------------------------------- pinning

    /// <summary>Pins an entry so retention never removes it. The note says why, e.g. "before trading".</summary>
    public VaultEntry Pin(VaultRef entry, string? note)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : new string(note.Trim().Where(c => !char.IsControl(c)).Take(MaxNoteLength).ToArray());
        lock (_lock)
        {
            var e = Load(entry) ?? throw new FileNotFoundException("Vault entry not found.");
            WriteEvent(entry, new VaultEvent(_clock.GetUtcNow(), VaultEventKinds.Pinned, Detail: note));
            _audit.Write("vault-pin", $"{entry}: pinned{(note is null ? "" : $" ({note})")}", new { game = entry.Game, id = entry.Id, note });
            return Load(e.Ref)!;
        }
    }

    public VaultEntry Unpin(VaultRef entry)
    {
        lock (_lock)
        {
            var e = Load(entry) ?? throw new FileNotFoundException("Vault entry not found.");
            if (!e.Pinned) return e;
            WriteEvent(entry, new VaultEvent(_clock.GetUtcNow(), VaultEventKinds.Unpinned));
            _audit.Write("vault-unpin", $"{entry}: unpinned", new { game = entry.Game, id = entry.Id });
            var after = Load(e.Ref)!;
            PruneLocked(keep: null);
            return after;
        }
    }

    // ---------------------------------------------------------------- reading

    public IReadOnlyList<VaultEntry> List()
    {
        lock (_lock)
        {
            var list = new List<VaultEntry>();
            foreach (var gameDir in Directory.GetDirectories(Root))
            {
                var game = Path.GetFileName(gameDir);
                if (!GamePattern().IsMatch(game)) continue;
                foreach (var entryDir in Directory.GetDirectories(gameDir))
                    if (Load(new VaultRef(game, Path.GetFileName(entryDir))) is { } e) list.Add(e);
            }
            return list.OrderByDescending(e => e.Record.ArchivedAt).ThenByDescending(e => e.Record.Id, StringComparer.Ordinal).ToList();
        }
    }

    public VaultEntry? Load(VaultRef entry)
    {
        if (!IsValidRef(entry.Game, entry.Id)) return null;
        var dir = EntryDir(entry);
        var recordPath = Path.Combine(dir, RecordFileName);
        if (!File.Exists(recordPath)) return null;
        VaultRecord? record;
        try { record = JsonSerializer.Deserialize<VaultRecord>(File.ReadAllText(recordPath)); }
        catch (JsonException) { return null; }
        if (record is null || record.Id != entry.Id || record.Game != entry.Game) return null;

        var events = new List<VaultEvent>();
        var eventsDir = Path.Combine(dir, EventsDir);
        if (Directory.Exists(eventsDir))
            foreach (var f in Directory.GetFiles(eventsDir, "*.json").Order(StringComparer.Ordinal))
            {
                try { if (JsonSerializer.Deserialize<VaultEvent>(File.ReadAllText(f)) is { } ev) events.Add(ev); }
                catch (JsonException) { } // a damaged event file only loses that event
            }
        long bytes = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return new VaultEntry(record, events, bytes, File.Exists(Path.Combine(dir, OriginalZipFileName)));
    }

    /// <summary>
    /// The stored <c>main</c>, after checking it still has the SHA-256 recorded when it was stored.
    /// Returns null for an unknown or malformed reference; nothing outside the vault is ever opened.
    /// </summary>
    public byte[]? ReadMain(VaultRef entry)
    {
        var e = Load(entry);
        if (e is null) return null;
        var bytes = File.ReadAllBytes(Path.Combine(EntryDir(entry), MainFileName));
        if (Hex.Sha256(bytes) != e.Record.Sha256) throw new InvalidDataException($"The stored save {entry} no longer matches its recorded SHA-256; it was changed on disk.");
        return bytes;
    }

    /// <summary>The JKSV zip the save came in, if it came as one, checked against its recorded hash.</summary>
    public byte[]? ReadOriginalZip(VaultRef entry)
    {
        var e = Load(entry);
        if (e is not { HasOriginalZip: true, Record.OriginalZipSha256: { } expected }) return null;
        var bytes = File.ReadAllBytes(Path.Combine(EntryDir(entry), OriginalZipFileName));
        if (Hex.Sha256(bytes) != expected) throw new InvalidDataException($"The stored zip of {entry} no longer matches its recorded SHA-256.");
        return bytes;
    }

    /// <summary>JKSV's own naming, "&lt;user&gt; - yyyy-MM-dd_HH-mm-ss", with anything unsafe in a file name replaced.</summary>
    public static string JksvName(VaultRecord r)
    {
        var who = new string(r.Trainer.Name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim();
        return $"{(who.Length == 0 ? "save" : who)} - {r.ArchivedAt.UtcDateTime:yyyy-MM-dd_HH-mm-ss}";
    }

    // ---------------------------------------------------------------- retention

    /// <summary>Deletes one entry on request. A pinned entry also needs <paramref name="allowPinned"/>.</summary>
    public void Delete(VaultRef entry, bool allowPinned, string reason)
    {
        lock (_lock)
        {
            var e = Load(entry) ?? throw new FileNotFoundException("Vault entry not found.");
            if (e.Pinned && !allowPinned)
                throw new InvalidOperationException($"This save is pinned{(e.Pin!.Detail is { } note ? $" ({note})" : "")}. Confirm that you want to delete it anyway.");
            Remove(e, "vault-delete", reason);
        }
    }

    /// <summary>Applies the retention rules now. Returns what was removed.</summary>
    public IReadOnlyList<VaultRef> Prune()
    {
        lock (_lock) return PruneLocked(keep: null);
    }

    /// <summary>Total bytes on disk, and whether the limits can't be met because the rest is pinned or newest.</summary>
    public (long Bytes, bool OverLimit) Usage()
    {
        var all = List();
        long bytes = all.Sum(e => e.Bytes);
        bool over = bytes > Options.MaxTotalBytes || all.GroupBy(e => e.Record.Game).Any(g => g.Count() > Options.KeepPerGame);
        return (bytes, over);
    }

    /// <summary>
    /// Per game, keep the newest <see cref="VaultOptions.KeepPerGame"/>; then keep the total under
    /// <see cref="VaultOptions.MaxTotalBytes"/>, oldest first. Never removed automatically: pinned entries,
    /// the newest entry of each game, and the entry just added.
    /// </summary>
    private List<VaultRef> PruneLocked(VaultRef? keep)
    {
        var all = List().ToList();
        var newestPerGame = all.GroupBy(e => e.Record.Game).Select(g => g.First().Ref).ToHashSet(); // List() is newest first
        bool Prunable(VaultEntry e) => !e.Pinned && !newestPerGame.Contains(e.Ref) && e.Ref != keep;
        static IEnumerable<VaultEntry> Oldest(IEnumerable<VaultEntry> xs) => xs.OrderBy(e => e.Record.ArchivedAt).ThenBy(e => e.Record.Id, StringComparer.Ordinal);

        var removed = new List<VaultRef>();
        var stuck = new List<string>();
        foreach (var game in all.GroupBy(e => e.Record.Game).ToList())
        {
            int excess = game.Count() - Options.KeepPerGame;
            if (excess <= 0) continue;
            var victims = Oldest(game.Where(Prunable)).Take(excess).ToList();
            foreach (var v in victims)
            {
                Remove(v, "vault-prune", $"more than {Options.KeepPerGame} saves for {v.Record.Edition}");
                removed.Add(v.Ref);
                all.Remove(v);
            }
            if (victims.Count < excess) stuck.Add($"{game.Key}: {game.Count() - victims.Count} saves kept (limit {Options.KeepPerGame}) because the rest are pinned");
        }

        long total = all.Sum(e => e.Bytes);
        if (total > Options.MaxTotalBytes)
        {
            foreach (var v in Oldest(all.Where(Prunable)).ToList())
            {
                if (total <= Options.MaxTotalBytes) break;
                Remove(v, "vault-prune", $"vault over its size limit ({Options.MaxTotalBytes / (1024 * 1024)} MB)");
                removed.Add(v.Ref);
                all.Remove(v);
                total -= v.Bytes;
            }
            if (total > Options.MaxTotalBytes) stuck.Add($"total {total} bytes is over the {Options.MaxTotalBytes}-byte limit; the remaining saves are pinned or the newest of their game");
        }
        if (stuck.Count > 0) _audit.Write("vault-over-limit", string.Join("; ", stuck));
        return removed;
    }

    private void Remove(VaultEntry e, string evt, string reason)
    {
        var dir = EntryDir(e.Ref);
        // Renamed out of the listing first, so a failure part way through never leaves a half entry visible.
        var trash = Path.Combine(Path.GetDirectoryName(dir)!, $".removing-{e.Record.Id}-{Guid.NewGuid():N}");
        Directory.Move(dir, trash);
        Directory.Delete(trash, recursive: true);
        _audit.Write(evt, $"{e.Ref}: {e.Record.Edition}, trainer {e.Record.Trainer.Name}, stored {e.Record.ArchivedAt:yyyy-MM-dd HH:mm:ss}Z ({reason})",
            new { game = e.Record.Game, id = e.Record.Id, sha256 = e.Record.Sha256, pinned = e.Pinned, reason });
    }

    // ---------------------------------------------------------------- helpers

    private VaultEntry? FindBySha(string sha) => List().FirstOrDefault(e => e.Record.Sha256 == sha);

    private string EntryDir(VaultRef r)
    {
        if (!IsValidRef(r.Game, r.Id)) throw new ArgumentException("Invalid vault reference.");
        var path = Path.GetFullPath(Path.Combine(Root, r.Game, r.Id));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("Invalid vault reference.");
        return path;
    }

    private string UniqueId(string game, DateTimeOffset now, string sha)
    {
        // Same second and same hash prefix only happens for different saves whose hashes collide there; step the stamp.
        var t = now;
        while (true)
        {
            var id = $"{t.UtcDateTime:yyyyMMdd-HHmmss}-{sha[..8]}";
            if (!Directory.Exists(Path.Combine(Root, game, id))) return id;
            t = t.AddSeconds(1);
        }
    }

    // Callers hold _lock. The leading counter keeps events in order even when two share a timestamp.
    private void WriteEvent(VaultRef entry, VaultEvent ev)
    {
        var dir = Path.Combine(EntryDir(entry), EventsDir);
        int seq = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Length : 0;
        var name = $"{seq:D6}-{ev.At.UtcDateTime:yyyyMMdd-HHmmssfff}-{ev.Kind}-{Guid.NewGuid().ToString("N")[..8]}.json";
        AtomicFile.WriteNew(Path.Combine(dir, name), JsonSerializer.SerializeToUtf8Bytes(ev, Json));
    }

    public static string GameKey(SaveFile sav)
    {
        var key = new string(sav.Version.ToString().ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return key.Length is > 0 and <= 16 ? key : "other";
    }
}
