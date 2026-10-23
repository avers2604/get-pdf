using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// A validated *candidate*, not a saved file. A trusted internal gateway still
/// needs session authorization, anti-replay, pinned server identity, max bytes,
/// virus scanning and a separate GUP-2-safe revision sink.
/// </summary>
public sealed record VerifiedSaveCandidate(
    string DocumentKey, int Status, Uri DownloadUrl, bool RequiresApprovedRevisionSink = true);

public static class OnlyOfficeCallbackVerifier
{
    public static VerifiedSaveCandidate? Plan(
        string signedCallbackJwt, string expectedDocumentKey,
        Uri documentServerOrigin, byte[] signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDocumentKey);
        if (expectedDocumentKey.Length > 128)
            throw new UnauthorizedAccessException("Недопустимый идентификатор документа.");
        var verified = Hs256Jwt.Validate(signedCallbackJwt, signingKey);
        try
        {
            if (!verified.TryGetProperty("operation", out var op) ||
                op.GetString() != "callback")
                throw new UnauthorizedAccessException("JWT не относится к callback.");
            var payload = verified.GetProperty("payload");
            string? key = payload.GetProperty("key").GetString();
            if (!string.Equals(key, expectedDocumentKey, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Callback относится к другому документу.");

            int status = payload.GetProperty("status").GetInt32();
            if (status is 1 or 4)
                return null;
            if (status is not (2 or 6))
                throw new InvalidDataException("Состояние callback не означает готовую редакцию.");

            string? url = payload.GetProperty("url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate))
                throw new UnauthorizedAccessException("Нет допустимой ссылки на новую редакцию.");
            try { OnlyOfficeUriPolicy.RequireTrustedUrl(candidate, documentServerOrigin); }
            catch (ArgumentException ex)
            {
                throw new UnauthorizedAccessException("Редакцию запрещено загружать со стороннего узла.", ex);
            }
            return new VerifiedSaveCandidate(key!, status, candidate);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UnauthorizedAccessException("Некорректный подписанный callback.", ex);
        }
    }
}
