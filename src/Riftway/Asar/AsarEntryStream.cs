using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Riftway.Asar;

/// <summary>A read-only, seekable view of one file in an archive. With an integrity, it reads a block at a time and
/// returns none of a block until the block matches its hash.</summary>
internal sealed class AsarEntryStream : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly bool _ownsHandle;
    private readonly long _start;
    private readonly long _length;
    private readonly AsarIntegrity? _integrity;
    private readonly string _name;
    private long _position;
    private byte[]? _block;
    private long _blockIndex = -1;
    private int _blockLength;

    internal AsarEntryStream(SafeFileHandle handle, bool ownsHandle, long start, long length, AsarIntegrity? integrity, string name)
    {
        _handle = handle;
        _ownsHandle = ownsHandle;
        _start = start;
        _length = length;
        _integrity = integrity;
        _name = name;
        if (integrity is not null && integrity.Blocks.Count != Math.Max(1, (length + integrity.BlockSize - 1) / integrity.BlockSize))
        {
            if (ownsHandle) handle.Dispose();
            throw new InvalidDataException($"\"{name}\" has {integrity.Blocks.Count} block hashes for {length} bytes.");
        }
    }

    public override bool CanRead => !_handle.IsClosed;
    public override bool CanSeek => !_handle.IsClosed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (_position >= _length || buffer.IsEmpty) return 0;
        if (_integrity is null)
        {
            var wanted = (int)Math.Min(buffer.Length, _length - _position);
            var read = RandomAccess.Read(_handle, buffer[..wanted], _start + _position);
            if (read == 0) throw new EndOfStreamException($"\"{_name}\" ends before its size.");
            _position += read;
            return read;
        }

        var index = _position / _integrity.BlockSize;
        LoadBlock(index);
        var within = (int)(_position - index * _integrity.BlockSize);
        var count = Math.Min(buffer.Length, _blockLength - within);
        _block.AsSpan(within, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    private void LoadBlock(long index)
    {
        if (index == _blockIndex) return;
        var size = _integrity!.BlockSize;
        _block ??= new byte[(int)Math.Min(size, _length)];
        var length = (int)Math.Min(size, _length - index * size);
        var filled = 0;
        while (filled < length)
        {
            var read = RandomAccess.Read(_handle, _block.AsSpan(filled, length - filled), _start + index * size + filled);
            if (read == 0) throw new InvalidDataException($"\"{_name}\" ends before its size.");
            filled += read;
        }
        _blockIndex = -1;
        if (Convert.ToHexStringLower(SHA256.HashData(_block.AsSpan(0, length))) != _integrity.Blocks[(int)index])
            throw new InvalidDataException($"Block {index} of \"{_name}\" does not match its integrity hash.");
        _blockIndex = index;
        _blockLength = length;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        SeekOrigin.End => _length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsHandle) _handle.Dispose();
        base.Dispose(disposing);
    }
}
