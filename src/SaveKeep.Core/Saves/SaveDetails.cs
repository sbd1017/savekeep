using System.Numerics;
using PKHeX.Core;

namespace SaveKeep.Core.Saves;

public sealed record PartyMember(
    int Slot, ushort Species, string SpeciesName, string Nickname, bool IsNicknamed, int Level, bool Shiny, bool IsEgg,
    byte Form, string Gender, string? HeldItem);

/// <summary>
/// What tells two saves apart at a glance: trainer identity (Secret ID included), progress and party.
/// Every value comes straight from PKHeX's save model. Values a game doesn't store, or stores unreliably, are null.
/// </summary>
public sealed record SaveDetails
{
    public required string Trainer { get; init; }
    public required string Gender { get; init; }
    public required string Language { get; init; }
    public required string Game { get; init; }
    public required int Generation { get; init; }
    /// <summary>The ID as the game shows it (6 digits from Gen 7 on, otherwise the 16-bit TID).</summary>
    public required uint Id { get; init; }
    /// <summary>The Secret ID in the same format (4 digits from Gen 7 on, otherwise the 16-bit SID).</summary>
    public required uint SecretId { get; init; }
    public required string IdFormat { get; init; }
    public required ushort Tid16 { get; init; }
    public required ushort Sid16 { get; init; }
    public required uint Money { get; init; }
    public required string PlayTime { get; init; }
    public required int PlayedHours { get; init; }
    public required int PlayedMinutes { get; init; }
    /// <summary>The game's own "last saved" time, by the console's clock. 3DS games only.</summary>
    public DateTime? LastSaved { get; init; }
    public int? Badges { get; init; }
    public int? DexSeen { get; init; }
    public int? DexCaught { get; init; }
    public required int BoxPokemon { get; init; }
    public required int BoxesUsed { get; init; }
    public required int BoxesUnlocked { get; init; }
    public required IReadOnlyList<PartyMember> Party { get; init; }
    /// <summary>Sword/Shield only.</summary>
    public uint? Watts { get; init; }

    // One unreadable value (a block a save lacks, say) shouldn't hide the rest.
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException or NullReferenceException) { return fallback; }
    }

    public static SaveDetails From(SaveFile sav)
    {
        var strings = GameInfo.GetStrings("en");
        var party = new List<PartyMember>();
        for (int i = 0; i < sav.PartyCount; i++)
        {
            var pk = sav.GetPartySlotAtIndex(i);
            if (pk.Species == 0) continue;
            string? item = pk.HeldItem > 0 && pk.HeldItem < strings.itemlist.Length ? strings.itemlist[pk.HeldItem] : null;
            party.Add(new PartyMember(i + 1, pk.Species, SpeciesName.GetSpeciesName(pk.Species, (int)LanguageID.English),
                pk.Nickname, pk.IsNicknamed, pk.CurrentLevel, pk.IsShiny, pk.IsEgg, pk.Form,
                pk.Gender switch { 0 => "M", 1 => "F", _ => "" }, string.IsNullOrWhiteSpace(item) ? null : item));
        }

        int boxPokemon = 0, boxesUsed = 0;
        for (int b = 0; b < sav.BoxCount; b++)
        {
            int inBox = 0;
            for (int s = 0; s < sav.BoxSlotCount; s++)
                if (sav.GetBoxSlotAtIndex(b, s).Species != 0) inBox++;
            boxPokemon += inBox;
            if (inBox > 0) boxesUsed++;
        }

        // Sword/Shield keeps a "last saved" value too, but it doesn't follow real saves (it can be years stale), so it isn't shown.
        DateTime? lastSaved = Safe<DateTime?>(() => sav switch
        {
            SAV7 s => s.Played.LastSavedDate,
            SAV6 s => s.Played.LastSavedDate,
            _ => null,
        }, null);
        // One bit per badge. Sword/Shield is left out: its badge byte reads 0 on saves well past the gyms.
        int? badges = Safe<int?>(() => sav switch
        {
            SAV6 s => BitOperations.PopCount((uint)s.Badges),
            SAV4 s => BitOperations.PopCount((uint)s.Badges),
            _ => null,
        }, null);
        bool sixDigit = sav.TrainerIDDisplayFormat == TrainerIDFormat.SixDigit;
        return new SaveDetails
        {
            Trainer = sav.OT,
            Gender = sav.Gender == 1 ? "female" : "male",
            Language = sav.Language is >= 0 and <= 255 && Enum.IsDefined((LanguageID)(byte)sav.Language) ? ((LanguageID)(byte)sav.Language).ToString() : sav.Language.ToString(),
            Game = sav.Version.ToString(),
            Generation = sav.Generation,
            Id = sav.DisplayTID,
            SecretId = sav.DisplaySID,
            IdFormat = sixDigit ? "6-digit ID / 4-digit SID (Gen 7+)" : "16-bit TID / SID",
            Tid16 = sav.TID16,
            Sid16 = sav.SID16,
            Money = Safe(() => sav.Money, 0u),
            PlayTime = $"{sav.PlayedHours}:{sav.PlayedMinutes:00}",
            PlayedHours = sav.PlayedHours,
            PlayedMinutes = sav.PlayedMinutes,
            LastSaved = lastSaved,
            Badges = badges,
            DexSeen = Safe<int?>(() => sav.HasPokeDex ? sav.SeenCount : null, null),
            DexCaught = Safe<int?>(() => sav.HasPokeDex ? sav.CaughtCount : null, null),
            BoxPokemon = boxPokemon,
            BoxesUsed = boxesUsed,
            BoxesUnlocked = Safe(() => sav.BoxesUnlocked, sav.BoxCount),
            Party = party,
            Watts = Safe<uint?>(() => sav is SAV8SWSH sw ? sw.MyStatus.Watt : null, null),
        };
    }
}
