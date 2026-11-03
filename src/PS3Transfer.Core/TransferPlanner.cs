using System.Globalization;
using System.Text;
using PS3Transfer.Core.Disc;

namespace PS3Transfer.Core;

public static class TransferPlanner
{
    public static TransferPlan Plan(PlanRequest request)
    {
        var source = request.Source;
        var reserved = request.Reserved ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kind = request.ForcedKind ?? source.Kind;
        if (source.Fatal)
            return Block(request, kind, source.Warning ?? "This item cannot be copied.");
        if (kind == ContentKind.Unknown)
            return Block(request, kind, source.Warning ?? "Choose the system for this file before copying.");
        if (kind == ContentKind.Rom)
            return PlanRom(request, reserved);
        if (source.Length <= 0)
            return Block(request, kind, "The file is empty.");
        if (!Compatible(kind, source))
            return Block(request, kind, "That type does not match this file. A folder game has to be a PS3_GAME directory, and a disc set cannot be renamed into one ISO.");

        try
        {
            return source.Group == GroupKind.Tree
                ? PlanTree(request, kind, reserved)
                : PlanLogical(request, kind, reserved);
        }
        catch (IOException ex)
        {
            return Block(request, kind, ex.Message);
        }
    }

    private static TransferPlan PlanLogical(PlanRequest request, ContentKind kind, HashSet<string> reserved)
    {
        var source = request.Source;
        var ext = ExtensionFor(source, kind);
        var split = Ps3Layout.NeedsSplit(request.FileSystem, source.Length, request.SplitSize);
        if (split && !CanSplit(kind, ext))
        {
            return Block(request, kind,
                "This file is over 4 GB and is not an .iso or .pkg. FAT32 cannot store it whole, and only those splits mount on a PS3. Convert it to an ISO, or use an NTFS drive with prepISO.");
        }

        if (kind is ContentKind.Rap or ContentKind.Edat)
        {
            if (!FatName.IsSafeFileName(source.OriginalName))
                return Block(request, kind, "The license file name has characters FAT32 cannot store, and renaming it would stop the PS3 from finding it.");
        }

        var category = Ps3Layout.CategoryFolder(kind, request.FileSystem);
        var categoryPath = Path.Combine(request.DriveRoot, category);
        string destBase;
        var scheme = SplitScheme.None;
        if (kind == ContentKind.Pkg && split)
        {
            var folderName = request.UseTitleNames
                ? FatName.Folder(TitleOf(source), source.TitleId)
                : FatName.Folder(Path.GetFileNameWithoutExtension(source.OriginalName), source.TitleId);
            var folder = ClaimFolder(categoryPath, folderName, reserved, PackageFolderUsable);
            var packageName = Path.GetFileName(folder) + ".pkg";
            destBase = Path.Combine(folder, packageName);
            scheme = SplitScheme.Pkg666;
            reserved.Add(folder);
        }
        else
        {
            var fileName = NameFor(request, source, kind, ext);
            if (kind is ContentKind.Rap or ContentKind.Edat)
            {
                destBase = Path.Combine(categoryPath, source.OriginalName);
                if (reserved.Contains(destBase))
                    return Block(request, kind, "Two license files would land on the same name in \\" + category + ".");
            }
            else
            {
                destBase = ClaimFile(Path.Combine(categoryPath, fileName), reserved);
            }

            if (split)
                scheme = Ps3Layout.SchemeFor(kind, insideTree: false);
        }

        if (!PathUtil.IsUnder(request.DriveRoot, destBase))
            return Block(request, kind, "The destination would fall outside the selected drive.");

        var state = DestinationState.Inspect(destBase, scheme, source.Length, request.SplitSize);
        var copy = new PlannedCopy
        {
            Index = 0,
            Sources = source.Parts,
            DestinationBase = destBase,
            Scheme = scheme,
            Length = source.Length,
            Existing = state.State,
            FullEnd = state.FullEnd,
        };
        Remember(reserved, copy, request.SplitSize);
        return Finish(request, kind, [copy], source.Parts, split, state.State, null);
    }

