using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SaveKeep.Core.Util;

namespace SaveKeep.Core.Backups;

public sealed class FtpException(string message, int code = 0, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The server's reply code, or 0 when the failure was local (connection, limits, refusal).</summary>
    public int Code { get; } = code;
}

public sealed record FtpEntry(string Name, bool IsDirectory, long Size, string ListedDate);

/// <summary>
/// A deliberately tiny, read-only FTP client for console homebrew servers (ftpd, sys-ftpd).
/// Anonymous login only (it never sends a password), passive mode only, and the data connection
/// always goes to the control connection's own address. The only commands it can send are in
/// <see cref="AllowedVerbs"/>; nothing that creates, changes or deletes a file on the console exists here.
/// Written for this project from RFC 959 / RFC 3659; no code copied.
/// </summary>
public sealed class ReadOnlyFtpClient : IAsyncDisposable
{
    /// <summary>Every verb this client can put on the wire.</summary>
    public static readonly IReadOnlySet<string> AllowedVerbs =
        new HashSet<string>(["USER", "TYPE", "PASV", "LIST", "NLST", "MDTM", "RETR", "QUIT"], StringComparer.Ordinal);

    private const int MaxLine = 8 * 1024;
    private const int MaxListing = 4 * 1024 * 1024;
    private static readonly Regex PasvReply = new(@"(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3})", RegexOptions.CultureInvariant);
    private static readonly Regex UnixListLine = new(
        @"^([\-dlbcps])[rwxsStTl\-]{9}\S*\s+\d+\s+\S+\s+\S+\s+(\d+)\s+([A-Za-z]{3}\s+\d{1,2}\s+(?:\d{1,2}:\d{2}|\d{4}))\s(.+)$",
        RegexOptions.CultureInvariant);

    private readonly TimeSpan _timeout;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly byte[] _buf = new byte[4096];
    private int _bufLen, _bufPos;

    public IPAddress Address { get; }
    public int Port { get; }
    public string? Greeting { get; private set; }
    /// <summary>Every command sent, in order (arguments included), for the audit log and tests.</summary>
    public List<string> Sent { get; } = [];

