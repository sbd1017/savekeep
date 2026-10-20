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

var sword = SampleSaves.MainBytes(SampleSaves.SwordShield("Sim"));
var rival = SampleSaves.MainBytes(SampleSaves.SwordShield("Rival"));
var ultraSun = SampleSaves.MainBytes(SampleSaves.UltraSunMoon("Sim"));
var now = DateTime.Now;

await using var ftp = new FtpSimulator(port, lan ? IPAddress.Any : IPAddress.Loopback);
ftp.AddJksvBackup("Pokemon Sword", "Sim", now.AddDays(-3), sword, zip: false);
ftp.AddJksvBackup("Pokemon Sword", "Sim", now.AddDays(-1), sword, zip: true);
ftp.AddJksvBackup("Pokemon Sword", "Rival", now.AddHours(-5), rival, zip: true);
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
    var fresh = SampleSaves.SwordShield("Sim");
    fresh.Money = (uint)Random.Shared.Next(1000, 999_999); // a different save each time
    var path = ftp.AddJksvBackup("Pokemon Sword", "Sim", DateTime.Now, SampleSaves.MainBytes(fresh), zip: true);
    Console.WriteLine("added " + path);
}
return 0;
