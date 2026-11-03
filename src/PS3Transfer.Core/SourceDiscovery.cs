namespace PS3Transfer.Core;

public enum DiscoveryMode
{
    LogicalParts = 0,
    JbFolder = 1,
    FileSet = 2,
    PkgFolder = 3,
}

public sealed record DiscoveredGroup(
    DiscoveryMode Mode,
    string Label,
    IReadOnlyList<string> Files,
    string? Note,
    bool Incomplete,
    string? ExtractRoot = null);

public sealed class DiscoveryResult
{
    public required IReadOnlyList<DiscoveredGroup> Groups { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

public static class SourceDiscovery
{
    public static DiscoveryResult Collect(IEnumerable<string> inputs)
    {
        var bag = new Bag();

        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                bag.Notes.Add("Could not use " + raw + ". " + ex.Message);
                continue;
            }

            if (Directory.Exists(full))
            {
                if (IsDriveRoot(full))
                {
                    bag.Notes.Add("Add the folder that contains the games, not the whole " + full + " drive.");
                    continue;
                }

                Expand(full, bag);
            }
            else if (File.Exists(full))
            {
                TakeFile(full, bag, explicitFile: true);
            }
            else
            {
                bag.Notes.Add("Nothing found at " + full + ".");
            }
        }

        var groups = new List<DiscoveredGroup>();
        foreach (var folder in Distinct(bag.Folders))
            groups.Add(Make(DiscoveryMode.JbFolder, folder, [folder], null, false, bag));
        foreach (var folder in Distinct(bag.PkgFolders))
            groups.Add(Make(DiscoveryMode.PkgFolder, folder, [folder], null, false, bag));

        groups.AddRange(GroupFiles(Distinct(bag.Files), bag.Explicit, bag.Notes, bag));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<DiscoveredGroup>();
        foreach (var group in groups)
        {
            var key = group.Mode + "|" + group.Files[0];
            if (seen.Add(key))
                unique.Add(group);
        }

        foreach (var root in bag.Extracted)
        {
            var used = false;
            foreach (var group in unique)
            {
                if (string.Equals(group.ExtractRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    used = true;
                    break;
                }
            }

            if (!used)
                TryDelete(root);
        }

        return new DiscoveryResult { Groups = unique, Notes = bag.Notes };
    }

    private static void TakeFile(string full, Bag bag, bool explicitFile)
    {
        if (GameArchive.IsArchive(full))
        {
            IngestArchive(full, bag);
            return;
        }

        bag.AddFile(full, explicitFile);
    }

    private static void IngestArchive(string archive, Bag bag)
    {
        var name = Path.GetFileName(archive);
        if (bag.Depth >= 3)
        {
            bag.Notes.Add(name + " is nested too deep to extract.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "PS3TransferSuite", Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, FatName.Folder(Path.GetFileNameWithoutExtension(archive), null));
        if (!GameArchive.TryExtract(archive, dest, out var error))
        {
            bag.Notes.Add(name + " was not extracted. " + (error ?? "The archive could not be read."));
            TryDelete(root);
            return;
        }

        bag.Extracted.Add(root);
        var filesBefore = bag.Files.Count;
        var foldersBefore = bag.Folders.Count;
        var pkgBefore = bag.PkgFolders.Count;
        var nested = new Bag
        {
            Notes = bag.Notes,
            Files = bag.Files,
            Folders = bag.Folders,
            PkgFolders = bag.PkgFolders,
            Explicit = bag.Explicit,
            Roots = bag.Roots,
            Extracted = bag.Extracted,
            ExtractRoot = root,
            Depth = bag.Depth + 1,
        };
        Expand(dest, nested);
        if (bag.Files.Count == filesBefore && bag.Folders.Count == foldersBefore && bag.PkgFolders.Count == pkgBefore)
            bag.Notes.Add(name + " does not contain a game. The archive was not added.");
        else
            bag.Notes.Add("Extracted " + name + ". The game inside is queued, not the archive folder.");
    }

    private static void Expand(string directory, Bag bag)
    {
        if (IsReparse(directory))
            return;
        if (IsJbFolder(directory))
        {
            bag.AddFolder(bag.Folders, directory);
            return;
        }

        if (IsPkgFolder(directory))
        {
            bag.AddFolder(bag.PkgFolders, directory);
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bag.Notes.Add("Could not read " + directory + ". " + ex.Message);
            return;
        }

        foreach (var child in children)
        {
            if (IsReparse(child))
                continue;
            if (Directory.Exists(child))
                Expand(child, bag);
            else if (GameArchive.IsArchive(child))
                IngestArchive(child, bag);
            else if (SplitNames.IsCandidateName(Path.GetFileName(child)))
                bag.AddFile(child, explicitFile: false);
        }
    }

    private static List<DiscoveredGroup> GroupFiles(
        IReadOnlyList<string> files,
        HashSet<string> explicitFiles,
        List<string> notes,
        Bag bag)
    {
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<DiscoveredGroup>();
        var byKey = new Dictionary<string, List<(int Index, string Path)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!SplitNames.TryParse(name, out var baseName, out var index, out var scheme))
                continue;
            var key = Path.GetDirectoryName(file) + "|" + scheme + "|" + baseName;
            if (!byKey.TryGetValue(key, out var list))
            {
                list = [];
                byKey[key] = list;
            }

            list.Add((index, file));
        }

        foreach (var (key, parts) in byKey)
        {
            var schemeSep = key.IndexOf('|');
            var schemeName = key[(schemeSep + 1)..];
            var schemeSep2 = schemeName.IndexOf('|');
            var scheme = Enum.Parse<SplitScheme>(schemeName[..schemeSep2]);
            var baseName = schemeName[(schemeSep2 + 1)..];
            var directory = key[..schemeSep];
            parts.Sort((a, b) => a.Index.CompareTo(b.Index));

            var indexes = parts.Select(p => p.Index).ToList();
            var paths = parts.Select(p => p.Path).ToList();
            string? note = null;
            var incomplete = false;

            if (scheme == SplitScheme.IsoDotNumber && indexes[0] == 1)
            {
                var loose = Path.Combine(directory, baseName);
                if (File.Exists(loose))
                {
                    paths.Insert(0, loose);
                    consumed.Add(loose);
                }
                else
                {
                    note = baseName + " is missing .iso.0.";
                    incomplete = true;
                }
            }
            else if (scheme == SplitScheme.IsoDotNumber && indexes[0] != 0)
            {
                note = baseName + " is missing .iso.0.";
                incomplete = true;
            }
            else if (scheme == SplitScheme.PkgNumber && indexes[0] is not 0 and not 1)
            {
                note = baseName + " is missing the first part.";
                incomplete = true;
            }
            else if (scheme != SplitScheme.IsoDotNumber && scheme != SplitScheme.PkgNumber && indexes[0] != 66600)
            {
                note = baseName + " is missing .66600.";
                incomplete = true;
            }

            if (!incomplete)
            {
                var start = scheme == SplitScheme.IsoDotNumber ? (paths.Count > indexes.Count ? 1 : 0) : 66600;
                // After inserting a loose first ISO, numbered indexes still start at 1.
                var cursor = scheme == SplitScheme.IsoDotNumber
                    ? (indexes[0] == 1 && paths.Count == indexes.Count + 1 ? 1 : 0)
                    : 66600;
                if (indexes[0] != cursor && !(scheme == SplitScheme.IsoDotNumber && indexes[0] == 0))
                {
                    // already handled
                }

                for (var i = 1; i < indexes.Count; i++)
                {
                    if (indexes[i] != indexes[i - 1] + 1)
                    {
                        note = baseName + " is missing part " + (indexes[i - 1] + 1) + ".";
                        incomplete = true;
                        break;
                    }
                }

                _ = start;
            }

            foreach (var part in parts)
                consumed.Add(part.Path);

            if (incomplete && note is not null)
                notes.Add(note);

            groups.Add(Make(DiscoveryMode.LogicalParts, baseName, paths, note, incomplete, bag));
        }

        GroupUnderscorePkgs(files, consumed, groups, notes, bag);

        foreach (var file in files)
        {
            if (consumed.Contains(file))
                continue;
            if (!Path.GetExtension(file).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                continue;

            var cueDir = Path.GetDirectoryName(file)!;
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add("Could not read " + file + ". " + ex.Message);
                continue;
            }

            var set = new List<string> { file };
            var missing = new List<string>();
            foreach (var referenced in CueSheetNames(text))
            {
                var full = Path.GetFullPath(Path.Combine(cueDir, referenced));
                if (!PathUtil.IsUnder(cueDir, full))
                {
                    missing.Add(referenced + " (the CUE points outside its folder)");
                    continue;
                }

                if (!File.Exists(full))
                {
                    missing.Add(referenced);
                    continue;
                }

                set.Add(full);
                consumed.Add(full);
            }

            consumed.Add(file);
            string? note = null;
            if (missing.Count > 0)
            {
                note = "Missing disc file " + string.Join(", ", missing) + ".";
                notes.Add(Path.GetFileName(file) + ": " + note);
            }

            groups.Add(Make(DiscoveryMode.FileSet, file, set, note, missing.Count > 0, bag));
        }

        foreach (var file in files)
        {
            if (consumed.Contains(file))
                continue;
            var ext = Path.GetExtension(file);
            if (ext.Equals(".mdf", StringComparison.OrdinalIgnoreCase))
            {
                var mds = Path.ChangeExtension(file, ".mds");
                if (File.Exists(mds))
                {
                    consumed.Add(file);
                    consumed.Add(mds);
                    groups.Add(Make(DiscoveryMode.FileSet, file, [file, mds], null, false, bag));
                    continue;
                }
            }

            if (ext.Equals(".mds", StringComparison.OrdinalIgnoreCase))
            {
                var mdf = Path.ChangeExtension(file, ".mdf");
                if (File.Exists(mdf))
                    continue;
            }

            if (!SplitNames.IsCandidateName(Path.GetFileName(file)) && !explicitFiles.Contains(file))
                continue;

            consumed.Add(file);
            groups.Add(Make(
                DiscoveryMode.LogicalParts,
                Path.GetFileName(file),
                [file],
                null,
                false,
                bag));
        }

        return groups;
    }

    private static void GroupUnderscorePkgs(
        IReadOnlyList<string> files,
        HashSet<string> consumed,
        List<DiscoveredGroup> groups,
        List<string> notes,
        Bag bag)
    {
        var pool = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (consumed.Contains(file) || !seen.Add(file))
                continue;
            pool.Add(file);
            if (!PkgParts.TryParse(Path.GetFileName(file), out var baseName, out _))
                continue;

            var directory = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(directory))
                continue;

            IEnumerable<string> siblings;
            try
            {
                siblings = Directory.EnumerateFiles(directory, "*.pkg");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add("Could not read " + directory + ". " + ex.Message);
                continue;
            }

            foreach (var sibling in siblings)
            {
                if (!PkgParts.TryParse(Path.GetFileName(sibling), out var otherBase, out _))
                    continue;
                if (!otherBase.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seen.Add(sibling))
                    pool.Add(sibling);
            }
        }

