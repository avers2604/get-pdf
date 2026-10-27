using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Local LibreOffice (MPL-2.0) as the Office engine: headless conversion to PDF for the
/// in-tab preview and printing, and the LibreOffice window for editing. No network, no
/// macros (conversion runs with macro security "very high" in a private profile), no
/// shell: every call is an argv list with a finite deadline.
/// </summary>
public static class LibreOfficeRuntime
{
    public static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(3);
    private const int MaxCachedPreviews = 60;
    // LibreOffice runs one process per profile; parallel conversions on the shared headless
    // profile deadlock on its first-start lock. Convert one document at a time.
    private static readonly SemaphoreSlim ConversionGate = new(1, 1);
    // soffice exit code 81: "restart requested" after initialising a fresh profile.
    private const int ExitRestartRequested = 81;

    /// <summary>Office formats LibreOffice opens for preview, printing and editing.</summary>
    public static bool Handles(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".docx" or ".doc" or ".odt" or ".rtf" or
        ".xlsx" or ".xls" or ".xlsb" or ".ods" or ".csv" or
        ".pptx" or ".ppt" or ".pps" or ".ppsx" or ".odp";

    /// <summary>Path of soffice, or null when LibreOffice is not installed.</summary>
    public static string? Locate()
    {
        foreach (string candidate in Candidates())
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static bool IsAvailable => Locate() is not null;

    /// <summary>Starts the background LibreOffice early so the first Office preview is quick.</summary>
    public static void Prewarm()
    {
        if (IsAvailable) LibreOfficeBridge.Shared.Prewarm();
    }

    internal static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("GETDOC_SOFFICE") is { Length: > 0 } overridePath)
            yield return overridePath;
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(AppContext.BaseDirectory, "libreoffice", "program", "soffice.exe");
            if (RegistryInstallPath() is { } fromRegistry)
                yield return Path.Combine(fromRegistry, "soffice.exe");
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string folder = Environment.GetFolderPath(root);
                if (folder.Length > 0) yield return Path.Combine(folder, "LibreOffice", "program", "soffice.exe");
            }
        }
        else
        {
            yield return "/usr/bin/soffice";
            yield return "/usr/lib/libreoffice/program/soffice";
            yield return "/opt/libreoffice/program/soffice";
            yield return "/snap/bin/libreoffice";
        }
    }

    private static string? RegistryInstallPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath");
            return key?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>soffice.com waits for the conversion on Windows; soffice.exe may return early.</summary>
    private static string ConsoleLauncher(string soffice)
    {
        if (!OperatingSystem.IsWindows()) return soffice;
        string com = Path.ChangeExtension(soffice, ".com");
        return File.Exists(com) ? com : soffice;
    }

    private static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GET-Document");

    /// <summary>Private headless profile: never shares or locks the user's LibreOffice profile.</summary>
    internal static string ConversionProfileUri(string name = "libreoffice-headless")
    {
        string profile = Path.Combine(AppDataRoot, name);
        Directory.CreateDirectory(profile);
        return new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
    }

    /// <summary>
    /// Cache key: full path + size + last write time. A save in LibreOffice changes the key,
    /// so a fresh preview is produced; unchanged files reuse the previous PDF.
    /// </summary>
    public static string PreviewCachePath(string documentPath)
    {
        var info = new FileInfo(Path.GetFullPath(documentPath));
        string identity = $"{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32].ToLowerInvariant();
        return Path.Combine(AppDataRoot, "previews", key + ".pdf");
    }

    /// <summary>Converts an Office document to PDF (cached); returns the PDF path.</summary>
    public static async Task<string> ConvertToPdfAsync(string documentPath, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(documentPath);
        if (!File.Exists(source)) throw new FileNotFoundException("Документ не найден.", source);
        if (!Handles(source)) throw new NotSupportedException("Этот формат LibreOffice не обрабатывает.");
        string soffice = Locate() ?? throw new InvalidOperationException(
            "LibreOffice не установлен. Установите ГЭТ Документ полным установщиком или LibreOffice 7.6+.");

        string cached = PreviewCachePath(source);
        if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);

        await ConversionGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;
            return await ConvertUnderGateAsync(source, soffice, cached, cancellationToken);
        }
        finally { ConversionGate.Release(); }
    }

    private static async Task<string> ConvertUnderGateAsync(string source, string soffice, string cached,
        CancellationToken cancellationToken)
    {
        // Work on a private copy: the original may be open (and locked) in the LibreOffice editor,
        // and the output name must not depend on characters in the user's file name.
        string work = Path.Combine(Path.GetTempPath(), "getdoc-lo-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        try
        {
            string input = Path.Combine(work, "document" + Path.GetExtension(source).ToLowerInvariant());
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (var to = new FileStream(input, FileMode.CreateNew, FileAccess.Write))
                await from.CopyToAsync(to, cancellationToken);

            string produced = Path.Combine(work, "document.pdf");
            // On Windows the editor's LibreOffice is already running (prewarmed at start-up): use it
            // rather than a second, headless LibreOffice process.
            if (LibreOfficeEditor.IsSupported)
            {
                try
                {
                    await LibreOfficeEditor.Shared.ConvertToPdfAsync(input, produced, cancellationToken);
                    if (File.Exists(produced) && new FileInfo(produced).Length > 0)
                    {
                        File.Move(produced, cached, overwrite: true);
                        PruneCache(Path.GetDirectoryName(cached)!);
                        return cached;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { }
            }
            // Fast path: the warm LibreOffice behind the UNO bridge.
            if (await LibreOfficeBridge.Shared.TryConvertAsync(input, produced, ConversionTimeout, cancellationToken))
            {
                File.Move(produced, cached, overwrite: true);
                PruneCache(Path.GetDirectoryName(cached)!);
                return cached;
            }

            // Fallback: one-shot soffice --convert-to on its own profile (the bridge owns the other).
            string[] args =
            [
                "-env:UserInstallation=" + ConversionProfileUri("libreoffice-headless-cli"),
                "--headless", "--invisible", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
                "--convert-to", "pdf", "--outdir", work, input
            ];
            int code = await RunAsync(ConsoleLauncher(soffice), args, ConversionTimeout, cancellationToken);
            if (code == ExitRestartRequested || (code == 0 && !File.Exists(produced)))
                code = await RunAsync(ConsoleLauncher(soffice), args, ConversionTimeout, cancellationToken);
            if (code != 0 || !File.Exists(produced) || new FileInfo(produced).Length == 0)
                throw new InvalidDataException(
                    "LibreOffice не смог подготовить просмотр документа. Файл может быть повреждён или защищён паролем.");
            File.Move(produced, cached, overwrite: true);
            PruneCache(Path.GetDirectoryName(cached)!);
            return cached;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Opens the document in the LibreOffice editor (the user's normal profile).</summary>
    public static void OpenForEditing(string documentPath)
    {
        string source = Path.GetFullPath(documentPath);
        string soffice = Locate() ?? throw new InvalidOperationException("LibreOffice не установлен.");
        var start = new ProcessStartInfo(soffice) { UseShellExecute = false };
        start.ArgumentList.Add("--norestore");
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add(source);
        try { Process.Start(start)?.Dispose(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("Не удалось запустить LibreOffice: " + ex.Message, ex); }
    }

    private static async Task<int> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить LibreOffice.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            var drainOut = process.StandardOutput.ReadToEndAsync(linked.Token);
            var drainErr = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(drainOut, drainErr);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException("LibreOffice не ответил вовремя. Попробуйте ещё раз или откройте документ на редактирование.");
            throw;
        }
    }

    private static void PruneCache(string folder)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles("*.pdf")
                .OrderByDescending(f => f.LastAccessTimeUtc).Skip(MaxCachedPreviews);
            foreach (var old in files) old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
