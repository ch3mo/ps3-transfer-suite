using System.Globalization;

namespace PS3Transfer.Core;

public enum ContentKind
{
    Unknown = 0,
    Ps3Iso,
    Ps2Iso,
    Ps1Disc,
    PspIso,
    Pkg,
    Rap,
    Edat,
    Ps3Folder,
    BdIso,
    DvdIso,
    PkgFolder,
    Rom,
}

public enum SplitScheme
{
    None = 0,
    IsoDotNumber = 1,
    Pkg666 = 2,
    File666 = 3,
    PkgNumber = 4,
}

public enum ExistState
{
    Missing = 0,
    Partial = 1,
    Complete = 2,
    Conflict = 3,
}

public enum TransferAction
{
    Copy = 0,
    Resume = 1,
    Replace = 2,
    Skip = 3,
    Blocked = 4,
}

/// <summary>
/// On-disk layout used by webMAN MOD, multiMAN, and IRISMAN.
/// </summary>
public static class Ps3Layout
{
    /// <summary>
    /// Part size from splitps3iso and PS3 ISO Tools. It is 64 KB aligned and
    /// stays under the FAT32 maximum file size of 4,294,967,295 bytes.
    /// </summary>
    public const long SplitSize = 0xFFFF0000L;

    public static readonly string[] LibraryFolders =
    [
        "PS3ISO", "PS2ISO", "PSXISO", "PSPISO", "PSPMINIS",
        "packages", "PKG", "GAMES", "GAMEZ", "GAMEI", "BDISO", "DVDISO", "exdata", "ROMS",
    ];

    public static bool IsFat(string fileSystem) =>
        fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
        || fileSystem.Equals("FAT", StringComparison.OrdinalIgnoreCase);

    public static bool NeedsSplit(string fileSystem, long length, long splitSize = SplitSize) =>
        IsFat(fileSystem) && length > splitSize;

    public static string CategoryFolder(ContentKind kind, string fileSystem) => kind switch
    {
        ContentKind.Ps3Iso => "PS3ISO",
        ContentKind.Ps2Iso => "PS2ISO",
        ContentKind.Ps1Disc => "PSXISO",
        ContentKind.PspIso => "PSPISO",
        ContentKind.Pkg => IsFat(fileSystem) ? "packages" : "PKG",
        ContentKind.Rap or ContentKind.Edat => "exdata",
        ContentKind.Ps3Folder => "GAMES",
        ContentKind.PkgFolder => "GAMEI",
        ContentKind.BdIso => "BDISO",
        ContentKind.DvdIso => "DVDISO",
        ContentKind.Rom => "ROMS",
        _ => "PS3ISO",
    };

    public static string KindLabel(ContentKind kind) => kind switch
    {
        ContentKind.Ps3Iso => "PS3 ISO",
        ContentKind.Ps2Iso => "PS2 ISO",
        ContentKind.Ps1Disc => "PS1",
        ContentKind.PspIso => "PSP ISO",
        ContentKind.Pkg => "PKG",
        ContentKind.Rap => "RAP",
        ContentKind.Edat => "EDAT",
        ContentKind.Ps3Folder => "PS3 folder",
        ContentKind.PkgFolder => "PKG folder",
        ContentKind.BdIso => "Blu-ray",
        ContentKind.DvdIso => "DVD",
        ContentKind.Rom => "ROM",
        _ => "Unknown",
    };

    public static int PartCount(long length, long splitSize)
    {
        if (length <= 0)
            return 0;
        if (splitSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(splitSize));
        return (int)((length + splitSize - 1) / splitSize);
    }

    public static long PartLength(long length, long splitSize, int index)
    {
        var start = (long)index * splitSize;
        if (start >= length)
            return 0;
        return Math.Min(splitSize, length - start);
    }

    public static string PartPath(string basePath, SplitScheme scheme, int index) => scheme switch
    {
        SplitScheme.None => basePath,
        SplitScheme.IsoDotNumber => basePath + "." + index.ToString(CultureInfo.InvariantCulture),
        SplitScheme.Pkg666 or SplitScheme.File666 => basePath + "." + (66600 + index).ToString("D5", CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unknown split scheme."),
    };

    public static SplitScheme SchemeFor(ContentKind kind, bool insideTree)
    {
        if (insideTree)
            return SplitScheme.File666;
        return kind switch
        {
            ContentKind.Pkg => SplitScheme.Pkg666,
            ContentKind.Ps3Iso or ContentKind.Ps2Iso or ContentKind.Ps1Disc
                or ContentKind.PspIso or ContentKind.BdIso or ContentKind.DvdIso => SplitScheme.IsoDotNumber,
            _ => SplitScheme.File666,
        };
    }

    public static IReadOnlyList<(string Path, long Length)> ExpectedParts(
        string basePath, SplitScheme scheme, long length, long splitSize)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (scheme == SplitScheme.None)
            return [(basePath, length)];

        var count = PartCount(length, splitSize);
        var list = new (string Path, long Length)[count];
        for (var i = 0; i < count; i++)
            list[i] = (PartPath(basePath, scheme, i), PartLength(length, splitSize, i));
        return list;
    }
}
