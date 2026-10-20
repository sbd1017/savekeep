using PKHeX.Core;

namespace SaveKeep.Core.Saves;

public sealed record TrainerSummary(string Name, ushort Tid16, ushort Sid16, uint DisplayTid, string Game, int Language, int Gender)
{
    public static TrainerSummary From(SaveFile sav) =>
        new(sav.OT, sav.TID16, sav.SID16, sav.DisplayTID, sav.Version.ToString(), sav.Language, sav.Gender);

    public bool SameAs(TrainerSummary other) => Name == other.Name && Tid16 == other.Tid16 && Sid16 == other.Sid16;
}

public sealed record SlotView(
    int Box, int Slot, bool Empty, bool ChecksumValid, ushort Species, byte Form, string SpeciesName,
    string Nickname, bool IsEgg, bool Shiny, int Level, string OriginalTrainer)
{
    public static SlotView From(int box, int slot, PKM pk)
    {
        bool empty = pk.Species == 0 && pk.ChecksumValid;
        string name = pk.Species == 0 ? "" : PKHeX.Core.SpeciesName.GetSpeciesName(pk.Species, (int)LanguageID.English);
        return new(box, slot, empty, pk.ChecksumValid, pk.Species, pk.Form, name,
            empty ? "" : pk.Nickname, pk.IsEgg, !empty && pk.IsShiny, empty ? 0 : pk.CurrentLevel,
            empty ? "" : pk.OriginalTrainerName);
    }
}
