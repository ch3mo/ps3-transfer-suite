using System.Text;
using PS3Transfer.Core.Disc;

namespace PS3Transfer.Core;

public static class DiscInspector
{
    public static InspectedSource Inspect(DiscoveredGroup group)
    {
        try
        {
            return group.Mode switch
            {
                DiscoveryMode.JbFolder => InspectFolder(group),
                DiscoveryMode.PkgFolder => InspectPkgFolder(group),
                DiscoveryMode.FileSet => InspectSet(group),
                _ => InspectLogical(group),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(group, ex.Message);
        }
    }

    public static long StampOf(IEnumerable<string> files)
    {
        long stamp = 17;
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            stamp = unchecked(stamp * 31 + info.Length);
            stamp = unchecked(stamp * 31 + info.LastWriteTimeUtc.Ticks);
        }

        return stamp;
    }

    private static InspectedSource InspectLogical(DiscoveredGroup group)
    {
        var length = 0L;
        foreach (var file in group.Files)
        {
            if (!File.Exists(file))
                return Failed(group, "Missing " + Path.GetFileName(file) + ".");
            length += new FileInfo(file).Length;
        }

        if (group.Incomplete)
            return Failed(group, group.Note ?? "The split set is incomplete.");
        if (length <= 0)
            return Failed(group, "The file is empty.");

        var ext = Path.GetExtension(group.Label);
        if (ext.Equals(".rap", StringComparison.OrdinalIgnoreCase))
        {
            return Logical(group, ContentKind.Rap, Path.GetFileNameWithoutExtension(group.Label), null, null, length,
                length == 16 ? null : "RAP files are normally 16 bytes. This one is " + length + " bytes.", false);
        }

        if (ext.Equals(".edat", StringComparison.OrdinalIgnoreCase))
            return Logical(group, ContentKind.Edat, Path.GetFileNameWithoutExtension(group.Label), null, null, length, null, false);

        if (RomLayout.TryClassify(group.Label, Path.GetDirectoryName(group.Files[0]), group.Files[0], out var rom))
        {
            return Logical(group, ContentKind.Rom, Path.GetFileNameWithoutExtension(group.Label), null, null, length, rom.Warning, false, rom.Folder);
        }

        using var stream = new PartStream(group.Files);
        var header = new byte[128];
        var got = stream.Read(header, 0, header.Length);
        stream.Position = 0;
        if (PkgHeader.TryRead(header.AsSpan(0, got), out var contentId, out var pkgTitle))
        {
            return Logical(group, ContentKind.Pkg, null, pkgTitle, contentId, length, null, false);
        }

        using var iso = IsoVolume.TryOpen(stream, ownsStream: false);
        if (iso is not null && TryClassify(iso, out var kind, out var title, out var titleId))
            return Logical(group, kind, title, titleId, null, length, null, false);

        var fallback = Fallback(group.Label, out var warning);
        return Logical(group, fallback, null, null, null, length, warning, false);
    }

    private static InspectedSource InspectFolder(DiscoveredGroup group)
    {
        var folder = group.Files[0];
        var sfoPath = FindChild(folder, "PS3_GAME", "PARAM.SFO");
        if (sfoPath is null)
            return Failed(group, "This folder has no PS3_GAME\\PARAM.SFO file.");

        string? title = null;
        string? titleId = null;
        try
        {
            var map = ParamSfo.Read(File.ReadAllBytes(sfoPath));
            map.TryGetValue("TITLE", out title);
            map.TryGetValue("TITLE_ID", out titleId);
        }
        catch (IOException ex)
        {
            return Failed(group, "Could not read PARAM.SFO. " + ex.Message);
        }

        var tree = EnumerateTree(folder);
        if (tree.Count == 0)
            return Failed(group, "The game folder is empty.");

        var length = tree.Sum(f => f.Length);
        return new InspectedSource
        {
            Label = new DirectoryInfo(folder).Name,
            DisplayPath = folder,
            Group = GroupKind.Tree,
            Kind = ContentKind.Ps3Folder,
            Title = Clean(title),
            TitleId = Clean(titleId),
            Warning = null,
            Fatal = false,
            OriginalName = new DirectoryInfo(folder).Name,
            Length = length,
            Tree = tree,
            AllowMemberSplit = true,
            SourceStamp = StampOf(tree.Select(f => f.FullPath)),
        };
    }

