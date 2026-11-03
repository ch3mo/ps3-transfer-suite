using System.Globalization;
using System.Text;

namespace PS3Transfer.Core;

/// <summary>
/// FAT32 and webMAN both reject parts of a normal Windows file name.
/// Names are kept to printable ASCII, which is what PS3 ISO Tools does for the same reason.
/// </summary>
public static class FatName
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value.Trim())
        {
            var use = c;
            if (c < 32 || c > 126 || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                use = ' ';
            if (use == ' ')
            {
                if (space || sb.Length == 0)
                    continue;
                space = true;
                sb.Append(' ');
                continue;
            }

            space = false;
            sb.Append(use);
        }

        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '.'))
            sb.Length--;
        return sb.ToString();
    }

    public static string File(string? title, string? titleId, string extension, int maxStem = 120)
    {
        var stem = Sanitize(title);
        if (stem.Length == 0)
            stem = "Game";
        stem = WithTitleId(stem, titleId);
        stem = Fit(stem, maxStem);
        stem = AvoidReserved(stem);
        return stem + NormalizeExtension(extension);
    }

    public static string Folder(string? title, string? titleId, int maxStem = 80)
    {
        var stem = Sanitize(title);
        if (stem.Length == 0)
            stem = "Game";
        stem = WithTitleId(stem, titleId);
        stem = Fit(stem, maxStem);
        return AvoidReserved(stem);
    }

    public static string Keep(string originalFileName, int maxStem = 120)
    {
        var ext = NormalizeExtension(Path.GetExtension(originalFileName));
        var stem = Sanitize(Path.GetFileNameWithoutExtension(originalFileName));
        if (stem.Length == 0)
            stem = "Game";
        stem = Fit(stem, maxStem);
        stem = AvoidReserved(stem);
        return stem + ext;
    }

    public static bool IsSafeFileName(string name)
    {
        if (name.Length == 0 || name != name.Trim())
            return false;
        if (name.EndsWith('.') || name.EndsWith(' '))
            return false;
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return false;
        foreach (var c in name)
        {
            if (c < 32 || c > 126)
                return false;
        }

        return !Reserved.Contains(Path.GetFileNameWithoutExtension(name));
    }

    private static string WithTitleId(string stem, string? titleId)
    {
        var id = Sanitize(titleId).Replace(" ", "", StringComparison.Ordinal);
        if (id.Length == 0)
            return stem;
        var tag = "[" + id + "]";
        if (stem.Contains(tag, StringComparison.OrdinalIgnoreCase))
            return stem;
        return stem + " " + tag;
    }

    private static string Fit(string stem, int max)
    {
        if (stem.Length <= max)
            return stem;
        var cut = stem[..max].TrimEnd(' ', '.');
        return cut.Length == 0 ? "Game" : cut;
    }

    private static string AvoidReserved(string stem)
    {
        var dot = stem.IndexOf('.');
        var head = dot < 0 ? stem : stem[..dot];
        return Reserved.Contains(head) ? stem + "_" : stem;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "";
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        return extension.ToLower(CultureInfo.InvariantCulture);
    }
}
