using SaveKeep.Core.Backups;
using SaveKeep.Core.Storage;
using SaveKeep.Core.Vault;

namespace SaveKeep.Web;

public sealed class SaveKeepOptions
{
    /// <summary>Vault, audit log, JKSV receiver state and FTP search history.</summary>
    public string DataDir { get; set; } = "data";
    /// <summary>Extra Host header names to accept (besides IP addresses, localhost, *.local and the machine name).</summary>
    public List<string> AllowedHostNames { get; set; } = [];
    /// <summary>ftpd's default port, on both Switch and 3DS.</summary>
    public int FtpPort { get; set; } = 5000;
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>Saves kept per game. Older ones are removed unless pinned.</summary>
    public int VaultKeepPerGame { get; set; } = 20;
    /// <summary>Size cap for the whole vault, in MB.</summary>
    public int VaultMaxTotalMB { get; set; } = 1024;
}

/// <summary>Process-wide state. FTP work is serialised through <see cref="FtpGate"/>.</summary>
public sealed class AppState
{
    public SaveKeepOptions Options { get; }
    public AuditLog Audit { get; }
    public SaveVault Vault { get; }
    /// <summary>JKSV's WebDAV uploads (/dav/jksv), filed into the vault. Off until a login is generated.</summary>
    public JksvReceiver Jksv { get; }
    /// <summary>Backups seen in earlier searches, so the suggestion never depends on a console's clock.</summary>
    public BackupHistory BackupHistory { get; }
    public SemaphoreSlim FtpGate { get; } = new(1, 1);
    /// <summary>The last FTP search: where it looked, for which game, and what it found.</summary>
    public BackupListing? Listing { get; set; }
    public TimeProvider Clock { get; }

    public AppState(SaveKeepOptions options, TimeProvider? clock = null)
    {
        Options = options;
        Clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(options.DataDir);
        Audit = new AuditLog(options.DataDir, Clock);
        Vault = new SaveVault(options.DataDir, Audit, new VaultOptions
        {
            KeepPerGame = Math.Max(1, options.VaultKeepPerGame),
            MaxTotalBytes = Math.Max(1, options.VaultMaxTotalMB) * 1024L * 1024,
        }, Clock);
        Jksv = new JksvReceiver(options.DataDir, Vault, Audit, Clock);
        BackupHistory = new BackupHistory(options.DataDir);
    }

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(Options.TimeoutSeconds, 1, 120));
}

public sealed record BackupListing(string Host, int Port, string GameId, BackupTarget Target, DateTimeOffset At, DiscoveryResult Result);
