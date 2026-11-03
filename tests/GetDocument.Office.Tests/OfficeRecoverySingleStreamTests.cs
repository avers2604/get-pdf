using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bb: synthetic ZIP and SHA tests for the same opened seekable
/// stream. No GUI, real documents, privileged paths or network required.
/// </summary>
[TestClass]
public sealed class OfficeRecoverySingleStreamTests
{
    [TestMethod]
    public void SameSeekableStreamVerifiesHashAndZipAndRemainsOpen()
    {
        using var input = NewOfficeStream(includeWordPart: true);
        string digest = Convert.ToHexString(SHA256.HashData(input.ToArray()));

        bool verified = OfficeRecoveryStore.VerifyOpenedPackage(
            input, ".docx", digest);

        Assert.IsTrue(verified);
        Assert.IsTrue(input.CanRead, "ZIP reader must leave its caller-owned stream open.");
        Assert.IsTrue(input.CanSeek);
        input.Position = 0;
        Assert.AreEqual((int)'P', input.ReadByte()); // ZIP local-file signature
    }

    [TestMethod]
    public void WrongHashDoesNotOpenOrTrustInvalidZip()
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("not a valid ZIP"));
        Assert.IsFalse(OfficeRecoveryStore.VerifyOpenedPackage(
            bytes, ".docx", new string('0', 64)));
        Assert.IsTrue(bytes.CanRead);

        string actualDigest = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeRecoveryStore.VerifyOpenedPackage(bytes, ".docx", actualDigest));
    }

    [TestMethod]
    public void CorrectHashWithoutRequiredOfficePartIsStillInvalid()
    {
        using var input = NewOfficeStream(includeWordPart: false);
        string digest = Convert.ToHexString(SHA256.HashData(input.ToArray()));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeRecoveryStore.VerifyOpenedPackage(input, ".docx", digest));
        Assert.IsTrue(input.CanRead);
    }

    [TestMethod]
    public void NonSeekableStreamIsRejectedBeforeHashAndZip()
    {
        using var input = new NonSeekableStream(NewOfficeStream(includeWordPart: true));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeRecoveryStore.VerifyOpenedPackage(input, ".docx", new string('0', 64)));
    }

    [TestMethod]
    public void CorruptedZipAfterHashFailsRatherThanAcceptingOldDigest()
    {
        using var input = NewOfficeStream(includeWordPart: true);
        string digest = Convert.ToHexString(SHA256.HashData(input.ToArray()));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeRecoveryStore.VerifyOpenedPackage(input, ".docx", digest, () =>
            {
                // Simulate concurrent truncation AFTER SHA, BEFORE ZIP.
                input.SetLength(8);
                input.Position = 0;
            }));
        Assert.IsTrue(input.CanRead);
    }

    private static MemoryStream NewOfficeStream(bool includeWordPart)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var xml = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                xml.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var other = new StreamWriter(zip.CreateEntry("_rels/.rels").Open()))
                other.Write("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>");
            if (includeWordPart)
            {
                using var main = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
                main.Write("<root/>");
            }
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
