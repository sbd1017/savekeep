using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaveKeep.Core.Backups;
using SaveKeep.Core.Storage;
using SaveKeep.Core.Util;

namespace SaveKeep.Core.Vault;

/// <summary>The receiver's login. Only a SHA-256 of the password is kept; the password itself is shown once.</summary>
public sealed record JksvCredentials(string Username, string PasswordSha256, DateTimeOffset CreatedAt);

/// <summary>One upload attempt as the web page lists it. Outcome is <c>added</c>, <c>duplicate</c> or <c>refused</c>.</summary>
public sealed record JksvUpload(
    DateTimeOffset At, string Path, long Size, string Outcome, string Message,
    string? Sha256 = null, string? Entry = null, string? Edition = null, string? Trainer = null, string? Remote = null);

/// <summary>What the WebDAV layer answers for a PUT.</summary>
public sealed record JksvUploadResult(int Status, JksvUpload Upload);

/// <summary>
/// Receives JKSV's WebDAV uploads into the vault. Upload only: a body goes to a staging file under a random name,
/// is checked (size, zip, a single <c>main</c>, PKHeX recognises it) and filed into the vault with source
/// <see cref="VaultSources.JksvWebDav"/>; the staging file is then removed. Names sent by the client never become
/// paths on disk, nothing is ever read back to the client, and nothing stored is overwritten or deleted from here.
/// Folders JKSV creates (MKCOL) are only remembered as names so its listing works; they hold nothing.
/// </summary>
public sealed class JksvReceiver
{
    /// <summary>The WebDAV root on the web port, also JKSV's <c>basepath</c>.</summary>
    public const string BasePath = "dav/jksv";
    /// <summary>A JKSV zip of a Sword/Shield save is about 1.5 MB; anything far larger is refused before it is stored.</summary>
    public const int MaxUploadBytes = 16 * 1024 * 1024;
    public const int MaxFolders = 256;
    /// <summary>The 2024 JKSV uses <c>JKSV/&lt;game&gt;/</c>, the 2025 rewrite <c>&lt;game&gt;/</c>; one spare level.</summary>
    public const int MaxFolderDepth = 3;
    public const int MaxSegmentLength = 255;
    public const int MaxPathLength = 1024;
    /// <summary>Wrong logins tolerated within <see cref="FailureWindow"/> before every request is refused for a while.</summary>
    public const int MaxAuthFailures = 20;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    private const string UsernameAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";
    private const string PasswordAlphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLine = new() { WriteIndented = false };

    private readonly SaveVault _vault;
    private readonly AuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset _lastFailureAudit = DateTimeOffset.MinValue;
    private JksvCredentials? _credentials;
    private readonly SortedSet<string> _folders = new(StringComparer.Ordinal);

    public string Dir { get; }
    public string IncomingDir { get; }
    private string CredentialsPath => Path.Combine(Dir, "credentials.json");
    private string FoldersPath => Path.Combine(Dir, "folders.json");
    private string LogPath => Path.Combine(Dir, "uploads.jsonl");

    public JksvReceiver(string dataDir, SaveVault vault, AuditLog audit, TimeProvider? clock = null)
    {
        Dir = Path.GetFullPath(Path.Combine(dataDir, "webdav"));
        IncomingDir = Path.Combine(Dir, "incoming");
        Directory.CreateDirectory(IncomingDir);
        // Anything left in staging is from an interrupted upload that was never filed.
        foreach (var f in Directory.GetFiles(IncomingDir)) File.Delete(f);
        _vault = vault;
        _audit = audit;
        _clock = clock ?? TimeProvider.System;
        if (File.Exists(CredentialsPath))
        {
            try { _credentials = JsonSerializer.Deserialize<JksvCredentials>(File.ReadAllText(CredentialsPath)); }
            catch (JsonException) { _credentials = null; } // a damaged file leaves the receiver off
        }
        if (File.Exists(FoldersPath))
        {
            try
            {
                foreach (var f in JsonSerializer.Deserialize<string[]>(File.ReadAllText(FoldersPath)) ?? [])
                    if (ParsePath(f + "/", out var segs, out _) is null && segs.Length is > 0 and <= MaxFolderDepth) _folders.Add(string.Join('/', segs));
            }
            catch (JsonException) { } // folders are only names; JKSV recreates them
        }
    }

    // ---------------------------------------------------------------- login

    public bool Enabled { get { lock (_lock) return _credentials is not null; } }
    public JksvCredentials? Credentials { get { lock (_lock) return _credentials; } }

