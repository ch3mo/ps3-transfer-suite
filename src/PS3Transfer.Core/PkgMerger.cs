namespace PS3Transfer.Core;

public sealed record PkgMergePlan(
    string Title,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Outputs,
    long Bytes,
    bool OneFile,
    string Summary);

public static class PkgMerger
{
    public static bool TryPlan(string driveRoot, string fileSystem, IReadOnlyList<string> files, out PkgMergePlan? plan, out string? error)
    {
        plan = null;
        if (files.Count < 2)
        {
            error = "A merge needs at least two pieces.";
            return false;
        }

        string? directory = null;
        string? baseName = null;
        var ordered = new List<(int Index, string Path)>();
        long bytes = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!PkgParts.TryParse(name, out var parsedBase, out var index))
            {
                error = name + " is not a numbered piece like nhl15_1.pkg.";
                return false;
            }

            if (baseName is null)
                baseName = parsedBase;
            else if (!baseName.Equals(parsedBase, StringComparison.OrdinalIgnoreCase))
            {
                error = "Those files are not pieces of one package.";
                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(file);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                error = ex.Message;
                return false;
            }

            if (!PathUtil.IsUnder(driveRoot, full))
            {
                error = "Refusing to merge a file outside the selected drive.";
                return false;
            }

            var dir = Path.GetDirectoryName(full) ?? "";
            if (directory is null)
                directory = dir;
            else if (!directory.Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                error = "The pieces are not in the same folder.";
                return false;
            }

            if (!File.Exists(full))
            {
                error = "Missing " + name + ".";
                return false;
            }

            bytes += new FileInfo(full).Length;
            ordered.Add((index, full));
        }

        ordered.Sort((a, b) =>
        {
            var byIndex = a.Index.CompareTo(b.Index);
            return byIndex != 0 ? byIndex : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Index == ordered[i - 1].Index)
            {
                error = baseName + " has two pieces numbered _" + ordered[i].Index + ".";
                return false;
            }
        }

        if (ordered[0].Index != 1)
        {
            error = baseName + " is missing part 1.";
            return false;
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Index != ordered[i - 1].Index + 1)
            {
                error = baseName + " is missing part " + (ordered[i - 1].Index + 1) + ".";
                return false;
            }
        }

        if (bytes <= 0 || baseName is null)
        {
            error = "The package is empty.";
            return false;
        }

        var sources = ordered.Select(item => item.Path).ToList();
        plan = Layout(driveRoot, fileSystem, baseName, sources, bytes);
        foreach (var output in plan.Outputs)
        {
            if (!PathUtil.IsUnder(driveRoot, output))
            {
                plan = null;
                error = "The merged package would fall outside the selected drive.";
                return false;
            }

            var outputFull = Path.GetFullPath(output);
            foreach (var source in sources)
            {
                if (outputFull.Equals(source, StringComparison.OrdinalIgnoreCase))
                {
                    plan = null;
                    error = "The merged name would replace one of the pieces.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    public static PkgMergePlan Layout(string driveRoot, string fileSystem, string baseName, IReadOnlyList<string> sources, long bytes)
    {
        var title = Path.GetFileNameWithoutExtension(baseName);
        var safeFile = FatName.Keep(baseName);
        var category = Ps3Layout.CategoryFolder(ContentKind.Pkg, fileSystem);
        var categoryPath = Path.Combine(driveRoot, category);
        var oneFile = !Ps3Layout.NeedsSplit(fileSystem, bytes);
        IReadOnlyList<string> outputs;
        if (oneFile)
        {
            outputs = [Path.Combine(categoryPath, safeFile)];
        }
        else
        {
            var folder = Path.Combine(categoryPath, FatName.Folder(title, null));
            var destinationBase = Path.Combine(folder, safeFile);
            var count = Ps3Layout.PartCount(bytes, Ps3Layout.SplitSize);
            var split = new string[count];
            for (var i = 0; i < count; i++)
                split[i] = Ps3Layout.PartPath(destinationBase, SplitScheme.Pkg666, i);
            outputs = split;
        }

        return new PkgMergePlan(title, sources, outputs, bytes, oneFile, Describe(driveRoot, title, sources, outputs, bytes, oneFile));
    }

    public static async Task<TransferResult> ApplyAsync(
        string driveRoot,
        PkgMergePlan plan,
        string fileSystem,
        bool verify,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var path in plan.Sources.Concat(plan.Outputs))
        {
            if (!PathUtil.IsUnder(driveRoot, path))
                return new TransferResult(TransferOutcome.Failed, 0, "Refusing to change a file outside the selected drive.", null);
        }

        var result = plan.OneFile
            ? await FileJoiner.JoinAsync(plan.Sources, plan.Outputs[0], fileSystem, verify, progress, cancellationToken).ConfigureAwait(false)
            : await FileJoiner.JoinToSplitAsync(plan.Sources, plan.Outputs, Ps3Layout.SplitSize, verify, progress, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != TransferOutcome.Copied)
            return result;

        var keep = new HashSet<string>(plan.Outputs.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var remove = plan.Sources.Where(source => !keep.Contains(Path.GetFullPath(source))).ToList();
        if (remove.Count == 0)
            return result;

        try
        {
            LibraryCatalog.Delete(driveRoot, new LibraryEntry(plan.Title, "", plan.Bytes, remove.Count, true, "", remove));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return result with { Warning = "The package was written, and the old pieces could not be deleted. " + ex.Message };
        }

        return result;
    }

    private static string Describe(
        string driveRoot,
        string title,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> outputs,
        long bytes,
        bool oneFile)
    {
        var names = new List<string>();
        foreach (var source in sources)
        {
            var name = Path.GetFileName(source);
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        var list = names.Count <= 4
            ? string.Join(", ", names)
            : names[0] + " through " + names[^1];
        var size = ByteSize.Format(bytes);
        var pieces = sources.Count == 1 ? "1 piece" : sources.Count + " pieces";
        var lead = title + " is " + size + " in " + pieces + " (" + list + "). ";
        if (oneFile)
        {
            return lead
                + "Package Manager installs one package file.\n\nMerge writes "
                + Show(driveRoot, outputs[0])
                + " and then deletes those pieces. The drive needs "
                + size
                + " free while the new file is written.";
        }

        var folder = Path.GetDirectoryName(outputs[0]) ?? outputs[0];
        return lead
            + "FAT32 cannot store that as a single file.\n\nMerge writes "
            + Show(driveRoot, outputs[0])
            + " and the rest of the parts in "
            + Show(driveRoot, folder)
            + ". multiMAN joins those onto the internal drive, and Package Manager installs the game there. The pieces are deleted after the new files check out. The drive needs "
            + size
            + " free while they are written.";
    }

    private static string Show(string driveRoot, string path)
    {
        var relative = PathUtil.Relative(driveRoot, path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return "\\" + relative;
    }
}
