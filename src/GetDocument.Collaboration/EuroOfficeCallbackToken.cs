using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Selects the signed outbox token according to Docs API's outbound rules:
/// an in-body token takes precedence; otherwise the configured Authorization
/// header is used. Both sources must pass the same HS256 validation in
/// EuroOfficeNativeCallback.Plan. Never fall back if the body token is malformed.
/// </summary>
public static class EuroOfficeCallbackToken
{
    public static string Select(string body, string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 65_536)
            throw new UnauthorizedAccessException("Недопустимый callback.");
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new UnauthorizedAccessException("Callback должен быть объектом.");

            if (json.RootElement.TryGetProperty("token", out var inBody))
            {
                if (inBody.ValueKind != JsonValueKind.String)
                    throw new UnauthorizedAccessException("Неверный JWT в теле callback.");
                string? token = inBody.GetString();
                if (string.IsNullOrWhiteSpace(token) || token.Length > 60_000)
                    throw new UnauthorizedAccessException("Пустой или чрезмерный JWT.");
                return token;
            }

            string text = authorizationHeader?.Trim() ?? "";
            if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                text = text[7..].Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 60_000)
                throw new UnauthorizedAccessException("Не передан JWT Euro-Office.");
            return text;
        }
        catch (JsonException exception)
        {
            throw new UnauthorizedAccessException("Повреждённый JSON callback.", exception);
        }
    }
}
