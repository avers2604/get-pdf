using System.Security.Cryptography;

namespace GetPdf;

/// <summary>
/// Offline integrity gate for the exact qpdf.exe approved by IT.
/// The deployment administrator provisions a single 64-hex-character SHA-256
/// digest in qpdf.sha256 beside the executable. This is only a hash pin,
/// NOT a code-signing trust chain; directory ACLs must prevent ordinary users
/// from replacing both the binary and its manifest.
/// </summary>
internal static class PdfQpdfBinaryApproval
{
    internal static string ManifestPath(string executablePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
            "qpdf.sha256");

    public static void RequireApproved(string executablePath, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var executable = Path.GetFullPath(executablePath);
        var manifest = Path.GetFullPath(manifestPath);
        if (!File.Exists(executable))
            throw new FileNotFoundException(
                "Одобренный исполняемый файл qpdf отсутствует.", executable);
        if (!File.Exists(manifest))
            throw new InvalidOperationException(
                "Найден qpdf.exe, но отсутствует утверждённый ИТ-службой " +
                "файл qpdf.sha256. Запуск заблокирован.");

        // Avoid executing a symlink target outside the admin-controlled
        // installation directory. Same rule applies to the manifest.
        if ((File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Ссылки вместо qpdf.exe или qpdf.sha256 не допускаются.");

        var info = new FileInfo(manifest);
        if (info.Length < 64 || info.Length > 128)
            throw new InvalidDataException(
                "Манифест qpdf.sha256 должен содержать только 64 символа SHA-256.");
        var text = File.ReadAllText(manifest).Trim();
        if (text.Length != 64 ||
            text.Any(c => !((c >= '0' && c <= '9') ||
                             (c >= 'a' && c <= 'f') ||
                             (c >= 'A' && c <= 'F'))))
            throw new InvalidDataException(
                "Неверный формат qpdf.sha256: требуется один SHA-256 без имени файла.");

        var expected = Convert.FromHexString(text);
        using var stream = new FileStream(executable, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidOperationException(
                "Контроль SHA-256 qpdf.exe не пройден. " +
                "Утилита изменена или не утверждена ИТ-службой; запуск заблокирован.");
    }
}
