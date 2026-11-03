namespace PS3Transfer.Core;

public static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>
    /// Continues a CRC. Append(Append(0, a), b) equals the CRC of a followed by b.
    /// </summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }
}
