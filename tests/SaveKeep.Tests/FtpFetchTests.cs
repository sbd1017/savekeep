using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKHeX.Core;
using SaveKeep.Core.Backups;
using SaveKeep.Core.Games;
using SaveKeep.Core.Saves;
using SaveKeep.Core.Util;
using SaveKeep.Core.Vault;
using SaveKeep.Sim;
using SaveKeep.Web;

namespace SaveKeep.Tests;

/// <summary>Finding and copying JKSV/Checkpoint backups over a simulated ftpd.</summary>
public class FtpFetchTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 33, 56);
    private static KnownGame Sword => KnownGames.ById("sw")!;
    private static KnownGame UltraSun => KnownGames.ById("us")!;

    private static BackupTarget SwordTarget(string? trainer = "SimTrainer") => BackupTarget.For(Sword, trainer);

    private static async Task<ReadOnlyFtpClient> Login(FtpSimulator ftp)
    {
        var c = new ReadOnlyFtpClient(IPAddress.Loopback, ftp.Port, TimeSpan.FromSeconds(5));
        await c.ConnectAsync(Ct);
        return c;
    }

    private static void AssertReadOnly(FtpSimulator ftp)
    {
        var verbs = ftp.Commands.Select(c => c.Split(' ')[0].ToUpperInvariant()).ToList();
        Assert.NotEmpty(verbs);
        Assert.All(verbs, v => Assert.Contains(v, ReadOnlyFtpClient.AllowedVerbs));
        Assert.DoesNotContain(verbs, v => FtpSimulator.WriteVerbs.Contains(v));
        Assert.DoesNotContain("PASS", verbs);
    }

    private static byte[] Bytes(SaveFile sav) => SampleSaves.MainBytes(sav);

    [Fact]
    public async Task Login_is_anonymous_and_only_read_commands_are_sent()
    {
        await using var ftp = new FtpSimulator();
        var before = ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0, Bytes(SampleSaves.SwordShield()), zip: true);
        var snapshot = ftp.Paths.ToDictionary(p => p, p => ftp.Read(p) is { } b ? Hex.Sha256(b) : "dir");

        await using (var client = await Login(ftp))
        {
            var found = await BackupDiscovery.FindAsync(client, SwordTarget(), Ct);
            await BackupFetcher.FetchAsync(client, found.NewestByName!, Sword, null, Ct);
            // Even from inside, a write command can't be sent.
            await Assert.ThrowsAsync<InvalidOperationException>(() => typeof(ReadOnlyFtpClient)
                .GetMethod("CommandAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(client, ["STOR", "/x", Ct]) as Task<(int, string)> ?? Task.CompletedTask);
        }

        Assert.Equal("USER anonymous", ftp.Commands.First());
        AssertReadOnly(ftp);
        Assert.Contains(ftp.Commands, c => c == "RETR " + before);
        Assert.Equal(snapshot, ftp.Paths.ToDictionary(p => p, p => ftp.Read(p) is { } b ? Hex.Sha256(b) : "dir"));
    }

    [Fact]
    public async Task A_password_prompt_is_refused_without_sending_a_password()
    {
        await using var ftp = new FtpSimulator { RequirePassword = true };
        var client = new ReadOnlyFtpClient(IPAddress.Loopback, ftp.Port, TimeSpan.FromSeconds(5));
        var e = await Assert.ThrowsAsync<FtpException>(() => client.ConnectAsync(Ct));
        Assert.Contains("password", e.Message);
        Assert.Equal(331, e.Code);
        await client.DisposeAsync();
        Assert.Equal(["USER anonymous"], ftp.Commands.ToArray());
    }

    [Fact]
    public void Only_local_ftp_servers_are_accepted()
    {
        Assert.Throws<ArgumentException>(() => new ReadOnlyFtpClient(IPAddress.Parse("8.8.8.8"), 5000));
        Assert.Throws<ArgumentException>(() => new ReadOnlyFtpClient(IPAddress.Parse("100.64.0.1"), 5000));
        _ = new ReadOnlyFtpClient(IPAddress.Parse("192.168.1.50"), 5000);
    }

    [Theory]
    [InlineData("/JKSV/../etc/passwd")]
    [InlineData("/JKSV/./main")]
    [InlineData("/JKSV/x\r\nDELE /JKSV/x")]
    [InlineData("/JKSV/a\\..\\b")]
    public void Unsafe_ftp_paths_are_rejected(string path) =>
        Assert.Throws<ArgumentException>(() => ReadOnlyFtpClient.ValidateArgument(path));

    [Fact]
    public async Task Zip_and_folder_backups_are_both_found_and_fetched()
    {
        await using var ftp = new FtpSimulator();
        var sav = SampleSaves.SwordShield();
        var save = Bytes(sav);
        ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0, save, zip: false);
        ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0.AddMinutes(5), save, zip: true);
        await using var client = await Login(ftp);

        var found = await BackupDiscovery.FindAsync(client, SwordTarget(), Ct);
        Assert.Null(found.Problem);
        Assert.Equal(2, found.Candidates.Count);
        var zip = Assert.Single(found.Candidates, c => c.IsZip);
        var folder = Assert.Single(found.Candidates, c => !c.IsZip);
        Assert.Equal(zip.Id, found.NewestByName!.Id);
        Assert.Null(found.Suggested); // no history, so nothing is suggested
        Assert.Equal(T0.AddMinutes(5), zip.NamedAt);
        Assert.Equal("SimTrainer", zip.UserLabel);
        Assert.Equal(save.Length, folder.Size);
        Assert.EndsWith("/main", folder.RemotePath);
        Assert.NotNull(zip.ModifiedAt);

        foreach (var c in new[] { zip, folder })
        {
            var f = await BackupFetcher.FetchAsync(client, c, Sword, TrainerSummary.From(sav), Ct);
            Assert.Equal(save, f.Main);
            Assert.Equal("SAV8SWSH", f.Save.GetType().Name);
            Assert.Equal(c.IsZip, f.OriginalZip is not null);
        }
        AssertReadOnly(ftp);
    }

    [Fact]
    public async Task Newest_by_name_is_shown_but_not_suggested_without_history()
    {
        await using var ftp = new FtpSimulator();
        var save = Bytes(SampleSaves.SwordShield());
        ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0.AddDays(-1), save, zip: true);
        ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0, save, zip: true);
        ftp.AddJksvBackup("Pokemon Sword", "Sibling", T0.AddHours(2), save, zip: true);       // newer, another user
        ftp.AddJksvBackup("Pokemon Shield", "SimTrainer", T0.AddHours(3), save, zip: true);   // another game
        await using var client = await Login(ftp);

        var found = await BackupDiscovery.FindAsync(client, SwordTarget(), Ct);
        Assert.Equal(3, found.Candidates.Count);
        Assert.All(found.Candidates, c => Assert.Equal("Pokemon Sword", c.GameFolder));
        Assert.Equal("Sibling", found.Candidates[0].UserLabel);
        Assert.Equal("Sibling", found.NewestByName!.UserLabel);
        Assert.True(found.Candidates.Single(c => c.Name == $"SimTrainer - {T0:yyyy-MM-dd_HH-mm-ss}.zip").MatchesTrainer);
        Assert.False(found.Candidates.Single(c => c.UserLabel == "Sibling").MatchesTrainer);
        Assert.Null(found.Suggested);
    }

    [Fact]
    public async Task Accented_or_title_id_game_folders_are_recognised()
    {
        await using var ftp = new FtpSimulator();
        var save = Bytes(SampleSaves.SwordShield());
        ftp.AddJksvBackup("Pokémon Sword", "SimTrainer", T0, save, zip: true);
        ftp.AddJksvBackup(Sword.TitleId, "SimTrainer", T0.AddMinutes(1), save, zip: false);
        await using var client = await Login(ftp);
        var found = await BackupDiscovery.FindAsync(client, SwordTarget(), Ct);
        Assert.Equal(2, found.Candidates.Count);
        Assert.Equal(Sword.TitleId, found.NewestByName!.GameFolder);
    }

    [Fact]
    public async Task A_missing_jksv_folder_or_game_is_reported()
    {
        await using var ftp = new FtpSimulator();
        await using var client = await Login(ftp);
        Assert.Contains("No /JKSV folder", (await BackupDiscovery.FindAsync(client, SwordTarget(), Ct)).Problem);
        ftp.AddJksvBackup("Pokemon Shield", "SimTrainer", T0, [1, 2, 3], zip: true);
        var r = await BackupDiscovery.FindAsync(client, SwordTarget(), Ct);
        Assert.Contains("No JKSV folder for Pokémon Sword", r.Problem);
        Assert.Contains("Pokemon Shield", r.Problem);
    }

    [Fact]
    public async Task Checkpoint_backups_on_3ds_are_found_newest_first()
    {
        var sav = SampleSaves.UltraSunMoon();
        await using var ftp = new FtpSimulator();
        ftp.AddCheckpointBackup("0x01B50 Pokémon Ultra Sun", T0, Bytes(sav));
        ftp.AddCheckpointBackup("0x01B50 Pokémon Ultra Sun", T0.AddMinutes(10), Bytes(sav));
        ftp.AddCheckpointBackup("0x01B51 Pokémon Ultra Moon", T0.AddHours(1), Bytes(sav));
        ftp.EnsureDir("/3ds/Checkpoint/saves/0x01B50 Pokémon Ultra Sun/empty-folder");
        await using var client = await Login(ftp);

        var found = await BackupDiscovery.FindAsync(client, BackupTarget.For(UltraSun), Ct);
        Assert.Equal(2, found.Candidates.Count);
        Assert.Equal(T0.AddMinutes(10), found.NewestByName!.NamedAt);
        Assert.Contains(found.Notes, n => n.Contains("empty-folder"));
        var f = await BackupFetcher.FetchAsync(client, found.NewestByName, UltraSun, TrainerSummary.From(sav), Ct);
        Assert.Equal("SAV7USUM", f.Save.GetType().Name);
        Assert.Null(f.OriginalZip);
        AssertReadOnly(ftp);
    }

    [Fact]
    public async Task Hostile_listing_names_are_skipped()
    {
        await using var ftp = new FtpSimulator();
        var save = Bytes(SampleSaves.SwordShield());
        ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0, save, zip: true);
        ftp.AddRawEntry("/JKSV/Pokemon Sword", "..", isDir: true);
        ftp.AddRawEntry("/JKSV/Pokemon Sword", "..\\..\\evil.zip", isDir: false, data: save);
        await using var client = await Login(ftp);
        var found = await BackupDiscovery.FindAsync(client, SwordTarget(), Ct);
        var only = Assert.Single(found.Candidates);
        Assert.DoesNotContain("..", only.RemotePath);
        Assert.Contains(found.Notes, n => n.Contains("unsafe name"));
        Assert.DoesNotContain(ftp.Commands, c => c.Contains(".."));
    }

    [Theory]
    [InlineData("../main")]
    [InlineData("/main")]
    [InlineData("save/../../main")]
    [InlineData("..\\main")]
    [InlineData("C:main")]
    public void Zip_path_traversal_is_rejected(string entry)
    {
        var zip = FtpSimulator.Zip((entry, [1, 2, 3]));
        var e = Assert.Throws<InvalidDataException>(() => BackupZip.ExtractMain(zip));
        Assert.Contains("unsafe path", e.Message);
    }

    [Fact]
    public void A_traversal_entry_next_to_a_good_main_still_refuses_the_archive()
    {
        var zip = FtpSimulator.Zip(("main", [1, 2, 3]), ("../../.bashrc", [9]));
        Assert.Throws<InvalidDataException>(() => BackupZip.ExtractMain(zip));
    }

    [Fact]
    public void Zip_limits_and_ambiguity_are_enforced()
    {
        Assert.Equal([1, 2, 3], BackupZip.ExtractMain(FtpSimulator.Zip(("main", [1, 2, 3]), ("backup", [4]))));
        Assert.Equal([7], BackupZip.ExtractMain(FtpSimulator.Zip(("Player/main", [7]))));
        Assert.Contains("no 'main'", Assert.Throws<InvalidDataException>(() => BackupZip.ExtractMain(FtpSimulator.Zip(("other", [1])))).Message);
        Assert.Contains("more than one", Assert.Throws<InvalidDataException>(() => BackupZip.ExtractMain(FtpSimulator.Zip(("main", [1]), ("a/main", [2])))).Message);
        var huge = new byte[SaveReader.MaxSaveSize + 1];
        Assert.Contains("too large", Assert.Throws<InvalidDataException>(() => BackupZip.ExtractMain(FtpSimulator.Zip(("main", huge)))).Message);
        Assert.Contains("not a valid zip", Assert.Throws<InvalidDataException>(() => BackupZip.ExtractMain([1, 2, 3, 4])).Message);
    }

    [Fact]
    public async Task An_oversized_download_is_stopped()
    {
        await using var ftp = new FtpSimulator();
        ftp.AddFile("/JKSV/Pokemon Sword/SimTrainer - 2026-10-20_12-33-56/main", new byte[SaveReader.MaxSaveSize + 10]);
        await using var client = await Login(ftp);
        var found = await BackupDiscovery.FindAsync(client, SwordTarget(), Ct);
        var e = await Assert.ThrowsAsync<FtpException>(() => BackupFetcher.FetchAsync(client, found.NewestByName!, Sword, null, Ct));
        Assert.Contains("limit", e.Message);
    }

    [Fact]
    public async Task A_backup_of_another_trainer_or_game_is_refused()
    {
        await using var ftp = new FtpSimulator();
        ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0, Bytes(SampleSaves.SwordShield("Rival")), zip: true);
        ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0.AddMinutes(1), Bytes(SampleSaves.UltraSunMoon()), zip: false);
        await using var client = await Login(ftp);
        var found = await BackupDiscovery.FindAsync(client, SwordTarget(), Ct);
        var expected = TrainerSummary.From(SampleSaves.SwordShield());
        var wrongGame = found.Candidates.Single(c => !c.IsZip);
        var wrongTrainer = found.Candidates.Single(c => c.IsZip);
        Assert.Contains("save for Pokémon Sword/Shield", (await Assert.ThrowsAsync<InvalidDataException>(() => BackupFetcher.FetchAsync(client, wrongGame, Sword, expected, Ct))).Message);
        Assert.Contains("belongs to trainer Rival", (await Assert.ThrowsAsync<InvalidDataException>(() => BackupFetcher.FetchAsync(client, wrongTrainer, Sword, expected, Ct))).Message);
    }

    [Fact]
    public void A_corrupted_sword_shield_file_is_not_read_as_a_save()
    {
        var bytes = Bytes(SampleSaves.SwordShield());
        Assert.Equal("SimTrainer", SaveReader.ReadAs(bytes, Sword).OT);
        Assert.Equal("SimTrainer", SaveReader.Read(bytes).OT);
        bytes[0x1000] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => SaveReader.ReadAs(bytes, Sword));
        Assert.Throws<InvalidDataException>(() => SaveReader.Read(bytes));
    }

    [Theory]
    [InlineData("Player One - 2026-10-20_12-33-56", "Player One", "2026-10-20 12:33:56")]
    [InlineData("Player 1 - 2026.10.20 @ 12.33.56", "Player 1", "2026-10-20 12:33:56")]
    [InlineData("2026-10-20_12-33-56", null, "2026-10-20 12:33:56")]
    [InlineData("my manual backup", null, null)]
    public void Jksv_names_are_parsed(string stem, string? user, string? stamp)
    {
        var (u, t) = BackupDiscovery.SplitJksvName(stem);
        Assert.Equal(user, u);
        Assert.Equal(stamp, t?.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    // ---------------------------------------------------------------- through the web API

    [Fact]
    public async Task Search_and_copy_through_the_api_stores_the_save_and_the_original_zip()
    {
        await using var ftp = new FtpSimulator();
        var sav = SampleSaves.SwordShield();
        var main = Bytes(sav);
        var zipPath = ftp.AddJksvBackup("Pokemon Sword", "SimTrainer", T0, main, zip: true);

        var options = new SaveKeepOptions { DataDir = TestEnv.TempDir() };
        var state = new AppState(options);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = options.DataDir });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(state);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseMiddleware<LanOnlyMiddleware>();
        BackupApi.Map(app.MapGroup("/api"));
        await app.StartAsync();
        var origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        using var http = new HttpClient { BaseAddress = origin };
        http.DefaultRequestHeaders.Add(LanOnlyMiddleware.CsrfHeader, "1");

        var find = await http.PostAsJsonAsync("/api/backup/find", new { host = "127.0.0.1", port = ftp.Port, game = "sw", trainer = "SimTrainer" });
        Assert.Equal(HttpStatusCode.OK, find.StatusCode);
        var listing = JsonDocument.Parse(await find.Content.ReadAsStringAsync()).RootElement;
        var id = listing.GetProperty("candidates")[0].GetProperty("id").GetString();
        Assert.True(listing.GetProperty("candidates")[0].GetProperty("matchesTrainer").GetBoolean());

        var fetch = await http.PostAsJsonAsync("/api/backup/fetch", new { candidateId = id });
        Assert.Equal(HttpStatusCode.OK, fetch.StatusCode);
        var entry = Assert.Single(state.Vault.List());
        Assert.Equal(VaultSources.FtpFetch, entry.Record.Source);
        Assert.Contains(zipPath, entry.Record.SourceDetail);
        Assert.Equal(main, state.Vault.ReadMain(entry.Ref));
        Assert.Equal(ftp.Read(zipPath), state.Vault.ReadOriginalZip(entry.Ref));

        // Copying the same backup again stores nothing new.
        var again = JsonDocument.Parse(await (await http.PostAsJsonAsync("/api/backup/fetch", new { candidateId = id })).Content.ReadAsStringAsync()).RootElement;
        Assert.False(again.GetProperty("vault").GetProperty("added").GetBoolean());
        Assert.Single(state.Vault.List());

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/backup/find", new { host = "8.8.8.8", port = ftp.Port, game = "sw" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/backup/find", new { host = "127.0.0.1", port = ftp.Port, game = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/backup/fetch", new { candidateId = "0000" })).StatusCode);
        AssertReadOnly(ftp);
        await app.StopAsync();
    }
}
