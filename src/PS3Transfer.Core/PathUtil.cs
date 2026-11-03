namespace PS3Transfer.Core;

public static class PathUtil
{
    public static string Win32(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    public static bool IsUnder(string root, string path)
    {
        var r = Trim(Path.GetFullPath(root));
        var p = Trim(Path.GetFullPath(path));
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase))
            return true;
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string Relative(string root, string path)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
