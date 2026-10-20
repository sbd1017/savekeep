using SaveKeep.Core.Util;

namespace SaveKeep.Tests;

public static class TestEnv
{
    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "savekeep-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Hashes every file under a directory, so a test can show nothing was changed.</summary>
    public static Dictionary<string, string> HashTree(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Hex.Sha256(File.ReadAllBytes(f)));
}

/// <summary>A clock the tests move by hand.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
