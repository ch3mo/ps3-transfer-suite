using SharpCompress.Common;
using SharpCompress.Writers;
using Xunit;

namespace PS3Transfer.Core.Tests;

public class ArchiveTests
{
    [Fact]
    public void A_7z_queues_the_iso_inside_and_not_the_folder()
    {
        using var source = new TempDir();
        var archive = source.Sub("My Game.7z");
        WriteArchive(archive, ArchiveType.SevenZip, ("Wrapper/My Game.iso", [1, 2, 3, 4]));

        string? root = null;
        try
        {
            var found = SourceDiscovery.Collect([archive]);
            var group = Assert.Single(found.Groups);
            Assert.Equal(DiscoveryMode.LogicalParts, group.Mode);
            Assert.Equal("My Game.iso", group.Label);
            Assert.EndsWith(Path.Combine("Wrapper", "My Game.iso"), group.Files[0], StringComparison.OrdinalIgnoreCase);
            Assert.False(group.Files[0].EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrEmpty(group.ExtractRoot));
            root = group.ExtractRoot;
            Assert.Contains(found.Notes, note => note.Contains("Extracted", StringComparison.Ordinal));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void A_7z_of_a_folder_game_queues_that_game_folder()
    {
        using var source = new TempDir();
        var archive = source.Sub("Ratchet.7z");
        var sfo = SfoBuilder.Build(("TITLE", "Ratchet"), ("TITLE_ID", "BCUS98124"));
        WriteArchive(archive, ArchiveType.SevenZip, ("Extra/Ratchet/PS3_GAME/PARAM.SFO", sfo), ("Extra/Ratchet/PS3_GAME/USRDIR/EBOOT.BIN", [9]));

        string? root = null;
        try
        {
            var found = SourceDiscovery.Collect([archive]);
            var group = Assert.Single(found.Groups);
            Assert.Equal(DiscoveryMode.JbFolder, group.Mode);
            Assert.EndsWith("Ratchet", group.Label, StringComparison.OrdinalIgnoreCase);
            Assert.False(group.Label.EndsWith("Extra", StringComparison.OrdinalIgnoreCase));
            root = group.ExtractRoot;
            var inspected = DiscInspector.Inspect(group);
            Assert.Equal(ContentKind.Ps3Folder, inspected.Kind);
            Assert.Equal(root, inspected.ExtractRoot);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void A_rar_queues_the_rom_inside_and_not_the_archive()
    {
        using var source = new TempDir();
        var archive = source.Sub("Mario.rar");
        File.WriteAllBytes(archive, Convert.FromBase64String(
            "UmFyIRoHAQAzkrXlCgEFBgAFAQGAgAC6HquuMAIDC4QABIQAINRIKkCAAAAUU3VwZXIgTWFyaW8gQnJvcy5uZXMKAwJNhPjjVWvdAU5FUxodd1ZRAwUEAA=="));

        string? root = null;
        try
        {
            var found = SourceDiscovery.Collect([archive]);
            var group = Assert.Single(found.Groups);
            Assert.Equal("Super Mario Bros.nes", group.Label);
            Assert.False(group.Files[0].EndsWith(".rar", StringComparison.OrdinalIgnoreCase));
            root = group.ExtractRoot;
            var inspected = DiscInspector.Inspect(group);
            Assert.Equal(ContentKind.Rom, inspected.Kind);
            Assert.Equal("NES", inspected.RomFolder);
        }
        finally
        {
            Delete(root);
        }
    }

    private static void WriteArchive(string path, ArchiveType type, params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = File.Create(path);
        using var writer = WriterFactory.OpenWriter(stream, type, new WriterOptions(CompressionType.LZMA) { LeaveStreamOpen = true });
        foreach (var (name, bytes) in entries)
            writer.Write(name, new MemoryStream(bytes));
    }

    private static void Delete(string? root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