    private static TransferPlan PlanTree(PlanRequest request, ContentKind kind, HashSet<string> reserved)
    {
        var source = request.Source;
        foreach (var file in source.Tree)
        {
            if (!SafeRelative(file.RelativePath))
            {
                return Block(request, kind,
                    "FAT32 cannot store this name, and renaming it would break the game: " + file.RelativePath);
            }
        }

        if (!source.AllowMemberSplit && Ps3Layout.IsFat(request.FileSystem))
        {
            foreach (var file in source.Tree)
            {
                if (file.Length > request.SplitSize)
                {
                    var message = kind == ContentKind.PkgFolder
                        ? "\"" + file.RelativePath + "\" is over 4 GB. webMAN launches a package from \\GAMEI only when every file fits. FAT32 cannot store this file, and splitting it inside the package folder will not mount. Copy this package to an NTFS drive, or use a PS3 ISO."
                        : "\"" + file.RelativePath + "\" is over 4 GB. Splitting a BIN or CUE set breaks the disc. Convert it to an ISO, or copy it to an NTFS drive and use prepISO.";
                    return Block(request, kind, message);
                }
            }
        }

        var categoryPath = Path.Combine(request.DriveRoot, Ps3Layout.CategoryFolder(kind, request.FileSystem));
        var folderName = kind == ContentKind.PkgFolder
            ? PkgFolderName(request, source)
            : request.UseTitleNames
                ? FatName.Folder(TitleOf(source), source.TitleId)
                : FatName.Folder(source.OriginalName, source.TitleId);
        var folder = ClaimFolder(categoryPath, folderName, reserved, path => FolderUsable(path, source));
        reserved.Add(folder);

        var copies = new List<PlannedCopy>();
        var anySplit = false;
        for (var i = 0; i < source.Tree.Count; i++)
        {
            var file = source.Tree[i];
            var dest = Path.Combine(folder, file.RelativePath);
            if (!PathUtil.IsUnder(folder, dest))
                return Block(request, kind, "A file in this set points outside its folder.");

            var split = source.AllowMemberSplit && Ps3Layout.NeedsSplit(request.FileSystem, file.Length, request.SplitSize);
            var scheme = split ? SplitScheme.File666 : SplitScheme.None;
            if (split)
                anySplit = true;
            var state = DestinationState.Inspect(dest, scheme, file.Length, request.SplitSize);
            var copy = new PlannedCopy
            {
                Index = i,
                Sources = [file.FullPath],
                DestinationBase = dest,
                Scheme = scheme,
                Length = file.Length,
                Existing = state.State,
                FullEnd = state.FullEnd,
            };
            copies.Add(copy);
            Remember(reserved, copy, request.SplitSize);
        }

        var combined = ExistRules.Combine(copies.Select(c => c.Existing));
        var folderDisplay = PathUtil.Relative(request.DriveRoot, folder) + Path.DirectorySeparatorChar;
        return Finish(request, kind, copies, source.Tree.Select(f => f.FullPath).ToArray(), anySplit, combined, null, folderDisplay);
    }