    private static InspectedSource InspectPkgFolder(DiscoveredGroup group)
    {
        var folder = group.Files[0];
        var sfoPath = FindChild(folder, "PARAM.SFO");
        if (sfoPath is null)
            return Failed(group, "This package folder has no PARAM.SFO file.");

        string? title = null;
        string? titleId = null;
        try
        {
            var map = ParamSfo.Read(File.ReadAllBytes(sfoPath));
            map.TryGetValue("TITLE", out title);
            map.TryGetValue("TITLE_ID", out titleId);
        }
        catch (IOException ex)
        {
            return Failed(group, "Could not read PARAM.SFO. " + ex.Message);
        }

        var tree = EnumerateTree(folder);
        if (tree.Count == 0)
            return Failed(group, "The package folder is empty.");

        return new InspectedSource
        {
            Label = new DirectoryInfo(folder).Name,
            DisplayPath = folder,
            Group = GroupKind.Tree,
            Kind = ContentKind.PkgFolder,
            Title = Clean(title),
            TitleId = Clean(titleId),
            Warning = null,
            Fatal = false,
            OriginalName = new DirectoryInfo(folder).Name,
            Length = tree.Sum(f => f.Length),
            Tree = tree,
            AllowMemberSplit = false,
            SourceStamp = StampOf(tree.Select(f => f.FullPath)),
        };
    }

    private static InspectedSource InspectSet(DiscoveredGroup group)
    {
        if (group.Incomplete)
            return Failed(group, group.Note ?? "The disc set is missing a file.");

        var root = Path.GetDirectoryName(group.Files[0])!;
        var tree = new List<TreeFile>();
        foreach (var file in group.Files)
        {
            if (!File.Exists(file))
                return Failed(group, "Missing " + file + ".");
            if (!PathUtil.IsUnder(root, file))
                return Failed(group, "A disc file points outside its folder.");
            var rel = Path.GetRelativePath(root, file);
            tree.Add(new TreeFile(file, rel, new FileInfo(file).Length));
        }

        if (tree.Sum(f => f.Length) <= 0)
            return Failed(group, "The disc set is empty.");

        string? titleId = null;
        var ps2 = false;
        var sawDisc = false;
        foreach (var file in group.Files)
        {
            if (Path.GetExtension(file).Equals(".cue", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(file).Equals(".mds", StringComparison.OrdinalIgnoreCase))
                continue;
            using var iso = IsoVolume.TryOpen(file);
            if (iso is null)
                continue;
            sawDisc = true;
            var cnf = iso.ReadSmall("SYSTEM.CNF", 64 * 1024);
            if (cnf is null)
                continue;
            var info = SystemCnf.Parse(Encoding.ASCII.GetString(cnf));
            ps2 = info.IsPs2;
            titleId = info.TitleId ?? titleId;
            break;
        }

        var kind = ps2 ? ContentKind.Ps2Iso : ContentKind.Ps1Disc;
        var warning = sawDisc ? null : "The disc header could not be read. It will be stored as a PS1 disc. Change the type if that is wrong.";
        if (ps2)
            warning = "PS2 BIN/CUE and MDF images are not what webMAN launches. Convert the game to an ISO for a one-step launch. The files are still copied into \\PS2ISO.";

        return new InspectedSource
        {
            Label = Path.GetFileName(group.Label),
            DisplayPath = group.Files[0],
            Group = GroupKind.Tree,
            Kind = kind,
            Title = Clean(Path.GetFileNameWithoutExtension(group.Label)),
            TitleId = titleId,
            Warning = warning,
            Fatal = false,
            OriginalName = Path.GetFileName(group.Label),
            Length = tree.Sum(f => f.Length),
            Tree = tree,
            AllowMemberSplit = false,
            SourceStamp = StampOf(tree.Select(f => f.FullPath)),
        };
    }

    private static bool TryClassify(IsoVolume iso, out ContentKind kind, out string? title, out string? titleId)
    {
        kind = ContentKind.Unknown;
        title = Clean(iso.VolumeLabel);
        titleId = null;

        var ps3 = iso.ReadSmall("PS3_GAME/PARAM.SFO", 256 * 1024);
        if (ps3 is not null)
        {
            kind = ContentKind.Ps3Iso;
            ReadSfo(ps3, ref title, ref titleId);
            return true;
        }

        var psp = iso.ReadSmall("PSP_GAME/PARAM.SFO", 256 * 1024);
        if (psp is not null)
        {
            kind = ContentKind.PspIso;
            ReadSfo(psp, ref title, ref titleId);
            return true;
        }

        var cnf = iso.ReadSmall("SYSTEM.CNF", 64 * 1024);
        if (cnf is not null)
        {
            var info = SystemCnf.Parse(Encoding.ASCII.GetString(cnf));
            kind = info.IsPs2 ? ContentKind.Ps2Iso : ContentKind.Ps1Disc;
            titleId = info.TitleId;
            return true;
        }

        if (iso.Exists("BDMV/index.bdmv", directory: false))
        {
            kind = ContentKind.BdIso;
            return true;
        }

        if (iso.Exists("VIDEO_TS", directory: true))
        {
            kind = ContentKind.DvdIso;
            return true;
        }

        return false;
    }

    private static void ReadSfo(byte[] data, ref string? title, ref string? titleId)
    {
        var map = ParamSfo.Read(data);
        if (map.TryGetValue("TITLE", out var sfoTitle))
            title = Clean(sfoTitle);
        if (map.TryGetValue("TITLE_ID", out var id))
            titleId = Clean(id);
    }

    private static ContentKind Fallback(string fileName, out string? warning)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            warning = "The PKG header could not be read. It will still be copied as a package.";
            return ContentKind.Pkg;
        }