    public ReadOnlyFtpClient(IPAddress address, int port, TimeSpan? timeout = null)
    {
        if (!LanAddress.IsLocal(address))
            throw new ArgumentException("The FTP server must be on your local network.");
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        Port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Connects and logs in as <c>anonymous</c>. Refuses (and disconnects) if the server asks for a password.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        using var cts = Linked(ct);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(Address, Port, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            tcp.Dispose();
            throw new FtpException($"Could not connect to the FTP server at {Address}:{Port} ({e.Message}). Is ftpd running on the console?", 0, e);
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        try
        {
            var (code, text) = await ReadReplyAsync(cts.Token).ConfigureAwait(false);
            if (code != 220) throw new FtpException($"Unexpected FTP greeting: {code} {text}", code);
            Greeting = text;

            (code, text) = await CommandAsync("USER", "anonymous", cts.Token).ConfigureAwait(false);
            if (code is 331 or 332)
                throw new FtpException("The FTP server asks for a password. SaveKeep only uses anonymous login and never sends a password: turn the password off in ftpd's settings, or download the backup yourself and upload it.", code);
            if (code != 230) throw new FtpException($"Anonymous login was refused: {code} {text}", code);

            // Binary transfers. Some servers only ever send binary and reject TYPE; that is harmless.
            await CommandAsync("TYPE", "I", cts.Token).ConfigureAwait(false);
        }
        catch
        {
            Drop();
            throw;
        }
    }

    public async Task<IReadOnlyList<FtpEntry>> ListAsync(string path, CancellationToken ct)
    {
        var bytes = await TransferAsync("LIST", path, MaxListing, ct).ConfigureAwait(false);
        var entries = new List<FtpEntry>();
        foreach (var line in Lines(bytes))
        {
            var m = UnixListLine.Match(line);
            if (!m.Success) continue;
            var name = m.Groups[4].Value;
            if (m.Groups[1].Value == "l") continue; // symlinks are never followed
            entries.Add(new FtpEntry(name, m.Groups[1].Value == "d", long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value));
        }
        return entries;
    }

    public async Task<IReadOnlyList<string>> NameListAsync(string path, CancellationToken ct)
    {
        var bytes = await TransferAsync("NLST", path, MaxListing, ct).ConfigureAwait(false);
        return Lines(bytes).Select(l => l.Contains('/') ? l[(l.LastIndexOf('/') + 1)..] : l).Where(l => l.Length > 0).ToList();
    }

    /// <summary>RFC 3659 modification time (UTC), or null if the server does not support it for this file.</summary>
    public async Task<DateTimeOffset?> GetModifiedAsync(string path, CancellationToken ct)
    {
        using var cts = Linked(ct);
        var (code, text) = await CommandAsync("MDTM", path, cts.Token).ConfigureAwait(false);
        if (code != 213) return null;
        var stamp = text.Trim();
        if (stamp.Length >= 14 && DateTime.TryParseExact(stamp[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return new DateTimeOffset(t, TimeSpan.Zero);
        return null;
    }

    /// <summary>Downloads one file into memory. Aborts if it is larger than <paramref name="maxBytes"/>.</summary>
    public Task<byte[]> DownloadAsync(string path, int maxBytes, CancellationToken ct) => TransferAsync("RETR", path, maxBytes, ct);

    // ---------------------------------------------------------------- protocol

    private async Task<byte[]> TransferAsync(string verb, string path, int maxBytes, CancellationToken ct)
    {
        // Console Wi-Fi can be slow; a whole transfer gets several times the per-reply timeout.
        using var cts = Linked(ct, _timeout * 6);
        var (code, text) = await CommandAsync("PASV", null, cts.Token).ConfigureAwait(false);
        if (code != 227) throw new FtpException($"The FTP server refused passive mode: {code} {text}", code);
        var m = PasvReply.Match(text);
        if (!m.Success) throw new FtpException($"Malformed passive-mode reply: {text}", code);
        int p1 = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture), p2 = int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
        if (p1 > 255 || p2 > 255 || p1 * 256 + p2 == 0) throw new FtpException($"Invalid passive-mode port in: {text}", code);
        // The advertised address is ignored: data only ever flows from the console we already talk to.
        using var data = new TcpClient();
        try { await data.ConnectAsync(Address, p1 * 256 + p2, cts.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            throw new FtpException($"Could not open the FTP data connection ({e.Message}).", 0, e);
        }

        (code, text) = await CommandAsync(verb, path, cts.Token).ConfigureAwait(false);
        if (code is not (125 or 150))
            throw new FtpException($"{verb} {path} failed: {code} {text}", code);

        var ms = new MemoryStream();
        var stream = data.GetStream();
        var chunk = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false);
                if (n == 0) break;
                if (ms.Length + n > maxBytes)
                {
                    Drop(); // the server will still be sending; this control connection is finished
                    throw new FtpException($"{path} is larger than the {maxBytes / 1024 / 1024} MB limit; download stopped.");
                }
                ms.Write(chunk, 0, n);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            Drop();
            throw new FtpException($"The FTP transfer of {path} was interrupted ({e.Message}).", 0, e);
        }
        data.Dispose();
        (code, text) = await ReadReplyAsync(cts.Token).ConfigureAwait(false);
        if (code is not (226 or 250))
            throw new FtpException($"{verb} {path} did not complete: {code} {text}", code);
        return ms.ToArray();
    }

    private async Task<(int Code, string Text)> CommandAsync(string verb, string? argument, CancellationToken ct)
    {
        if (!AllowedVerbs.Contains(verb))
            throw new InvalidOperationException($"FTP command {verb} is not allowed; this client is read-only.");
        if (argument is not null) ValidateArgument(argument);
        var stream = _stream ?? throw new FtpException("Not connected to the FTP server.");
        var line = argument is null ? verb : verb + " " + argument;
        Sent.Add(line);
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Drop();
            throw new FtpException($"FTP connection lost during {verb} ({e.Message}).", 0, e);
        }
        return await ReadReplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Paths are absolute, and may not climb, carry control characters or smuggle a second command.</summary>
    public static void ValidateArgument(string argument)
    {
        if (argument.Length is 0 or > 1024) throw new ArgumentException("FTP path is empty or too long.");
        if (argument.Any(c => char.IsControl(c)))
            throw new ArgumentException("FTP path contains control characters.");
        if (argument.Split('/').Any(seg => seg is ".." or "."))
            throw new ArgumentException("FTP path may not contain '.' or '..' segments.");
        if (argument.Contains('\\')) throw new ArgumentException("FTP path may not contain backslashes.");
    }

    private async Task<(int Code, string Text)> ReadReplyAsync(CancellationToken ct)
    {
        try
        {
            var first = await ReadLineAsync(ct).ConfigureAwait(false);
            if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                throw new FtpException($"Malformed FTP reply: {first}");
            if (first.Length > 3 && first[3] == '-')
            {
                // Multi-line reply: ends at a line starting with the same code and a space.
                var sb = new StringBuilder(first[4..]);
                for (int i = 0; i < 200; i++)
                {
                    var next = await ReadLineAsync(ct).ConfigureAwait(false);
                    if (next.Length >= 4 && next.StartsWith(first[..3], StringComparison.Ordinal) && next[3] == ' ')
                        return (code, sb.Append('\n').Append(next[4..]).ToString());
                    sb.Append('\n').Append(next);
                }
                throw new FtpException("FTP reply too long.");
            }
            return (code, first.Length > 4 ? first[4..] : "");
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Drop();
            throw new FtpException($"FTP connection lost ({e.Message}).", 0, e);
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("Not connected.");
        var line = new List<byte>();
        while (true)
        {
            if (_bufPos == _bufLen)
            {
                _bufLen = await stream.ReadAsync(_buf, ct).ConfigureAwait(false);
                _bufPos = 0;
                if (_bufLen == 0) throw new IOException("The FTP server closed the connection.");
            }
            byte b = _buf[_bufPos++];
            if (b == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
            line.Add(b);
            if (line.Count > MaxLine) throw new IOException("FTP reply line too long.");
        }
    }

    private static IEnumerable<string> Lines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    private CancellationTokenSource Linked(CancellationToken ct, TimeSpan? timeout = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? _timeout);
        return cts;
    }

    private void Drop()
    {
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _tcp = null;
        _stream = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await CommandAsync("QUIT", null, cts.Token).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
        Drop();
    }
}
