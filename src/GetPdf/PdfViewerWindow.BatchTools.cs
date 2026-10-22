using System.Windows;
using Microsoft.Win32;

namespace GetPdf;

public partial class PdfViewerWindow
{
    internal PdfCrossTabSnapshot? GetCleanTransferSnapshot()
    {
        if (IsBusy || !_canModify || _plan is null || _sourcePath is null ||
            _originalPath is null || _plan.HasChanges || HasPendingChanges ||
            _pageIndex < 0 || _pageIndex >= _plan.Pages.Count)
            return null;
        return new PdfCrossTabSnapshot(_sourcePath, _originalPath,
            _plan.Snapshot(), _pageIndex, _plan.SourcePageCount);
    }

    // Destination may already contain page-order changes and notes. Its
    // append-only working PDF still retains all source versions for Undo.
    internal PdfCrossTabSnapshot? GetWritableTransferSnapshot()
    {
        if (IsBusy || !_canModify || _nativeDrafts.Count > 0 ||
            _plan is null || _history is null || _sourcePath is null ||
            _originalPath is null || _pageIndex < 0 ||
            _pageIndex >= _plan.Pages.Count ||
            _plan.Pages.Count >= WorkstationPolicy.Current.MaxDocumentPages ||
            _plan.SourcePageCount >= 10000)
            return null;
        return new PdfCrossTabSnapshot(_sourcePath, _originalPath,
            _plan.Snapshot(), _pageIndex, _plan.SourcePageCount);
    }

    internal void SetCrossTabExportBusy(CancellationTokenSource? job)
    {
        if (job is not null && IsBusy)
            throw new InvalidOperationException("В документе выполняется другое задание.");
        _batchCancel = job;
        _loading = job is not null;
        SetControls();
    }

    private Point? _pageDragOrigin;
    private Guid? _pageDragId;

    private void PageList_DragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _pageDragOrigin = null;
        _pageDragId = null;
        if (!_editingToolsVisible || GetCleanTransferSnapshot() is not { } snapshot ||
            e.OriginalSource is not DependencyObject element)
            return;
        var container = System.Windows.Controls.ItemsControl.ContainerFromElement(
            PageList, element) as System.Windows.Controls.ListBoxItem;
        if (container is null) return;
        int index = PageList.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0 || index >= snapshot.Pages.Length) return;
        _pageDragId = snapshot.Pages[index].Id;
        _pageDragOrigin = e.GetPosition(PageList);
    }

    private void PageList_DragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pageDragOrigin is not Point start || _pageDragId is not Guid pageId) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _pageDragOrigin = null;
            _pageDragId = null;
            return;
        }
        Point now = e.GetPosition(PageList);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _pageDragOrigin = null;
        _pageDragId = null;
        if (GetCleanTransferSnapshot() is null) return;
        var data = new DataObject(typeof(PdfTabPageDrag),
            new PdfTabPageDrag(this, pageId));
        DragDrop.DoDragDrop(PageList, data, DragDropEffects.Copy);
    }

    private void CopyPageToTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button &&
            HostWindow is PdfTabbedWindow host)
            host.ShowPageCopyTargets(this, button);
    }

    private void NumberAllPages_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspace.CanEdit || _plan is null) return;
        int count = _plan.Pages.Count;
        bool existing = _plan.Pages.Any(p => p.PageNumber.Length > 0);
        var dialog = new PdfPageNumberingDialog(count, existing) { Owner = HostWindow };
        if (dialog.ShowDialog() != true) return;
        if (dialog.RemoveRequested)
        {
            ApplyEdit(p => p.SetPageNumbers(_ => null, PdfPageNumberPosition.BottomCenter));
            StatusText.Text = "Нумерация убрана из черновика.";
            return;
        }
        var options = dialog.Options!;
        // Numbers are separate from stamps: existing stamps stay untouched.
        ApplyEdit(p => p.SetPageNumbers(i => PdfPageNumbering.Label(i, count, options), options.Position));
        StatusText.Text = "Страницы пронумерованы. Нажмите «Сохранить изменения…», чтобы записать PDF.";
    }

    private async void CopyPageText_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _plan is null || _loading) return;
        try
        {
            string source = _sourcePath;
            int index = _plan.Pages[_pageIndex].SourcePage;
            _textIndex ??= await Task.Run(() => PdfTextReader.Build(source));
            string text = _textIndex.GetSourceText(index);
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "Текстового слоя нет. Для изображения используйте OCR.";
                return;
            }
            if (text.Length > 200_000)
            {
                StatusText.Text = "Страница слишком велика для копирования в буфер обмена.";
                return;
            }
            Clipboard.SetText(text);
            StatusText.Text = "Текст страницы скопирован в буфер обмена.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-copy-text", ex);
            StatusText.Text = "Не удалось скопировать текст страницы.";
        }
    }

    private async void SplitIntoPages_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspace.CanExtract || _plan is null || _sourcePath is null || _originalPath is null) return;
        var pages = _plan.Pages.ToArray();
        if (pages.Length > PdfSplitPageNaming.MaxPerOperation)
        {
            GetMessageBox.Show(HostWindow, $"За один раз можно разделить не более {PdfSplitPageNaming.MaxPerOperation} страниц.",
                "Разделение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ConfirmStructuralExport()) return;
        var dialog = NewPdfOutputDialog(Path.GetFileNameWithoutExtension(_originalPath) + "_страница");
        dialog.Title = "Выберите папку и префикс имён для отдельных PDF";
        if (dialog.ShowDialog(HostWindow) != true) return;
        var outputs = PdfSplitPageNaming.Plan(dialog.FileName, pages.Length);
        if (outputs.Any(File.Exists))
        {
            GetMessageBox.Show(HostWindow, "Один или несколько файлов уже существуют. Укажите другое имя префикса.",
                "Разделение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (GetMessageBox.Show(HostWindow, $"Создать {pages.Length} отдельных PDF? Исходный файл не меняется.",
            "Разделение PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var cancel = new CancellationTokenSource();
        _batchCancel = cancel;
        _loading = true; SetControls();
        string input = _sourcePath;
        var completed = new List<string>();
        StatusText.Text = "Разделяем PDF на страницы…";
        try
        {
            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                for (int i = 0; i < pages.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int page = i;
                    await Task.Run(() => PdfDocumentWriter.SaveAs(input, outputs[page], [pages[page]], token), token);
                    completed.Add(outputs[page]);
                }
                return true;
            }, cancel.Token);
            StatusText.Text = $"Создано {completed.Count} отдельных PDF.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-split", ex);
            foreach (string path in completed)
            {
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { DiagnosticLog.Failure("pdf-split-cleanup", cleanup); }
            }
            StatusText.Text = "Разделение не завершено. Созданные файлы по возможности удалены.";
            if (ex is not OperationCanceledException)
                GetMessageBox.Show(HostWindow, "Разделить документ не удалось. Проверьте свободное место и права на папку.",
                    "Разделение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _batchCancel = null; _loading = false; SetControls(); }
    }
}
