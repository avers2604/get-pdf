using System.Text;
using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Native DesktopEditors feasibility probe only. There is NO executable host,
/// Qt/CEF adapter or production document writeback wired to this contract yet.
/// </summary>
public sealed record EuroOfficeDesktopProbeCapabilities(IReadOnlySet<string> Features);

/// <summary>
/// Strict protocol boundary for a future *out-of-process* Qt/CEF lab host.
/// A JSON hello does not authenticate the executable, validate its signature,
/// or prove that an SDK method works. Native production integration is disabled.
/// </summary>
public static class EuroOfficeDesktopProbeProtocol
{
    public const string Protocol = "getdoc-eurodesktop-probe/1";
    public const string PinnedUpstreamCommit = "6a671bb1ad238c61965a64de3a005c420940c814";
    private static readonly HashSet<string> KnownFeatures =
        new(StringComparer.Ordinal)
        {
            "open-local", "save-as-new-revision", "export-pdf", "resize"
        };
    private const long MaxStagedDocumentBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Lab-only JSON line protocol. Does not start a process and does not grant
    /// access to files or claim a Qt/CEF editor is running. The caller must own
    /// a verified, sandboxed native child process before passing its streams.
    /// </summary>
    public static async Task<EuroOfficeDesktopProbeCapabilities> ExchangeHelloAsync(
        TextReader replies, TextWriter requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replies);
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();

        string request = JsonSerializer.Serialize(new
        {
            command = "hello",
            protocol = Protocol,
            upstreamCommit = PinnedUpstreamCommit
        });
        await requests.WriteLineAsync(request.AsMemory(), cancellationToken);
        await requests.FlushAsync(cancellationToken);

