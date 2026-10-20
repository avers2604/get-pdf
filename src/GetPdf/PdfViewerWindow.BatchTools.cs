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
        bool existing = _plan.Pages.Any(p => p.Stamp.Length > 0);
        string question = existing
            ? "На некоторых страницах уже есть штампы. Заменить их нумерацией всех страниц?"
            : "Добавить номера на все страницы? Их можно отменить (Ctrl+Z).";
        if (MessageBox.Show(HostWindow, question, "Нумерация PDF",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyEdit(p =>
        {
            for (int i = 0; i < count; i++)
                p.SetStamp(i, PdfBatchPageLabels.Number(i + 1, count));
        });
        StatusText.Text = "Номера добавлены к черновику; сохраните отдельную копию PDF.";
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
            MessageBox.Show(HostWindow, $"За один раз можно разделить не более {PdfSplitPageNaming.MaxPerOperation} страниц.",
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
            MessageBox.Show(HostWindow, "Один или несколько файлов уже существуют. Укажите другое имя префикса.",
                "Разделение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(HostWindow, $"Создать {pages.Length} отдельных PDF? Исходный файл не меняется.",
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
                MessageBox.Show(HostWindow, "Разделить документ не удалось. Проверьте свободное место и права на папку.",
                    "Разделение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _batchCancel = null; _loading = false; SetControls(); }
    }
}