        var requested = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var set in PkgParts.Group(pool))
        {
            var touchesRequest = false;
            foreach (var part in set.Files)
            {
                if (!requested.Contains(part))
                    continue;
                touchesRequest = true;
                consumed.Add(part);
            }

            if (!touchesRequest)
                continue;
            if (!set.Complete && set.Note is not null)
                notes.Add(set.Note);
            groups.Add(Make(DiscoveryMode.LogicalParts, set.BaseName, set.Files, set.Note, !set.Complete, bag));
        }
    }

    private static DiscoveredGroup Make(
        DiscoveryMode mode,
        string label,
        IReadOnlyList<string> files,
        string? note,
        bool incomplete,
        Bag bag)
    {
        bag.Roots.TryGetValue(files[0], out var root);
        return new DiscoveredGroup(mode, label, files, note, incomplete, root);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Bag
    {
        public List<string> Notes { get; init; } = [];
        public List<string> Files { get; init; } = [];
        public List<string> Folders { get; init; } = [];
        public List<string> PkgFolders { get; init; } = [];
        public HashSet<string> Explicit { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Roots { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Extracted { get; init; } = [];
        public string? ExtractRoot { get; init; }
        public int Depth { get; init; }

        public void AddFile(string path, bool explicitFile)
        {
            Files.Add(path);
            if (explicitFile)
                Explicit.Add(path);
            if (ExtractRoot is not null)
                Roots[path] = ExtractRoot;
        }

        public void AddFolder(List<string> list, string path)
        {
            list.Add(path);
            if (ExtractRoot is not null)
                Roots[path] = ExtractRoot;
        }
    }

    private static IEnumerable<string> CueSheetNames(string text)
    {
        foreach (var name in Disc.CueSheet.ReferencedFiles(text))
            yield return name;
    }

    public static bool IsJbFolder(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!Path.GetFileName(child).Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var file in Directory.EnumerateFiles(child))
                {
                    if (Path.GetFileName(file).Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    public static bool IsPkgFolder(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;
            var sfo = false;
            var usr = false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) && File.Exists(entry))
                    sfo = true;
                if (name.Equals("USRDIR", StringComparison.OrdinalIgnoreCase) && Directory.Exists(entry))
                    usr = true;
            }

            return sfo && usr;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDriveRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full);
        if (root is null)
            return false;
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(trimmed, StringComparison.OrdinalIgnoreCase);
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
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static List<string> Distinct(List<string> paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var path in paths)
        {
            if (set.Add(path))
                list.Add(path);
        }

        return list;
    }
}
