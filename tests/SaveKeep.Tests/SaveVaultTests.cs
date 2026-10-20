using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using PKHeX.Core;
using SaveKeep.Core.Backups;
using SaveKeep.Core.Games;
using SaveKeep.Core.Util;
using SaveKeep.Core.Vault;
using SaveKeep.Sim;
using SaveKeep.Web;

namespace SaveKeep.Tests;

public class SaveVaultTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Bytes(SaveFile sav) => SampleSaves.MainBytes(sav);

    private static (AppState State, ManualClock Clock) App(int keep = 20, int maxMb = 1024)
    {
        var clock = new ManualClock(T0);
        var s = new AppState(new SaveKeepOptions { DataDir = TestEnv.TempDir(), VaultKeepPerGame = keep, VaultMaxTotalMB = maxMb }, clock);
        return (s, clock);
    }

    private static List<JsonElement> Audit(AppState s, string evt) =>
        s.Audit.Tail(1000).Where(a => a.GetProperty("Event").GetString() == evt).ToList();

    // ---------------------------------------------------------------- storing

    [Fact]
    public void Upload_is_stored_with_what_identifies_the_save()
    {
        var (s, _) = App();
        var sav = SampleSaves.SwordShield();
        var bytes = Bytes(sav);
        var note = VaultApi.StoreUpload(s, bytes, "main");

        Assert.True(note.Added);
        Assert.Null(note.Error);
        var entry = Assert.Single(s.Vault.List());
        Assert.Equal(note.Entry, entry.Ref.ToString());
        var r = entry.Record;
        Assert.Equal(("sw", "Sword", "Switch", KnownGames.ById("sw")!.TitleId), (r.Game, r.Edition, r.Platform, r.TitleId));
        Assert.Equal(sav.OT, r.Trainer.Name);
        Assert.Equal(sav.DisplayTID, r.Trainer.DisplayTid);
        Assert.Equal(3, r.BoxPokemon);
        Assert.Equal("SAV8SWSH", r.SaveType);
        Assert.Equal(Hex.Sha256(bytes), r.Sha256);
        Assert.Equal(VaultSources.Upload, r.Source);
        Assert.Equal(T0, r.ArchivedAt);
        Assert.Equal($"20261020-120000-{r.Sha256[..8]}", r.Id);
        Assert.Equal(bytes, s.Vault.ReadMain(entry.Ref));
        Assert.True(File.Exists(Path.Combine(s.Options.DataDir, "vault", "sw", r.Id, "vault.json")));
        Assert.Single(Audit(s, "vault-add"));
    }

    [Fact]
    public void A_3ds_upload_is_stored_under_its_own_game_and_only_the_file_name_is_recorded()
    {
        var (s, _) = App();
        var note = VaultApi.StoreUpload(s, Bytes(SampleSaves.UltraSunMoon("Kai")), "../../main");
        Assert.True(note.Added);
        var r = Assert.Single(s.Vault.List()).Record;
        Assert.Equal(("us", "Ultra Sun", "3DS", "00040000001B5000", VaultSources.Upload), (r.Game, r.Edition, r.Platform, r.TitleId, r.Source));
        Assert.Equal("uploaded main", r.SourceDetail);
        Assert.Equal("Kai", r.Trainer.Name);
        Assert.Equal(2, r.BoxPokemon);
    }

    [Fact]
    public void Something_that_is_not_a_save_is_reported_not_stored()
    {
        var (s, _) = App();
        var note = VaultApi.StoreUpload(s, [1, 2, 3, 4], "junk.bin");
        Assert.NotNull(note.Error);
        Assert.Null(note.Entry);
        Assert.Empty(s.Vault.List());
        Assert.Single(Audit(s, "vault-add-failed"));
    }

    [Fact]
    public void A_save_for_the_wrong_game_is_refused_when_the_game_is_known()
    {
        var (s, _) = App();
        var e = Assert.Throws<InvalidDataException>(() => s.Vault.Add(Bytes(SampleSaves.UltraSunMoon()), VaultSources.FtpFetch, null, KnownGames.ById("sw")));
        Assert.Contains("Sword/Shield", e.Message);
        Assert.Empty(s.Vault.List());
    }

    // ---------------------------------------------------------------- de-duplication and write-once

    [Fact]
    public void The_same_save_from_any_source_is_stored_once_and_files_are_never_rewritten()
    {
        var (s, clock) = App();
        var bytes = Bytes(SampleSaves.SwordShield());
        var first = s.Vault.Add(bytes, VaultSources.Upload, "a");
        var dir = Path.Combine(s.Vault.Root, first.Entry.Record.Game, first.Entry.Record.Id);
        var before = TestEnv.HashTree(dir);
        var stamps = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

        clock.Advance(TimeSpan.FromHours(1));
        var again = s.Vault.Add(bytes, VaultSources.FtpFetch, "b");
        s.Vault.Pin(first.Entry.Ref, "keep");

        Assert.False(again.Added);
        Assert.Equal(first.Entry.Ref, again.Entry.Ref);
        Assert.Single(s.Vault.List());
        var after = TestEnv.HashTree(dir);
        foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
        foreach (var (path, at) in stamps) Assert.Equal(at, File.GetLastWriteTimeUtc(path));
        Assert.Equal(2, after.Keys.Except(before.Keys).Count(k => k.StartsWith("events")));
        var entry = s.Vault.Load(first.Entry.Ref)!;
        Assert.Equal(T0, entry.Record.ArchivedAt);
        Assert.Equal([VaultSources.Upload, VaultSources.FtpFetch], entry.Sources);
        Assert.Single(Audit(s, "vault-seen"));

        // A different save is a different entry, even in the same second.
        var other = s.Vault.Add(Bytes(SampleSaves.SwordShield("Other")), VaultSources.Upload, "c");
        Assert.True(other.Added);
        Assert.Equal(2, s.Vault.List().Count);
    }

    // ---------------------------------------------------------------- retention

    private static List<VaultRef> AddSaves(AppState s, ManualClock clock, int n, string prefix = "T")
    {
        var refs = new List<VaultRef>();
        for (int i = 0; i < n; i++)
        {
            refs.Add(s.Vault.Add(Bytes(SampleSaves.SwordShield($"{prefix}{i}")), VaultSources.Upload, null).Entry.Ref);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        return refs;
    }

    [Fact]
    public void Keeps_the_newest_per_game_and_prunes_the_oldest_with_a_log_entry()
    {
        var (s, clock) = App(keep: 3);
        var refs = AddSaves(s, clock, 5);
        var usum = s.Vault.Add(Bytes(SampleSaves.UltraSunMoon()), VaultSources.Upload, null).Entry.Ref; // another game counts separately

        Assert.Equal([usum, refs[4], refs[3], refs[2]], s.Vault.List().Select(e => e.Ref));
        var prunes = Audit(s, "vault-prune");
        Assert.Equal(2, prunes.Count);
        Assert.Contains(prunes, a => a.GetProperty("Message").GetString()!.StartsWith(refs[0].ToString()));
        Assert.Contains(prunes, a => a.GetProperty("Message").GetString()!.StartsWith(refs[1].ToString()));
        Assert.False(Directory.Exists(Path.Combine(s.Vault.Root, refs[0].Game, refs[0].Id)));
        Assert.Empty(Directory.GetDirectories(Path.Combine(s.Vault.Root, "sw"), ".*")); // no staging or removal leftovers
    }

    [Fact]
    public void A_pinned_save_is_never_pruned_until_it_is_unpinned()
    {
        var (s, clock) = App(keep: 2);
        var a = AddSaves(s, clock, 2, "A");
        s.Vault.Pin(a[0], "before trading");                 // the oldest
        var b0 = AddSaves(s, clock, 1, "B")[0];
        Assert.Equal([b0, a[0]], s.Vault.List().Select(e => e.Ref)); // a[1] went instead of the pinned a[0]

        s.Vault.Pin(b0, null);
        var c0 = AddSaves(s, clock, 1, "C")[0];
        Assert.Equal([c0, b0, a[0]], s.Vault.List().Select(e => e.Ref)); // one over the limit: the rest are pinned or newest
        Assert.True(s.Vault.Usage().OverLimit);
        Assert.Single(Audit(s, "vault-over-limit"));

        var unpinned = s.Vault.Unpin(a[0]);
        Assert.False(unpinned.Pinned);
        Assert.Equal([c0, b0], s.Vault.List().Select(e => e.Ref)); // unpinning applies the limit straight away
        Assert.False(s.Vault.Usage().OverLimit);
    }

    [Fact]
    public void The_latest_pin_or_unpin_wins_and_the_note_is_kept()
    {
        var (s, _) = App();
        var r = s.Vault.Add(Bytes(SampleSaves.SwordShield()), VaultSources.Upload, null).Entry.Ref;
        Assert.False(s.Vault.Load(r)!.Pinned);
        s.Vault.Pin(r, "  before\u0007 the shiny hunt  ");
        Assert.Equal("before the shiny hunt", s.Vault.Load(r)!.Pin!.Detail);
        s.Vault.Unpin(r);
        Assert.False(s.Vault.Load(r)!.Pinned);
        s.Vault.Pin(r, new string('x', 500));
        Assert.Equal(SaveVault.MaxNoteLength, s.Vault.Load(r)!.Pin!.Detail!.Length);
        Assert.Throws<FileNotFoundException>(() => s.Vault.Pin(new VaultRef("sw", "20991231-000000-00000000"), null));
    }

    [Fact]
    public void Deleting_a_pinned_save_needs_a_second_confirmation()
    {
        var (s, _) = App();
        var entry = s.Vault.Add(Bytes(SampleSaves.SwordShield()), VaultSources.Upload, null).Entry.Ref;
        s.Vault.Pin(entry, "before trading");

        var e = Assert.Throws<InvalidOperationException>(() => s.Vault.Delete(entry, allowPinned: false, "test"));
        Assert.Contains("before trading", e.Message);
        Assert.NotNull(s.Vault.Load(entry));

        s.Vault.Delete(entry, allowPinned: true, "test");
        Assert.Null(s.Vault.Load(entry));
        var del = Assert.Single(Audit(s, "vault-delete"));
        Assert.True(del.GetProperty("Detail").GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public void The_size_cap_prunes_oldest_but_never_the_newest_save_of_a_game()
    {
        var (s, clock) = App(keep: 100, maxMb: 1); // each Sword/Shield save is about 1.5 MB, so only one fits
        var refs = AddSaves(s, clock, 3);
        var usum = s.Vault.Add(Bytes(SampleSaves.UltraSunMoon()), VaultSources.Upload, null).Entry.Ref;
        Assert.Equal([usum, refs[2]], s.Vault.List().Select(e => e.Ref)); // over the cap, but each game keeps its newest
        Assert.True(s.Vault.Usage().OverLimit);
    }

    // ---------------------------------------------------------------- downloads

    [Theory]
    [InlineData("..", "20261020-120000-abcdef12")]
    [InlineData("sw", "..")]
    [InlineData("sw", "../../audit.jsonl")]
    [InlineData("sw/../..", "20261020-120000-abcdef12")]
    [InlineData("sw", "20261020-120000-abcdef12/../../x")]
    [InlineData("SW", "20261020-120000-abcdef12")]
    [InlineData("sw", "20261020-120000-ABCDEF12")]
    [InlineData("sw", "%2e%2e")]
    [InlineData("sw\\..", "x")]
    [InlineData("", "")]
    public void Download_refuses_anything_but_a_listed_entry(string game, string id)
    {
        var (s, _) = App();
        s.Vault.Add(Bytes(SampleSaves.SwordShield()), VaultSources.Upload, null);
        File.WriteAllText(Path.Combine(s.Options.DataDir, "secret.txt"), "not yours");
        Assert.False(SaveVault.IsValidRef(game, id) && s.Vault.Load(new VaultRef(game, id)) is not null);
        var result = VaultApi.Serve(s, game, id, r => s.Vault.ReadMain(r), _ => "main", "application/octet-stream");
        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public void Downloads_serve_the_stored_bytes_and_refuse_a_tampered_copy()
    {
        var (s, _) = App();
        var bytes = Bytes(SampleSaves.SwordShield());
        var e = s.Vault.Add(bytes, VaultSources.Upload, null).Entry;

        var ok = Assert.IsType<FileContentHttpResult>(VaultApi.Serve(s, e.Record.Game, e.Record.Id, r => s.Vault.ReadMain(r), _ => "main", "application/octet-stream"));
        Assert.Equal(bytes, ok.FileContents.ToArray());
        Assert.Equal("main", ok.FileDownloadName);

        var zip = BackupZip.MainOnly(bytes, e.Record.ArchivedAt);
        Assert.Equal(bytes, BackupZip.ExtractMain(zip));
        using (var archive = new ZipArchive(new MemoryStream(zip)))
            Assert.Equal("main", Assert.Single(archive.Entries).FullName);
        Assert.Equal("SimTrainer - 2026-10-20_12-00-00", SaveVault.JksvName(e.Record));
        Assert.Null(s.Vault.ReadOriginalZip(e.Ref)); // an upload has no original zip

        var path = Path.Combine(s.Vault.Root, e.Record.Game, e.Record.Id, "main");
        var tampered = bytes.ToArray();
        tampered[0x200] ^= 0xFF;
        File.WriteAllBytes(path, tampered);
        var refused = VaultApi.Serve(s, e.Record.Game, e.Record.Id, r => s.Vault.ReadMain(r), _ => "main", "application/octet-stream");
        Assert.Equal(409, Assert.IsType<ProblemHttpResult>(refused).StatusCode);
    }

    [Fact]
    public void Listing_groups_by_game_newest_first_and_ignores_stray_folders()
    {
        var (s, clock) = App();
        var refs = AddSaves(s, clock, 2);
        s.Vault.Pin(refs[0], "first");
        Directory.CreateDirectory(Path.Combine(s.Vault.Root, "sw", "not-an-entry"));
        Directory.CreateDirectory(Path.Combine(s.Vault.Root, "..hidden"));
        Directory.CreateDirectory(Path.Combine(s.Vault.Root, "sw", ".incoming-deadbeef"));
        var json = JsonSerializer.SerializeToElement(VaultApi.List(s));
        var game = Assert.Single(json.GetProperty("games").EnumerateArray());
        Assert.Equal("sw", game.GetProperty("game").GetString());
        var entries = game.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal([refs[1].Id, refs[0].Id], entries.Select(x => x.GetProperty("Id").GetString()));
        Assert.Equal("first", entries[1].GetProperty("pinNote").GetString());
        Assert.Equal("T0", entries[1].GetProperty("details").GetProperty("Trainer").GetString());
        Assert.Equal(20, json.GetProperty("keepPerGame").GetInt32());
    }
}
