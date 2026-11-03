using PS3Transfer.Core.Disc;
using Xunit;

namespace PS3Transfer.Core.Tests;

public class FormatTests
{
    [Fact]
    public void Crc32_matches_the_standard_vector()
    {
        var crc = Crc32.Compute("123456789"u8);
        Assert.Equal(0xCBF43926u, crc);
        var left = Crc32.Compute("12345"u8);
        var both = Crc32.Append(left, "6789"u8);
        Assert.Equal(crc, both);
    }

    [Fact]
    public void Fat_names_drop_characters_webman_rejects()
    {
        Assert.Equal("Uncharted Drake's Fortune", FatName.Sanitize("Uncharted: Drake's Fortune"));
        Assert.Equal("Game", FatName.Sanitize("Game™"));
        Assert.Equal("Foo [BLUS12345].iso", FatName.File("Foo", "BLUS12345", ".iso"));
        Assert.Equal("CON_.iso", FatName.File("CON", null, ".ISO"));
        Assert.True(FatName.IsSafeFileName("PARAM.SFO"));
        Assert.False(FatName.IsSafeFileName("bad:name.bin"));
    }

    [Fact]
    public void Sfo_round_trip_reads_title_and_id()
    {
        var bytes = SfoBuilder.Build(("TITLE", "Uncharted 2"), ("TITLE_ID", "BCUS98123"));
        var map = ParamSfo.Read(bytes);
        Assert.Equal("Uncharted 2", map["TITLE"]);
        Assert.Equal("BCUS98123", map["TITLE_ID"]);
    }

    [Fact]
    public void System_cnf_distinguishes_ps1_and_ps2()
    {
        var ps2 = SystemCnf.Parse("BOOT2 = cdrom0:\\SLUS_200.01;1\nVER = 1.00\n");
        Assert.True(ps2.IsPs2);
        Assert.Equal("SLUS-20001", ps2.TitleId);

        var ps1 = SystemCnf.Parse("BOOT = cdrom:\\SLUS_001.90;1\n");
        Assert.False(ps1.IsPs2);
        Assert.Equal("SLUS-00190", ps1.TitleId);
    }

    [Fact]
    public void Cue_sheet_reads_quoted_and_bare_names()
    {
        var names = CueSheet.ReferencedFiles("FILE \"Crash.bin\" BINARY\n  TRACK 01 MODE2/2352\nFILE OTHER.BIN BINARY\n");
        Assert.Equal(["Crash.bin", "OTHER.BIN"], names);
    }

    [Fact]
    public void Pkg_header_reads_content_id()
    {
        var header = new byte[0x80];
        header[0] = 0x7F;
        header[1] = (byte)'P';
        header[2] = (byte)'K';
        header[3] = (byte)'G';
        var id = "UP0001-NPUB31154_00-GTAVDIGITAL00001"u8;
        id.CopyTo(header.AsSpan(0x30));
        Assert.True(PkgHeader.TryRead(header, out var contentId, out var titleId));
        Assert.Equal("UP0001-NPUB31154_00-GTAVDIGITAL00001", contentId);
        Assert.Equal("NPUB31154", titleId);
    }

    [Fact]
    public void Iso_reader_finds_param_sfo()
    {
        var sfo = SfoBuilder.Build(("TITLE", "Ice Age"), ("TITLE_ID", "BLES01234"));
        var iso = IsoBuilder.Build(("PS3_GAME/PARAM.SFO", sfo));
        using var stream = new MemoryStream(iso);
        using var volume = IsoVolume.TryOpen(stream);
        Assert.NotNull(volume);
        var read = volume!.ReadSmall("PS3_GAME/PARAM.SFO", 64 * 1024);
        Assert.NotNull(read);
        var map = ParamSfo.Read(read);
        Assert.Equal("Ice Age", map["TITLE"]);
        Assert.Equal("BLES01234", map["TITLE_ID"]);
        Assert.Equal("TESTVOLUME", volume.VolumeLabel);
    }

    [Fact]
    public void Iso_reader_understands_2352_byte_tracks()
    {
        var cnf = "BOOT2 = cdrom0:\\SLUS_200.02;1\n"u8.ToArray();
        var raw = IsoBuilder.To2352(IsoBuilder.Build(("SYSTEM.CNF", cnf)));
        using var stream = new MemoryStream(raw);
        using var volume = IsoVolume.TryOpen(stream);
        Assert.NotNull(volume);
        var read = volume!.ReadSmall("SYSTEM.CNF", 4096);
        Assert.NotNull(read);
        var info = SystemCnf.Parse(System.Text.Encoding.ASCII.GetString(read));
        Assert.True(info.IsPs2);
        Assert.Equal("SLUS-20002", info.TitleId);
    }
}
