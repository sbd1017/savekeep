using PKHeX.Core;

namespace SaveKeep.Sim;

/// <summary>Made-up saves for the simulator and tests. Never restore these to a console.</summary>
public static class SampleSaves
{
    public static SAV8SWSH SwordShield(string ot = "SimTrainer")
    {
        var sav = new SAV8SWSH();
        Configure(sav, ot, GameVersion.SW);
        Fill(sav, [(ushort)Species.Wooloo, (ushort)Species.Rookidee, (ushort)Species.Skwovet]);
        return sav;
    }

    public static SAV7USUM UltraSunMoon(string ot = "SimTrainer")
    {
        var sav = new SAV7USUM();
        Configure(sav, ot, GameVersion.US);
        Fill(sav, [(ushort)Species.Pikipek, (ushort)Species.Yungoos]);
        return sav;
    }

    public static SAV7SM SunMoon(string ot = "SimTrainer", GameVersion version = GameVersion.SN)
    {
        var sav = new SAV7SM();
        Configure(sav, ot, version);
        Fill(sav, [(ushort)Species.Pikipek, (ushort)Species.Yungoos, (ushort)Species.Rockruff]);
        return sav;
    }

    public static SAV6XY XY(string ot = "SimTrainer", GameVersion version = GameVersion.X)
    {
        var sav = new SAV6XY();
        Configure(sav, ot, version);
        Fill(sav, [(ushort)Species.Fletchling, (ushort)Species.Bunnelby, (ushort)Species.Scatterbug]);
        return sav;
    }

    public static SAV6AO OmegaRubyAlphaSapphire(string ot = "SimTrainer", GameVersion version = GameVersion.OR)
    {
        var sav = new SAV6AO();
        Configure(sav, ot, version);
        Fill(sav, [(ushort)Species.Zigzagoon, (ushort)Species.Wurmple, (ushort)Species.Poochyena]);
        return sav;
    }

    /// <summary>
    /// The save as a console backup's <c>main</c>. PKHeX's blank Sword/Shield save leaves many blocks untyped,
    /// which it cannot read back; those are typed as plain objects first so the bytes round-trip.
    /// </summary>
    public static byte[] MainBytes(SaveFile sav)
    {
        if (sav is SAV8SWSH swsh)
        {
            var typeField = typeof(SCBlock).GetField("<Type>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("PKHeX SCBlock layout changed.");
            foreach (var block in swsh.AllBlocks.Where(b => b.Type == SCTypeCode.None))
                typeField.SetValue(block, SCTypeCode.Object);
        }
        return sav.Write().ToArray();
    }

    private static void Configure(SaveFile sav, string ot, GameVersion version)
    {
        sav.Version = version;
        sav.OT = ot;
        sav.Language = (int)LanguageID.English;
        sav.Gender = 0;
        sav.TID16 = 24680;
        sav.SID16 = 13579;
    }

    private static void Fill(SaveFile sav, ushort[] species)
    {
        int slot = 0;
        foreach (var s in species)
        {
            var blank = EntityBlank.GetBlank(sav);
            blank.Species = s;
            var enc = EncounterMovesetGenerator.GenerateEncounters(blank, sav, ReadOnlyMemory<ushort>.Empty, sav.Version).FirstOrDefault();
            if (enc is not IEncounterConvertible conv) continue;
            var pk = conv.ConvertToPKM(sav);
            pk.RefreshChecksum();
            sav.SetBoxSlotAtIndex(pk, slot++, new EntityImportSettings(EntityImportOption.Disable, EntityImportOption.Disable, EntityImportOption.Disable));
        }
    }
}
