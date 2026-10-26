using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GetDocument.Office.LibreOffice;

namespace GetDocument.Desktop.Preview;

/// <summary>
/// DOCX/XLSX/PPTX (and ODF, legacy DOC/XLS/PPT, RTF, CSV) tab: a faithful page preview
/// rendered by local LibreOffice into PDF, editing in the LibreOffice window, and an
/// automatic refresh when the file is saved there. Printing uses the same PDF.
/// </summary>
internal sealed class OfficeDocumentPane : DockPanel, IDisposable
{
    private readonly string _path;
    private readonly Action<string> _status;
    private readonly ContentControl _body = new();
    private readonly TextBlock _note = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _edit;
    private readonly Button _refresh;
    private readonly Button _exportPdf;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private CancellationTokenSource _conversion = new();
    private PdfPreviewPane? _preview;
    private bool _disposed;

    /// <summary>PDF rendering of the current file version, or null while it is being prepared.</summary>
    public string? PreviewPdf { get; private set; }
    public string DocumentPath => _path;

    public OfficeDocumentPane(string path, Action<string> status)
    {
        _path = Path.GetFullPath(path);
        _status = status;
        _note.Classes.Add("hint");

        _edit = Command("Редактировать", Icons.Edit, "Открыть документ в LibreOffice. После сохранения просмотр обновится сам.");
        _edit.Classes.Add("primary");
        _edit.Click += (_, _) => Edit();
        _refresh = Command("Обновить", Icons.Refresh, "Перечитать файл и обновить просмотр");
        _refresh.Click += (_, _) => _ = RefreshAsync();
        _exportPdf = Command("Сохранить как PDF…", Icons.FileText, "Сохранить копию документа в PDF");
        _exportPdf.Click += async (_, _) => await ExportPdfAsync();
        _exportPdf.IsEnabled = false;

        var bar = new DockPanel { Margin = new Thickness(20, 12), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_edit);
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_exportPdf);
        DockPanel.SetDock(buttons, Dock.Left);
        bar.Children.Add(buttons);
        _note.Margin = new Thickness(16, 0, 0, 0);
        _note.TextTrimming = TextTrimming.CharacterEllipsis;
        bar.Children.Add(_note);
        var header = new Border { Child = bar };
        header.Classes.Add("header");
        SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(_body);

        if (!LibreOfficeRuntime.IsAvailable)
        {
            _edit.IsEnabled = _refresh.IsEnabled = false;
            ShowMessage("LibreOffice не установлен",
                "Для просмотра, редактирования и печати документов Word, Excel и PowerPoint нужен LibreOffice. " +
                "Он устанавливается вместе с ГЭТ Документ полным установщиком; попросите ИТ-службу переустановить программу.");
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            // LibreOffice saves via a temp file + rename: react to both, debounced.
            _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(Restart);
            _watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(Restart);
            _watcher.Created += (_, _) => Dispatcher.UIThread.Post(Restart);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            _watcher = null; // network share without change notifications: "Обновить" still works
        }
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(); };
        _ = RefreshAsync();
    }

    private static Button Command(string text, Geometry icon, string tip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(Icons.Create(icon, 18));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, MinHeight = 44 };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    private void Restart()
    {
        if (_disposed) return;
        _debounce.Stop();
        _debounce.Start();
    }

    public void Edit()
    {
        try
        {
            LibreOfficeRuntime.OpenForEditing(_path);
            _status($"«{Path.GetFileName(_path)}» открыт в LibreOffice. Сохраните там (Ctrl+S) — просмотр обновится.");
        }
        catch (InvalidOperationException ex) { _status(ex.Message); }
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        _conversion.Cancel();
        _conversion.Dispose();
        _conversion = new CancellationTokenSource();
        var token = _conversion.Token;
        _refresh.IsEnabled = false;
        if (_preview is null) ShowMessage("Готовим просмотр…", "LibreOffice преобразует документ для просмотра. Обычно это занимает несколько секунд.");
        _note.Text = "Готовим просмотр…";
        try
        {
            string pdf = await Task.Run(() => LibreOfficeRuntime.ConvertToPdfAsync(_path, token), token);
            if (_disposed || token.IsCancellationRequested) return;
            PreviewPdf = pdf;
            _preview?.Dispose();
            _preview = new PdfPreviewPane(pdf);
            _body.Content = _preview;
            _exportPdf.IsEnabled = true;
            var info = new FileInfo(_path);
            _note.Text = $"Просмотр как при печати · изменён {info.LastWriteTime:dd.MM.yyyy HH:mm}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                                   TimeoutException or UnauthorizedAccessException or NotSupportedException)
        {
            if (_disposed) return;
            if (_preview is null)
                ShowMessage("Не удалось показать документ", ex.Message + " Документ можно открыть на редактирование.");
            _note.Text = "Просмотр не обновлён: " + ex.Message;
        }
        finally
        {
            if (!_disposed) _refresh.IsEnabled = true;
        }
    }

    private async Task ExportPdfAsync()
    {
        if (PreviewPdf is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var chosen = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить как PDF",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + ".pdf",
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });
        if (chosen is null || !chosen.Path.IsFile) return;
        try
        {
            File.Copy(PreviewPdf, chosen.Path.LocalPath, overwrite: true);
            _status("PDF сохранён: " + Path.GetFileName(chosen.Path.LocalPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status("Не удалось сохранить PDF: " + ex.Message);
        }
    }

    private void ShowMessage(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(40, 32), Spacing = 10, MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left };
        var heading = new TextBlock { Text = title };
        heading.Classes.Add("title");
        var body = new TextBlock { Text = text };
        body.Classes.Add("hint");
        panel.Children.Add(heading);
        panel.Children.Add(body);
        _body.Content = panel;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        _conversion.Cancel();
        _watcher?.Dispose();
        _preview?.Dispose();
    }
}
