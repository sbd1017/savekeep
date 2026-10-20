using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SaveKeep.Sim;

/// <summary>
/// A stand-in for the ftpd homebrew app: an in-memory SD card served over FTP, passive mode only.
/// It answers the read commands (LIST, NLST, MDTM, SIZE, RETR) and records everything it receives, so tests
/// can prove that no write command (STOR, DELE, MKD, RNFR, ...) is ever sent. Write commands are refused.
/// </summary>
public sealed class FtpSimulator : IAsyncDisposable
{
    public static readonly IReadOnlySet<string> WriteVerbs = new HashSet<string>(
        ["STOR", "STOU", "APPE", "DELE", "MKD", "XMKD", "RMD", "XRMD", "RNFR", "RNTO", "SITE", "ALLO", "REST", "PORT", "EPRT"], StringComparer.OrdinalIgnoreCase);

    private sealed record Node(bool IsDir, byte[] Data, DateTime Modified);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Node> _fs = new(StringComparer.Ordinal);

    /// <summary>Every command line received, in order.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();
    /// <summary>Reply 331 to USER, as ftpd does when a user name and password are configured.</summary>
    public bool RequirePassword { get; set; }
    /// <summary>Address advertised in PASV replies. Deliberately wrong by default: clients must ignore it.</summary>
    public string AdvertisedAddress { get; set; } = "10,99,99,99";
    /// <summary>Send a multi-line greeting, as some servers do.</summary>
    public bool MultiLineGreeting { get; set; } = true;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FtpSimulator(int port = 0, IPAddress? bind = null)
    {
        _fs["/"] = new Node(true, [], DateTime.UtcNow);
        _listener = new TcpListener(bind ?? IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    // ---------------------------------------------------------------- SD card contents

    public void AddFile(string path, byte[] data, DateTime? modifiedUtc = null)
    {
        EnsureDir(Parent(path));
        _fs[path] = new Node(false, data, modifiedUtc ?? DateTime.UtcNow);
    }

    public void EnsureDir(string path)
    {
        if (path == "/" || _fs.ContainsKey(path)) return;
        EnsureDir(Parent(path));
        _fs[path] = new Node(true, [], DateTime.UtcNow);
    }

    public byte[]? Read(string path) => _fs.TryGetValue(path, out var n) && !n.IsDir ? n.Data : null;
    public IReadOnlyCollection<string> Paths => _fs.Keys.ToList();

    /// <summary>Backup names carry the console's local time; servers report modification times in UTC.</summary>
    private static DateTime Utc(DateTime stamp) => stamp.Kind == DateTimeKind.Utc ? stamp : stamp.ToUniversalTime();

    /// <summary>JKSV on Switch: <c>/JKSV/&lt;game&gt;/&lt;user&gt; - YYYY-MM-DD_HH-MM-SS[.zip]</c>.</summary>
    public string AddJksvBackup(string gameFolder, string user, DateTime stamp, byte[] main, bool zip)
    {
        var name = $"{user} - {stamp:yyyy-MM-dd_HH-mm-ss}";
        var dir = $"/JKSV/{gameFolder}";
        if (zip)
        {
            AddFile($"{dir}/{name}.zip", Zip(("main", main)), Utc(stamp));
            return $"{dir}/{name}.zip";
        }
        AddFile($"{dir}/{name}/main", main, Utc(stamp));
        return $"{dir}/{name}/main";
    }

    /// <summary>Checkpoint on 3DS: <c>/3ds/Checkpoint/saves/0x&lt;id&gt; &lt;game&gt;/YYYYMMDD-HHMMSS/main</c>.</summary>
    public string AddCheckpointBackup(string gameFolder, DateTime stamp, byte[] main)
    {
        var path = $"/3ds/Checkpoint/saves/{gameFolder}/{stamp:yyyyMMdd-HHmmss}/main";
        AddFile(path, main, Utc(stamp));
        return path;
    }

    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- protocol

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, new UTF8Encoding(false));
        TcpListener? pasv = null;
        async Task Reply(string line) => await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), _cts.Token);
        try
        {
            if (MultiLineGreeting) { await Reply("220-Simulated ftpd (SaveKeep.Sim)"); await Reply("220 Hello!"); }
            else await Reply("220 Hello!");
            bool loggedIn = false;
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                Commands.Enqueue(line);
                int sp = line.IndexOf(' ');
                var verb = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                var arg = sp < 0 ? "" : line[(sp + 1)..];
                if (WriteVerbs.Contains(verb)) { await Reply("550 Read-only simulator."); continue; }
                switch (verb)
                {
                    case "USER":
                        if (RequirePassword) await Reply("331 Need password.");
                        else { loggedIn = true; await Reply("230 OK"); }
                        break;
                    case "PASS":
                        await Reply("530 Login incorrect.");
                        break;
                    case "QUIT":
                        await Reply("221 Bye.");
                        return;
                    case "NOOP":
                        await Reply("200 OK");
                        break;
                    default:
                        if (!loggedIn) { await Reply("530 Not logged in."); break; }
                        switch (verb)
                        {
                            case "TYPE": await Reply("200 Type set."); break;
                            case "PASV":
                                pasv?.Stop();
                                pasv = new TcpListener(((IPEndPoint)client.Client.LocalEndPoint!).Address, 0);
                                pasv.Start();
                                var p = ((IPEndPoint)pasv.LocalEndpoint).Port;
                                await Reply($"227 Entering Passive Mode ({AdvertisedAddress},{p / 256},{p % 256})");
                                break;
                            case "MDTM":
                                if (_fs.TryGetValue(arg, out var f) && !f.IsDir) await Reply($"213 {f.Modified:yyyyMMddHHmmss}");
                                else await Reply("550 No such file.");
                                break;
                            case "SIZE":
                                if (_fs.TryGetValue(arg, out var sz) && !sz.IsDir) await Reply($"213 {sz.Data.Length}");
                                else await Reply("550 No such file.");
                                break;
                            case "LIST" or "NLST" or "RETR":
                                if (pasv is null) { await Reply("425 Use PASV first."); break; }
                                var payload = verb == "RETR" ? Retrieve(arg) : Listing(arg, verb == "LIST");
                                if (payload is null) { await Reply("550 No such file or directory."); pasv.Stop(); pasv = null; break; }
                                using (var data = await pasv.AcceptTcpClientAsync(_cts.Token))
                                {
                                    await Reply("150 Opening data connection.");
                                    await data.GetStream().WriteAsync(payload, _cts.Token);
                                }
                                pasv.Stop(); pasv = null;
                                await Reply("226 Transfer complete.");
                                break;
                            default:
                                await Reply("502 Not implemented.");
                                break;
                        }
                        break;
                }
            }
        }
        catch { } // client went away
        finally { pasv?.Stop(); }
    }

    private byte[]? Retrieve(string path) => _fs.TryGetValue(path, out var n) && !n.IsDir ? n.Data : null;

    private byte[]? Listing(string path, bool longFormat)
    {
        path = path.Length == 0 ? "/" : path.TrimEnd('/').Length == 0 ? "/" : path.TrimEnd('/');
        if (!_fs.TryGetValue(path, out var dir) || !dir.IsDir) return null;
        var prefix = path == "/" ? "/" : path + "/";
        var sb = new StringBuilder();
        foreach (var (p, n) in _fs.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key != prefix && !kv.Key[prefix.Length..].Contains('/')).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var name = p[prefix.Length..];
            if (longFormat)
                sb.Append(CultureInfo.InvariantCulture, $"{(n.IsDir ? 'd' : '-')}rwxrwxrwx 1 3DS 3DS {n.Data.Length} {n.Modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)} {name}\r\n");
            else
                sb.Append(name).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Adds a raw entry name to a directory listing, bypassing path rules (for hostile-server tests).</summary>
    public void AddRawEntry(string dir, string name, bool isDir, byte[]? data = null)
    {
        EnsureDir(dir);
        _fs[(dir == "/" ? "/" : dir + "/") + name] = new Node(isDir, data ?? [], DateTime.UtcNow);
    }

    private static string Parent(string path)
    {
        var i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
