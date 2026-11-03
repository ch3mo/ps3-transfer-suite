using System.Globalization;

namespace PS3Transfer.Core;

public static class SplitNames
{
    public static bool TryParse(string fileName, out string baseName, out int index, out SplitScheme scheme)
    {
        baseName = "";
        index = 0;
        scheme = SplitScheme.None;
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot >= fileName.Length - 1)
            return false;

        var suffix = fileName[(dot + 1)..];
        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out index))
            return false;

        baseName = fileName[..dot];
        var canonical = suffix == index.ToString(CultureInfo.InvariantCulture);
        if (!canonical)
        {
            if (suffix.Length == 3
                && index is >= 0 and <= 999
                && baseName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            {
                scheme = SplitScheme.PkgNumber;
                return true;
            }

            return false;
        }

        if (suffix.Length == 5 && index is >= 66600 and <= 66699)
        {
            scheme = baseName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)
                ? SplitScheme.Pkg666
                : SplitScheme.File666;
            return true;
        }

        if (index is >= 0 and <= 999 && IsDiscBase(baseName))
        {
            scheme = SplitScheme.IsoDotNumber;
            return true;
        }

        if (index is >= 0 and <= 999
            && suffix.Length <= 3
            && baseName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            scheme = SplitScheme.PkgNumber;
            return true;
        }

        return false;
    }

    public static bool IsRelatedName(string fileName, string baseName)
    {
        if (fileName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = baseName + ".";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var suffix = fileName[prefix.Length..];
        if (suffix.Length == 0)
            return false;
        foreach (var c in suffix)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }

    public static bool IsDiscBase(string baseName)
    {
        var ext = Path.GetExtension(baseName);
        return ext.Equals(".iso", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdf", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsJunk(string fileName)
    {
        if (fileName.StartsWith("._", StringComparison.Ordinal))
            return true;
        return fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ehthumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCandidateName(string fileName)
    {
        if (IsJunk(fileName))
            return false;
        if (TryParse(fileName, out _, out _, out _))
            return true;
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".iso", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".rap", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".edat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cue", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mds", StringComparison.OrdinalIgnoreCase)
            || RomLayout.IsRomName(fileName);
    }
}
