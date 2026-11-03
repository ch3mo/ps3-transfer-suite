using System.Collections.Concurrent;
using PS3Transfer.App.Mvvm;
using PS3Transfer.Core;

namespace PS3Transfer.App.Services;

public enum DriveVerdict
{
    Ready = 0,
    Caution = 1,
    Blocked = 2,
}

public sealed class DriveRow : ObservableObject
{
    private bool _selected;

    public required string Root { get; init; }
    public required string Title { get; init; }
    public required string Line { get; init; }
    public required string FileSystem { get; init; }
    public required long FreeBytes { get; init; }
    public required long TotalBytes { get; init; }
    public required uint Serial { get; init; }
    public required DriveVerdict Verdict { get; init; }
    public required string Badge { get; init; }
    public required string Reason { get; init; }

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

public readonly record struct DriveScan(IReadOnlyList<DriveRow> Rows, bool Again);

public static class DriveService
{
    private static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<string, byte> Probing = new(StringComparer.OrdinalIgnoreCase);

    public static DriveScan List()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        DriveInfo[] found;
        try
        {
            found = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write(ex.Message);
            return new DriveScan([], true);
        }

        var probes = new List<Task<ProbeHit>>();
        foreach (var drive in found)
        {
            if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory)
                continue;
            var captured = drive;
            probes.Add(Task.Run(() => Probe(captured, system)));
        }

        if (probes.Count == 0)
            return new DriveScan([], false);

        bool finished;
        try
        {
            finished = Task.WaitAll(probes.ToArray(), ProbeLimit);
        }
        catch (AggregateException ex)
        {
            AppLog.Write(ex.GetBaseException().Message);
            finished = false;
        }
        var rows = new List<DriveRow>();
        var again = !finished;
        foreach (var probe in probes)
        {
            if (!probe.IsCompletedSuccessfully)
            {
                again = true;
                continue;
            }

            if (probe.Result.Retry)
                again = true;
            if (probe.Result.Row is { } row)
                rows.Add(row);
        }

        return new DriveScan(rows, again);
    }

    private readonly record struct ProbeHit(DriveRow? Row, bool Retry);

    private static ProbeHit Probe(DriveInfo drive, string systemRoot)
    {
        var root = drive.Name;
        if (!Probing.TryAdd(root, 0))
            return new ProbeHit(null, true);

        try
        {
            if (!drive.IsReady)
                return new ProbeHit(null, false);
            return new ProbeHit(Describe(drive, systemRoot), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Write(root + " " + ex.Message);
            return new ProbeHit(null, false);
        }
        finally
        {
            Probing.TryRemove(root, out _);
        }
    }

    public static string? Eject(string root) => VolumeApi.Eject(root);

    private static DriveRow Describe(DriveInfo drive, string systemRoot)
    {
        var root = drive.Name;
        var fileSystem = drive.DriveFormat;
        long free = 0;
        long total = 0;
        try
        {
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (IOException)
        {
        }

        var system = string.Equals(Trim(root), Trim(systemRoot), StringComparison.OrdinalIgnoreCase);
        var usb = VolumeApi.IsUsb(root) || drive.DriveType == DriveType.Removable;
        var partition = VolumeApi.PartitionStyle(root);
        var cluster = VolumeApi.ClusterBytes(root);
        var serial = VolumeApi.Serial(root);
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Drive" : drive.VolumeLabel.Trim();
        var reasons = new List<string>();
        DriveVerdict verdict;

        if (system)
        {
            verdict = DriveVerdict.Blocked;
            reasons.Add("This is the Windows drive. Plug in the USB SSD the PS3 will use.");
        }
        else if (fileSystem is not ("FAT32" or "FAT" or "NTFS" or "exFAT"))
        {
            verdict = DriveVerdict.Blocked;
            reasons.Add("The file system is " + fileSystem + ". Use FAT32, or NTFS with prepISO.");
        }
        else
        {
            var ready = usb && Ps3Layout.IsFat(fileSystem) && partition == "MBR" && cluster is > 0 and <= 32768;
            verdict = ready ? DriveVerdict.Ready : DriveVerdict.Caution;
            if (!usb)
                reasons.Add("This disk is not on USB. The PS3 reads a USB drive.");
            if (!Ps3Layout.IsFat(fileSystem))
                reasons.Add(fileSystem + " keeps each package as one file. Run prepISO on the PS3 before webMAN can see it.");
            if (partition == "GPT")
                reasons.Add("The partition table is GPT. The PS3 reads MBR USB drives.");
            else if (partition is "RAW" or "Unknown")
                reasons.Add("The partition style could not be confirmed as MBR. The PS3 expects MBR.");
            if (cluster > 32768)
                reasons.Add("The cluster size is " + ByteSize.Format(cluster) + ". FAT32 clusters of 32 KB or smaller are the reliable match for the PS3.");
            else if (cluster <= 0 && Ps3Layout.IsFat(fileSystem))
                reasons.Add("The cluster size could not be read.");
            if (verdict == DriveVerdict.Ready)
                reasons.Add("USB, FAT32, MBR, " + ByteSize.Format(cluster) + " clusters. Large ISOs are split the way webMAN mounts them. Extracted packages go in \\GAMEI. ROMs go in \\ROMS for RetroArch.");
        }

        var bus = usb ? "USB" : drive.DriveType.ToString();
        return new DriveRow
        {
            Root = root,
            Title = label + " (" + root.TrimEnd('\\') + ")",
            Line = ByteSize.Format(free) + " free · " + fileSystem + " · " + partition + " · " + bus,
            FileSystem = fileSystem,
            FreeBytes = free,
            TotalBytes = total,
            Serial = serial,
            Verdict = verdict,
            Badge = verdict switch
            {
                DriveVerdict.Ready => "Ready",
                DriveVerdict.Caution => "Check",
                _ => "Blocked",
            },
            Reason = string.Join(" ", reasons),
        };
    }

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
