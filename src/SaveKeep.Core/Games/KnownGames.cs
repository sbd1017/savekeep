using PKHeX.Core;

namespace SaveKeep.Core.Games;

public enum GamePlatform { Switch, Nintendo3DS }

/// <summary>
/// A game whose backups can be searched for over FTP. <see cref="Id"/> matches the vault folder name
/// (the save's <see cref="GameVersion"/>, lower case). <see cref="Pair"/> is the paired release, e.g. "Sword/Shield".
/// </summary>
public sealed record KnownGame(string Id, string Edition, string Pair, GamePlatform Platform, string TitleId, GameVersion Version, Type SaveType)
{
    public string Tool => Platform == GamePlatform.Switch ? "JKSV" : "Checkpoint";
    public bool Accepts(SaveFile sav) => SaveType.IsInstanceOfType(sav);
}

/// <summary>
/// Title IDs as listed by PKSM (FlagBrew) for the 3DS games and by the Switch eShop for Sword/Shield.
/// Any save PKHeX recognises can go into the vault; this list only matters for FTP search and for
/// recognising blank or unusual saves that PKHeX's size-based detection misses.
/// </summary>
public static class KnownGames
{
    public static IReadOnlyList<KnownGame> All { get; } =
    [
        new("sw", "Sword", "Sword/Shield", GamePlatform.Switch, "0100ABF008968000", GameVersion.SW, typeof(SAV8SWSH)),
        new("sh", "Shield", "Sword/Shield", GamePlatform.Switch, "01008DB008C2C000", GameVersion.SH, typeof(SAV8SWSH)),
        new("us", "Ultra Sun", "Ultra Sun/Ultra Moon", GamePlatform.Nintendo3DS, "00040000001B5000", GameVersion.US, typeof(SAV7USUM)),
        new("um", "Ultra Moon", "Ultra Sun/Ultra Moon", GamePlatform.Nintendo3DS, "00040000001B5100", GameVersion.UM, typeof(SAV7USUM)),
        new("sn", "Sun", "Sun/Moon", GamePlatform.Nintendo3DS, "0004000000164800", GameVersion.SN, typeof(SAV7SM)),
        new("mn", "Moon", "Sun/Moon", GamePlatform.Nintendo3DS, "0004000000175E00", GameVersion.MN, typeof(SAV7SM)),
        new("or", "Omega Ruby", "Omega Ruby/Alpha Sapphire", GamePlatform.Nintendo3DS, "000400000011C400", GameVersion.OR, typeof(SAV6AO)),
        new("as", "Alpha Sapphire", "Omega Ruby/Alpha Sapphire", GamePlatform.Nintendo3DS, "000400000011C500", GameVersion.AS, typeof(SAV6AO)),
        new("x", "X", "X/Y", GamePlatform.Nintendo3DS, "0004000000055D00", GameVersion.X, typeof(SAV6XY)),
        new("y", "Y", "X/Y", GamePlatform.Nintendo3DS, "0004000000055E00", GameVersion.Y, typeof(SAV6XY)),
    ];

    public static KnownGame? ById(string? id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>The entry for a parsed save, matched on both save type and version.</summary>
    public static KnownGame? For(SaveFile sav) => All.FirstOrDefault(g => g.Version == sav.Version && g.Accepts(sav));
}
