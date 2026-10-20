using PKHeX.Core;
using SaveKeep.Core.Games;
using SaveKeep.Core.Saves;

namespace SaveKeep.Core.Backups;

/// <summary>A backup copied off the console into memory and checked. <see cref="Download"/> is the zip, or <c>main</c> itself.</summary>
public sealed record FetchedBackup(BackupCandidate Candidate, byte[] Download, byte[] Main, SaveFile Save)
{
    public TrainerSummary Trainer => TrainerSummary.From(Save);
    public byte[]? OriginalZip => Candidate.IsZip ? Download : null;
}

/// <summary>
/// Downloads a discovered backup (nothing on the console is changed), takes <c>main</c> out of JKSV zips
/// safely, and checks it is a save for the expected game, and for the expected trainer if one is given.
/// </summary>
public static class BackupFetcher
{
    public static async Task<FetchedBackup> FetchAsync(
        ReadOnlyFtpClient ftp, BackupCandidate candidate, KnownGame game, TrainerSummary? expectedTrainer, CancellationToken ct)
    {
        ReadOnlyFtpClient.ValidateArgument(candidate.RemotePath);
        var downloaded = await ftp.DownloadAsync(candidate.RemotePath, candidate.IsZip ? BackupZip.MaxArchiveSize : SaveReader.MaxSaveSize, ct).ConfigureAwait(false);
        var main = candidate.IsZip ? BackupZip.ExtractMain(downloaded) : downloaded;
        if (main.Length is 0 or > SaveReader.MaxSaveSize) throw new InvalidDataException("The backup's 'main' is empty or too large to be a save.");

        var sav = SaveReader.ReadAs(main, game);
        var trainer = TrainerSummary.From(sav);
        if (expectedTrainer is not null && !trainer.SameAs(expectedTrainer))
            throw new InvalidDataException($"The backup belongs to trainer {trainer.Name} (ID {trainer.DisplayTid}), not {expectedTrainer.Name} (ID {expectedTrainer.DisplayTid}).");
        return new FetchedBackup(candidate, downloaded, main, sav);
    }
}
