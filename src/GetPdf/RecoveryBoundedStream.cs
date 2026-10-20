namespace GetPdf;

/// <summary>Seekable encoder output whose file length cannot exceed an admitted byte allowance.</summary>
internal sealed class RecoveryBoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumLength;
    private readonly bool _leaveOpen;

    internal RecoveryBoundedStream(Stream inner, long maximumLength, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanSeek || !inner.CanWrite) throw new ArgumentException("Seekable output required.", nameof(inner));
        if (maximumLength <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (inner.Length > maximumLength || inner.Position > maximumLength)
            throw new IOException("Выход превышает квоту резервного снимка.");
        _inner = inner;
        _maximumLength = maximumLength;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => false;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set { CheckPosition(value); _inner.Position = value; }
    }
    private void CheckPosition(long value)
    {
        if (value < 0 || value > _maximumLength)
            throw new IOException("Превышена квота резервного снимка.");
    }
    private void CheckWrite(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (_inner.Position > _maximumLength - count)
            throw new IOException("Превышена квота резервного снимка.");
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        CheckWrite(count);
        _inner.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        _inner.Write(buffer);
    }
    public override void WriteByte(byte value)
    {
        CheckWrite(1);
        _inner.WriteByte(value);
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long basis = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => Position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        long position;
        try { position = checked(basis + offset); }
        catch (OverflowException ex) { throw new IOException("Недопустимая позиция резервного снимка.", ex); }
        CheckPosition(position);
        return _inner.Seek(position, SeekOrigin.Begin);
    }
    public override void SetLength(long value)
    {
        CheckPosition(value);
        _inner.SetLength(value);
    }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}
