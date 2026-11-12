using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27ca / BLOCK 1: bounded IN-MEMORY transport envelope.
/// Detects accidental truncation and byte corruption before journal replay.
/// SHA-256 is unkeyed: an attacker can replace BOTH data and digest.
/// This code neither stores the journal nor verifies durable OS state.
/// </summary>
internal static class RecoveryCrashJournalWireEnvelope
{
    internal const int HeaderBytes = 48;
    internal const int FormatVersion = 1;
    internal const int MaxEnvelopeBytes =
        HeaderBytes + RecoveryCrashJournalCodec.MaxEnvelopeBytes;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GD31JCA1");

    internal static bool HasIndependentlyTrustedSigner => false;
    internal static bool CanAuthorizeMutation => false;
    internal static bool CanAuthorizeCleanup => false;

    /// <summary>
    /// A synthetic packet, not a write-ahead-log commit record.
    /// The caller must still use the journal reconciler to validate event
    /// order, transaction ID, SHA chain, artifacts and anchor consistency.
    /// </summary>
    internal static byte[] EncodeSynthetic(ReadOnlySpan<byte> journalBytes)
    {
        if (journalBytes.Length is < 1 or > RecoveryCrashJournalCodec.MaxEnvelopeBytes)
            throw new InvalidDataException("Недопустимый размер транспортного журнала.");

        // Do not wrap arbitrary JSON or ambiguous representations.
        _ = RecoveryCrashJournalCodec.DecodeCandidate(journalBytes.ToArray());

        byte[] wire = new byte[checked(HeaderBytes + journalBytes.Length)];
        Magic.CopyTo(wire, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(8, 2), FormatVersion);
        // Reserved flags at [10..12] are deliberately all zero.
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(12, 4),
            checked((uint)journalBytes.Length));
        journalBytes.CopyTo(wire.AsSpan(HeaderBytes));
        byte[] digest = SHA256.HashData(journalBytes);
        try
        {
            digest.CopyTo(wire.AsSpan(16, 32));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }

        return wire;
    }

    /// <summary>
    /// Strict single-record parser: exact length, format, reserved bytes,
    /// payload SHA-256 and canonical journal. No recovery authority.
    /// </summary>
    internal static byte[] DecodeCandidate(ReadOnlySpan<byte> wire)
    {
        if (wire.Length is <= HeaderBytes or > MaxEnvelopeBytes)
            throw new InvalidDataException("Неполный или слишком большой контейнер журнала.");
        if (!wire[..8].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(wire.Slice(8, 2)) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(wire.Slice(10, 2)) != 0)
            throw new InvalidDataException("Неверная версия, сигнатура или флаги журнала.");

        uint payloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(12, 4));
        if (payloadBytes is < 1 or > RecoveryCrashJournalCodec.MaxEnvelopeBytes ||
            wire.Length != HeaderBytes + (int)payloadBytes)
            throw new InvalidDataException("Длина записи журнала не совпадает с контейнером.");

        ReadOnlySpan<byte> payload = wire.Slice(HeaderBytes, (int)payloadBytes);
        byte[] computed = SHA256.HashData(payload);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    computed, wire.Slice(16, 32)))
                throw new InvalidDataException("Контрольная сумма содержимого не совпала.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(computed);
        }

        byte[] candidate = payload.ToArray();
        _ = RecoveryCrashJournalCodec.DecodeCandidate(candidate);
        return candidate;
    }
}
