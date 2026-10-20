using System.Text;

namespace SaveKeep.Core.Storage;

public static class AtomicFile
{
    /// <summary>Writes to a temp file, fsyncs, then renames it over the destination.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Creates a new file and fails if it already exists. Saves are never overwritten.</summary>
    public static void WriteNew(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(data);
        fs.Flush(flushToDisk: true);
    }
}
