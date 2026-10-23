using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Minimal HS256 JWT for server-side integration only. Secret MUST remain on the
/// internal document gateway, never in the Avalonia UI or browser JavaScript.
/// ONLYOFFICE supplies its own JWT security for its internal services.
/// </summary>
public static class Hs256Jwt
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes(
        """{"alg":"HS256","typ":"JWT"}""");

    public static string Sign<T>(T body, byte[] secret)
    {
        ValidateSecret(secret);
        ArgumentNullException.ThrowIfNull(body);
        string header = Base64Url(Header);
        string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(body));
        string data = header + "." + payload;
        byte[] signature = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(data));
        return data + "." + Base64Url(signature);
    }

    public static JsonElement Validate(string token, byte[] secret)
    {
        ValidateSecret(secret);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 65_536)
            throw new UnauthorizedAccessException("JWT отсутствует или слишком большой.");

        string[] segments = token.Split('.');
        if (segments.Length != 3 || segments.Any(s => s.Length == 0))
            throw new UnauthorizedAccessException("Неподдерживаемый формат JWT.");

        try
        {
            byte[] h = Decode(segments[0]);
            byte[] body = Decode(segments[1]);
            byte[] signature = Decode(segments[2]);
            if (signature.Length != 32)
                throw new UnauthorizedAccessException("Неверная длина подписи JWT.");
            byte[] calculated = HMACSHA256.HashData(secret,
                Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]));
            if (!CryptographicOperations.FixedTimeEquals(calculated, signature))
                throw new UnauthorizedAccessException("Подпись JWT неверна.");
            using var header = JsonDocument.Parse(h);
            if (header.RootElement.GetProperty("alg").GetString() != "HS256" ||
                header.RootElement.GetProperty("typ").GetString() != "JWT")
                throw new UnauthorizedAccessException("Неподдерживаемый алгоритм JWT.");
            using var payload = JsonDocument.Parse(body);
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
                throw new UnauthorizedAccessException("JWT payload должен быть объектом.");
            return payload.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UnauthorizedAccessException("Неверная структура подписанного JWT.", ex);
        }
    }

    private static void ValidateSecret(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length < 32)
            throw new ArgumentException("Серверный ключ HS256 должен содержать минимум 32 байта.", nameof(secret));
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string data)
    {
        string base64 = data.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }
}