        if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            warning = "Could not read the disc header. Choose the system before copying.";
            return ContentKind.Unknown;
        }

        if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) || ext.Equals(".img", StringComparison.OrdinalIgnoreCase))
        {
            warning = "No CUE file, and the disc header could not be read. It will be stored as a PS1 disc.";
            return ContentKind.Ps1Disc;
        }

        if (ext.Equals(".mdf", StringComparison.OrdinalIgnoreCase))
        {
            warning = "Could not read this MDF. It will be stored as a PS2 disc.";
            return ContentKind.Ps2Iso;
        }

        warning = "This file is not a recognized ISO, PKG, or license file. Choose a type, or remove it.";
        return ContentKind.Unknown;
    }

    private static InspectedSource Logical(
        DiscoveredGroup group,
        ContentKind kind,
        string? title,
        string? titleId,
        string? contentId,
        long length,
        string? warning,
        bool fatal,
        string? romFolder = null) => new()
    {
        Label = group.Label,
        DisplayPath = group.Files[0],
        Group = GroupKind.Logical,
        Kind = kind,
        Title = Clean(title),
        TitleId = Clean(titleId),
        ContentId = contentId,
        RomFolder = romFolder,
        Warning = warning,
        Fatal = fatal,
        OriginalName = group.Label,
        Parts = group.Files,
        Length = length,
        AllowMemberSplit = false,
        SourceStamp = StampOf(group.Files),
    };

    private static InspectedSource Failed(DiscoveredGroup group, string warning) => new()
    {
        Label = group.Label,
        DisplayPath = group.Files.Count > 0 ? group.Files[0] : group.Label,
        Group = group.Mode == DiscoveryMode.LogicalParts ? GroupKind.Logical : GroupKind.Tree,
        Kind = ContentKind.Unknown,
        Warning = warning,
        Fatal = true,
        OriginalName = group.Label,
        Parts = group.Mode == DiscoveryMode.LogicalParts ? group.Files : [],
        SourceStamp = 0,
    };

    public static List<TreeFile> EnumerateTree(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var list = new List<TreeFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            if (IsReparse(file))
                continue;
            var name = Path.GetFileName(file);
            if (SplitNames.IsJunk(name))
                continue;
            list.Add(new TreeFile(file, Path.GetRelativePath(root, file), new FileInfo(file).Length));
        }

        return list;
    }

    private static string? FindChild(string root, params string[] parts)
    {
        var current = root;
        foreach (var part in parts)
        {
            string? next = null;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (Path.GetFileName(entry).Equals(part, StringComparison.OrdinalIgnoreCase))
                    {
                        next = entry;
                        break;
                    }
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            if (next is null)
                return null;
            current = next;
        }

        return File.Exists(current) ? current : null;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsReparse(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return true;
        }
    }
}
