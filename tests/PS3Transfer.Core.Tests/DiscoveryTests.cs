using PS3Transfer.Core.Disc;
using Xunit;

namespace PS3Transfer.Core.Tests;

public class DiscoveryTests
{
    [Fact]
    public void Folder_of_isos_and_a_cue_set_and_a_jb_game_stay_separate()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Sub("loose.iso"), [1, 2, 3]);
        Directory.CreateDirectory(dir.Sub("set"));
        File.WriteAllText(dir.Sub("set", "Crash.cue"), "FILE \"Crash.bin\" BINARY\n");
        File.WriteAllBytes(dir.Sub("set", "Crash.bin"), [9]);
        var jb = dir.Sub("Ratchet");
        Directory.CreateDirectory(Path.Combine(jb, "PS3_GAME"));
        var sfo = SfoBuilder.Build(("TITLE", "Ratchet"), ("TITLE_ID", "BCUS98124"));
        File.WriteAllBytes(Path.Combine(jb, "PS3_GAME", "PARAM.SFO"), sfo);
        File.WriteAllBytes(dir.Sub("Big.iso.0"), new byte[10]);
        File.WriteAllBytes(dir.Sub("Big.iso.1"), new byte[4]);

        var found = SourceDiscovery.Collect([dir.Path]);
        Assert.Contains(found.Groups, g => g.Mode == DiscoveryMode.JbFolder && g.Label.EndsWith("Ratchet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found.Groups, g => g.Mode == DiscoveryMode.FileSet && g.Files.Count == 2);
        Assert.Contains(found.Groups, g => g.Mode == DiscoveryMode.LogicalParts && g.Label.Equals("loose.iso", StringComparison.OrdinalIgnoreCase));
        var split = Assert.Single(found.Groups, g => g.Label.Equals("Big.iso", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, split.Files.Count);
        Assert.False(split.Incomplete);
    }

    [Fact]
    public void Split_pkg_style_iso_is_one_set_and_a_gap_is_incomplete()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Sub("Game.iso.66600"), [1]);
        File.WriteAllBytes(dir.Sub("Game.iso.66601"), [2]);
        File.WriteAllBytes(dir.Sub("Gap.iso.0"), [1]);
        File.WriteAllBytes(dir.Sub("Gap.iso.2"), [1]);

        var found = SourceDiscovery.Collect([dir.Path]);
        var pkgStyle = Assert.Single(found.Groups, g => g.Label.Equals("Game.iso", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, pkgStyle.Files.Count);
        Assert.False(pkgStyle.Incomplete);
        var gap = Assert.Single(found.Groups, g => g.Label.Equals("Gap.iso", StringComparison.OrdinalIgnoreCase));
        Assert.True(gap.Incomplete);
    }

    [Fact]
    public void Inspector_reads_ps3_iso_ps2_iso_and_pkg()
    {
        using var dir = new TempDir();
        var sfo = SfoBuilder.Build(("TITLE", "Ice Age"), ("TITLE_ID", "BLES01234"));
        File.WriteAllBytes(dir.Sub("ice.iso"), IsoBuilder.Build(("PS3_GAME/PARAM.SFO", sfo)));
        var cnf = System.Text.Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLES_500.00;1\n");
        File.WriteAllBytes(dir.Sub("ps2.iso"), IsoBuilder.Build(("SYSTEM.CNF", cnf)));
        var pkg = new byte[256];
        pkg[0] = 0x7F;
        pkg[1] = (byte)'P';
        pkg[2] = (byte)'K';
        pkg[3] = (byte)'G';
        "EP0001-BLES01234_00-CONTENTIDCONTENT1"u8.CopyTo(pkg.AsSpan(0x30));
        File.WriteAllBytes(dir.Sub("game.pkg"), pkg);

        var found = SourceDiscovery.Collect([dir.Path]);
        var sources = found.Groups.Select(DiscInspector.Inspect).ToList();
        var ps3 = Assert.Single(sources, s => s.Kind == ContentKind.Ps3Iso);
        Assert.Equal("Ice Age", ps3.Title);
        Assert.Equal("BLES01234", ps3.TitleId);
        var ps2 = Assert.Single(sources, s => s.Kind == ContentKind.Ps2Iso);
        Assert.Equal("SLES-50000", ps2.TitleId);
        var package = Assert.Single(sources, s => s.Kind == ContentKind.Pkg);
        Assert.Equal("BLES01234", package.TitleId);
    }

    [Fact]
    public void Numbered_pkg_parts_are_one_package_and_an_extracted_folder_is_gamei()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Sub("Game.pkg.001"), [1, 2]);
        File.WriteAllBytes(dir.Sub("Game.pkg.002"), [3]);
        var extracted = dir.Sub("GTA");
        Directory.CreateDirectory(Path.Combine(extracted, "USRDIR"));
        File.WriteAllBytes(Path.Combine(extracted, "PARAM.SFO"), SfoBuilder.Build(("TITLE", "GTA V"), ("TITLE_ID", "NPUB31154")));
        File.WriteAllBytes(Path.Combine(extracted, "USRDIR", "EBOOT.BIN"), [9]);

        var found = SourceDiscovery.Collect([dir.Path]);
        var parts = Assert.Single(found.Groups, g => g.Label.Equals("Game.pkg", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, parts.Files.Count);
        Assert.False(parts.Incomplete);
        var folder = Assert.Single(found.Groups, g => g.Mode == DiscoveryMode.PkgFolder);
        var inspected = DiscInspector.Inspect(folder);
        Assert.Equal(ContentKind.PkgFolder, inspected.Kind);
        Assert.Equal("NPUB31154", inspected.TitleId);
        Assert.False(inspected.AllowMemberSplit);
    }

    [Fact]
    public void Drive_root_is_rejected()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var found = SourceDiscovery.Collect([root]);
        Assert.Empty(found.Groups);
        Assert.NotEmpty(found.Notes);
    }
}
