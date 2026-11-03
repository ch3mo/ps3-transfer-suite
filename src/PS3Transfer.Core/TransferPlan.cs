namespace PS3Transfer.Core;

public sealed class PlannedCopy
{
    public required int Index { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public required string DestinationBase { get; init; }
    public required SplitScheme Scheme { get; init; }
    public required long Length { get; init; }
    public required ExistState Existing { get; init; }
    public required long FullEnd { get; init; }
}

public sealed class TransferPlan
{
    public required TransferAction Action { get; init; }
    public required ExistState Disk { get; init; }
    public required string DisplayPath { get; init; }
    public required string PlayTitle { get; init; }
    public required string PlayBody { get; init; }
    public string? Warning { get; init; }
    public required long TotalBytes { get; init; }
    public required long AlreadyOnDrive { get; init; }
    public required int PartCount { get; init; }
    public required bool Splits { get; init; }
    public required bool FatDestination { get; init; }
    public required long SplitSize { get; init; }
    public required long SourceStamp { get; init; }
    public required string Signature { get; init; }
    public required string ManifestKey { get; init; }
    public required string DriveRoot { get; init; }
    public required IReadOnlyList<string> SourceFiles { get; init; }
    public required IReadOnlyList<PlannedCopy> Copies { get; init; }
    public required IReadOnlyList<string> OutputPaths { get; init; }

    public long BytesToWrite => Action switch
    {
        TransferAction.Copy or TransferAction.Replace => TotalBytes,
        TransferAction.Resume => Math.Max(0, TotalBytes - AlreadyOnDrive),
        _ => 0,
    };

    public string StatusLine => Action switch
    {
        TransferAction.Copy => Splits ? "Ready to copy · split for FAT32" : "Ready to copy",
        TransferAction.Resume => "Ready to resume",
        TransferAction.Replace => "Will replace the files on the drive",
        TransferAction.Skip when Disk == ExistState.Complete => "Already on the drive",
        TransferAction.Skip => "Skipped · a different copy already uses this name",
        TransferAction.Blocked => Warning ?? "Cannot copy",
        _ => "",
    };
}

public sealed class PlanRequest
{
    public required InspectedSource Source { get; init; }
    public required string DriveRoot { get; init; }
    public required string FileSystem { get; init; }
    public uint VolumeSerial { get; init; }
    public bool UseTitleNames { get; init; } = true;
    public long SplitSize { get; init; } = Ps3Layout.SplitSize;
    public ContentKind? ForcedKind { get; init; }
    public string? RomFolder { get; init; }
    public bool Replace { get; init; }
    public HashSet<string>? Reserved { get; init; }
}
