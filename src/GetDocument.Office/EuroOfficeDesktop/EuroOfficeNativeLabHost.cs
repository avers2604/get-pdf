using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// IT-attested executable policy for the SEPARATE, non-editing Qt lab probe.
/// Verifying a local hash is not equivalent to OS code signing or a secure
/// sandbox. This type is never wired into the production document launcher.
/// </summary>
public sealed class EuroOfficeNativeLabHostPolicy
{
    private const long MaxBinaryBytes = 512L * 1024 * 1024;
    public string ApprovedDirectory { get; }
    public string ExecutablePath { get; }
    public string ExpectedSha256 { get; }

    public EuroOfficeNativeLabHostPolicy(string approvedDirectory, string executablePath,
        string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(approvedDirectory) ||
            string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(approvedDirectory) ||
            !Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("Нужны абсолютные локальные пути к проверенному Qt стенду.");
        if (expectedSha256 is null ||
            !Regex.IsMatch(expectedSha256, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Требуется точный SHA-256 проверенной сборки.", nameof(expectedSha256));
        ApprovedDirectory = Path.GetFullPath(approvedDirectory);
        ExecutablePath = Path.GetFullPath(executablePath);
        ExpectedSha256 = expectedSha256.ToLowerInvariant();
    }

    public string VerifyBinary()
    {
        if (OperatingSystem.IsWindows() &&
            (ApprovedDirectory.StartsWith(@"\\", StringComparison.Ordinal) ||
             ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal)))
            throw new ArgumentException("Сетевой исполняемый файл запрещён.");

        string relative = Path.GetRelativePath(ApprovedDirectory, ExecutablePath);
        if (relative is "." or ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Программа не находится в утверждённом каталоге.");

        if (!Directory.Exists(ApprovedDirectory))
            throw new DirectoryNotFoundException("Не найден локальный каталог Qt стенда.");

        // Reject reparse points from filesystem root to the executable to
        // prevent a link inside or above the approved lab directory.
        string current = Path.GetPathRoot(ExecutablePath)!;
        foreach (string part in ExecutablePath[current.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new FileNotFoundException("Проверенный Qt host отсутствует.", ExecutablePath);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Исполняемый файл не должен проходить через ссылку.");
        }

        var info = new FileInfo(ExecutablePath);
        if (info.Length is <= 0 or > MaxBinaryBytes)
            throw new InvalidDataException("Недопустимый размер бинарного Qt стенда.");
        using var stream = File.OpenRead(ExecutablePath);
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, ExpectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("SHA-256 нативного Qt host не совпадает.");
        return ExecutablePath;
    }

    public ProcessStartInfo CreateStartInfo(bool headless)
    {
        var start = new ProcessStartInfo(VerifyBinary())
        {
            WorkingDirectory = ApprovedDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = headless,
        };
        start.ArgumentList.Add(headless ? "--headless-probe" : "--gui-probe");
        return start;
    }
}

/// <summary>
/// Real process launcher + native JSON handshake, intentionally with no file
/// commands, native HWND handles or unsupported editor capabilities.
/// The process is killed/reaped if a handshake fails or the owner disposes.
/// </summary>
public sealed class EuroOfficeNativeLabHostSession : IAsyncDisposable
{
    private readonly Process _process;
    public EuroOfficeDesktopProbeCapabilities Capabilities { get; }
    public bool IsRunning => !_process.HasExited;

    private EuroOfficeNativeLabHostSession(Process process,
        EuroOfficeDesktopProbeCapabilities capabilities)
    {
        _process = process;
        Capabilities = capabilities;
    }

    public static async Task<EuroOfficeNativeLabHostSession> StartAsync(
        EuroOfficeNativeLabHostPolicy policy, bool headless,
        bool labApproved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!labApproved)
            throw new NotSupportedException("Запуск нативного Qt стенда должен быть явно разрешён ИТ.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var process = new Process { StartInfo = policy.CreateStartInfo(headless) };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Нативный Qt стенд не запустился.");

            EuroOfficeDesktopProbeCapabilities capabilities =
                await EuroOfficeDesktopProbeProtocol.ExchangeHelloAsync(
                    process.StandardOutput, process.StandardInput, timeout.Token);

            // Stage 2 deliberately never trusts "open-local" advertised by an
            // arbitrary binary merely because a hello was signed by NO ONE.
            if (capabilities.Features.Count != 0)
                throw new InvalidDataException("На этом этапе Qt host должен запрещать все функции редактора.");

            if (headless)
            {
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0)
                    throw new InvalidDataException("Qt стенд завершился с ошибкой.");
            }
            return new EuroOfficeNativeLabHostSession(process, capabilities);
        }
        catch
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None); }
                catch (InvalidOperationException) { }
            }
            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        try { await _process.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }
}
