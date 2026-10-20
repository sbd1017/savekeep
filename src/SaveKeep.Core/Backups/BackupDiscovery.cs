using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SaveKeep.Core.Games;

namespace SaveKeep.Core.Backups;

public enum BackupTool { Jksv, Checkpoint }

/// <summary>The game a backup must belong to, and optionally the trainer name to highlight matching JKSV backups.</summary>
public sealed record BackupTarget(GamePlatform Platform, string TitleId, string Edition, string? TrainerName)
{
    public static BackupTarget For(KnownGame game, string? trainerName = null) =>
        new(game.Platform, game.TitleId, game.Edition, string.IsNullOrWhiteSpace(trainerName) ? null : trainerName.Trim());
}

public sealed record BackupCandidate
{
    public required string Id { get; init; }
    public required BackupTool Tool { get; init; }
    public required string GameFolder { get; init; }
    /// <summary>Folder or zip name as JKSV/Checkpoint wrote it.</summary>
    public required string Name { get; init; }
    /// <summary>Path on the console of the zip, or of the backup folder's <c>main</c>.</summary>
    public required string RemotePath { get; init; }
    public required bool IsZip { get; init; }
    public long? Size { get; init; }
    /// <summary>Time in the backup's name: the console's local clock, no time zone.</summary>
    public DateTime? NamedAt { get; init; }
    /// <summary>Modification time reported by the server (MDTM, UTC), if any.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }
    /// <summary>JKSV: the Switch user name in the backup name. Checkpoint: none.</summary>
    public string? UserLabel { get; init; }
    public bool MatchesTrainer { get; init; }
    /// <summary>When this backup was first listed, by the server's clock. Null without search history.</summary>
    public DateTimeOffset? FirstSeenAt { get; init; }
    /// <summary>Not listed at the previous search of this game (or its size changed since).</summary>
    public bool IsNew { get; init; }

    /// <summary>Console-clock order. Only a display hint, never the basis of the suggestion.</summary>
    internal DateTime SortKey => NamedAt ?? ModifiedAt?.UtcDateTime ?? DateTime.MinValue;
}

/// <param name="Suggested">
/// The one backup that appeared since the previous search. Null when there was no previous search, nothing new,
/// or more than one new backup; the notes then say why and the user picks.
/// </param>
/// <param name="NewestByName">Newest according to the console's clock (names or file dates). May be wrong.</param>
/// <param name="PreviousSearchAt">The previous search of this game, if any.</param>
public sealed record DiscoveryResult(
    IReadOnlyList<BackupCandidate> Candidates, BackupCandidate? Suggested, IReadOnlyList<string> Notes, string? Problem,
    BackupCandidate? NewestByName = null, DateTimeOffset? PreviousSearchAt = null)
{
    /// <summary>Every backup listed, including ones beyond the inspection limit; all are remembered as seen.</summary>
    public IReadOnlyList<BackupCandidate>? Listed { get; init; }
}

/// <summary>
/// Finds save backups on a console's SD card over FTP, read-only:
/// JKSV on Switch (<c>/JKSV/&lt;game&gt;/&lt;user&gt; - &lt;time&gt;</c>, as a folder or a .zip) and
/// Checkpoint on 3DS (<c>/3ds/Checkpoint/saves/0x&lt;id&gt; &lt;game&gt;/&lt;backup&gt;/main</c>).
/// </summary>
public static class BackupDiscovery
{
    public const string JksvRoot = "/JKSV";
    public const string CheckpointRoot = "/3ds/Checkpoint/saves";
    public const string SaveFileName = "main";
    /// <summary>Only the newest backups are inspected in detail (one listing each).</summary>
    public const int MaxInspected = 20;

    private static readonly Regex[] Stamps =
    [
        // JKSV: "YYYY-MM-DD_HH-MM-SS" and "YYYY.MM.DD @ HH.MM.SS"
        new(@"(\d{4})[-.](\d{2})[-.](\d{2})(?:_| @ |\s)(\d{2})[-.:](\d{2})[-.:](\d{2})", RegexOptions.CultureInvariant),
        // Checkpoint: "YYYYMMDD-HHMMSS"
        new(@"(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})", RegexOptions.CultureInvariant),
    ];

