using System.Text.RegularExpressions;

namespace PS3Transfer.Core.Disc;

public static partial class CueSheet
{
    public static IReadOnlyList<string> ReferencedFiles(string cueText)
    {
        var list = new List<string>();
        foreach (var raw in cueText.Split('\n'))
        {
            var match = FileRegex().Match(raw);
            if (!match.Success)
                continue;
            var name = match.Groups[1].Success && match.Groups[1].Length > 0
                ? match.Groups[1].Value
                : match.Groups[2].Value;
            if (name.Length > 0)
                list.Add(name);
        }

        return list;
    }

    [GeneratedRegex("^\\s*FILE\\s+(?:\"([^\"]+)\"|(\\S+))\\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileRegex();
}
