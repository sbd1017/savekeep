using System.Net;
using SaveKeep.Core.Backups;
using SaveKeep.Core.Games;
using SaveKeep.Sim;

namespace SaveKeep.Tests;

/// <summary>
/// The suggested backup must not depend on the console's clock. The listings below are the kind seen on real SD cards:
/// a 3DS whose clock was never set (still 2011, so a new backup sorts before an older one) and JKSV zips on a Switch.
/// </summary>
public class BackupSuggestionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Pi0 = new(2026, 10, 20, 13, 0, 0, TimeSpan.Zero);
    private const string UsumFolder = "0x01B50 Pokémon Ultra Sun";

    private static BackupTarget UltraSun => BackupTarget.For(KnownGames.ById("us")!, "Kai");
    private static BackupTarget Sword => BackupTarget.For(KnownGames.ById("sw")!, "Player");

    private static async Task<DiscoveryResult> Find(FtpSimulator ftp, BackupTarget target, BackupHistory history, DateTimeOffset now)
    {
        await using var c = new ReadOnlyFtpClient(IPAddress.Loopback, ftp.Port, TimeSpan.FromSeconds(5));
        await c.ConnectAsync(Ct);
        return await BackupDiscovery.FindAsync(c, target, Ct, history, now);
    }

    /// <summary>File dates come from the same wrong console clock as the names; undated names get an old date.</summary>
    private static void AddCheckpoint(FtpSimulator ftp, string name, byte[] main) =>
        ftp.AddFile($"/3ds/Checkpoint/saves/{UsumFolder}/{name}/main", main,
            BackupDiscovery.ParseStamp(name) is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static FtpSimulator UnsetClockUltraSunCard(byte[] main)
    {
        var ftp = new FtpSimulator();
        foreach (var n in new[] { "first", "second", "20110101-000120", "20110101-000530", "20110203-081500", "20110104-101010" })
            AddCheckpoint(ftp, n, main);
        return ftp;
    }

    [Fact]
    public async Task A_wrong_3ds_clock_does_not_pick_an_old_backup()
    {
        var main = SampleSaves.MainBytes(SampleSaves.UltraSunMoon("Kai"));
        await using var ftp = UnsetClockUltraSunCard(main);
        var history = new BackupHistory(null);

        // First search ever: nothing can be suggested without knowing what is new.
        var first = await Find(ftp, UltraSun, history, Pi0);
        Assert.Null(first.Suggested);
        Assert.Null(first.PreviousSearchAt);
        Assert.Equal("20110203-081500", first.NewestByName!.Name); // what sorting by name would pick
        Assert.Contains(first.Notes, n => n.Contains("First search"));

        // Save, back up with Checkpoint: the console clock names it 20110106-120000, older-looking than 20110203-081500.
        AddCheckpoint(ftp, "20110106-120000", main);
        var second = await Find(ftp, UltraSun, history, Pi0.AddMinutes(10));
        Assert.Equal("20110106-120000", second.Suggested!.Name);
        Assert.True(second.Suggested.IsNew);
        Assert.Equal("20110203-081500", second.NewestByName!.Name);
        Assert.Contains(second.Notes, n => n.Contains("console's clock") && n.Contains("20110203-081500"));
        Assert.Equal(Pi0, second.PreviousSearchAt);
        Assert.Equal("20110106-120000", second.Candidates[0].Name); // new listed first
        Assert.All(second.Candidates.Skip(1), c => Assert.False(c.IsNew));
    }

    [Fact]
    public async Task Switch_jksv_backups_are_handled_the_same_way()
    {
        var main = SampleSaves.MainBytes(SampleSaves.SwordShield("Player"));
        await using var ftp = new FtpSimulator();
        ftp.AddJksvBackup("Pokemon Sword", "Player", new DateTime(2025, 6, 14, 18, 30, 0), main, zip: false);
        ftp.AddJksvBackup("Pokemon Sword", "Sibling", new DateTime(2026, 3, 9, 10, 5, 0), main, zip: true);
        var history = new BackupHistory(null);
        Assert.Null((await Find(ftp, Sword, history, Pi0)).Suggested);

        // A backup whose name time is older than an existing one (the Switch clock was wrong).
        ftp.AddJksvBackup("Pokemon Sword", "Player", new DateTime(2024, 1, 1, 9, 0, 0), main, zip: true);
        var r = await Find(ftp, Sword, history, Pi0.AddMinutes(3));
        Assert.Equal("Player - 2024-01-01_09-00-00.zip", r.Suggested!.Name);
        Assert.Equal("Sibling - 2026-03-09_10-05-00.zip", r.NewestByName!.Name);
    }

    [Fact]
    public async Task Nothing_new_or_several_new_means_no_suggestion()
    {
        var main = SampleSaves.MainBytes(SampleSaves.UltraSunMoon("Kai"));
        await using var ftp = UnsetClockUltraSunCard(main);
        var history = new BackupHistory(null);
        await Find(ftp, UltraSun, history, Pi0);

        var none = await Find(ftp, UltraSun, history, Pi0.AddMinutes(1));
        Assert.Null(none.Suggested);
        Assert.Contains(none.Notes, n => n.Contains("No new backup"));

        AddCheckpoint(ftp, "20110105-180000", main);
        AddCheckpoint(ftp, "20110105-180500", main);
        var two = await Find(ftp, UltraSun, history, Pi0.AddMinutes(2));
        Assert.Null(two.Suggested);
        Assert.Equal(2, two.Candidates.Count(c => c.IsNew));
        Assert.Contains(two.Notes, n => n.Contains("2 backups appeared"));
    }

    [Fact]
    public async Task History_survives_a_restart_and_a_changed_size_counts_as_new()
    {
        var dir = TestEnv.TempDir();
        var main = SampleSaves.MainBytes(SampleSaves.UltraSunMoon("Kai"));
        await using var ftp = UnsetClockUltraSunCard(main);
        await Find(ftp, UltraSun, new BackupHistory(dir), Pi0);
        Assert.True(File.Exists(Path.Combine(dir, "backup-history.json")));

        // After a restart a new history object reads the file. The same name rewritten with a different size counts as new.
        AddCheckpoint(ftp, "second", main.Concat(new byte[16]).ToArray());
        var r = await Find(ftp, UltraSun, new BackupHistory(dir), Pi0.AddMinutes(5));
        Assert.Equal(Pi0, r.PreviousSearchAt);
        Assert.Equal("second", r.Suggested!.Name);
    }

    [Fact]
    public async Task Unseen_backups_are_inspected_even_beyond_the_listing_limit()
    {
        var main = SampleSaves.MainBytes(SampleSaves.UltraSunMoon("Kai"));
        await using var ftp = new FtpSimulator();
        for (int i = 0; i < BackupDiscovery.MaxInspected + 5; i++)
            AddCheckpoint(ftp, $"2026{(i % 9) + 1:D2}01-0000{i:D2}", main);
        var history = new BackupHistory(null);
        await Find(ftp, UltraSun, history, Pi0);
        AddCheckpoint(ftp, "19990101-000000", main); // oldest-looking name of all
        var r = await Find(ftp, UltraSun, history, Pi0.AddMinutes(1));
        Assert.Equal("19990101-000000", r.Suggested!.Name);
    }

    [Fact]
    public async Task Searching_is_still_read_only()
    {
        var main = SampleSaves.MainBytes(SampleSaves.UltraSunMoon("Kai"));
        await using var ftp = UnsetClockUltraSunCard(main);
        var history = new BackupHistory(null);
        await Find(ftp, UltraSun, history, Pi0);
        AddCheckpoint(ftp, "20110106-120000", main);
        await Find(ftp, UltraSun, history, Pi0.AddMinutes(1));
        var verbs = ftp.Commands.Select(c => c.Split(' ')[0].ToUpperInvariant()).ToList();
        Assert.All(verbs, v => Assert.Contains(v, ReadOnlyFtpClient.AllowedVerbs));
        Assert.DoesNotContain(verbs, v => FtpSimulator.WriteVerbs.Contains(v));
    }
}