    /// <param name="history">Earlier searches. Without it nothing is suggested (only <see cref="DiscoveryResult.NewestByName"/>).</param>
    /// <param name="now">The server's clock, for the history.</param>
    public static async Task<DiscoveryResult> FindAsync(ReadOnlyFtpClient ftp, BackupTarget target, CancellationToken ct,
        BackupHistory? history = null, DateTimeOffset? now = null)
    {
        var key = BackupHistory.KeyFor(target);
        var previous = history?.Previous(key);
        var result = target.Platform == GamePlatform.Switch
            ? await FindJksvAsync(ftp, target, previous, ct).ConfigureAwait(false)
            : await FindCheckpointAsync(ftp, target, previous, ct).ConfigureAwait(false);
        if (history is not null && result.Problem is null)
        {
            var at = now ?? DateTimeOffset.UtcNow;
            history.Record(key, result.Listed ?? result.Candidates, at);
            result = result with
            {
                Candidates = result.Candidates.Select(c => c with { FirstSeenAt = c.FirstSeenAt ?? at }).ToList(),
                Suggested = result.Suggested is { } sg ? sg with { FirstSeenAt = sg.FirstSeenAt ?? at } : null,
            };
        }
        return result;
    }

    private static async Task<DiscoveryResult> FindJksvAsync(ReadOnlyFtpClient ftp, BackupTarget target, BackupHistory.Entry? previous, CancellationToken ct)
    {
        var notes = new List<string>();
        var roots = await TryListAsync(ftp, JksvRoot, ct).ConfigureAwait(false);
        if (roots is null) return new([], null, notes, $"No {JksvRoot} folder on the SD card. Make a backup with JKSV first.");
        var games = roots.Where(e => e.IsDirectory && SafeName(e.Name) && IsGameFolder(e.Name, target)).ToList();
        if (games.Count == 0)
            return new([], null, notes, $"No JKSV folder for Pokémon {target.Edition} under {JksvRoot}. Folders there: {Describe(roots)}.");

        var found = new List<BackupCandidate>();
        foreach (var game in games)
        {
            var gamePath = $"{JksvRoot}/{game.Name}";
            var entries = await TryListAsync(ftp, gamePath, ct).ConfigureAwait(false) ?? [];
            foreach (var e in entries)
            {
                if (!SafeName(e.Name)) { notes.Add($"Skipped an entry with an unsafe name in {gamePath}."); continue; }
                bool zip = !e.IsDirectory && e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                if (!zip && !e.IsDirectory) continue;
                var stem = zip ? e.Name[..^4] : e.Name;
                var (user, stamp) = SplitJksvName(stem);
                found.Add(new BackupCandidate
                {
                    Id = MakeId(gamePath, e.Name), Tool = BackupTool.Jksv, GameFolder = game.Name, Name = e.Name,
                    RemotePath = zip ? $"{gamePath}/{e.Name}" : $"{gamePath}/{e.Name}/{SaveFileName}",
                    IsZip = zip, Size = zip ? e.Size : null, NamedAt = stamp, UserLabel = user,
                    MatchesTrainer = user is not null && target.TrainerName is not null && Fold(user) == Fold(target.TrainerName),
                });
            }
        }
        notes.Add("JKSV names backups after the Switch user, which may not be the trainer name. The trainer inside the save is shown after download.");
        return await FinishAsync(ftp, found, notes, previous, ct, $"No JKSV backups for Pokémon {target.Edition} yet.").ConfigureAwait(false);
    }

