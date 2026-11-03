using System.Text;

namespace PS3Transfer.Core.Disc;

public static class ParamSfo
{
    public static Dictionary<string, string> Read(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.Length < 20 || data[0] != 0x00 || data[1] != (byte)'P' || data[2] != (byte)'S' || data[3] != (byte)'F')
            return result;

        var keyStart = ReadI32(data, 8);
        var dataStart = ReadI32(data, 12);
        var count = ReadI32(data, 16);
        if (keyStart < 20 || dataStart < keyStart || count < 0 || count > 4096)
            return result;

        for (var i = 0; i < count; i++)
        {
            var entry = 20 + i * 16;
            if (entry + 16 > data.Length)
                break;

            var keyOff = ReadU16(data, entry);
            var format = ReadU16(data, entry + 2);
            var len = ReadI32(data, entry + 4);
            var dataOff = ReadI32(data, entry + 12);
            if (len < 0 || dataOff < 0)
                continue;

            var key = ReadCString(data, keyStart + keyOff, dataStart);
            if (key.Length == 0)
                continue;

            if ((format == 0x0004 || format == 0x0204) && dataStart + dataOff + len <= data.Length)
                result[key] = ReadUtf8(data.Slice(dataStart + dataOff, len));
            else if (format == 0x0404 && len >= 4 && dataStart + dataOff + 4 <= data.Length)
                result[key] = ReadI32(data, dataStart + dataOff).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static string ReadUtf8(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        while (end > 0 && (bytes[end - 1] == 0 || bytes[end - 1] == (byte)'\n' || bytes[end - 1] == (byte)'\r'))
            end--;
        return Encoding.UTF8.GetString(bytes[..end]);
    }

    private static string ReadCString(ReadOnlySpan<byte> data, int offset, int limit)
    {
        if (offset < 0 || offset >= data.Length)
            return "";
        var end = offset;
        var stop = Math.Min(data.Length, Math.Max(limit, offset));
        while (end < stop && data[end] != 0)
            end++;
        return Encoding.UTF8.GetString(data.Slice(offset, end - offset));
    }

    private static int ReadI32(ReadOnlySpan<byte> data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static int ReadU16(ReadOnlySpan<byte> data, int offset) =>
        data[offset] | (data[offset + 1] << 8);
}
