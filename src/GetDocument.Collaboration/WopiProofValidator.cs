using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GetDocument.Collaboration;

/// <summary>
/// WOPI proof-key verification contract (MS-WOPI):
/// 4-byte network-order length + UTF-8 token,
/// 4-byte network-order length + UTF-8 uppercase absolute request URL,
/// 4-byte network-order length (8) + little-endian 64-bit UTC ticks.
/// RSA public keys MUST be obtained from pinned, authenticated discovery XML;
/// never use keys supplied by a WOPI request itself.
/// </summary>
public static class WopiProofValidator
{
    public static byte[] BuildExpectedProof(string accessToken, Uri absoluteRequestUrl, long timestampTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(absoluteRequestUrl);
        if (accessToken.Length > 16_384 ||
            !absoluteRequestUrl.IsAbsoluteUri ||
            absoluteRequestUrl.Scheme != Uri.UriSchemeHttps ||
            absoluteRequestUrl.AbsoluteUri.Length > 4096 ||
            absoluteRequestUrl.UserInfo.Length > 0 ||
            absoluteRequestUrl.Fragment.Length > 0)
            throw new ArgumentException("Недопустимый WOPI URL или access_token.");
        byte[] token = Encoding.UTF8.GetBytes(accessToken);
        byte[] url = Encoding.UTF8.GetBytes(absoluteRequestUrl.AbsoluteUri.ToUpperInvariant());
        byte[] data = new byte[4 + token.Length + 4 + url.Length + 4 + sizeof(long)];
        int offset = 0;
        WriteLength(token.Length);
        token.CopyTo(data, offset);
        offset += token.Length;
        WriteLength(url.Length);
        url.CopyTo(data, offset);
        offset += url.Length;
        WriteLength(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset), timestampTicks);
        return data;

        void WriteLength(int length)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), length);
            offset += 4;
        }
    }

    public static bool Verify(
        string accessToken, Uri requestUrl, long timestampTicks,
        string? proof, string? oldProof,
        RSAParameters currentPublicKey, RSAParameters oldPublicKey,
        Uri trustedWopiHostOrigin, DateTimeOffset now)
    {
        try
        {
            OnlyOfficeUriPolicy.RequireTrustedUrl(requestUrl, trustedWopiHostOrigin);
            DateTimeOffset timestamp = new(new DateTime(timestampTicks, DateTimeKind.Utc));
            if (timestamp < now.AddMinutes(-20) || timestamp > now.AddMinutes(5))
                return false;
            byte[] expected = BuildExpectedProof(accessToken, requestUrl, timestampTicks);

            // The three combinations specified by the WOPI proof-key protocol.
            // Old signature + old key is deliberately NOT accepted.
            return Check(proof, currentPublicKey, expected) ||
                   Check(oldProof, currentPublicKey, expected) ||
                   Check(proof, oldPublicKey, expected);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or
                                   CryptographicException)
        {
            return false;
        }
    }

    private static bool Check(string? signatureBase64, RSAParameters key, byte[] expected)
    {
        if (string.IsNullOrEmpty(signatureBase64) || signatureBase64.Length > 2048)
            return false;
        try
        {
            byte[] signature = Convert.FromBase64String(signatureBase64);
            using var verifier = RSA.Create();
            verifier.ImportParameters(key);
            return verifier.VerifyData(
                expected, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