    private static async Task<DiscoveryResult> FindCheckpointAsync(ReadOnlyFtpClient ftp, BackupTarget target, BackupHistory.Entry? previous, CancellationToken ct)
    {
        var notes = new List<string>();
        var roots = await TryListAsync(ftp, CheckpointRoot, ct).ConfigureAwait(false);
        if (roots is null) return new([], null, notes, $"No {CheckpointRoot} folder on the SD card. Make a backup with Checkpoint first.");
        var games = roots.Where(e => e.IsDirectory && SafeName(e.Name) && IsGameFolder(e.Name, target)).ToList();
        if (games.Count == 0)
            return new([], null, notes, $"No Checkpoint folder for Pokémon {target.Edition} (title {target.TitleId}) under {CheckpointRoot}. Folders there: {Describe(roots)}.");

        var found = new List<BackupCandidate>();
        foreach (var game in games)
        {
            var gamePath = $"{CheckpointRoot}/{game.Name}";
            var entries = await TryListAsync(ftp, gamePath, ct).ConfigureAwait(false) ?? [];
            foreach (var e in entries.Where(e => e.IsDirectory))
            {
                if (!SafeName(e.Name)) { notes.Add($"Skipped an entry with an unsafe name in {gamePath}."); continue; }
                found.Add(new BackupCandidate
                {
                    Id = MakeId(gamePath, e.Name), Tool = BackupTool.Checkpoint, GameFolder = game.Name, Name = e.Name,
                    RemotePath = $"{gamePath}/{e.Name}/{SaveFileName}", IsZip = false, NamedAt = ParseStamp(e.Name),
                });
            }
        }
        notes.Add("Checkpoint backup names don't include the trainer. The trainer inside the save is shown after download.");
        return await FinishAsync(ftp, found, notes, previous, ct, $"No Checkpoint backups for Pokémon {target.Edition} yet.").ConfigureAwait(false);
    }

    /// <summary>
    /// Inspects backups (size of <c>main</c>, server time) and suggests one without trusting the console clock:
    /// only a backup that wasn't there at the previous search can be suggested, and only if it is the only one.
    /// </summary>
    private static async Task<DiscoveryResult> FinishAsync(ReadOnlyFtpClient ftp, List<BackupCandidate> found, List<string> notes,
        BackupHistory.Entry? previous, CancellationToken ct, string none)
    {
        bool Unseen(BackupCandidate c) => previous is not null && !previous.Seen.ContainsKey(BackupHistory.NameKey(c));
        // Unseen backups are always inspected, whatever their names claim; then the newest by name.
        var ordered = found.OrderByDescending(Unseen).ThenByDescending(c => c.SortKey).ThenByDescending(c => c.Name, StringComparer.Ordinal).ToList();
        int limit = Math.Max(MaxInspected, ordered.Count(Unseen));
        var result = new List<BackupCandidate>();
        foreach (var c in ordered.Take(limit))
        {
            var item = c;
            if (!c.IsZip)
            {
                var folder = c.RemotePath[..c.RemotePath.LastIndexOf('/')];
                var inside = await TryListAsync(ftp, folder, ct).ConfigureAwait(false);
                var main = inside?.FirstOrDefault(e => !e.IsDirectory && e.Name == SaveFileName);
                if (main is null) { notes.Add($"{c.Name} has no '{SaveFileName}' file; skipped."); continue; }
                item = item with { Size = main.Size };
            }
            item = item with { ModifiedAt = await TryModifiedAsync(ftp, c.RemotePath, ct).ConfigureAwait(false) };
            if (previous is not null)
            {
                var seen = previous.Seen.GetValueOrDefault(BackupHistory.NameKey(item));
                bool sizeChanged = seen?.Size is { } was && item.Size is { } now && was != now;
                item = item with { FirstSeenAt = sizeChanged ? null : seen?.FirstSeenAt, IsNew = seen is null || sizeChanged };
            }
            result.Add(item);
        }
        if (ordered.Count > limit) notes.Add($"{ordered.Count - limit} older backup(s) not shown.");
        if (result.Count == 0) return new([], null, notes, none);

        var byName = result.OrderByDescending(c => c.SortKey).ThenByDescending(c => c.Name, StringComparer.Ordinal).First();
        // New first, then most recently first seen, then the console's own order.
        result = result.OrderByDescending(c => c.IsNew).ThenByDescending(c => c.FirstSeenAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.SortKey).ThenByDescending(c => c.Name, StringComparer.Ordinal).ToList();
        var fresh = result.Where(c => c.IsNew).ToList();

        BackupCandidate? suggested = null;
        var since = previous is null ? "" : $" since the last search ({previous.LastSearchAt:HH:mm} UTC)";
        if (previous is null)
            notes.Add("First search for this game, so there's no way to tell which backup is the one you just made: backup names and dates come from the console's clock, which may be wrong. Pick it yourself, or search once before making the backup and again after, and the new one will be suggested.");
        else if (fresh.Count == 1)
        {
            suggested = fresh[0];
            notes.Add($"Suggested because it is the only backup that appeared{since}.");
        }
        else if (fresh.Count == 0)
            notes.Add($"No new backup has appeared{since}. If you just made one, check that JKSV/Checkpoint finished, then search again.");
        else
            notes.Add($"{fresh.Count} backups appeared{since}. Pick the one you want.");
        if (suggested is not null && !ReferenceEquals(byName, suggested) && byName.Name != suggested.Name)
            notes.Add($"By the console's clock, '{byName.Name}' looks newest. The console clock may be wrong, so the suggestion goes by what is new instead.");
        // Remember everything listed (inspected entries carry their sizes).
        var listed = found.Select(f => result.FirstOrDefault(r => r.Id == f.Id) ?? f).ToList();
        return new(result, suggested, notes, null, byName, previous?.LastSearchAt) { Listed = listed };
    }

