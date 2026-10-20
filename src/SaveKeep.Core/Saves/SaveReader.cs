using PKHeX.Core;
using SaveKeep.Core.Games;

namespace SaveKeep.Core.Saves;

/// <summary>Turns the bytes of a backup's <c>main</c> into a PKHeX save, without ever trusting the file blindly.</summary>
public static class SaveReader
{
    public const int MaxSaveSize = 4 * 1024 * 1024;

    /// <summary>
    /// Reads a save of any game. PKHeX's own detection comes first; if that fails, the file is tried as each
    /// known game's save type, gated by that type's integrity check (see <see cref="ReadAs"/>).
    /// </summary>
    public static SaveFile Read(byte[] main)
    {
        CheckSize(main);
        if (SaveUtil.TryGetSaveFile(main.ToArray(), out var sav)) return sav;
        foreach (var game in KnownGames.All.DistinctBy(g => g.SaveType))
        {
            try { return ReadAs(main, game); }
            catch (InvalidDataException) { }
        }
        throw new InvalidDataException("PKHeX does not recognise this file as a save.");
    }

    /// <summary>
    /// Reads a save that must belong to <paramref name="game"/>'s pair of releases. PKHeX matches exact sizes, so
    /// some valid files (blank saves among them) are not detected; those are read as the expected type, but only when
    /// that type's own integrity check passes: the SHA-256 footer for Sword/Shield, every block checksum on 3DS.
    /// </summary>
    public static SaveFile ReadAs(byte[] main, KnownGame game)
    {
        CheckSize(main);
        if (SaveUtil.TryGetSaveFile(main.ToArray(), out var detected))
            return game.Accepts(detected) ? detected
                : throw new InvalidDataException($"The file is a {detected.GetType().Name}, not a save for Pokémon {game.Pair}.");

        SaveFile? typed = null;
        try
        {
            var copy = main.ToArray(); // the constructors decrypt in place
            typed = game.SaveType.Name switch
            {
                nameof(SAV8SWSH) when SwishCrypto.GetIsHashValid(copy) => new SAV8SWSH(copy),
                nameof(SAV7USUM) when copy.Length == SaveUtil.SIZE_G7USUM => new SAV7USUM(copy),
                nameof(SAV7SM) when copy.Length == SaveUtil.SIZE_G7SM => new SAV7SM(copy),
                nameof(SAV6XY) when copy.Length == SaveUtil.SIZE_G6XY => new SAV6XY(copy),
                nameof(SAV6AO) when copy.Length == SaveUtil.SIZE_G6ORAS => new SAV6AO(copy),
                _ => null,
            };
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidOperationException) { typed = null; }
        if (typed is null || (typed is not SAV8SWSH && !typed.ChecksumsValid))
            throw new InvalidDataException($"The file is not a recognised save for Pokémon {game.Pair}.");
        return typed;
    }

    private static void CheckSize(byte[] main)
    {
        if (main.Length is 0 or > MaxSaveSize) throw new InvalidDataException("Not a save file (size).");
    }

    /// <summary>Platform, title ID and edition name for a save. Games outside the known list get a best guess.</summary>
    public static (string Platform, string? TitleId, string Edition) Describe(SaveFile sav)
    {
        if (KnownGames.For(sav) is { } game)
            return (game.Platform == GamePlatform.Switch ? "Switch" : "3DS", game.TitleId, game.Edition);
        var platform = sav switch
        {
            SAV7b => "Switch",
            _ when sav.Generation >= 8 => "Switch",
            SAV6 or SAV7 => "3DS",
            SAV1 { IsVirtualConsole: true } or SAV2 { IsVirtualConsole: true } => "3DS",
            _ => "Other",
        };
        return (platform, null, sav.Version.ToString());
    }

    public static int CountBoxPokemon(SaveFile sav)
    {
        int n = 0;
        for (int i = 0; i < sav.SlotCount; i++)
            if (sav.GetBoxSlotAtIndex(i).Species != 0) n++;
        return n;
    }
}
