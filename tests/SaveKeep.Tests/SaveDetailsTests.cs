using SaveKeep.Core.Saves;
using SaveKeep.Core.Util;
using SaveKeep.Sim;

namespace SaveKeep.Tests;

public class SaveDetailsTests
{
    [Fact]
    public void Sword_details_show_ids_money_party_and_watts_but_not_unreliable_fields()
    {
        var sav = SampleSaves.SwordShield("Player");
        sav.Money = 9_932_820;
        sav.MyStatus.Watt = 23_400;
        sav.PlayedHours = 69; sav.PlayedMinutes = 15;
        var mon = sav.GetBoxSlotAtIndex(0);
        sav.SetPartySlotAtIndex(mon, 0);

        var d = SaveDetails.From(sav);

        Assert.Equal("Player", d.Trainer);
        Assert.Equal("SW", d.Game);
        Assert.Equal(sav.ID32 % 1_000_000, d.Id);      // the 6-digit ID the game shows
        Assert.Equal(sav.ID32 / 1_000_000, d.SecretId); // the 4-digit Secret ID
        Assert.Equal(sav.TID16, d.Tid16);
        Assert.Equal(sav.SID16, d.Sid16);
        Assert.StartsWith("6-digit", d.IdFormat);
        Assert.Equal(9_932_820u, d.Money);
        Assert.Equal("69:15", d.PlayTime);
        Assert.Equal(23_400u, d.Watts);
        Assert.Equal(mon.Species, Assert.Single(d.Party).Species);
        Assert.Equal(3, d.BoxPokemon);
        Assert.Equal(1, d.BoxesUsed);
        // Unreliable in Sword/Shield saves, so not reported.
        Assert.Null(d.Badges);
        Assert.Null(d.LastSaved);
    }

    [Fact]
    public void Gen6_badges_are_counted_from_their_bits()
    {
        var sav = SampleSaves.XY();
        sav.Badges = 0b0000_0111;
        Assert.Equal(3, SaveDetails.From(sav).Badges);
        Assert.Equal(sav.SID16, SaveDetails.From(sav).SecretId); // Gen 6 shows the 16-bit SID
    }

    [Fact]
    public void Gen7_details_use_six_digit_ids_and_no_watts()
    {
        var sav = SampleSaves.UltraSunMoon();
        var d = SaveDetails.From(sav);
        Assert.Equal("US", d.Game);
        Assert.StartsWith("6-digit", d.IdFormat);
        Assert.Null(d.Watts);
        Assert.Equal(2, d.BoxPokemon);
    }

    [Fact]
    public void Inspect_reads_without_changing_the_bytes()
    {
        var bytes = SampleSaves.MainBytes(SampleSaves.UltraSunMoon());
        var hash = Hex.Sha256(bytes);
        var s = SaveInspector.Inspect(bytes, "/some/where/main");
        Assert.Equal("main", s.FileName);
        Assert.Equal(hash, s.Sha256);
        Assert.Equal("SAV7USUM", s.SaveType);
        Assert.Equal(2, s.Occupied);
        Assert.Equal(s.Trainer.DisplayTid, s.Details.Id);
        Assert.True(s.ChecksumsValid);
        Assert.Equal(hash, Hex.Sha256(bytes));
    }

    [Fact]
    public void Inspect_copes_with_a_sword_save_that_has_unreadable_blocks()
    {
        // The made-up Sword save has blocks PKHeX can't decode (the box-unlock counter among them).
        var s = SaveInspector.Inspect(SampleSaves.MainBytes(SampleSaves.SwordShield()), "main");
        Assert.Equal("SAV8SWSH", s.SaveType);
        Assert.Equal(3, s.Occupied);
        Assert.Equal(s.BoxCount, s.BoxesUnlocked);
    }

    [Fact]
    public void Inspect_refuses_files_that_are_not_saves()
    {
        Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect([], "empty"));
        Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(new byte[1000], "zeros"));
        Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(new byte[SaveReader.MaxSaveSize + 1], "huge"));
    }
}
