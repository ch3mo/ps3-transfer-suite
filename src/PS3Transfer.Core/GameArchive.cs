using SharpCompress.Archives;
using SharpCompress.Common;

namespace PS3Transfer.Core;

public static class GameArchive
{
    public static bool IsArchive(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".rar", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryExtract(string archivePath, string destination, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(destination);
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var wrote = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory)
                    continue;
                if (string.IsNullOrEmpty(entry.Key))
                    continue;
                using var input = entry.OpenEntryStream();
                if (!TrySafeTarget(destination, entry.Key, out var target))
                {
                    input.CopyTo(Stream.Null);
                    error = "The archive contains a path that leaves its folder.";
                    return false;
                }

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                wrote++;
            }

            if (wrote == 0)
            {
                error = "The archive is empty.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidFormatException or CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = ex is CryptographicException ? "The archive is password protected." : ex.Message;
            return false;
        }
    }

    private static bool TrySafeTarget(string root, string key, out string target)
    {
        target = "";
        var normalized = key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (normalized.Length == 0 || Path.IsPathRooted(normalized))
            return false;
        if (normalized.Contains(':'))
            return false;

        var combined = Path.GetFullPath(Path.Combine(root, normalized));
        if (!PathUtil.IsUnder(root, combined) || string.Equals(Path.GetFullPath(root), combined, StringComparison.OrdinalIgnoreCase))
            return false;
        target = combined;
        return true;
    }
}
