using System.Collections.Concurrent;
using SaveKeep.Core.Backups;
using SaveKeep.Core.Saves;
using SaveKeep.Core.Vault;

namespace SaveKeep.Web;

/// <summary>Listing, downloads, pinning and deletion. Everything here works on the data directory only.</summary>
public static class VaultApi
{
    /// <summary>What the browser is told about a store attempt.</summary>
    public sealed record StoreNote(string? Entry, bool Added, string? Error, IReadOnlyList<string> Pruned);

    public static StoreNote Store(AppState s, string what, Func<VaultAddResult> add)
    {
        try
        {
            var r = add();
            return new StoreNote(r.Entry.Ref.ToString(), r.Added, null, r.Pruned.Select(p => p.ToString()).ToList());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            s.Audit.Write("vault-add-failed", $"{what}: {e.Message}");
            return new StoreNote(null, false, $"Not stored: {e.Message}", []);
        }
    }

    public static StoreNote StoreUpload(AppState s, byte[] bytes, string fileName) =>
        Store(s, fileName, () => s.Vault.Add(bytes, VaultSources.Upload, $"uploaded {Path.GetFileName(fileName)}"));

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/vault", (AppState s) => Results.Ok(List(s)));

        api.MapGet("/vault/{game}/{id}/main", (string game, string id, AppState s) =>
            Serve(s, game, id, r => s.Vault.ReadMain(r), _ => SaveVault.MainFileName, "application/octet-stream"));

        // Switch only: a zip shaped like JKSV's own (main at the root), built on request.
        api.MapGet("/vault/{game}/{id}/jksv.zip", (string game, string id, AppState s) =>
            Serve(s, game, id, r => s.Vault.Load(r) is { Record.Platform: "Switch" } e && s.Vault.ReadMain(r) is { } main ? BackupZip.MainOnly(main, e.Record.ArchivedAt) : null,
                e => SaveVault.JksvName(e.Record) + " (main only).zip", "application/zip"));

        api.MapGet("/vault/{game}/{id}/original.zip", (string game, string id, AppState s) =>
            Serve(s, game, id, r => s.Vault.ReadOriginalZip(r), e => SaveVault.JksvName(e.Record) + ".zip", "application/zip"));

        api.MapPost("/vault/{game}/{id}/pin", (string game, string id, VaultPinRequest req, AppState s) =>
        {
            if (!SaveVault.IsValidRef(game, id)) return Results.NotFound();
            try
            {
                if (req.Pinned) s.Vault.Pin(new VaultRef(game, id), req.Note);
                else s.Vault.Unpin(new VaultRef(game, id));
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
            return Results.Ok(List(s));
        });

        api.MapPost("/vault/{game}/{id}/delete", (string game, string id, VaultDeleteRequest req, AppState s) =>
        {
            if (!SaveVault.IsValidRef(game, id)) return Results.NotFound();
            if (!req.Confirm) return Results.BadRequest(new { error = "Confirmation required." });
            try { s.Vault.Delete(new VaultRef(game, id), req.ConfirmPinned, "deleted from the web page"); }
            catch (FileNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message, pinned = true }); }
            return Results.Ok(List(s));
        });
    }

    /// <summary>
    /// Resolves a download strictly through the vault: the route values must match the fixed name patterns, the entry
    /// must be listed, and the stored bytes must still have their recorded SHA-256. No path from the request is used.
    /// </summary>
    public static IResult Serve(AppState s, string game, string id, Func<VaultRef, byte[]?> read, Func<VaultEntry, string> name, string type)
    {
        if (!SaveVault.IsValidRef(game, id)) return Results.NotFound();
        var r = new VaultRef(game, id);
        if (s.Vault.Load(r) is not { } entry) return Results.NotFound();
        byte[]? bytes;
        try { bytes = read(r); }
        catch (InvalidDataException e) { return Results.Problem(e.Message, statusCode: 409); }
        if (bytes is null) return Results.NotFound();
        return Results.File(bytes, type, name(entry));
    }

    // Stored files never change, so details keyed by SHA-256 never go stale.
    private static readonly ConcurrentDictionary<string, (SaveDetails? Details, string? Error)> DetailsCache = new();

    public static (SaveDetails? Details, string? Error) Details(AppState s, VaultEntry e) =>
        DetailsCache.GetOrAdd(e.Record.Sha256, _ =>
        {
            try
            {
                var main = s.Vault.ReadMain(e.Ref);
                return main is null ? (null, "not found") : (SaveDetails.From(SaveReader.Read(main)), null);
            }
            catch (Exception ex) { return (null, ex.Message); } // display only: one bad save must not break the list
        });

    public static object List(AppState s)
    {
        var entries = s.Vault.List();
        var (bytes, over) = s.Vault.Usage();
        return new
        {
            keepPerGame = s.Vault.Options.KeepPerGame,
            maxTotalBytes = s.Vault.Options.MaxTotalBytes,
            totalBytes = bytes,
            overLimit = over,
            games = entries.GroupBy(e => e.Record.Game).Select(g => new
            {
                game = g.Key,
                edition = g.First().Record.Edition,
                platform = g.First().Record.Platform,
                entries = g.Select(e =>
                {
                    var (details, error) = Details(s, e);
                    return new
                    {
                        e.Record.Id, e.Record.Game, e.Record.Edition, e.Record.Platform, e.Record.TitleId,
                        trainer = e.Record.Trainer, e.Record.BoxPokemon, e.Record.PartyCount, e.Record.SaveType, e.Record.Sha256, e.Record.Size,
                        e.Record.Source, e.Record.SourceDetail, e.Record.ArchivedAt, sources = e.Sources,
                        lastSeenAt = e.Events.Where(x => x.Kind == VaultEventKinds.Seen).Select(x => (DateTimeOffset?)x.At).LastOrDefault(),
                        pinned = e.Pinned, pinnedAt = e.Pin?.At, pinNote = e.Pin?.Detail,
                        e.Bytes, e.HasOriginalZip, details, detailsError = error,
                    };
                }),
            }),
        };
    }
}

public sealed record VaultPinRequest(bool Pinned, string? Note);
public sealed record VaultDeleteRequest(bool Confirm, bool ConfirmPinned);
