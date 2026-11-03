using System.IO.Compression;
using Xunit;

namespace PS3Transfer.Core.Tests;

public class RomTests
{
    [Fact]
    public void A_nes_file_is_a_rom_and_keeps_its_name()
    {
        using var source = new TempDir();
        using var drive = new TempDir();
        var path = source.Sub("Super Mario Bros. (USA).nes");
        File.WriteAllBytes(path, [0x4E, 0x45, 0x53, 0x1A]);

        var found = SourceDiscovery.Collect([path]);
        var group = Assert.Single(found.Groups);
        var inspected = DiscInspector.Inspect(group);
        Assert.Equal(ContentKind.Rom, inspected.Kind);
        Assert.Equal("NES", inspected.RomFolder);

        var plan = TransferPlanner.Plan(new PlanRequest
        {
            Source = inspected,
            DriveRoot = drive.Path,
            FileSystem = "FAT32",
            UseTitleNames = true,
        });
        Assert.Equal(TransferAction.Copy, plan.Action);
        Assert.False(plan.Splits);
        Assert.EndsWith(Path.Combine("ROMS", "NES", "Super Mario Bros. (USA).nes"), plan.DisplayPath);
        Assert.Contains("RetroArch", plan.PlayBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rom_over_4_gb_is_not_split()
    {
        using var drive = new TempDir();
        var plan = TransferPlanner.Plan(new PlanRequest
        {
            Source = new InspectedSource
            {
                Label = "huge.sfc",
                DisplayPath = "huge.sfc",
                Group = GroupKind.Logical,
                Kind = ContentKind.Rom,
                RomFolder = "SNES",
                OriginalName = "huge.sfc",
                Parts = ["huge.sfc"],
                Length = Ps3Layout.SplitSize + 1,
            },
            DriveRoot = drive.Path,
            FileSystem = "FAT32",
        });
        Assert.Equal(TransferAction.Blocked, plan.Action);
        Assert.Contains("over 4 GB", plan.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip_of_a_snes_rom_lands_in_snes()
    {
        using var source = new TempDir();
        var zipPath = source.Sub("Mario.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("Super Mario World.sfc").Open();
            entry.Write(new byte[] { 1, 2, 3, 4 });
        }

        Assert.True(RomLayout.TryClassify("Mario.zip", source.Path, zipPath, out var hit));
        Assert.Equal("SNES", hit.Folder);
        Assert.Null(hit.Warning);
    }

    [Fact]
    public void Organize_moves_loose_roms_and_leaves_ps3_games()
    {
        using var drive = new TempDir();
        Directory.CreateDirectory(drive.Sub("dump"));
        Directory.CreateDirectory(drive.Sub("PS3ISO"));
        Directory.CreateDirectory(drive.Sub("ROMS", "NES"));
        File.WriteAllBytes(drive.Sub("dump", "Zelda.sfc"), [1, 2, 3]);
        File.WriteAllBytes(drive.Sub("ROMS", "NES", "wrong.sfc"), [4, 5]);
        File.WriteAllBytes(drive.Sub("ROMS", "NES", "keep.nes"), [6]);
        File.WriteAllBytes(drive.Sub("PS3ISO", "Game.iso"), [7, 8, 9]);

        var plan = RomOrganizer.Plan(drive.Path);
        Assert.Contains(plan.Moves, move => move.Folder == "SNES" && move.Source.EndsWith("Zelda.sfc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Moves, move => move.Folder == "SNES" && move.Source.EndsWith("wrong.sfc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Moves, move => move.Source.EndsWith("keep.nes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Moves, move => move.Source.EndsWith("Game.iso", StringComparison.OrdinalIgnoreCase));

        var moved = RomOrganizer.Apply(drive.Path, plan.Moves);
        Assert.Equal(2, moved);
        Assert.True(File.Exists(drive.Sub("ROMS", "SNES", "Zelda.sfc")));
        Assert.True(File.Exists(drive.Sub("ROMS", "SNES", "wrong.sfc")));
        Assert.True(File.Exists(drive.Sub("ROMS", "NES", "keep.nes")));
        Assert.True(File.Exists(drive.Sub("PS3ISO", "Game.iso")));
        Assert.False(File.Exists(drive.Sub("dump", "Zelda.sfc")));

        var library = LibraryCatalog.Scan(drive.Path);
        Assert.Contains(library, entry => entry.Category == "ROMS\\SNES" && entry.Title == "Zelda");
        Assert.Contains(library, entry => entry.Category == "ROMS\\NES" && entry.Title == "keep");
        Assert.DoesNotContain(library, entry => entry.Category == "Loose ROM");
    }

    [Fact]
    public void A_loose_rom_shows_up_before_it_is_organized()
    {
        using var drive = new TempDir();
        Directory.CreateDirectory(drive.Sub("inbox"));
        File.WriteAllBytes(drive.Sub("inbox", "Metroid.gb"), [1]);
        var library = LibraryCatalog.Scan(drive.Path);
        var row = Assert.Single(library);
        Assert.Equal("Loose ROM", row.Category);
        Assert.Equal("Metroid", row.Title);
    }
}
