using System.Net;
using SaveKeep.Sim;

// A fake console SD card served over FTP, for trying SaveKeep without a Switch or 3DS.
//   savekeep-sim [--port 5021] [--lan]
// --lan listens on every interface instead of loopback. Type "n" and Enter to add a fresh Sword backup.

int port = 5021;
bool lan = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
        case "--lan": lan = true; break;
        default:
            Console.Error.WriteLine("usage: savekeep-sim [--port N] [--lan]");
            return 2;
    }
}

var now = DateTime.Now;
await using var ftp = new FtpSimulator(port, lan ? IPAddress.Any : IPAddress.Loopback);
ftp.AddJksvBackup("Pokemon Sword", "Alex", now.AddDays(-3), Dressed(SampleSaves.SwordShield("Alex"), 412_300, 38, 12, 2), zip: false);
ftp.AddJksvBackup("Pokemon Sword", "Alex", now.AddDays(-1), Dressed(SampleSaves.SwordShield("Alex"), 1_284_950, 52, 40, 3), zip: true);
ftp.AddJksvBackup("Pokemon Sword", "Rival", now.AddHours(-5), Dressed(SampleSaves.SwordShield("Rival"), 96_000, 9, 3, 1), zip: true);
var ultraSun = Dressed(SampleSaves.UltraSunMoon("Robin"), 233_800, 21, 5, 2);
ftp.AddCheckpointBackup("0x01B50 Pokémon Ultra Sun", new DateTime(2011, 1, 6, 12, 0, 0), ultraSun); // a 3DS whose clock was never set
ftp.AddCheckpointBackup("0x01B50 Pokémon Ultra Sun", new DateTime(2011, 2, 3, 8, 15, 0), ultraSun);

Console.WriteLine($"Simulated ftpd on {(lan ? "0.0.0.0" : "127.0.0.1")}:{ftp.Port}. Saves are made up; never restore them to a console.");
Console.WriteLine("Type n + Enter to add a new Sword backup, q + Enter to quit.");
while (true)
{
    var line = Console.ReadLine();
    if (line is null) { await Task.Delay(Timeout.Infinite); break; } // no terminal (run in the background): keep serving
    if (line.Trim() == "q") break;
    if (line.Trim() != "n") continue;
    // A little more progress each time, so every new backup is a different save.
    var fresh = Dressed(SampleSaves.SwordShield("Alex"), (uint)Random.Shared.Next(1_300_000, 2_000_000), 53, Random.Shared.Next(0, 60), 3);
    var path = ftp.AddJksvBackup("Pokemon Sword", "Alex", DateTime.Now, fresh, zip: true);
    Console.WriteLine("added " + path);
}
return 0;

// Gives a blank sample save its own trainer ID, some money, play time and a levelled party,
// so the save details have something to show. All of it is made up.
static byte[] Dressed(PKHeX.Core.SaveFile sav, uint money, int hours, int minutes, int party)
{
    uint seed = 2166136261;
    foreach (var c in sav.OT) seed = (seed ^ c) * 16777619;
    sav.TID16 = (ushort)(seed & 0xFFFF);
    sav.SID16 = (ushort)(seed >> 16);
    sav.Money = money;
    sav.PlayedHours = hours;
    sav.PlayedMinutes = minutes;
    var keep = new PKHeX.Core.EntityImportSettings(PKHeX.Core.EntityImportOption.Disable, PKHeX.Core.EntityImportOption.Disable, PKHeX.Core.EntityImportOption.Disable);
    for (int i = 0; i < 6; i++)
    {
        var pk = sav.GetBoxSlotAtIndex(i);
        if (pk.Species == 0) break;
        pk.CurrentLevel = (byte)Math.Min(100, 6 + hours * 3 / 4 + i * 4);
        pk.RefreshChecksum();
        sav.SetBoxSlotAtIndex(pk, i, keep);
        if (i < party) sav.SetPartySlotAtIndex(pk, i);
    }
    if (sav is PKHeX.Core.SAV8SWSH swsh) swsh.MyStatus.Watt = money / 70;
    return SampleSaves.MainBytes(sav);
}