    private static TransferPlan Finish(
        PlanRequest request,
        ContentKind kind,
        IReadOnlyList<PlannedCopy> copies,
        IReadOnlyList<string> sourceFiles,
        bool splits,
        ExistState disk,
        string? extraWarning,
        string? displayOverride = null)
    {
        var action = request.Source.Fatal
            ? TransferAction.Blocked
            : request.Replace && disk != ExistState.Missing
                ? TransferAction.Replace
                : disk switch
                {
                    ExistState.Missing => TransferAction.Copy,
                    ExistState.Partial => TransferAction.Resume,
                    ExistState.Complete => TransferAction.Skip,
                    _ => TransferAction.Skip,
                };

        long floor = 0;
        if (action == TransferAction.Resume)
        {
            foreach (var copy in copies)
            {
                if (copy.Existing == ExistState.Complete)
                    floor += copy.Length;
                else if (copy.Existing == ExistState.Partial)
                    floor += copy.FullEnd;
            }
        }

        var output = new List<string>();
        foreach (var copy in copies)
        {
            foreach (var part in Ps3Layout.ExpectedParts(copy.DestinationBase, copy.Scheme, copy.Length, request.SplitSize))
                output.Add(part.Path);
        }

        var display = displayOverride
            ?? (copies.Count == 0
                ? request.Source.Label
                : DisplayOf(request.DriveRoot, copies[0], splits, request.SplitSize));
        var notes = NotesFor(request, kind, splits);
        var warning = JoinWarnings(request.Source.Warning, extraWarning, disk == ExistState.Conflict && action == TransferAction.Skip
            ? "A different copy already uses this name on the drive. It will be left as it is. Choose Replace to overwrite it."
            : null);

        var signature = SignatureOf(copies, request.SplitSize, request.Source.SourceStamp);
        return new TransferPlan
        {
            Action = action,
            Disk = disk,
            DisplayPath = display,
            PlayTitle = notes.Title,
            PlayBody = notes.Body,
            Warning = warning,
            TotalBytes = request.Source.Length,
            AlreadyOnDrive = floor,
            PartCount = output.Count,
            Splits = splits,
            FatDestination = Ps3Layout.IsFat(request.FileSystem),
            SplitSize = request.SplitSize,
            SourceStamp = request.Source.SourceStamp,
            Signature = signature,
            ManifestKey = ManifestStore.Key(request.VolumeSerial, signature),
            DriveRoot = request.DriveRoot,
            SourceFiles = sourceFiles,
            Copies = copies,
            OutputPaths = output,
        };
    }

    private static TransferPlan Block(PlanRequest request, ContentKind kind, string warning)
    {
        return new TransferPlan
        {
            Action = TransferAction.Blocked,
            Disk = ExistState.Missing,
            DisplayPath = request.Source.Label,
            PlayTitle = "Cannot copy",
            PlayBody = warning,
            Warning = warning,
            TotalBytes = request.Source.Length,
            AlreadyOnDrive = 0,
            PartCount = 0,
            Splits = false,
            FatDestination = Ps3Layout.IsFat(request.FileSystem),
            SplitSize = request.SplitSize,
            SourceStamp = request.Source.SourceStamp,
            Signature = "",
            ManifestKey = new string('0', 64),
            DriveRoot = request.DriveRoot,
            SourceFiles = [],
            Copies = [],
            OutputPaths = [],
        };
    }

    private static TransferPlan PlanRom(PlanRequest request, HashSet<string> reserved)
    {
        var source = request.Source;
        var folder = string.IsNullOrEmpty(request.RomFolder) ? source.RomFolder : request.RomFolder;
        var system = RomLayout.Find(folder);
        if (system is null)
            return Block(request, ContentKind.Rom, "Choose the system for this ROM.");
        if (source.Length <= 0)
            return Block(request, ContentKind.Rom, "The file is empty.");
        if (!Compatible(ContentKind.Rom, source))
            return Block(request, ContentKind.Rom, "That type does not match this file.");
        if (Ps3Layout.NeedsSplit(request.FileSystem, source.Length, request.SplitSize))
            return Block(request, ContentKind.Rom, "This ROM is over 4 GB. RetroArch on the PS3 cannot open a file that was split to fit FAT32.");

        var fileName = FatName.Keep(source.OriginalName);
        if (!FatName.IsSafeFileName(fileName))
            return Block(request, ContentKind.Rom, "This ROM name has characters FAT32 cannot store, and renaming it would stop RetroArch from matching it.");

        var renamed = !fileName.Equals(source.OriginalName, StringComparison.Ordinal);
        var dest = ClaimFile(Path.Combine(request.DriveRoot, RomLayout.Root, system.Folder, fileName), reserved);
        if (!PathUtil.IsUnder(request.DriveRoot, dest))
            return Block(request, ContentKind.Rom, "The destination would fall outside the selected drive.");

        var state = DestinationState.Inspect(dest, SplitScheme.None, source.Length, request.SplitSize);
        var copy = new PlannedCopy
        {
            Index = 0,
            Sources = source.Parts,
            DestinationBase = dest,
            Scheme = SplitScheme.None,
            Length = source.Length,
            Existing = state.State,
            FullEnd = state.FullEnd,
        };
        Remember(reserved, copy, request.SplitSize);
        var warning = renamed ? "The file name was cleaned to characters FAT32 allows." : null;
        return Finish(request, ContentKind.Rom, [copy], source.Parts, false, state.State, warning);
    }