    /// <summary>Creates a new random login (replacing any old one) and turns the receiver on. The password is returned once.</summary>
    public (string Username, string Password) Generate()
    {
        var username = "jksv-" + RandomNumberGenerator.GetString(UsernameAlphabet, 8);
        var password = RandomNumberGenerator.GetString(PasswordAlphabet, 24);
        var creds = new JksvCredentials(username, Hex.Sha256(Encoding.UTF8.GetBytes(password)), _clock.GetUtcNow());
        lock (_lock)
        {
            bool replaced = _credentials is not null;
            AtomicFile.WriteAllBytes(CredentialsPath, JsonSerializer.SerializeToUtf8Bytes(creds, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(CredentialsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _credentials = creds;
            _failures.Clear();
            _audit.Write("webdav-on", $"JKSV receiver {(replaced ? "given a new login" : "turned on")}: user {username}");
        }
        return (username, password);
    }

    public void Disable()
    {
        lock (_lock)
        {
            if (_credentials is null) return;
            File.Delete(CredentialsPath);
            _credentials = null;
            _audit.Write("webdav-off", "JKSV receiver turned off; its login no longer works");
        }
    }

    /// <summary>Checks an <c>Authorization: Basic ...</c> header against the stored login, in constant time.</summary>
    public bool CheckBasic(string? authorization)
    {
        var creds = Credentials;
        if (creds is null || authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim())); }
        catch (FormatException) { return false; }
        int colon = decoded.IndexOf(':');
        if (colon < 0) return false;
        var userOk = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(decoded[..colon])), SHA256.HashData(Encoding.UTF8.GetBytes(creds.Username)));
        var passOk = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(decoded[(colon + 1)..])), Convert.FromHexString(creds.PasswordSha256));
        return userOk & passOk;
    }

    /// <summary>True while too many wrong logins were seen recently; every request is then refused.</summary>
    public bool LockedOut
    {
        get
        {
            lock (_lock)
            {
                Expire();
                return _failures.Count >= MaxAuthFailures;
            }
        }
    }

    public void RecordAuthFailure(string? remote)
    {
        lock (_lock)
        {
            Expire();
            _failures.Enqueue(_clock.GetUtcNow());
            var now = _clock.GetUtcNow();
            if (_failures.Count == MaxAuthFailures)
                _audit.Write("webdav-locked", $"{MaxAuthFailures} wrong JKSV logins within {FailureWindow.TotalMinutes:0} minutes (last from {remote}); refusing all uploads for a while");
            else if (now - _lastFailureAudit > TimeSpan.FromMinutes(1))
            {
                _lastFailureAudit = now;
                _audit.Write("webdav-auth-failed", $"Wrong or missing JKSV login from {remote}");
            }
        }
    }

    private void Expire()
    {
        var cutoff = _clock.GetUtcNow() - FailureWindow;
        while (_failures.Count > 0 && _failures.Peek() < cutoff) _failures.Dequeue();
    }

    // ---------------------------------------------------------------- paths and folders

    /// <summary>
    /// Splits a decoded request path (below <see cref="BasePath"/>) into its names. Empty segments are dropped
    /// (the 2025 JKSV sends <c>//</c> after a folder it created itself). Returns an error for anything unsafe:
    /// dot segments, backslashes, encoded separators, control characters, or names and paths that are too long or deep.
    /// </summary>
    public static string? ParsePath(string path, out string[] segments, out bool isCollection)
    {
        segments = [];
        isCollection = path.Length == 0 || path.EndsWith('/');
        if (path.Length > MaxPathLength) return "Path too long.";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p is "." or "..") return "Path climbs out of the upload folder; refused.";
            if (p.Length > MaxSegmentLength) return "Name too long.";
            if (p.Any(char.IsControl) || p.Contains('\\')) return "Unsafe character in path; refused.";
            if (p.Contains("%2f", StringComparison.OrdinalIgnoreCase) || p.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return "Encoded separator in path; refused.";
        }
        if (parts.Length > MaxFolderDepth + 1 || (isCollection && parts.Length > MaxFolderDepth)) return "Path too deep.";
        segments = parts;
        return null;
    }

    public bool FolderExists(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return true;
        lock (_lock) return _folders.Contains(string.Join('/', segments));
    }

    /// <summary>The folders directly inside one, as names.</summary>
    public IReadOnlyList<string> ChildFolders(IReadOnlyList<string> segments)
    {
        var prefix = segments.Count == 0 ? "" : string.Join('/', segments) + "/";
        lock (_lock)
            return _folders.Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.IndexOf('/', prefix.Length) < 0)
                .Select(f => f[prefix.Length..]).ToList();
    }

    public enum MkcolOutcome { Created, Exists, NoParent, TooMany }

    /// <summary>Remembers a folder name. Nothing is created on disk; uploads never land in these folders.</summary>
    public MkcolOutcome CreateFolder(IReadOnlyList<string> segments, string? remote)
    {
        if (segments.Count == 0) return MkcolOutcome.Exists;
        var key = string.Join('/', segments);
        lock (_lock)
        {
            if (_folders.Contains(key)) return MkcolOutcome.Exists;
            if (segments.Count > 1 && !_folders.Contains(string.Join('/', segments.Take(segments.Count - 1)))) return MkcolOutcome.NoParent;
            if (_folders.Count >= MaxFolders) return MkcolOutcome.TooMany;
            _folders.Add(key);
            AtomicFile.WriteAllBytes(FoldersPath, JsonSerializer.SerializeToUtf8Bytes(_folders.ToArray(), Json));
            _audit.Write("webdav-folder", $"JKSV created folder '{key}' (a name only; nothing is stored in it)", new { remote });
            return MkcolOutcome.Created;
        }
    }

    // ---------------------------------------------------------------- uploads

    /// <summary>
    /// Receives one PUT. The body is streamed into staging against <see cref="MaxUploadBytes"/>, then checked and
    /// filed into the vault (or refused). An identical save already in the vault is only noted as seen again.
    /// </summary>
    public async Task<JksvUploadResult> ReceiveAsync(Stream body, long? contentLength, string path, string? remote, CancellationToken ct)
    {
        var name = path.Length > 200 ? path[..200] + "..." : path;
        if (contentLength > MaxUploadBytes)
            return Refuse(413, name, contentLength.Value, $"Larger than {MaxUploadBytes / (1024 * 1024)} MB; not a save backup.", null, remote);
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return Refuse(415, name, contentLength ?? 0, "Only JKSV zip backups are accepted (turn on 'Export to zip' in JKSV).", null, remote);

        await _uploadGate.WaitAsync(ct).ConfigureAwait(false);
        var staging = Path.Combine(IncomingDir, $"{Guid.NewGuid():N}.part");
        try
        {
            long total = 0;
            await using (var fs = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxUploadBytes)
                        return Refuse(413, name, total, $"Larger than {MaxUploadBytes / (1024 * 1024)} MB; not a save backup.", null, remote);
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            if (total == 0) return Refuse(400, name, 0, "Empty upload.", null, remote);

            var bytes = await File.ReadAllBytesAsync(staging, ct).ConfigureAwait(false);
            var sha = Hex.Sha256(bytes);
            if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K' || bytes[2] != 3 || bytes[3] != 4)
                return Refuse(415, name, total, "Not a zip file.", sha, remote);

            VaultAddResult added;
            try
            {
                var main = BackupZip.ExtractMain(bytes);
                added = _vault.Add(main, VaultSources.JksvWebDav, $"JKSV WebDAV upload '{name}' from {remote}", null, bytes);
            }
            catch (InvalidDataException e) { return Refuse(422, name, total, e.Message, sha, remote); }

            var r = added.Entry.Record;
            var upload = new JksvUpload(_clock.GetUtcNow(), name, total, added.Added ? "added" : "duplicate",
                added.Added ? $"Stored as {added.Entry.Ref}." : $"Already stored as {added.Entry.Ref}; not kept twice.",
                sha, added.Entry.Ref.ToString(), r.Edition, $"{r.Trainer.Name} ({r.Trainer.DisplayTid})", remote);
            Log(upload);
            _audit.Write("webdav-upload", $"'{name}' ({total} bytes) from {remote}: {upload.Message}", new { sha256 = sha, entry = upload.Entry, upload.Outcome, pruned = added.Pruned.Select(p => p.ToString()) });
            return new JksvUploadResult(added.Added ? 201 : 204, upload);
        }
        finally
        {
            try { File.Delete(staging); } catch (IOException) { } // removed at the next start
            _uploadGate.Release();
        }
    }

    private JksvUploadResult Refuse(int status, string name, long size, string why, string? sha, string? remote)
    {
        var upload = new JksvUpload(_clock.GetUtcNow(), name, size, "refused", why, sha, Remote: remote);
        Log(upload);
        _audit.Write("webdav-upload-refused", $"'{name}' ({size} bytes) from {remote}: {why}", new { sha256 = sha, status });
        return new JksvUploadResult(status, upload);
    }

    private void Log(JksvUpload upload)
    {
        var line = JsonSerializer.Serialize(upload, JsonLine) + "\n";
        lock (_lock) File.AppendAllText(LogPath, line);
    }

    /// <summary>The latest upload attempts, newest first.</summary>
    public IReadOnlyList<JksvUpload> RecentUploads(int count = 20)
    {
        lock (_lock)
        {
            if (!File.Exists(LogPath)) return [];
            var list = new List<JksvUpload>();
            foreach (var l in File.ReadLines(LogPath).TakeLast(count).Reverse())
            {
                try { if (JsonSerializer.Deserialize<JksvUpload>(l) is { } u) list.Add(u); }
                catch (JsonException) { } // a damaged line only loses that line
            }
            return list;
        }
    }
}
