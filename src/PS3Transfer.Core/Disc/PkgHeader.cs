using System.Text;
using System.Text.RegularExpressions;

namespace PS3Transfer.Core.Disc;

public static partial class PkgHeader
{
    public static bool IsPkg(ReadOnlySpan<byte> header) =>
        header.Length >= 4
        && header[0] == 0x7F
        && header[1] == (byte)'P'
        && header[2] == (byte)'K'
        && header[3] == (byte)'G';

    public static bool TryRead(ReadOnlySpan<byte> header, out string? contentId, out string? titleId)
    {
        contentId = null;
        titleId = null;
        if (!IsPkg(header) || header.Length < 0x60)
            return false;

        var take = Math.Min(0x30, header.Length - 0x30);
        if (take <= 0)
            return true;

        var raw = Encoding.ASCII.GetString(header.Slice(0x30, take)).Trim('\0', ' ');
        if (raw.Length == 0)
            return true;

        contentId = raw;
        var match = TitleIdRegex().Match(raw);
        if (match.Success)
            titleId = match.Groups[1].Value.ToUpperInvariant();
        return true;
    }

    [GeneratedRegex(@"^[A-Z0-9]{2,6}-([A-Z]{4}[0-9]{5})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleIdRegex();
}
