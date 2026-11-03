namespace PS3Transfer.Core;

public sealed record LibraryEntry(
    string Title,
    string Category,
    long Bytes,
    int Parts,
    bool Split,
    string RelativePath,
    IReadOnlyList<string> Files);

public static class LibraryCatalog
{
    public static IReadOnlyList<LibraryEntry> Scan(string driveRoot)
    {
        var entries = new List<LibraryEntry>();
        foreach (var folder in Ps3Layout.LibraryFolders)
        {
            var path = Path.Combine(driveRoot, folder);
            if (!Directory.Exists(path))
                continue;
            if (folder is "GAMES" or "GAMEZ" or "GAMEI")
                entries.AddRange(ScanGames(driveRoot, path, folder));
            else if (folder is "ROMS")
                entries.AddRange(ScanRoms(driveRoot, path));
            else if (folder is "exdata")
                entries.AddRange(ScanLoose(driveRoot, path, folder, license: true));
            else
                entries.AddRange(ScanLoose(driveRoot, path, folder, license: false));
        }

        entries.AddRange(ScanLooseRoms(driveRoot, entries));

        entries.Sort((a, b) =>
        {
            var folder = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return folder != 0 ? folder : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });
        return entries;
    }

    public static void Delete(string driveRoot, LibraryEntry entry)
    {
        foreach (var file in entry.Files)
        {
            if (!PathUtil.IsUnder(driveRoot, file))
                throw new InvalidOperationException("Refusing to delete outside the selected drive.");
        }

        foreach (var file in entry.Files)
        {
            if (!File.Exists(file))
                continue;
            var attributes = File.GetAttributes(file);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Ps3Layout.LibraryFolders)
            roots.Add(Path.GetFullPath(Path.Combine(driveRoot, folder)));

        foreach (var file in entry.Files)
        {
            var directory = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(directory) && PathUtil.IsUnder(driveRoot, directory) && !roots.Contains(Path.GetFullPath(directory)))
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                    continue;
                }

                break;
            }
        }
    }

    private static IEnumerable<LibraryEntry> ScanRoms(string driveRoot, string romsRoot)
    {
        List<string> files;
        try
        {
            files = SafeFiles(romsRoot);
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (SplitNames.IsJunk(name))
                continue;
            var under = PathUtil.Relative(romsRoot, file);
            var split = under.IndexOfAny(['\\', '/']);
            var category = split < 0 ? "ROMS" : "ROMS\\" + under[..split];
            yield return new LibraryEntry(
                Path.GetFileNameWithoutExtension(file),
                category,
                new FileInfo(file).Length,
                1,
                false,
                PathUtil.Relative(driveRoot, file),
                [file]);
        }
    }

    private static IEnumerable<LibraryEntry> ScanLooseRoms(string driveRoot, List<LibraryEntry> already)
    {
        RomOrganizePlan plan;
        try
        {
            plan = RomOrganizer.Plan(driveRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in already)
        {
            foreach (var file in entry.Files)
                seen.Add(Path.GetFullPath(file));
        }

        var romsRoot = Path.Combine(driveRoot, RomLayout.Root);
        foreach (var move in plan.Moves)
        {
            if (PathUtil.IsUnder(romsRoot, move.Source))
                continue;
            if (!seen.Add(Path.GetFullPath(move.Source)))
                continue;
            if (!File.Exists(move.Source))
                continue;
            yield return new LibraryEntry(
                Path.GetFileNameWithoutExtension(move.Source),
                "Loose ROM",
                new FileInfo(move.Source).Length,
                1,
                false,
                PathUtil.Relative(driveRoot, move.Source),
                [move.Source]);
        }
    }

    private static IEnumerable<LibraryEntry> ScanGames(string driveRoot, string folder, string category)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(folder);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (IsReparse(child))
                continue;
            var files = SafeFiles(child);
            if (files.Count == 0)
                continue;
            long bytes = 0;
            var split = false;
            foreach (var file in files)
            {
                bytes += new FileInfo(file).Length;
                if (SplitNames.TryParse(Path.GetFileName(file), out _, out _, out var scheme) && scheme != SplitScheme.IsoDotNumber)
                    split = true;
            }

            yield return new LibraryEntry(
                Path.GetFileName(child),
                category,
                bytes,
                files.Count,
                split,
                PathUtil.Relative(driveRoot, child),
                files);
        }
    }

    private static IEnumerable<LibraryEntry> ScanLoose(string driveRoot, string folder, string category, bool license)
    {
        var files = SafeFiles(folder);
        var groups = new Dictionary<string, List<(int Order, string Path)>>(StringComparer.OrdinalIgnoreCase);
        var singles = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (SplitNames.IsJunk(name))
                continue;
            if (!license && SplitNames.TryParse(name, out var baseName, out var index, out var scheme))
            {
                var key = Path.GetDirectoryName(file) + "|" + scheme + "|" + baseName;
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }

                list.Add((index, file));
                continue;
            }

            if (license || SplitNames.IsCandidateName(name))
                singles.Add(file);
        }

        foreach (var pair in groups)
        {
            var tail = pair.Key[(pair.Key.IndexOf('|') + 1)..];
            var baseName = tail[(tail.IndexOf('|') + 1)..];
            var ordered = pair.Value.OrderBy(v => v.Order).Select(v => v.Path).ToList();
            long bytes = 0;
            foreach (var file in ordered)
                bytes += new FileInfo(file).Length;
            var title = Path.GetFileNameWithoutExtension(baseName);
            if (title.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || title.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
                title = Path.GetFileNameWithoutExtension(title);
            yield return new LibraryEntry(title, category, bytes, ordered.Count, ordered.Count > 1, PathUtil.Relative(driveRoot, ordered[0]), ordered);
        }

        foreach (var file in singles)
        {
            var title = Path.GetFileNameWithoutExtension(file);
            yield return new LibraryEntry(
                title,
                category,
                new FileInfo(file).Length,
                1,
                false,
                PathUtil.Relative(driveRoot, file),
                [file]);
        }
    }

    private static List<string> SafeFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).Where(path => !IsReparse(path)).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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