    private static (string Title, string Body) NotesFor(PlanRequest request, ContentKind kind, bool splits)
    {
        if (kind == ContentKind.Rom)
        {
            var folder = string.IsNullOrEmpty(request.RomFolder) ? request.Source.RomFolder : request.RomFolder;
            var system = RomLayout.Find(folder);
            if (system is not null)
                return PlayNotes.DescribeRom(system.Folder, system.Label, system.ListedByWebMan, Ps3Layout.IsFat(request.FileSystem));
        }

        return PlayNotes.Describe(kind, splits, request.FileSystem);
    }

    private static bool Compatible(ContentKind kind, InspectedSource source)
    {
        if (kind == ContentKind.Rom)
            return source.Kind == ContentKind.Rom && source.Group == GroupKind.Logical;
        if (kind == ContentKind.Ps3Folder)
            return source.Group == GroupKind.Tree && source.AllowMemberSplit;
        if (kind == ContentKind.PkgFolder)
            return source.Group == GroupKind.Tree && !source.AllowMemberSplit;
        if (source.Group == GroupKind.Tree && source.AllowMemberSplit)
            return kind == ContentKind.Ps3Folder;
        if (source.Group == GroupKind.Tree)
            return kind is ContentKind.Ps1Disc or ContentKind.Ps2Iso or ContentKind.PspIso or ContentKind.PkgFolder;
        return kind is not ContentKind.Ps3Folder and not ContentKind.PkgFolder;
    }

