using System.Text.RegularExpressions;

namespace PS3Transfer.Core.Disc;

public readonly record struct SystemCnfInfo(bool IsPs2, string? TitleId);

public static partial class SystemCnf
{
    public static SystemCnfInfo Parse(string text)
    {
        var ps2 = Boot2Regex().IsMatch(text);
        var match = IdRegex().Match(text);
        string? id = null;
        if (match.Success)
            id = match.Groups[1].Value.ToUpperInvariant() + "-" + match.Groups[2].Value + match.Groups[3].Value;
        return new SystemCnfInfo(ps2, id);
    }

    [GeneratedRegex(@"(?im)^\s*BOOT2\s*=")]
    private static partial Regex Boot2Regex();

    [GeneratedRegex(@"(?im)^\s*BOOT2?\s*=.*?([A-Za-z]{4})[_-](\d{3})\.(\d{2})")]
    private static partial Regex IdRegex();
}
