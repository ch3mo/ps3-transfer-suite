using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PS3Transfer.App.Services;

internal static class VolumeApi
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetPartitionInfoEx = 0x00070048;
    private const uint IoctlStorageEjectMedia = 0x002D4808;
    private const uint FsctlGetRetrievalPointers = 0x00090073;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlUnlockVolume = 0x0009001C;
    private const uint FsctlDismountVolume = 0x00090020;
    private const int ErrorMoreData = 234;

    public static bool IsUsb(string root)
    {
        using var handle = Open(root, write: false);
        if (handle.IsInvalid)
            return false;
        var input = new byte[12];
        var output = new byte[1024];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
            return false;
        if (returned < 32)
            return false;
        var size = BitConverter.ToUInt32(output, 4);
        if (size < 32)
            return false;
        return BitConverter.ToInt32(output, 28) == 7;
    }

    public static string PartitionStyle(string root)
    {
        using var handle = Open(root, write: false);
        if (handle.IsInvalid)
            return "Unknown";
        var output = new byte[256];
        if (!DeviceIoControl(handle, IoctlDiskGetPartitionInfoEx, null, 0, output, output.Length, out var returned, IntPtr.Zero) || returned < 4)
            return "Unknown";
        return BitConverter.ToInt32(output, 0) switch
        {
            0 => "MBR",
            1 => "GPT",
            2 => "RAW",
            _ => "Unknown",
        };
    }

    public static int ClusterBytes(string root)
    {
        if (!GetDiskFreeSpace(root, out var sectors, out var bytes, out _, out _))
            return 0;
        try
        {
            return checked((int)(sectors * bytes));
        }
        catch (OverflowException)
        {
            return 0;
        }
    }

    public static uint Serial(string root)
    {
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        return GetVolumeInformation(root, label, label.Capacity, out var serial, out _, out _, fs, fs.Capacity)
            ? serial
            : 0;
    }

    public static int? ExtentCount(string path)
    {
        using var handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        var input = new byte[8];
        var output = new byte[16 + 48 * 16];
        var ok = DeviceIoControl(handle, FsctlGetRetrievalPointers, input, input.Length, output, output.Length, out var returned, IntPtr.Zero);
        var error = Marshal.GetLastWin32Error();
        if (!ok && error != ErrorMoreData)
            return null;
        if (returned < 4)
            return null;
        var count = BitConverter.ToInt32(output, 0);
        return error == ErrorMoreData ? Math.Max(count, 33) : count;
    }

    public static string? Eject(string root)
    {
        using var handle = Open(root, write: true);
        if (handle.IsInvalid)
            return "Windows could not open the drive. Close any window that is using it, then try again.";
        if (!DeviceIoControl(handle, FsctlLockVolume, null, 0, null, 0, out _, IntPtr.Zero))
            return "The drive is in use. Close Explorer windows that show it, then eject again.";
        DeviceIoControl(handle, FsctlDismountVolume, null, 0, null, 0, out _, IntPtr.Zero);
        if (!DeviceIoControl(handle, IoctlStorageEjectMedia, null, 0, null, 0, out _, IntPtr.Zero))
        {
            DeviceIoControl(handle, FsctlUnlockVolume, null, 0, null, 0, out _, IntPtr.Zero);
            return "Windows could not eject the drive.";
        }

        return null;
    }

    private static SafeFileHandle Open(string root, bool write)
    {
        var letter = root.TrimEnd('\\', '/');
        var access = write ? GenericRead | GenericWrite : GenericRead;
        return CreateFile(@"\\.\" + letter, access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