    // ---------------------------------------------------------------- names

    /// <summary>"Player - 2026-10-20_12-33-56" gives ("Player", 20 Oct 2026 12:33:56).</summary>
    public static (string? User, DateTime? Stamp) SplitJksvName(string stem)
    {
        var stamp = ParseStamp(stem);
        int sep = stem.LastIndexOf(" - ", StringComparison.Ordinal);
        var user = sep > 0 ? stem[..sep].Trim() : null;
        return (string.IsNullOrEmpty(user) ? null : user, stamp);
    }

    public static DateTime? ParseStamp(string name)
    {
        foreach (var rx in Stamps)
        {
            var m = rx.Match(name);
            if (!m.Success) continue;
            var parts = Enumerable.Range(1, 6).Select(i => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
            try { return new DateTime(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], DateTimeKind.Unspecified); }
            catch (ArgumentOutOfRangeException) { }
        }
        return null;
    }

    /// <summary>JKSV: "Pokémon Sword", "Pokemon Sword" or the title ID. Checkpoint: "0x01B50 Pokémon Ultra Sun".</summary>
    public static bool IsGameFolder(string folder, BackupTarget target)
    {
        if (folder.Contains(target.TitleId, StringComparison.OrdinalIgnoreCase)) return true;
        var expected = Fold("Pokemon " + target.Edition);
        if (target.Platform == GamePlatform.Nintendo3DS)
        {
            var uniqueId = (Convert.ToUInt64(target.TitleId, 16) & 0xFFFFFFFF) >> 8;
            var prefix = $"0x{uniqueId:X5}";
            if (folder.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase) || folder.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            var rest = folder.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && folder.IndexOf(' ') > 0 ? folder[(folder.IndexOf(' ') + 1)..] : folder;
            return Fold(rest) == expected;
        }
        return Fold(folder) == expected;
    }

    /// <summary>Lower-case letters and digits only, accents removed: "Pokémon Sword" becomes "pokemonsword".</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>A single path component from a listing: never empty, '.', '..', or containing separators or control characters.</summary>
    public static bool SafeName(string name) =>
        name.Length is > 0 and <= 255 && name is not "." and not ".." && !name.Contains('/') && !name.Contains('\\') && !name.Any(char.IsControl);

    private static string MakeId(string folder, string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder + "/" + name)))[..16];

    private static string Describe(IReadOnlyList<FtpEntry> entries)
    {
        var dirs = entries.Where(e => e.IsDirectory && SafeName(e.Name)).Select(e => e.Name).Take(12).ToList();
        return dirs.Count == 0 ? "none" : string.Join(", ", dirs);
    }

    private static async Task<IReadOnlyList<FtpEntry>?> TryListAsync(ReadOnlyFtpClient ftp, string path, CancellationToken ct)
    {
        try { return await ftp.ListAsync(path, ct).ConfigureAwait(false); }
        catch (FtpException e) when (e.Code >= 400) { return null; }
    }

    private static async Task<DateTimeOffset?> TryModifiedAsync(ReadOnlyFtpClient ftp, string path, CancellationToken ct)
    {
        try { return await ftp.GetModifiedAsync(path, ct).ConfigureAwait(false); }
        catch (FtpException e) when (e.Code >= 400) { return null; }
    }
}
