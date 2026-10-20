using PKHeX.Core;
using SaveKeep.Core.Util;

namespace SaveKeep.Core.Saves;

public sealed record SaveSummary(
    string FileName, string Sha256, int Size, string SaveType, string Game, int Generation, string Context,
    TrainerSummary Trainer, int BoxCount, int BoxesUnlocked, int SlotsPerBox, int Occupied, int PartyCount,
    bool ChecksumsValid, string ChecksumInfo, List<SlotView> Slots, SaveDetails Details);

/// <summary>Reads a save file and reports what is in it. The bytes are only read; nothing is written anywhere.</summary>
public static class SaveInspector
{
    public static SaveSummary Inspect(byte[] bytes, string fileName)
    {
        var sav = SaveReader.Read(bytes);
        var slots = new List<SlotView>();
        for (int i = 0; i < sav.SlotCount; i++)
            slots.Add(SlotView.From(i / sav.BoxSlotCount, i % sav.BoxSlotCount, sav.GetBoxSlotAtIndex(i)));
        // A save missing a block (or holding one PKHeX can't decode) still gets a summary.
        int unlocked;
        try { unlocked = sav.BoxesUnlocked; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { unlocked = sav.BoxCount; }
        return new SaveSummary(Path.GetFileName(fileName), Hex.Sha256(bytes), bytes.Length, sav.GetType().Name, sav.Version.ToString(), sav.Generation,
            sav.Context.ToString(), TrainerSummary.From(sav), sav.BoxCount, unlocked, sav.BoxSlotCount,
            slots.Count(s => !s.Empty), sav.PartyCount, sav.ChecksumsValid, sav.ChecksumInfo, slots, SaveDetails.From(sav));
    }
}
