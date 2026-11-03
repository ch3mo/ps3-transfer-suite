using System.Globalization;

namespace PS3Transfer.Core;

public sealed record PkgPartSet(
    string Directory,
    string BaseName,
    IReadOnlyList<string> Files,
    bool Complete,
    string? Note);

/// <summary>
/// Split packages named like nhl15_1.pkg, nhl15_2.pkg. Package Manager installs one file,
/// so a complete set is joined before it is stored for the PS3.
/// </summary>
public static class PkgParts
{
    public static bool TryParse(string fileName, out string baseName, out int index)
    {
        baseName = "";
        index = 0;
        if (!fileName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) || fileName.Length < 6)
            return false;

        var stem = fileName[..^4];
        var split = stem.LastIndexOf('_');
        if (split <= 0 || split >= stem.Length - 1)
            return false;

        var suffix = stem[(split + 1)..];
        if (suffix.Length is < 1 or > 3)
            return false;
        foreach (var c in suffix)
        {
            if (c is < '0' or > '9')
                return false;
        }

        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index is < 1 or > 999)
            return false;

        var head = stem[..split].Trim();
        if (head.Length == 0)
            return false;

        baseName = head + ".pkg";
        return true;
    }

    public static IReadOnlyList<PkgPartSet> Group(IEnumerable<string> files)
    {
        var byKey = new Dictionary<string, List<(int Index, string Path)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!TryParse(name, out var baseName, out var index))
                continue;
            var directory = Path.GetDirectoryName(file) ?? "";
            var key = directory + "|" + baseName;
            if (!byKey.TryGetValue(key, out var list))
            {
                list = [];
                byKey[key] = list;
            }

            list.Add((index, file));
        }

        var sets = new List<PkgPartSet>();
        foreach (var pair in byKey)
        {
            if (pair.Value.Count < 2)
                continue;

            pair.Value.Sort((a, b) =>
            {
                var byIndex = a.Index.CompareTo(b.Index);
                return byIndex != 0 ? byIndex : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });

            var separator = pair.Key.IndexOf('|');
            var directory = pair.Key[..separator];
            var baseName = pair.Key[(separator + 1)..];
            var ordered = pair.Value.Select(item => item.Path).ToList();
            string? note = null;
            var complete = true;
            for (var i = 1; i < pair.Value.Count; i++)
            {
                if (pair.Value[i].Index == pair.Value[i - 1].Index)
                {
                    note = baseName + " has two pieces numbered _" + pair.Value[i].Index.ToString(CultureInfo.InvariantCulture) + ".";
                    complete = false;
                    break;
                }
            }

            if (complete && pair.Value[0].Index != 1)
            {
                note = baseName + " is missing part 1.";
                complete = false;
            }

            if (complete)
            {
                for (var i = 1; i < pair.Value.Count; i++)
                {
                    if (pair.Value[i].Index != pair.Value[i - 1].Index + 1)
                    {
                        note = baseName + " is missing part " + (pair.Value[i - 1].Index + 1).ToString(CultureInfo.InvariantCulture) + ".";
                        complete = false;
                        break;
                    }
                }
            }

            sets.Add(new PkgPartSet(directory, baseName, ordered, complete, note));
        }

        return sets;
    }

    public static bool TryCollect(string anyPart, out IReadOnlyList<string> parts, out string? error)
    {
        parts = [];
        var name = Path.GetFileName(anyPart);
        if (!TryParse(name, out var baseName, out _))
        {
            error = "Choose the first split piece: a .iso.0 file, a .pkg.1 file, a _1.pkg file, or a .66600 file.";
            return false;
        }

        var directory = Path.GetDirectoryName(anyPart);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            error = "The folder for that split set is missing.";
            return false;
        }

        List<string> found;
        try
        {
            found = Directory.EnumerateFiles(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }

        PkgPartSet? set = null;
        foreach (var candidate in Group(found))
        {
            if (!candidate.BaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(candidate.Directory, directory, StringComparison.OrdinalIgnoreCase))
                continue;
            set = candidate;
            break;
        }

        if (set is null)
        {
            error = "No other pieces of " + baseName + " were found in that folder.";
            return false;
        }

        if (!set.Complete)
        {
            error = set.Note ?? "The set is incomplete.";
            return false;
        }

        parts = set.Files;
        error = null;
        return true;
    }
}
