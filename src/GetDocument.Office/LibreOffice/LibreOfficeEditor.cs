using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// The LibreOffice that edits documents inside GET Document tabs. Unlike the headless
/// preview bridge it shows windows, so it runs as its own process with its own profile
/// (light appearance, no tips, no crash dialogs, no format nags). lo_editor.py opens each
/// document in a hidden LibreOffice window without menus or toolbars; the desktop reparents
/// that window into the tab and drives it from GET DS controls.
/// </summary>
public sealed class LibreOfficeEditor : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);
    private static readonly Lazy<LibreOfficeEditor> SharedInstance = new(() => new LibreOfficeEditor());
    public static LibreOfficeEditor Shared => SharedInstance.Value;

    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, EditorDocument> _documents = new();
    private Process? _office;
    private Process? _python;
    private TaskCompletionSource<bool>? _ready;
    private int _nextRequest;

    private LibreOfficeEditor()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    /// <summary>Embedded editing needs Windows (window reparenting) and LibreOffice with its Python.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() && LibreOfficeRuntime.Locate() is { } soffice && LibreOfficeBridge.PythonFor(soffice) is not null;

    /// <summary>Closes the editor's documents and LibreOffice, if the editor was ever started.</summary>
    public static void ShutdownIfStarted()
    {
        if (SharedInstance.IsValueCreated) SharedInstance.Value.Dispose();
    }

    /// <summary>Starts LibreOffice in the background so the first document opens quickly.</summary>
    public void Prewarm() => _ = Task.Run(async () =>
    {
        try { await EnsureStartedAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or Win32Exception) { }
    });

    /// <summary>
    /// Opens <paramref name="path"/> in a hidden LibreOffice window titled with a unique marker
    /// (the desktop finds and reparents the window by it).
    /// </summary>
    public async Task<EditorDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(path);
        if (!File.Exists(source)) throw new FileNotFoundException("Документ не найден.", source);
        await EnsureStartedAsync(cancellationToken);
        string id = Guid.NewGuid().ToString("N")[..12];
        var document = new EditorDocument(this, id, "getdoc-editor-" + id);
        _documents[id] = document;
        try
        {
            var reply = await CallAsync(new { op = "open", id, path = source, marker = document.Marker }, cancellationToken);
            document.Kind = reply.GetProperty("kind").GetString() ?? "writer";
            document.ReadOnly = reply.TryGetProperty("readonly", out var ro) && ro.GetBoolean();
            return document;
        }
        catch
        {
            _documents.TryRemove(id, out _);
            throw;
        }
    }

    internal async Task<JsonElement> CallAsync(object request, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        int number = Interlocked.Increment(ref _nextRequest);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[number] = waiter;
        var fields = JsonSerializer.SerializeToNode(request)!.AsObject();
        fields["re"] = number;
        try
        {
            await _write.WaitAsync(cancellationToken);
            try
            {
                var input = _python?.StandardInput ?? throw new IOException("LibreOffice не запущен.");
                await input.WriteLineAsync(fields.ToJsonString().AsMemory(), cancellationToken);
                await input.FlushAsync(cancellationToken);
            }
            finally { _write.Release(); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CallTimeout);
            var reply = await waiter.Task.WaitAsync(deadline.Token);
            if (reply.GetProperty("ok").GetBoolean()) return reply;
            string error = reply.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            throw new InvalidOperationException(Describe(error));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("LibreOffice не ответил вовремя.");
        }
        finally { _pending.TryRemove(number, out _); }
    }

    private static string Describe(string error)
    {
        if (error.Contains("IOException", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("IllegalArgument", StringComparison.OrdinalIgnoreCase))
            return "LibreOffice не смог открыть или записать файл. Возможно, он повреждён, защищён паролем или открыт другим пользователем.";
        return error.Length > 0 ? "LibreOffice: " + error : "LibreOffice не выполнил команду.";
    }

    internal void Forget(string id) => _documents.TryRemove(id, out _);

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _start.WaitAsync(cancellationToken);
        try
        {
            if (_python is { HasExited: false } && _office is { HasExited: false } && _ready is { Task.IsCompletedSuccessfully: true })
                return;
            Stop();
            string soffice = LibreOfficeRuntime.Locate() ?? throw new InvalidOperationException(
                "LibreOffice не установлен. Переустановите ГЭТ Документ полным установщиком.");
            string python = LibreOfficeBridge.PythonFor(soffice) ?? throw new InvalidOperationException(
                "В комплекте LibreOffice нет Python для встроенного редактора.");
            string profile = LibreOfficeRuntime.ConversionProfileUri("libreoffice-editor");
            SeedProfile(new Uri(profile).LocalPath);

            string pipe = $"getdoc-edit-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
            var office = new ProcessStartInfo(soffice) { UseShellExecute = false };
            foreach (string arg in new[]
                     {
                         "-env:UserInstallation=" + profile,
                         // --invisible: no start centre and no window until a document is shown.
                         "--invisible", "--nologo", "--norestore", "--nolockcheck",
                         $"--accept=pipe,name={pipe};urp;StarOffice.ComponentContext"
                     })
                office.ArgumentList.Add(arg);
            _office = Process.Start(office) ?? throw new IOException("LibreOffice не запустился.");

            var bridge = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            bridge.ArgumentList.Add(LibreOfficeBridge.ScriptPath("lo_editor.py"));
            bridge.ArgumentList.Add(pipe);
            bridge.ArgumentList.Add(((int)StartTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            bridge.Environment["PYTHONIOENCODING"] = "utf-8";
            var process = _python = Process.Start(bridge) ?? throw new IOException("Мост LibreOffice не запустился.");
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None); // never let stderr block
            var ready = _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => ReadLoopAsync(process, ready));
            try { await ready.Task.WaitAsync(StartTimeout + TimeSpan.FromSeconds(10), cancellationToken); }
            catch (TimeoutException)
            {
                Stop();
                throw new TimeoutException("LibreOffice не запустился вовремя.");
            }
        }
        finally { _start.Release(); }
    }

    private async Task ReadLoopAsync(Process process, TaskCompletionSource<bool> ready)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonElement message;
                try { message = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { continue; }
                if (message.TryGetProperty("re", out var re) && re.ValueKind == JsonValueKind.Number)
                {
                    if (_pending.TryRemove(re.GetInt32(), out var waiter)) waiter.TrySetResult(message);
                    continue;
                }
                if (!message.TryGetProperty("event", out var kind)) continue;
                string name = kind.GetString() ?? "";
                if (name == "ready") { ready.TrySetResult(true); continue; }
                if (message.TryGetProperty("id", out var id) && id.GetString() is { } docId &&
                    _documents.TryGetValue(docId, out var document))
                    document.Raise(name, message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        // LibreOffice or the bridge went away: fail what is waiting and tell every open document.
        ready.TrySetException(new IOException("LibreOffice завершился."));
        foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("LibreOffice завершился."));
        foreach (var document in _documents.Values) document.Raise("closed", default);
        _documents.Clear();
    }

    /// <summary>
    /// First-run settings of the private editor profile: light UI regardless of the Windows
    /// theme, Russian locale, no tips / what's-new / donation prompts, no crash-report or
    /// document-recovery dialogs, no "keep current format" question on save.
    /// </summary>
    internal static void SeedProfile(string profileFolder)
    {
        string user = Path.Combine(profileFolder, "user");
        string file = Path.Combine(user, "registrymodifications.xcu");
        if (File.Exists(file)) return;
        Directory.CreateDirectory(user);
        static string Item(string path, string name, string value) =>
            $"<item oor:path=\"{path}\"><prop oor:name=\"{name}\" oor:op=\"fuse\"><value>{value}</value></prop></item>";
        var items = new[]
        {
            Item("/org.openoffice.Office.Common/Appearance", "ApplicationAppearance", "1"),
            Item("/org.openoffice.Office.Common/Misc", "ShowTipOfTheDay", "false"),
            Item("/org.openoffice.Office.Common/Misc", "FirstRun", "false"),
            Item("/org.openoffice.Office.Common/Misc", "CrashReport", "false"),
            Item("/org.openoffice.Office.Common/Save/Document", "WarnAlienFormat", "false"),
            // "Very high": macros only from trusted locations, and none are configured.
            Item("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel", "3"),
            Item("/org.openoffice.Office.Recovery/RecoveryInfo", "Enabled", "false"),
            Item("/org.openoffice.Office.Recovery/AutoSave", "Enabled", "false"),
            Item("/org.openoffice.Setup/L10N", "ooLocale", "ru-RU"),
            Item("/org.openoffice.Office.Linguistic/General", "DefaultLocale", "ru-RU"),
            Item("/org.openoffice.Setup/Product", "ooSetupLastVersion", "99.0"),
            Item("/org.openoffice.Setup/Product", "LastTimeGetInvolvedShown", "9999999999"),
            Item("/org.openoffice.Setup/Product", "LastTimeDonateShown", "9999999999")
        };
        File.WriteAllText(file,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<oor:items xmlns:oor=\"http://openoffice.org/2001/registry\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" + string.Join("\n", items) + "\n</oor:items>\n",
            new UTF8Encoding(false));
    }

    private void Stop()
    {
        foreach (var process in new[] { _python, _office })
        {
            if (process is null) continue;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            process.Dispose();
        }
        _python = null;
        _office = null;
    }

    public void Dispose()
    {
        // Closing stdin lets the bridge close documents and LibreOffice itself; kill as a fallback.
        try { _python?.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        try { _python?.WaitForExit(3000); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        Stop();
    }
}

/// <summary>One document open in the embedded LibreOffice editor.</summary>
public sealed class EditorDocument
{
    private readonly LibreOfficeEditor _editor;
    private bool _closed;

    internal EditorDocument(LibreOfficeEditor editor, string id, string marker)
    {
        _editor = editor;
        Id = id;
        Marker = marker;
    }

    public string Id { get; }
    /// <summary>Unique text in the LibreOffice window title, used to find the window to embed.</summary>
    public string Marker { get; }
    /// <summary>"writer", "calc", "impress" or "draw".</summary>
    public string Kind { get; internal set; } = "writer";
    public bool ReadOnly { get; internal set; }
    public bool IsModified { get; private set; }

    /// <summary>Raised on a LibreOffice thread: command, enabled, value (bool / number / name or null).</summary>
    public event Action<string, bool, JsonElement>? StateChanged;
    public event Action<bool>? ModifiedChanged;
    /// <summary>A shortcut such as Ctrl+S or Ctrl+P was pressed inside the document (".uno:Save"...).</summary>
    public event Action<string>? CommandRequested;
    /// <summary>LibreOffice closed the document or exited.</summary>
    public event Action? Closed;

    internal void Raise(string name, JsonElement message)
    {
        switch (name)
        {
            case "state":
                StateChanged?.Invoke(message.GetProperty("cmd").GetString() ?? "",
                    message.GetProperty("enabled").GetBoolean(), message.GetProperty("value"));
                break;
            case "modified":
                IsModified = message.GetProperty("value").GetBoolean();
                ModifiedChanged?.Invoke(IsModified);
                break;
            case "command":
                CommandRequested?.Invoke(message.GetProperty("cmd").GetString() ?? "");
                break;
            case "closed":
                _closed = true;
                Closed?.Invoke();
                break;
        }
    }

    public Task ShowAsync() => _editor.CallAsync(new { op = "show", id = Id }, CancellationToken.None);
    public Task FocusAsync() => _editor.CallAsync(new { op = "focus", id = Id }, CancellationToken.None);

    /// <summary>Runs a LibreOffice command (".uno:Bold", ".uno:InsertTable"...) on the document.</summary>
    public Task DispatchAsync(string command, IReadOnlyDictionary<string, object>? arguments = null, bool focus = true) =>
        _editor.CallAsync(new { op = "dispatch", id = Id, cmd = command, args = arguments, focus }, CancellationToken.None);

    /// <summary>Runs one of the explicit GET Office capabilities, never an arbitrary UNO operation.</summary>
    public Task ExecuteFeatureAsync(string featureId)
    {
        OfficeFeatureCatalog.Require(Kind, featureId, ReadOnly);
        return _editor.CallAsync(new { op = "feature", id = Id, feature = featureId }, CancellationToken.None);
    }

    public async Task SaveAsync()
    {
        var reply = await _editor.CallAsync(new { op = "save", id = Id }, CancellationToken.None);
        SetModified(reply);
    }

    /// <summary>Saves a copy in the format of <paramref name="path"/>'s extension; the document stays bound to its file.</summary>
    public async Task SaveAsAsync(string path)
    {
        var reply = await _editor.CallAsync(new { op = "saveas", id = Id, path = Path.GetFullPath(path), filter = FilterFor(path) },
            CancellationToken.None);
        SetModified(reply);
    }

    /// <summary>Exports an OOXML recovery snapshot without marking the original document as saved.</summary>
    public Task ExportRecoveryCopyAsync(string path) =>
        _editor.CallAsync(new { op = "saveas", id = Id, path = Path.GetFullPath(path), filter = FilterFor(path) },
            CancellationToken.None);

    /// <summary>Writes the document as it is now (unsaved edits included) to a PDF.</summary>
    public Task ExportPdfAsync(string path) =>
        _editor.CallAsync(new { op = "pdf", id = Id, path = Path.GetFullPath(path) }, CancellationToken.None);

    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        try { await _editor.CallAsync(new { op = "close", id = Id }, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { }
        finally { _editor.Forget(Id); }
    }

    private void SetModified(JsonElement reply)
    {
        if (!reply.TryGetProperty("modified", out var modified)) return;
        IsModified = modified.GetBoolean();
        ModifiedChanged?.Invoke(IsModified);
    }

    /// <summary>LibreOffice export filter for a target file extension.</summary>
    public static string FilterFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => "MS Word 2007 XML",
        ".doc" => "MS Word 97",
        ".odt" => "writer8",
        ".rtf" => "Rich Text Format",
        ".xlsx" => "Calc MS Excel 2007 XML",
        ".xls" => "MS Excel 97",
        ".ods" => "calc8",
        ".csv" => "Text - txt - csv (StarCalc)",
        ".pptx" => "Impress MS PowerPoint 2007 XML",
        ".ppt" => "MS PowerPoint 97",
        ".odp" => "impress8",
        ".pdf" => throw new ArgumentException("Для PDF используйте ExportPdfAsync."),
        _ => throw new NotSupportedException("Этот формат LibreOffice не сохраняет.")
    };
}
