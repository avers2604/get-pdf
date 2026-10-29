using System.Runtime.InteropServices;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// .NET 10-compatible single-directory hardlink create-new adapter for the
/// disposable synthetic laboratory. File.CreateHardLink exists in .NET 11,
/// not in the net10.0 target of this repository. No copy/move/overwrite
/// fallback: syscall/API failure leaves the attempt for manual inspection.
/// Windows: CreateHardLinkW(new, existing, NULL); Linux: link(old, new).
/// Not a sandbox or WORM/durable filesystem commitment.
/// </summary>
internal static class EuroOfficeNativeHardLinkNoReplace
{
    internal static void Create(string destination, string pending)
    {
        if (string.IsNullOrWhiteSpace(destination) ||
            string.IsNullOrWhiteSpace(pending) ||
            !Path.IsPathFullyQualified(destination) ||
            !Path.IsPathFullyQualified(pending))
            throw new ArgumentException("Только абсолютные пути синтетической лаборатории.");
        string output = Path.GetFullPath(destination);
        string input = Path.GetFullPath(pending);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(input, output, comparison))
            throw new ArgumentException("Путь исходной и новой записи не может совпадать.");
        string inputFolder = Path.GetDirectoryName(input)!;
        string outputFolder = Path.GetDirectoryName(output)!;
        if (!string.Equals(inputFolder, outputFolder, comparison))
            throw new InvalidDataException("Жёсткая ссылка разрешена только в одном каталоге.");
        if (OperatingSystem.IsWindows() &&
            (input.StartsWith(@"\\", StringComparison.Ordinal) ||
             output.StartsWith(@"\\", StringComparison.Ordinal)))
            throw new InvalidDataException("UNC/SMB не разрешены.");
        if (!File.Exists(input))
            throw new FileNotFoundException("Локальная временная копия отсутствует.", input);
        // Reuse the same ancestor-wide reparse/symlink guard as synthetic
        // staging instead of checking only the immediate parent. Path checks
        // still require an isolated exclusive lab account to avoid TOCTOU.
        EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath(
            inputFolder, expectDirectory: true);
        EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath(
            input, expectDirectory: false);
        if (File.Exists(output) || Directory.Exists(output))
            throw new IOException("Конфликт синтетического конечного имени.");
        // This pre-check is informative. The native create-new operation is
        // what actually rejects concurrent attempts to occupy the final name.
        bool done;
        if (OperatingSystem.IsWindows())
            done = CreateHardLinkWindows(output, input, IntPtr.Zero);
        else if (OperatingSystem.IsLinux())
            done = LinkUnix(input, output) == 0;
        else
            throw new PlatformNotSupportedException(
                "Hardlink-лаборатория разрешена только в Windows и Linux.");
        if (!done)
            throw new IOException(
                "Системная операция создания новой жёсткой ссылки отказала. Код: " +
                Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link",
        CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    private static extern int LinkUnix(string oldPath, string newPath);
}