        const int maxReplyChars = 16 * 1024;
        var response = new StringBuilder(256);
        var single = new char[1];
        for (int i = 0; i < maxReplyChars; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await replies.ReadAsync(single.AsMemory(), cancellationToken);
            if (read == 0)
                throw new InvalidDataException("Нативный хост закрыл соединение до подтверждения версии.");
            if (single[0] == '\n')
                return ParseHello(response.ToString().TrimEnd('\r'));
            response.Append(single[0]);
        }
        throw new InvalidDataException("Ответ нативного хоста превысил допустимую длину.");
    }

    public static EuroOfficeDesktopProbeCapabilities ParseHello(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16 * 1024)
            throw new InvalidDataException("Некорректный размер ответа DesktopEditors.");

        try
        {
            using JsonDocument payload = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            JsonElement root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ожидался объект подтверждения DesktopEditors.");

            // Disallow unknown/duplicate properties rather than silently
            // overlooking extra executable permissions or a second "engine".
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                if (!seen.Add(property.Name) ||
                    property.Name is not ("protocol" or "engine" or "upstreamCommit" or "features"))
                    throw new InvalidDataException("Недопустимое поле ответа DesktopEditors.");
            if (seen.Count != 4 ||
                root.GetProperty("protocol").ValueKind != JsonValueKind.String ||
                root.GetProperty("protocol").GetString() != Protocol ||
                root.GetProperty("engine").ValueKind != JsonValueKind.String ||
                root.GetProperty("engine").GetString() != "EuroOfficeDesktopEditors" ||
                root.GetProperty("upstreamCommit").ValueKind != JsonValueKind.String ||
                root.GetProperty("upstreamCommit").GetString() != PinnedUpstreamCommit)
                throw new InvalidDataException("Несовместимая версия DesktopEditors или протокола.");

            JsonElement offered = root.GetProperty("features");
            if (offered.ValueKind != JsonValueKind.Array || offered.GetArrayLength() > KnownFeatures.Count)
                throw new InvalidDataException("Недопустимый список возможностей DesktopEditors.");

            var features = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in offered.EnumerateArray())
            {
                string? feature = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (feature is null || !KnownFeatures.Contains(feature) || !features.Add(feature))
                    throw new InvalidDataException("Неизвестная или повторная возможность DesktopEditors.");
            }
            return new EuroOfficeDesktopProbeCapabilities(features);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Повреждённый JSON-ответ DesktopEditors.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Неполный JSON-ответ DesktopEditors.", ex);
        }
    }

    public static void Require(EuroOfficeDesktopProbeCapabilities capabilities, string feature,
        bool labApproved, bool wormOriginal = false)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!labApproved || !KnownFeatures.Contains(feature) || !capabilities.Features.Contains(feature))
            throw new NotSupportedException(
                "Нативная функция EuroOffice не подтверждена на изолированном стенде.");
        if (wormOriginal && feature == "save-as-new-revision")
            throw new InvalidOperationException("WORM-оригинал не подлежит изменению.");
    }

    public static string CreateOpenRequest(string stagedRoot, string source)
    {
        string file = CheckStagedDocument(stagedRoot, source, mustExist: true);
        return JsonSerializer.Serialize(new { command = "open-local", source = file });
    }

    public static string CreateSaveRequest(string stagedRoot, string source, string destination)
    {
        string original = CheckStagedDocument(stagedRoot, source, mustExist: true);
        string revision = CheckStagedDocument(stagedRoot, destination, mustExist: false);
        if (string.Equals(original, revision,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Исходный файл нельзя перезаписывать.");
        if (!Path.GetExtension(original).Equals(Path.GetExtension(revision),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Расширение новой редакции должно совпадать с оригиналом.");
        // This REQUEST does not save anything; future native host MUST recheck
        // existence and publish with atomic no-overwrite semantics after validating.
        return JsonSerializer.Serialize(new
        {
            command = "save-as-new-revision",
            source = original,
            destination = revision
        });
    }

    /// <summary>
    /// Produces a request to render a temporary PDF. Actual PDF bytes and
    /// printing remain with a separately attested host and the GET print wizard.
    /// No direct printer API or automatic print dispatch is exposed.
    /// </summary>
    public static string CreateExportPdfRequest(string stagedRoot, string source, string destination)
    {
        string original = CheckStagedDocument(stagedRoot, source, mustExist: true);
        string target = CheckStagedDocument(stagedRoot, destination,
            mustExist: false, acceptPdfDestination: true);
        if (!Path.GetExtension(target).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Результат пробного экспорта должен иметь формат PDF.");
        return JsonSerializer.Serialize(new
        {
            command = "export-pdf",
            source = original,
            destination = target
        });
    }

    public static string CreateResizeRequest(int width, int height, double scale)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 ||
            !double.IsFinite(scale) || scale is < 0.5 or > 4.0)
            throw new ArgumentOutOfRangeException(nameof(width),
                "Размер и DPI должны находиться в пределах безопасного диапазона.");
        return JsonSerializer.Serialize(new { command = "resize", width, height, scale });
    }

    private static string CheckStagedDocument(string root, string candidate,
        bool mustExist, bool acceptPdfDestination = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        if (root.Contains("://", StringComparison.Ordinal) ||
            candidate.Contains("://", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(candidate))
            throw new ArgumentException("Допускаются только абсолютные локальные пути.");

        string basePath = Path.GetFullPath(root);
        string path = Path.GetFullPath(candidate);
        string relative = Path.GetRelativePath(basePath, path);
        if (relative == "." || Path.IsPathRooted(relative) ||
            relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new ArgumentException("Файл должен располагаться внутри изолированного каталога.");

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".docx" or ".xlsx" or ".pptx") &&
            !(acceptPdfDestination && !mustExist && extension == ".pdf"))
            throw new NotSupportedException("Пробный хост принимает только DOCX/XLSX/PPTX, а для экспорта — PDF.");

        if (!Directory.Exists(basePath) || !Directory.Exists(Path.GetDirectoryName(path)))
            throw new DirectoryNotFoundException("Каталог тестового документа отсутствует.");
        // Path canonicalization does not resolve filesystem links. Conservatively
        // reject reparse-point components to prevent escape outside the lab root.
        string next = basePath;
        if ((File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Символические ссылки для тестового каталога запрещены.");
        foreach (string part in relative.Split(Path.DirectorySeparatorChar)[..^1])
        {
            next = Path.Combine(next, part);
            if ((File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Символические ссылки для тестового каталога запрещены.");
        }

        if (mustExist)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaxStagedDocumentBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Неверный или слишком большой тестовый документ.");
        }
        else if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Новая редакция уже существует; перезапись запрещена.");
        }
        return path;
    }
}
