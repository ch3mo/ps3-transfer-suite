using System.Text;

namespace PS3Transfer.Core.Disc;

/// <summary>
/// Reads a small set of files out of an ISO9660 image, including 2352-byte BIN tracks.
/// PS3, PS2, PS1, and PSP headers all live near the start of the image.
/// </summary>
public sealed class IsoVolume : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _owns;
    private readonly int _raw;
    private readonly int _userOffset;
    private readonly DirRoot _primary;
    private readonly DirRoot? _joliet;
    private bool _disposed;

    public string? VolumeLabel { get; }

    private IsoVolume(Stream stream, bool owns, int raw, int userOffset, DirRoot primary, DirRoot? joliet, string? label)
    {
        _stream = stream;
        _owns = owns;
        _raw = raw;
        _userOffset = userOffset;
        _primary = primary;
        _joliet = joliet;
        VolumeLabel = label;
    }

    public static IsoVolume? TryOpen(string path)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return TryOpen(stream, ownsStream: true);
        }
        catch (IOException)
        {
            stream?.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null;
        }
    }

    public static IsoVolume? TryOpen(Stream stream, bool ownsStream = false)
    {
        try
        {
            IsoVolume? volume = null;
            if (stream.CanSeek && stream.Length >= 17L * 2048 && LooksLikePrimary(stream, 2048, 0))
                volume = Open(stream, ownsStream, 2048, 0);
            else if (stream.CanSeek && stream.Length >= 17L * 2352 && LooksLikePrimary(stream, 2352, 16))
                volume = Open(stream, ownsStream, 2352, 16);
            if (volume is null && ownsStream)
                stream.Dispose();
            return volume;
        }
        catch (IOException)
        {
            if (ownsStream)
                stream.Dispose();
            return null;
        }
    }

    public byte[]? ReadSmall(string path, int maxBytes)
    {
        var hit = ReadFrom(_primary, path, maxBytes, joliet: false);
        if (hit is not null)
            return hit;
        return _joliet is { } joliet ? ReadFrom(joliet, path, maxBytes, joliet: true) : null;
    }

    public bool Exists(string path, bool directory)
    {
        if (Find(_primary, path, joliet: false, directory) is not null)
            return true;
        return _joliet is { } joliet && Find(joliet, path, joliet: true, directory) is not null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_owns)
            _stream.Dispose();
    }

    private static IsoVolume? Open(Stream stream, bool owns, int raw, int userOffset)
    {
        try
        {
            var scratch = new IsoVolume(stream, owns: false, raw, userOffset, default, null, null);
            DirRoot? primary = null;
            DirRoot? joliet = null;
            string? label = null;
            for (var sector = 16; sector < 48; sector++)
            {
                var desc = scratch.ReadUser(sector * 2048L, 2048);
                if (desc is null || desc.Length < 190)
                    break;
                if (desc[1] != (byte)'C' || desc[2] != (byte)'D' || desc[3] != (byte)'0' || desc[4] != (byte)'0' || desc[5] != (byte)'1')
                    break;
                if (desc[0] == 255)
                    break;
                if (desc[0] == 1 && primary is null)
                {
                    label = Encoding.ASCII.GetString(desc, 40, 32).Trim('\0', ' ');
                    if (label.Length == 0)
                        label = null;
                    primary = ReadRoot(desc);
                }
                else if (desc[0] == 2 && joliet is null && IsJoliet(desc))
                {
                    joliet = ReadRoot(desc);
                }
            }

            if (primary is null)
                return null;

            return new IsoVolume(stream, owns, raw, userOffset, primary.Value, joliet, label);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool LooksLikePrimary(Stream stream, int raw, int userOffset)
    {
        var pos = 16L * raw + userOffset;
        if (pos + 6 > stream.Length)
            return false;
        stream.Position = pos;
        Span<byte> id = stackalloc byte[6];
        return stream.Read(id) == 6
            && id[0] == 1
            && id[1] == (byte)'C'
            && id[2] == (byte)'D'
            && id[3] == (byte)'0'
            && id[4] == (byte)'0'
            && id[5] == (byte)'1';
    }

    private static bool IsJoliet(byte[] desc)
    {
        var esc = Encoding.ASCII.GetString(desc, 88, 32);
        return esc.Contains("%/@", StringComparison.Ordinal)
            || esc.Contains("%/C", StringComparison.Ordinal)
            || esc.Contains("%/E", StringComparison.Ordinal);
    }

    private static DirRoot ReadRoot(byte[] desc)
    {
        var extent = ReadU32(desc, 156 + 2);
        var size = ReadU32(desc, 156 + 10);
        return new DirRoot(extent, size);
    }

    private byte[]? ReadFrom(DirRoot root, string path, int maxBytes, bool joliet)
    {
        var entry = Find(root, path, joliet, directory: false);
        if (entry is null || entry.Value.IsDirectory)
            return null;
        var take = (int)Math.Min(entry.Value.Size, maxBytes);
        if (take <= 0)
            return [];
        return ReadUser(entry.Value.Extent * 2048L, take);
    }

    private DirEntry? Find(DirRoot root, string path, bool joliet, bool directory)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return null;

        var extent = root.Extent;
        var size = root.Size;
        for (var i = 0; i < parts.Length; i++)
        {
            var entries = ReadDirectory(extent, size, joliet);
            if (entries is null)
                return null;
            DirEntry? match = null;
            foreach (var entry in entries)
            {
                if (string.Equals(Normalize(entry.Name), parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = entry;
                    break;
                }
            }

            if (match is null)
                return null;
            var last = i == parts.Length - 1;
            if (last)
                return directory == match.Value.IsDirectory || !directory && !match.Value.IsDirectory ? match : null;
            if (!match.Value.IsDirectory)
                return null;
            extent = match.Value.Extent;
            size = match.Value.Size;
        }

        return null;
    }

    private List<DirEntry>? ReadDirectory(uint extent, uint size, bool joliet)
    {
        if (size == 0 || size > 2 * 1024 * 1024)
            return null;
        var data = ReadUser(extent * 2048L, (int)size);
        if (data is null)
            return null;

        var list = new List<DirEntry>();
        var offset = 0;
        while (offset + 33 < data.Length)
        {
            var len = data[offset];
            if (len == 0)
            {
                offset = ((offset / 2048) + 1) * 2048;
                continue;
            }

            if (len < 34 || offset + len > data.Length)
                break;

            var nameLen = data[offset + 32];
            if (33 + nameLen > len)
                break;
            if (nameLen == 1 && (data[offset + 33] == 0 || data[offset + 33] == 1))
            {
                offset += len;
                continue;
            }

            var identLen = joliet ? nameLen - (nameLen & 1) : nameLen;
            var name = joliet
                ? Encoding.BigEndianUnicode.GetString(data, offset + 33, identLen)
                : Encoding.ASCII.GetString(data, offset + 33, identLen);
            var flags = data[offset + 25];
            list.Add(new DirEntry(
                Normalize(name),
                ReadU32(data, offset + 2),
                ReadU32(data, offset + 10),
                (flags & 0x02) != 0));
            offset += len;
        }

        return list;
    }

    private byte[]? ReadUser(long userPos, int count)
    {
        if (count < 0 || userPos < 0)
            return null;
        var dest = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            var logical = userPos + filled;
            var logicalSector = logical / 2048;
            var within = (int)(logical % 2048);
            var physical = logicalSector * _raw + _userOffset + within;
            var chunk = Math.Min(2048 - within, count - filled);
            if (physical < 0 || physical + chunk > _stream.Length)
                return null;
            _stream.Position = physical;
            var got = 0;
            while (got < chunk)
            {
                var n = _stream.Read(dest, filled + got, chunk - got);
                if (n == 0)
                    return null;
                got += n;
            }

            filled += chunk;
        }

        return dest;
    }

    private static string Normalize(string name)
    {
        var semi = name.LastIndexOf(';');
        if (semi > 0 && semi < name.Length - 1 && IsAllDigits(name.AsSpan(semi + 1)))
            name = name[..semi];
        return name.Trim();
    }

    private static bool IsAllDigits(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static uint ReadU32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private readonly record struct DirRoot(uint Extent, uint Size);

    private readonly record struct DirEntry(string Name, uint Extent, uint Size, bool IsDirectory);
}
