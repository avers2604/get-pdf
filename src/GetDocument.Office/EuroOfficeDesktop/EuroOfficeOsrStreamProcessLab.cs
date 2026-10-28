using System.Diagnostics;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Real lab-only OS process supervising a finite CEF OnPaint stream.
/// NEVER wired into the normal GET Document launcher. The process may host
/// upstream CEF without a sandbox: only approved, disposable, offline VM.
/// This wrapper does not grant office save/print, WORM or GUP-2 access.
/// </summary>
public static class EuroOfficeOsrStreamProcessLab
{
    private static readonly HashSet<string> SyntheticNames = new(StringComparer.Ordinal)
    {
        "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
    };

    public static void ValidateFixtureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !SyntheticNames.Contains(name))
            throw new ArgumentException("Только три синтетических OOXML тестовых файла.", nameof(name));
    }

    public static ProcessStartInfo CreateStartInfo(EuroOfficeNativeLabHostPolicy? host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved)
    {
        if (!labApproved)
            throw new NotSupportedException("Нужна отдельная ИБ-приёмка изолированного CEF Wayland стенда.");
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("CEF OSR stream доступен только под Linux/Wayland.");
        ArgumentNullException.ThrowIfNull(host);
        ValidateFixtureName(fixtureName);
        if (sessionId == Guid.Empty || tabId == Guid.Empty ||
            string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory) ||
            stagingDirectory.Contains("://", StringComparison.Ordinal) ||
            stagingDirectory.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Нужны уникальные session/tab и локальный staging root.");

        string root = Path.GetFullPath(stagingDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Синтетический staging root отсутствует.");
        // Verify every component, not only the final fixture, against symlinks.
        CheckNoLinks(root);
        string fixturePath = Path.Combine(root, fixtureName);
        string manifest = Path.Combine(root, "gd31j6-fixture-manifest.json");
        CheckNoLinks(fixturePath);
        CheckNoLinks(manifest);
        if (!File.Exists(fixturePath) || !File.Exists(manifest))
            throw new FileNotFoundException("Синтетический файл или manifest не подготовлен.");
        if (new FileInfo(fixturePath).Length is <= 0 or > 128L * 1024 * 1024 ||
            new FileInfo(manifest).Length is <= 0 or > 16 * 1024)
            throw new InvalidDataException("Некорректный размер синтетических файлов.");

        // The native host verifies actual fixture SHA from the manifest
        // again before opening. This is NOT file provenance or OS sandbox.
        var info = new ProcessStartInfo(host.VerifyBinary())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
            WorkingDirectory = host.ApprovedDirectory
        };
        info.ArgumentList.Add("--open-staged-wayland-stream-lab");
        info.ArgumentList.Add(root);
        info.ArgumentList.Add(fixtureName);
        info.ArgumentList.Add(sessionId.ToString("N"));
        info.ArgumentList.Add(tabId.ToString("N"));
        // Never force QT_QPA_PLATFORM here: the operator must configure a
        // legitimate, approved Wayland desktop before starting the program.
        return info;
    }

    public static async Task<int> RunAsync(EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId,
        Func<EuroOfficeOsrStreamFrame, Task> onFrame,
        bool labApproved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        var info = CreateStartInfo(host, stagingDirectory, fixtureName,
            sessionId, tabId, labApproved);
        using var process = new Process { StartInfo = info };
        bool started = false;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(TimeSpan.FromSeconds(35)); // 15 s CEF lab + startup/cleanup
            started = process.Start();
            if (!started)
                throw new InvalidOperationException("CEF OSR лабораторный процесс не запустился.");
            var receiver = new EuroOfficeOsrStreamReader(sessionId, tabId);
            int received = await receiver.ReadAsync(
                process.StandardOutput, onFrame, lifetime.Token);
            await process.WaitForExitAsync(lifetime.Token);
            if (process.ExitCode != 0)
                throw new InvalidDataException("CEF OSR хост завершился с ошибкой.");
            return received; // Count of LAB previews, never document approval
        }
        finally
        {
            if (started)
                await StopProcessAsync(process);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // Bounded cleanup only. Local lab acceptance must verify child
            // process containment and kill/reap behavior on the actual OS.
        }
    }

    private static void CheckNoLinks(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new FileNotFoundException("Путь лаборатории не существует.", current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Символические ссылки в лабораторных путях запрещены.");
        }
    }
}
