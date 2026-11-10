using System.Text.Json;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27by: bounded CANONICAL JSON byte envelope for synthetic journal
/// frames. No filesystem open/write and no durable journal implementation.
/// A canonical envelope and an unkeyed SHA chain do NOT prove authorship,
/// complete history, OS operations, trusted File IDs, or write authority.
/// </summary>
internal static class RecoveryCrashJournalCodec
{
    internal const int MaxEnvelopeBytes = 8192;
    internal static bool CanAuthorizeFileMutation => false;

    internal static byte[] EncodeSynthetic(
        IReadOnlyList<RecoveryCrashJournalFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count > RecoveryCrashJournalReconciler.MaxFrames)
            throw new InvalidDataException("Слишком много событий в журнале.");

        // Serialization is a deterministic in-memory interchange format.
        // Caller-controlled frames are not verified here. The reconciler
        // must perform full chain/sequence/transaction validation separately.
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(frames);
        if (canonical.Length == 0 || canonical.Length > MaxEnvelopeBytes)
            throw new InvalidDataException("Размер журнала превышает лимит 8 KiB.");
        return canonical;
    }

    internal static RecoveryCrashJournalFrame[] DecodeCandidate(
        ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxEnvelopeBytes)
            throw new InvalidDataException("Размер журнала не подтверждён.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions
                {
                    MaxDepth = 8,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() >
                    RecoveryCrashJournalReconciler.MaxFrames)
                throw new InvalidDataException("Некорректное число записей журнала.");

            RecoveryCrashJournalFrame[] result =
                JsonSerializer.Deserialize<RecoveryCrashJournalFrame[]>(bytes.Span)
                ?? throw new InvalidDataException("Нулевой журнал недопустим.");

            // Require the one exact encoder representation: no duplicate or
            // unknown fields, comments, whitespace padding, inconsistent
            // casing, reordered keys or ambiguous number/guid formats.
            byte[] canonical = EncodeSynthetic(result);
            if (!bytes.Span.SequenceEqual(canonical))
                throw new InvalidDataException(
                    "Неканоническое или неоднозначное представление журнала.");
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Журнал не является допустимым JSON.", ex);
        }
    }
}
