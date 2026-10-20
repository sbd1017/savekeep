using System.Globalization;
using System.Net.Sockets;
using SaveKeep.Core.Backups;
using SaveKeep.Core.Games;
using SaveKeep.Core.Vault;

namespace SaveKeep.Web;

/// <summary>
/// Finding and copying JKSV/Checkpoint backups off a console running ftpd. Read-only towards the console:
/// the FTP client can't send any command that changes a file.
/// </summary>
public static class BackupApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/backup", (AppState s) => Results.Ok(new { listing = ListingDto(s.Listing) }));

        api.MapPost("/backup/find", async (BackupFindRequest req, AppState s, CancellationToken ct) =>
        {
            if (KnownGames.ById(req.Game) is not { } game) return Results.BadRequest(new { error = "Choose a game." });
            int port = req.Port ?? s.Options.FtpPort;
            if (port is < 1 or > 65535) return Results.BadRequest(new { error = "The FTP port must be between 1 and 65535." });

            await s.FtpGate.WaitAsync(ct);
            ReadOnlyFtpClient? ftp = null;
            try
            {
                var address = await Network.ResolveConsoleAsync(req.Host ?? "", ct);
                var target = BackupTarget.For(game, req.Trainer);
                ftp = new ReadOnlyFtpClient(address, port, s.Timeout);
                await ftp.ConnectAsync(ct);
                var now = s.Clock.GetUtcNow();
                var result = await BackupDiscovery.FindAsync(ftp, target, ct, s.BackupHistory, now);
                s.Listing = new BackupListing(address.ToString(), port, game.Id, target, now, result);
                s.Audit.Write("backup-find", $"FTP {address}:{port}: {result.Candidates.Count} backup(s) of {game.Edition}; suggested {result.Suggested?.Name ?? "none"}", new { commands = ftp.Sent });
                return Results.Ok(ListingDto(s.Listing));
            }
            catch (Exception e) when (e is ArgumentException or SocketException)
            {
                return Results.BadRequest(new { error = e.Message });
            }
            catch (FtpException e)
            {
                s.Audit.Write("backup-find-failed", e.Message, new { commands = ftp?.Sent });
                return Results.BadRequest(new { error = e.Message });
            }
            finally
            {
                if (ftp is not null) await ftp.DisposeAsync();
                s.FtpGate.Release();
            }
        });

        api.MapPost("/backup/fetch", async (BackupFetchRequest req, AppState s, CancellationToken ct) =>
        {
            if (s.Listing is not { } listing) return Results.BadRequest(new { error = "Search for backups first." });
            var candidate = listing.Result.Candidates.FirstOrDefault(c => c.Id == req.CandidateId);
            if (candidate is null) return Results.BadRequest(new { error = "That backup is not in the last search. Search again." });
            var game = KnownGames.ById(listing.GameId)!;

            await s.FtpGate.WaitAsync(ct);
            ReadOnlyFtpClient? ftp = null;
            FetchedBackup fetched;
            try
            {
                ftp = new ReadOnlyFtpClient(System.Net.IPAddress.Parse(listing.Host), listing.Port, s.Timeout);
                await ftp.ConnectAsync(ct);
                fetched = await BackupFetcher.FetchAsync(ftp, candidate, game, null, ct);
                s.Audit.Write("backup-fetch", $"{candidate.RemotePath} ({fetched.Download.Length} bytes): {fetched.Save.GetType().Name}, trainer {fetched.Trainer.Name}", new { commands = ftp.Sent });
            }
            catch (Exception e) when (e is FtpException or InvalidDataException or IOException)
            {
                s.Audit.Write("backup-fetch-failed", $"{candidate.RemotePath}: {e.Message}", new { commands = ftp?.Sent });
                return Results.BadRequest(new { error = e.Message });
            }
            finally
            {
                if (ftp is not null) await ftp.DisposeAsync();
                s.FtpGate.Release();
            }

            var vault = VaultApi.Store(s, candidate.RemotePath, () =>
                s.Vault.Add(fetched.Main, VaultSources.FtpFetch, $"copied over FTP from {candidate.RemotePath}", game, fetched.OriginalZip));
            return Results.Ok(new { vault, trainer = fetched.Trainer, saveType = fetched.Save.GetType().Name });
        });
    }

    public static object? ListingDto(BackupListing? l) => l is null ? null : new
    {
        l.Host, l.Port, l.GameId, game = l.Target.Edition, l.Target.TitleId, trainer = l.Target.TrainerName, l.At,
        tool = l.Target.Platform == GamePlatform.Switch ? "JKSV" : "Checkpoint",
        l.Result.Problem, l.Result.Notes, suggestedId = l.Result.Suggested?.Id, newestByNameId = l.Result.NewestByName?.Id, l.Result.PreviousSearchAt,
        candidates = l.Result.Candidates.Select(c => new
        {
            c.Id, tool = c.Tool.ToString(), c.GameFolder, c.Name, c.RemotePath, c.IsZip, c.Size,
            namedAt = c.NamedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), c.ModifiedAt, c.UserLabel, c.MatchesTrainer,
            c.IsNew, c.FirstSeenAt,
        }),
    };
}

public sealed record BackupFindRequest(string? Host, int? Port, string? Game, string? Trainer);
public sealed record BackupFetchRequest(string? CandidateId);
