namespace Oriel.Blazor;

// A read-only stream of two streams back to back; the shim goes before blazor.webview.js without copying it.
internal sealed class ConcatStream(Stream first, Stream second) : Stream
{
    private bool _onSecond;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => first.Length + second.Length;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_onSecond)
        {
            var read = first.Read(buffer, offset, count);
            if (read > 0) return read;
            _onSecond = true;
        }
        return second.Read(buffer, offset, count);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { first.Dispose(); second.Dispose(); }
        base.Dispose(disposing);
    }
}
