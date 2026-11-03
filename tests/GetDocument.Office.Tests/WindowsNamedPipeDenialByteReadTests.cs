using System.Text;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27am: tests the Windows client response parser WITHOUT a Windows
/// named pipe. No OS privileges, native editor, document, or file writes.
/// </summary>
[TestClass]
public sealed class WindowsNamedPipeDenialByteReadTests
{
    [TestMethod]
    public async Task ReadsExactDenialInByteModeUntilEndOfStream()
    {
        using var stream = new MemoryStream(WindowsNamedPipeDenialLab.Denied);
        byte[] response = await WindowsNamedPipeDenialLab.ReadDenialToEofAsync(
            stream, CancellationToken.None);
        WindowsNamedPipeDenialLab.RequireExactDenial(response);
        Assert.AreEqual(stream.Length, stream.Position);
    }

    [TestMethod]
    public async Task ReadsFragmentedByteStreamWithoutMessageMode()
    {
        using var stream = new SingleByteReadStream(WindowsNamedPipeDenialLab.Denied);
        byte[] response = await WindowsNamedPipeDenialLab.ReadDenialToEofAsync(
            stream, CancellationToken.None);
        CollectionAssert.AreEqual(WindowsNamedPipeDenialLab.Denied, response);
    }

    [TestMethod]
    public async Task RejectsTruncatedDenialEvenAfterCleanEof()
    {
        using var stream = new MemoryStream(WindowsNamedPipeDenialLab.Denied[..^1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => WindowsNamedPipeDenialLab.ReadDenialToEofAsync(
                stream, CancellationToken.None));
    }

    [TestMethod]
    public async Task RejectsTrailingByteAfterExactDenial()
    {
        byte[] oversized = WindowsNamedPipeDenialLab.Denied.Concat(new byte[] { 0 }).ToArray();
        using var stream = new MemoryStream(oversized);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => WindowsNamedPipeDenialLab.ReadDenialToEofAsync(
                stream, CancellationToken.None));
    }

    [TestMethod]
    public async Task RejectsSemanticallyForgedSameLengthReply()
    {
        byte[] forgery = WindowsNamedPipeDenialLab.Denied;
        forgery[0] = (byte)'X';
        using var stream = new MemoryStream(forgery);
        byte[] response = await WindowsNamedPipeDenialLab.ReadDenialToEofAsync(
            stream, CancellationToken.None);
        Assert.ThrowsExactly<InvalidDataException>(
            () => WindowsNamedPipeDenialLab.RequireExactDenial(response));
    }

    [TestMethod]
    public async Task CancellationDoesNotReturnApparentDenial()
    {
        using var stream = new MemoryStream(WindowsNamedPipeDenialLab.Denied);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => WindowsNamedPipeDenialLab.ReadDenialToEofAsync(
                stream, cancelled.Token));
    }

    private sealed class SingleByteReadStream(byte[] data) : Stream
    {
        private readonly MemoryStream _memory = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _memory.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _memory.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _memory.Dispose();
            base.Dispose(disposing);
        }
    }
}
