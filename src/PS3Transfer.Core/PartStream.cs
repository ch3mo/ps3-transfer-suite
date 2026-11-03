namespace PS3Transfer.Core;

/// <summary>
/// Reads a sequence of files as one stream. Used for split ISOs and for the copy itself.
/// </summary>
public sealed class PartStream : Stream
{
    private readonly (string Path, long Length)[] _parts;
    private int _index = -1;
    private long _partStart;
    private FileStream? _current;
    private long _position;
    private bool _disposed;

    public PartStream(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0)
            throw new ArgumentException("A part stream needs at least one file.", nameof(parts));
        _parts = new (string, long)[parts.Count];
        long total = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var length = new FileInfo(parts[i]).Length;
            _parts[i] = (parts[i], length);
            total += length;
        }

        Length = total;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position
    {
        get => _position;
        set => SeekTo(value);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_index < 0)
            SeekTo(_position);

        var total = 0;
        while (total < buffer.Length && _position < Length)
        {
            var room = _parts[_index].Length - (_position - _partStart);
            if (room <= 0)
            {
                if (_index + 1 >= _parts.Length)
                    break;
                Open(_index + 1, _partStart + _parts[_index].Length);
                continue;
            }

            var n = _current!.Read(buffer.Slice(total, (int)Math.Min(buffer.Length - total, room)));
            if (n == 0)
                break;
            total += n;
            _position += n;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        SeekTo(target);
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _current?.Dispose();
            _current = null;
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    private void SeekTo(long position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (position < 0 || position > Length)
            throw new IOException("Seek is outside the split set.");
        long cursor = 0;
        for (var i = 0; i < _parts.Length; i++)
        {
            var end = cursor + _parts[i].Length;
            if (position < end || i == _parts.Length - 1)
            {
                Open(i, cursor);
                _current!.Position = position - cursor;
                _position = position;
                return;
            }

            cursor = end;
        }
    }

    private void Open(int index, long partStart)
    {
        if (_index == index && _current is not null)
        {
            _partStart = partStart;
            return;
        }

        _current?.Dispose();
        _current = new FileStream(
            PathUtil.Win32(_parts[index].Path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1 << 16,
            FileOptions.SequentialScan);
        _index = index;
        _partStart = partStart;
    }
}
