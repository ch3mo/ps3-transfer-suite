using System.Text;

namespace PS3Transfer.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ps3suite-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public SyncProgress(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}

internal static class SfoBuilder
{
    public static byte[] Build(params (string Key, string Value)[] entries)
    {
        var keys = new List<byte>();
        var data = new List<byte>();
        var index = new List<byte>();
        foreach (var (key, value) in entries)
        {
            var keyOff = (ushort)keys.Count;
            keys.AddRange(Encoding.UTF8.GetBytes(key));
            keys.Add(0);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            var dataOff = data.Count;
            data.AddRange(valueBytes);
            data.Add(0);
            var len = valueBytes.Length + 1;
            var max = (len + 3) & ~3;
            while (data.Count < dataOff + max)
                data.Add(0);

            index.AddRange(U16(keyOff));
            index.AddRange(U16(0x0004));
            index.AddRange(U32((uint)len));
            index.AddRange(U32((uint)max));
            index.AddRange(U32((uint)dataOff));
        }

        var keyStart = 20 + index.Count;
        var dataStart = keyStart + keys.Count;
        dataStart += (4 - (dataStart & 3)) & 3;
        var result = new byte[dataStart + data.Count];
        result[1] = (byte)'P';
        result[2] = (byte)'S';
        result[3] = (byte)'F';
        Write(result, 4, 0x00000101);
        Write(result, 8, (uint)keyStart);
        Write(result, 12, (uint)dataStart);
        Write(result, 16, (uint)entries.Length);
        index.CopyTo(result, 20);
        keys.CopyTo(result, keyStart);
        data.CopyTo(result, dataStart);
        return result;
    }

    private static byte[] U16(ushort value) => [(byte)value, (byte)(value >> 8)];
    private static byte[] U32(uint value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
    private static void Write(byte[] dest, int offset, uint value)
    {
        dest[offset] = (byte)value;
        dest[offset + 1] = (byte)(value >> 8);
        dest[offset + 2] = (byte)(value >> 16);
        dest[offset + 3] = (byte)(value >> 24);
    }
}

internal static class IsoBuilder
{
    private sealed class Node
    {
        public string Name = "";
        public bool Directory;
        public byte[]? Data;
        public uint Extent;
        public uint Size;
        public List<Node> Children = [];
    }

    public static byte[] Build(params (string Path, byte[] Data)[] files)
    {
        var root = new Node { Directory = true };
        foreach (var (path, data) in files)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var node = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var last = i == parts.Length - 1;
                var child = node.Children.FirstOrDefault(c => c.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new Node { Name = parts[i], Directory = !last, Data = last ? data : null };
                    node.Children.Add(child);
                }

                node = child;
            }
        }

        uint sector = 18;
        void AllocDirs(Node dir)
        {
            dir.Extent = sector;
            dir.Size = (uint)Measure(dir);
            sector += (uint)Math.Max(1, ((int)dir.Size + 2047) / 2048);
            foreach (var child in dir.Children.Where(c => c.Directory))
                AllocDirs(child);
        }

        void AllocFiles(Node dir)
        {
            foreach (var child in dir.Children.Where(c => !c.Directory))
            {
                child.Extent = sector;
                child.Size = (uint)(child.Data?.Length ?? 0);
                sector += (uint)Math.Max(1, ((int)child.Size + 2047) / 2048);
            }

            foreach (var child in dir.Children.Where(c => c.Directory))
                AllocFiles(child);
        }

        AllocDirs(root);
        AllocFiles(root);
        var image = new byte[sector * 2048];
        WritePvd(image, root, (int)sector);
        image[17 * 2048] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, 17 * 2048 + 1);
        image[17 * 2048 + 6] = 1;
        WriteTree(image, root, root);
        return image;
    }

    public static byte[] To2352(byte[] iso)
    {
        var sectors = iso.Length / 2048;
        var raw = new byte[sectors * 2352];
        for (var i = 0; i < sectors; i++)
        {
            for (var j = 1; j <= 10; j++)
                raw[i * 2352 + j] = 0xFF;
            Buffer.BlockCopy(iso, i * 2048, raw, i * 2352 + 16, 2048);
        }

        return raw;
    }

    private static void WriteTree(byte[] image, Node dir, Node parent)
    {
        var pos = 0;
        var start = (int)(dir.Extent * 2048);
        void Add(uint extent, uint size, bool directory, byte[] ident)
        {
            var len = RecordLength(ident.Length);
            var sectorOff = pos % 2048;
            if (sectorOff != 0 && sectorOff + len > 2048)
                pos += 2048 - sectorOff;
            WriteRecord(image, start + pos, extent, size, directory, ident);
            pos += len;
        }

        Add(dir.Extent, dir.Size, true, [0]);
        Add(parent.Extent, parent.Size, true, [1]);
        foreach (var child in dir.Children)
            Add(child.Extent, child.Size, child.Directory, Ident(child));
        foreach (var child in dir.Children.Where(c => c.Directory))
            WriteTree(image, child, dir);
        foreach (var child in dir.Children.Where(c => !c.Directory && c.Data is not null))
            Buffer.BlockCopy(child.Data!, 0, image, (int)(child.Extent * 2048), child.Data!.Length);
    }

    private static int Measure(Node dir)
    {
        var pos = 0;
        void Add(int identLen)
        {
            var len = RecordLength(identLen);
            var sectorOff = pos % 2048;
            if (sectorOff != 0 && sectorOff + len > 2048)
                pos += 2048 - sectorOff;
            pos += len;
        }

        Add(1);
        Add(1);
        foreach (var child in dir.Children)
            Add(Ident(child).Length);
        return pos;
    }

    private static byte[] Ident(Node node)
    {
        var name = node.Name.ToUpperInvariant();
        if (!node.Directory)
            name += ";1";
        return Encoding.ASCII.GetBytes(name);
    }

    private static int RecordLength(int identLen)
    {
        var len = 33 + identLen;
        if ((len & 1) == 1)
            len++;
        return len;
    }

    private static void WritePvd(byte[] image, Node root, int sectors)
    {
        var o = 16 * 2048;
        image[o] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, o + 1);
        image[o + 6] = 1;
        Encoding.ASCII.GetBytes("TESTVOLUME").CopyTo(image, o + 40);
        Both32(image, o + 80, (uint)sectors);
        Both16(image, o + 128, 2048);
        WriteRecord(image, o + 156, root.Extent, root.Size, true, [0]);
    }

    private static void WriteRecord(byte[] dest, int o, uint extent, uint size, bool directory, byte[] ident)
    {
        dest[o] = (byte)RecordLength(ident.Length);
        Both32(dest, o + 2, extent);
        Both32(dest, o + 10, size);
        dest[o + 25] = directory ? (byte)2 : (byte)0;
        Both16(dest, o + 28, 1);
        dest[o + 32] = (byte)ident.Length;
        Buffer.BlockCopy(ident, 0, dest, o + 33, ident.Length);
    }

    private static void Both32(byte[] dest, int o, uint value)
    {
        dest[o] = (byte)value;
        dest[o + 1] = (byte)(value >> 8);
        dest[o + 2] = (byte)(value >> 16);
        dest[o + 3] = (byte)(value >> 24);
        dest[o + 4] = dest[o + 3];
        dest[o + 5] = dest[o + 2];
        dest[o + 6] = dest[o + 1];
        dest[o + 7] = dest[o];
    }

    private static void Both16(byte[] dest, int o, ushort value)
    {
        dest[o] = (byte)value;
        dest[o + 1] = (byte)(value >> 8);
        dest[o + 2] = dest[o + 1];
        dest[o + 3] = dest[o];
    }
}
