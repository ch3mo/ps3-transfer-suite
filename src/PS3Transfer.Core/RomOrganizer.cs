namespace PS3Transfer.Core;

public sealed record RomMove(string Source, string Destination, string Folder);

public sealed class RomOrganizePlan
{
    public required IReadOnlyList<RomMove> Moves { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Moves loose ROM files into \ROMS\&lt;system&gt;. It does not format the drive or touch PS3 game folders.
/// </summary>
public static class RomOrganizer
{
    private static readonly HashSet<string> SkipAtRoot = new(Ps3Layout.LibraryFolders.Where(name => !name.Equals(RomLayout.Root, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);

    public static RomOrganizePlan Plan(string driveRoot)
    {
        var moves = new List<RomMove>();
        var notes = new List<string>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(driveRoot);
        Walk(root, root, moves, notes, reserved);
        return new RomOrganizePlan { Moves = moves, Notes = notes };
    }

    public static int Apply(string driveRoot, IReadOnlyList<RomMove> moves)
    {
        var moved = 0;
        foreach (var move in moves)
        {
            if (!File.Exists(move.Source))
                continue;
            if (!PathUtil.IsUnder(driveRoot, move.Source) || !PathUtil.IsUnder(driveRoot, move.Destination))
                throw new InvalidOperationException("Refusing to move a ROM outside the selected drive.");
            if (File.Exists(move.Destination))
                continue;

            var parent = Path.GetDirectoryName(move.Destination);
            if (string.IsNullOrEmpty(parent))
                continue;
            Directory.CreateDirectory(parent);
            var attributes = File.GetAttributes(move.Source);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(move.Source, FileAttributes.Normal);
            var fromDir = Path.GetDirectoryName(move.Source);
            File.Move(move.Source, move.Destination);
            moved++;
            if (!string.IsNullOrEmpty(fromDir))
                TrimEmpty(driveRoot, fromDir);
        }

        return moved;
    }

    private static void Walk(string driveRoot, string directory, List<RomMove> moves, List<string> notes, HashSet<string> reserved)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (IsReparse(child))
                continue;
            var name = Path.GetFileName(child);
            if (SplitNames.IsJunk(name) || name.StartsWith('.'))
                continue;
            if (name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Directory.Exists(child))
            {
                if (directory.Equals(driveRoot, StringComparison.OrdinalIgnoreCase) && SkipAtRoot.Contains(name))
                    continue;
                Walk(driveRoot, child, moves, notes, reserved);
                continue;
            }

            if (!RomLayout.IsRomName(name))
                continue;
            if (!RomLayout.TryClassify(name, directory, child, out var hit))
                continue;

            var safe = FatName.Keep(name);
            if (!FatName.IsSafeFileName(safe))
            {
                notes.Add(name + " has a name FAT32 cannot store.");
                continue;
            }

            var destination = Path.Combine(driveRoot, RomLayout.Root, hit.Folder, safe);
            if (Path.GetFullPath(child).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!reserved.Add(destination))
            {
                notes.Add(name + " would land on the same name as another ROM in \\ROMS\\" + hit.Folder + ".");
                continue;
            }

            if (File.Exists(destination))
            {
                var same = new FileInfo(destination).Length == new FileInfo(child).Length;
                notes.Add(same
                    ? name + " is already in \\ROMS\\" + hit.Folder + "."
                    : name + " conflicts with a different file in \\ROMS\\" + hit.Folder + ".");
                continue;
            }

            moves.Add(new RomMove(child, destination, hit.Folder));
        }
    }

    private static void TrimEmpty(string driveRoot, string start)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(driveRoot),
            Path.GetFullPath(Path.Combine(driveRoot, RomLayout.Root)),
        };
        foreach (var folder in Ps3Layout.LibraryFolders)
            stop.Add(Path.GetFullPath(Path.Combine(driveRoot, folder)));

        var directory = start;
        while (!string.IsNullOrEmpty(directory)
               && PathUtil.IsUnder(driveRoot, directory)
               && !stop.Contains(Path.GetFullPath(directory)))
        {
            try
            {
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                    break;
                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
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