    private static bool CanSplit(ContentKind kind, string extension)
    {
        if (kind == ContentKind.Pkg && extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            return true;
        return extension.Equals(".iso", StringComparison.OrdinalIgnoreCase)
            && kind is ContentKind.Ps3Iso or ContentKind.Ps2Iso or ContentKind.Ps1Disc
                or ContentKind.PspIso or ContentKind.BdIso or ContentKind.DvdIso;
    }

    private static string ExtensionFor(InspectedSource source, ContentKind kind)
    {
        if (kind == ContentKind.Pkg)
            return ".pkg";
        if (kind == ContentKind.Rap)
            return ".rap";
        if (kind == ContentKind.Edat)
            return ".edat";
        var ext = Path.GetExtension(source.OriginalName);
        if (string.IsNullOrEmpty(ext))
            return ".iso";
        return ext.ToLower(CultureInfo.InvariantCulture);
    }

    private static string NameFor(PlanRequest request, InspectedSource source, ContentKind kind, string extension)
    {
        if (!request.UseTitleNames)
            return FatName.Keep(Path.GetFileNameWithoutExtension(source.OriginalName) + extension);
        var stem = TitleOf(source);
        return FatName.File(stem, source.TitleId, extension);
    }

    private static string TitleOf(InspectedSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.Title))
            return source.Title;
        var stem = Path.GetFileNameWithoutExtension(source.OriginalName);
        return string.IsNullOrWhiteSpace(stem) ? source.Label : stem;
    }

    private static string ClaimFile(string path, HashSet<string> reserved)
    {
        if (!reserved.Contains(path))
            return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; n < 500; n++)
        {
            var candidate = Path.Combine(directory, stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + ext);
            if (!reserved.Contains(candidate))
                return candidate;
        }

        throw new IOException("Too many files already use the name " + Path.GetFileName(path) + ".");
    }

    private static string ClaimFolder(string parent, string name, HashSet<string> reserved, Func<string, bool> usable)
    {
        for (var n = 1; n < 500; n++)
        {
            var folderName = n == 1 ? name : name + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
            var path = Path.Combine(parent, folderName);
            if (reserved.Contains(path))
                continue;
            if (!Directory.Exists(path) || usable(path))
                return path;
        }

        throw new IOException("Too many folders already use the name " + name + ".");
    }

    private static string PkgFolderName(PlanRequest request, InspectedSource source)
    {
        if (request.UseTitleNames && !string.IsNullOrEmpty(source.TitleId))
            return FatName.Folder(source.TitleId, null);
        return FatName.Folder(source.OriginalName, null);
    }

    private static bool FolderUsable(string folder, InspectedSource source)
    {
        if (source.AllowMemberSplit)
            return SameFolderGame(folder, source.TitleId);
        return !HasForeign(folder, source);
    }

    private static bool PackageFolderUsable(string folder)
    {
        try
        {
            if (Directory.EnumerateDirectories(folder).Any())
                return false;
            var packageName = Path.GetFileName(folder) + ".pkg";
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (SplitNames.IsJunk(name))
                    continue;
                if (!SplitNames.IsRelatedName(name, packageName))
                    return false;
            }
        }
        catch (IOException)
        {
            return false;
        }

        return true;
    }

    private static bool SameFolderGame(string folder, string? titleId)
    {
        var sfo = FindSfo(folder);
        if (sfo is null)
            return !Directory.EnumerateFileSystemEntries(folder).Any();
        if (string.IsNullOrEmpty(titleId))
            return false;
        try
        {
            var map = ParamSfo.Read(File.ReadAllBytes(sfo));
            return map.TryGetValue("TITLE_ID", out var existing)
                && existing.Trim().Equals(titleId, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? FindSfo(string folder)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                if (!Path.GetFileName(dir).Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (Path.GetFileName(file).Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    private static bool HasForeign(string folder, InspectedSource source)
    {
        var ours = new HashSet<string>(source.Tree.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true }))
            {
                var rel = Path.GetRelativePath(folder, file);
                if (ours.Contains(rel) || SplitNames.IsJunk(Path.GetFileName(file)))
                    continue;
                if (SplitNames.TryParse(Path.GetFileName(rel), out var baseName, out _, out _))
                {
                    var parent = Path.GetDirectoryName(rel);
                    var combined = string.IsNullOrEmpty(parent) ? baseName : Path.Combine(parent, baseName);
                    if (ours.Contains(combined))
                        continue;
                }

                return true;
            }
        }
        catch (IOException)
        {
            return true;
        }

        return false;
    }

    private static bool SafeRelative(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length == 0)
            return false;
        foreach (var part in parts)
        {
            if (part is "" or "." or "..")
                return false;
            if (!FatName.IsSafeFileName(part))
                return false;
        }

        return true;
    }

    private static void Remember(HashSet<string> reserved, PlannedCopy copy, long splitSize)
    {
        reserved.Add(copy.DestinationBase);
        var directory = Path.GetDirectoryName(copy.DestinationBase);
        if (!string.IsNullOrEmpty(directory))
            reserved.Add(directory);
        foreach (var part in Ps3Layout.ExpectedParts(copy.DestinationBase, copy.Scheme, copy.Length, splitSize))
            reserved.Add(part.Path);
    }

    private static string DisplayOf(string root, PlannedCopy first, bool splits, long splitSize)
    {
        var shown = splits && first.Scheme != SplitScheme.None
            ? Ps3Layout.PartPath(first.DestinationBase, first.Scheme, 0)
            : first.DestinationBase;
        var relative = PathUtil.Relative(root, shown);
        if (splits && first.Scheme != SplitScheme.None && Ps3Layout.PartCount(first.Length, splitSize) > 1)
            relative += " …";
        return relative;
    }

    private static string SignatureOf(IReadOnlyList<PlannedCopy> copies, long splitSize, long stamp)
    {
        var sb = new StringBuilder();
        sb.Append(splitSize.ToString(CultureInfo.InvariantCulture)).Append('|').Append(stamp.ToString(CultureInfo.InvariantCulture));
        foreach (var copy in copies)
        {
            sb.Append('|').Append((int)copy.Scheme).Append(':').Append(copy.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(copy.DestinationBase);
        }

        return sb.ToString();
    }

    private static string? JoinWarnings(params string?[] parts)
    {
        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }
}
