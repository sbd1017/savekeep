using System.IO.Compression;
using SaveKeep.Core.Saves;

namespace SaveKeep.Core.Backups;

/// <summary>Reading and writing JKSV-style zips. Nothing is ever extracted by entry name.</summary>
public static class BackupZip
{
    /// <summary>A JKSV zip of a Sword/Shield save is about 1.5 MB; anything far larger is refused.</summary>
    public const int MaxArchiveSize = 32 * 1024 * 1024;
    public const int MaxEntries = 4096;

    /// <summary>
    /// Returns the single <c>main</c> entry of a JKSV zip. The whole archive is refused if any entry name is
    /// absolute, climbs out (".."), or is otherwise unsafe, if there are too many entries, or if <c>main</c> is
    /// missing, ambiguous or larger than a save can be. The caller stores the bytes under a fixed name.
    /// </summary>
    public static byte[] ExtractMain(byte[] zipBytes)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException e) { throw new InvalidDataException($"The backup is not a valid zip ({e.Message}).", e); }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("The zip has too many entries.");
            foreach (var e in zip.Entries)
                if (!SafeEntryName(e.FullName))
                    throw new InvalidDataException($"The zip contains an unsafe path ({Printable(e.FullName)}); refused.");
            var mains = zip.Entries.Where(e => e.Name == BackupDiscovery.SaveFileName && Depth(e.FullName) <= 2).ToList();
            if (mains.Count == 0) throw new InvalidDataException("The zip has no 'main' save file.");
            if (mains.Count > 1) throw new InvalidDataException("The zip has more than one 'main'; refused.");
            var entry = mains[0];
            if (entry.Length is 0 or > SaveReader.MaxSaveSize) throw new InvalidDataException("'main' in the zip is empty or too large to be a save.");
            try
            {
                using var src = entry.Open();
                var buffer = new byte[SaveReader.MaxSaveSize + 1];
                int total = 0, n;
                // Read against our own limit; the size in the header isn't trusted.
                while ((n = src.Read(buffer, total, buffer.Length - total)) > 0)
                {
                    total += n;
                    if (total > SaveReader.MaxSaveSize) throw new InvalidDataException("'main' in the zip expands beyond the size limit; refused.");
                }
                return buffer.AsSpan(0, total).ToArray();
            }
            catch (Exception e) when (e is not InvalidDataException and (IOException or NotSupportedException))
            {
                throw new InvalidDataException($"Could not read 'main' from the zip ({e.Message}).", e);
            }
        }
    }

    /// <summary>A zip holding only <c>main</c> at its root, the way JKSV writes a zipped backup.</summary>
    public static byte[] MainOnly(byte[] main, DateTimeOffset at)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(BackupDiscovery.SaveFileName, CompressionLevel.Optimal);
            entry.LastWriteTime = at;
            using var s = entry.Open();
            s.Write(main);
        }
        return ms.ToArray();
    }

    public static bool SafeEntryName(string name)
    {
        if (name.Length is 0 or > 512) return false;
        if (name.Any(char.IsControl) || name.Contains('\\') || name.Contains(':')) return false;
        if (name.StartsWith('/')) return false;
        var parts = name.TrimEnd('/').Split('/');
        return parts.All(p => p.Length > 0 && p is not "." and not "..");
    }

    private static int Depth(string fullName) => fullName.TrimEnd('/').Split('/').Length;

    private static string Printable(string s) => new(s.Select(c => char.IsControl(c) ? '?' : c).Take(80).ToArray());
}
