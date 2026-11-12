using System.Buffers.Binary;
using System.Text;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ca / BLOCK 1. Author-only tests until an approved local
/// dotnet test run. A SHA-256 transport check is never a signature.
/// </summary>
[TestClass]
public sealed class RecoveryCrashJournalWireEnvelopeTests
{
    private static byte[] Journal() =>
        RecoveryCrashJournalCodec.EncodeSynthetic(
            Array.Empty<RecoveryCrashJournalFrame>());

    private static byte[] Envelope() =>
        RecoveryCrashJournalWireEnvelope.EncodeSynthetic(Journal());

    [TestMethod]
    public void CanonicalJournalRoundTripIsExact()
    {
        byte[] journal = Journal();
        byte[] wire = Envelope();
        CollectionAssert.AreEqual(journal,
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(wire));
        Assert.AreEqual(RecoveryCrashJournalWireEnvelope.HeaderBytes +
            journal.Length, wire.Length);
    }

    [TestMethod]
    public void EncodingSameJournalIsDeterministic()
    {
        CollectionAssert.AreEqual(Envelope(), Envelope());
        Assert.IsFalse(RecoveryCrashJournalWireEnvelope.CanAuthorizeMutation);
        Assert.IsFalse(RecoveryCrashJournalWireEnvelope.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void TruncationAndEmptyBufferFailClosed()
    {
        byte[] bytes = Envelope();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(bytes[..^1]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(Array.Empty<byte>()));
    }

    [TestMethod]
    public void AppendedBytesCannotBeSilentlyIgnored()
    {
        byte[] wire = Envelope();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(
                wire.Concat(new byte[] { 0 }).ToArray()));
    }

    [TestMethod]
    public void ChangedPayloadFailsHash()
    {
        byte[] wire = Envelope();
        wire[RecoveryCrashJournalWireEnvelope.HeaderBytes] ^= 0x01;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(wire));
    }

    [TestMethod]
    public void ChangedDigestFailsVerification()
    {
        byte[] wire = Envelope();
        wire[16] ^= 0x80;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(wire));
    }

    [TestMethod]
    public void WrongMagicOrVersionIsNeverReadAsJournal()
    {
        byte[] wire = Envelope();
        wire[0] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(wire));

        wire = Envelope();
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(8, 2), 2);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(wire));
    }

    [TestMethod]
    public void ReservedFlagsMustBeZero()
    {
        byte[] wire = Envelope();
        wire[10] = 1;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(wire));
    }

    [TestMethod]
    public void ForgedLengthAndOverLimitPayloadFailClosed()
    {
        byte[] wire = Envelope();
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(12, 4),
            (uint)(RecoveryCrashJournalCodec.MaxEnvelopeBytes + 1));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(wire));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.EncodeSynthetic(
                new byte[RecoveryCrashJournalCodec.MaxEnvelopeBytes + 1]));
    }

    [TestMethod]
    public void NoncanonicalJsonIsRejectedBeforeEncoding()
    {
        byte[] malformed = Encoding.UTF8.GetBytes("[ ]");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalWireEnvelope.EncodeSynthetic(malformed));
    }

    [TestMethod]
    public void ARecomputedUnkeyedDigestStillCannotAuthorizeRecovery()
    {
        byte[] wire = Envelope();
        CollectionAssert.AreEqual(Journal(),
            RecoveryCrashJournalWireEnvelope.DecodeCandidate(wire));
        Assert.IsFalse(RecoveryCrashJournalWireEnvelope.HasIndependentlyTrustedSigner);
        Assert.IsFalse(RecoveryCrashJournalWireEnvelope.CanAuthorizeMutation);
    }
}
