using Xunit;

namespace PS3Transfer.Core.Tests;

public class TransferTests
{
    [Fact]
    public void Fat32_iso_is_split_and_ntfs_iso_is_not()
    {
        using var drive = new TempDir();
        var length = Ps3Layout.SplitSize + 25;
        var source = Source("Game.iso", length, "Ice Age", "BLES01234", ContentKind.Ps3Iso);
        var fat = Plan(source, drive.Path, "FAT32");
        Assert.Equal(TransferAction.Copy, fat.Action);
        Assert.True(fat.Splits);
        Assert.Equal(2, fat.PartCount);
        Assert.EndsWith("Ice Age [BLES01234].iso.0 …", fat.DisplayPath);
        Assert.All(fat.OutputPaths, path => Assert.StartsWith(Path.Combine(drive.Path, "PS3ISO"), path, StringComparison.OrdinalIgnoreCase));

        var ntfs = Plan(source, drive.Path, "NTFS");
        Assert.False(ntfs.Splits);
        Assert.Equal(1, ntfs.PartCount);
        Assert.EndsWith("Ice Age [BLES01234].iso", ntfs.DisplayPath);
    }

    [Fact]
    public void Large_pkg_uses_66600_parts_and_a_small_one_stays_whole()
    {
        using var drive = new TempDir();
        var large = Plan(Source("Game.pkg", Ps3Layout.SplitSize + 8, "GTA V", "NPUB31154", ContentKind.Pkg), drive.Path, "FAT32");
        Assert.True(large.Splits);
        Assert.Contains(large.OutputPaths, path => path.EndsWith("GTA V [NPUB31154].pkg.66600", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(large.OutputPaths, path => path.Contains(Path.Combine("packages", "GTA V [NPUB31154]"), StringComparison.OrdinalIgnoreCase));

        var small = Plan(Source("Game.pkg", 100, "Patch", "BLES00001", ContentKind.Pkg), drive.Path, "FAT32");
        Assert.False(small.Splits);
        Assert.EndsWith(Path.Combine("packages", "Patch [BLES00001].pkg"), small.DisplayPath);
    }

    [Fact]
    public void Two_games_with_the_same_name_do_not_share_a_file()
    {
        using var drive = new TempDir();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = Plan(Source("a.iso", 10, "Same", "BLES00001", ContentKind.Ps3Iso), drive.Path, "FAT32", reserved);
        var second = Plan(Source("b.iso", 10, "Same", "BLES00001", ContentKind.Ps3Iso), drive.Path, "FAT32", reserved);
        Assert.NotEqual(first.OutputPaths[0], second.OutputPaths[0]);
        Assert.Contains("(2)", second.DisplayPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_bin_in_a_cue_set_is_blocked_on_fat32()
    {
        using var drive = new TempDir();
        var source = new InspectedSource
        {
            Label = "Crash.cue",
            DisplayPath = "Crash.cue",
            Group = GroupKind.Tree,
            Kind = ContentKind.Ps1Disc,
            Title = "Crash",
            OriginalName = "Crash.cue",
            Length = Ps3Layout.SplitSize + 1,
            AllowMemberSplit = false,
            SourceStamp = 1,
            Tree =
            [
                new TreeFile("Crash.cue", "Crash.cue", 20),
                new TreeFile("Crash.bin", "Crash.bin", Ps3Layout.SplitSize + 1),
            ],
        };
        var plan = Plan(source, drive.Path, "FAT32");
        Assert.Equal(TransferAction.Blocked, plan.Action);
        Assert.Contains("CUE", plan.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Copy_splits_resumes_and_matches_the_source()
    {
        using var root = new TempDir();
        var sourcePath = root.Sub("source.iso");
        var payload = new byte[2500];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 17);
        await File.WriteAllBytesAsync(sourcePath, payload);

        var source = new InspectedSource
        {
            Label = "source.iso",
            DisplayPath = sourcePath,
            Group = GroupKind.Logical,
            Kind = ContentKind.Ps3Iso,
            Title = "Resume Game",
            TitleId = "BLUS00001",
            OriginalName = "source.iso",
            Parts = [sourcePath],
            Length = payload.Length,
            SourceStamp = DiscInspector.StampOf([sourcePath]),
        };
        var drive = root.Sub("drive");
        Directory.CreateDirectory(drive);
        var manifests = new ManifestStore(root.Sub("manifests"));
        var plan = Plan(source, drive, "FAT32", splitSize: 1000, manifestsSerial: 7);

        var cts = new CancellationTokenSource();
        var first = await TransferEngine.CopyAsync(plan, new TransferOptions { Manifests = manifests, HideExtraParts = true },
            new SyncProgress<TransferProgress>(p =>
            {
                if (p.SessionBytes >= 1500)
                    cts.Cancel();
            }), cts.Token);
        Assert.Equal(TransferOutcome.Canceled, first.Outcome);

        var resumed = Plan(source, drive, "FAT32", splitSize: 1000, manifestsSerial: 7);
        Assert.Equal(TransferAction.Resume, resumed.Action);
        var second = await TransferEngine.CopyAsync(resumed, new TransferOptions
        {
            Manifests = manifests,
            HideExtraParts = true,
            VerifyCrc32 = true,
        }, progress: null, CancellationToken.None);
        Assert.Equal(TransferOutcome.Copied, second.Outcome);
        Assert.Null(second.Error);

        var parts = resumed.OutputPaths.Select(File.ReadAllBytes).SelectMany(b => b).ToArray();
        Assert.Equal(payload, parts);
        Assert.False(File.GetAttributes(resumed.OutputPaths[0]).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(resumed.OutputPaths[1]).HasFlag(FileAttributes.Hidden));

        var again = Plan(source, drive, "FAT32", splitSize: 1000, manifestsSerial: 7);
        Assert.Equal(TransferAction.Skip, again.Action);
        Assert.Equal("Already on the drive", again.StatusLine);
    }

    [Fact]
    public void Extracted_pkg_lands_in_gamei_under_its_title_id()
    {
        using var drive = new TempDir();
        var source = new InspectedSource
        {
            Label = "GTA",
            DisplayPath = "GTA",
            Group = GroupKind.Tree,
            Kind = ContentKind.PkgFolder,
            Title = "GTA V",
            TitleId = "NPUB31154",
            OriginalName = "GTA",
            Length = 12,
            AllowMemberSplit = false,
            Tree = [new TreeFile("GTA\\PARAM.SFO", "PARAM.SFO", 4), new TreeFile("GTA\\USRDIR\\EBOOT.BIN", "USRDIR\\EBOOT.BIN", 8)],
            SourceStamp = 1,
        };

        var plan = Plan(source, drive.Path, "FAT32");
        Assert.Equal(TransferAction.Copy, plan.Action);
        Assert.False(plan.Splits);
        Assert.Contains(plan.OutputPaths, path => path.EndsWith(Path.Combine("GAMEI", "NPUB31154", "USRDIR", "EBOOT.BIN"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Plays from the SSD", plan.PlayTitle);
    }

    [Fact]
    public void Library_groups_split_parts_into_one_game()
    {
        using var drive = new TempDir();
        var folder = drive.Sub("PS3ISO");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Ice Age [BLES01234].iso.0"), new byte[4]);
        File.WriteAllBytes(Path.Combine(folder, "Ice Age [BLES01234].iso.1"), new byte[3]);
        var entries = LibraryCatalog.Scan(drive.Path);
        var entry = Assert.Single(entries);
        Assert.Equal("Ice Age [BLES01234]", entry.Title);
        Assert.Equal(2, entry.Parts);
        Assert.Equal(7, entry.Bytes);
        Assert.True(entry.Split);
    }

    private static InspectedSource Source(string name, long length, string title, string titleId, ContentKind kind) => new()
    {
        Label = name,
        DisplayPath = name,
        Group = GroupKind.Logical,
        Kind = kind,
        Title = title,
        TitleId = titleId,
        OriginalName = name,
        Parts = [name],
        Length = length,
        SourceStamp = 1,
    };

    private static TransferPlan Plan(
        InspectedSource source,
        string drive,
        string fileSystem,
        HashSet<string>? reserved = null,
        long? splitSize = null,
        uint manifestsSerial = 1) =>
        TransferPlanner.Plan(new PlanRequest
        {
            Source = source,
            DriveRoot = drive,
            FileSystem = fileSystem,
            VolumeSerial = manifestsSerial,
            SplitSize = splitSize ?? Ps3Layout.SplitSize,
            Reserved = reserved,
        });
}
