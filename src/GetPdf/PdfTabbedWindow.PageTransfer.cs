using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GetPdf;

/// <summary>
/// Explicit cross-document page copy. Neither source editor is modified:
/// the exported PDF is a fresh, atomically saved document opened in a new tab.
/// This intentionally does not offer destructive move until a supported
/// cross-document undo journal is designed and tested.
/// </summary>
internal sealed partial class PdfTabbedWindow
{
    internal void ShowPageCopyTargets(PdfViewerWindow source, Button owner)
    {
        if (_shutdownPending || source.GetCleanTransferSnapshot() is null)
        {
            MessageBox.Show(this, "Для переноса нужны открытые PDF без несохранённых изменений. " +
                "Сначала сохраните копию и заново откройте её.",
                "Копирование страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var targets = _handles.Values.Where(handle =>
            !ReferenceEquals(handle.Viewer, source) &&
            handle.Viewer.CurrentDocumentPath is not null).ToArray();
        var menu = new ContextMenu
        {
            PlacementTarget = owner,
            Placement = PlacementMode.Bottom
        };
        foreach (var handle in targets)
        {
            var target = handle.Viewer;
            var item = new MenuItem
            {
                Header = handle.Caption.Text,
                ToolTip = target.CurrentDocumentPath
            };
            item.Click += async (_, _) => await CopyPageToTabAsync(source, target);
            menu.Items.Add(item);
        }
        if (targets.Length == 0)
            menu.Items.Add(new MenuItem
            {
                Header = "Откройте второй PDF во вкладке",
                IsEnabled = false
            });
        owner.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private async Task CopyPageToTabAsync(PdfViewerWindow source,
        PdfViewerWindow destination, Guid? draggedPageId = null)
    {
        var sourceState = source.GetCleanTransferSnapshot();
        // Target tabs are lazy-loaded until selected. An explicit copy
        // operation may require opening the target without tab switching.
        try { await destination.EnsureInitialDocumentLoadedAsync(); }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-cross-tab-target-open", ex);
            MessageBox.Show(this, "Не удалось открыть PDF назначения.",
                "Копирование страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var targetState = destination.GetCleanTransferSnapshot();
        if (sourceState is null || targetState is null)
        {
            MessageBox.Show(this, "Оба PDF должны быть открыты без несохранённых изменений. " +
                "Сохраните копии документов и откройте их повторно.",
                "Копирование страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PdfSourcePageReference[] plan;
        int sourcePageIndex = draggedPageId is Guid id
            ? PdfCrossTabPageTransfer.FindPageById(sourceState.Pages, id)
            : sourceState.SelectedPageIndex;
        if (sourcePageIndex < 0 || sourcePageIndex >= sourceState.Pages.Length)
        {
            MessageBox.Show(this, "Выбранная страница больше не существует.",
                "Копирование страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            plan = PdfCrossTabPageTransfer.Build(
                targetState.WorkingPath, targetState.Pages,
                targetState.SelectedPageIndex, targetState.SourcePageCount,
                sourceState.WorkingPath, sourceState.Pages[sourcePageIndex],
                sourceState.SourcePageCount, WorkstationPolicy.Current.MaxDocumentPages);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Копирование страницы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Создать отдельную PDF-копию со вставленной страницей",
            Filter = "PDF (*.pdf)|*.pdf",
            DefaultExt = ".pdf", AddExtension = true,
            FileName = System.IO.Path.GetFileNameWithoutExtension(
                targetState.OriginalPath) + "_с_дополнением.pdf",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!PdfSaveSafety.TryValidateNewCopy(dialog.FileName,
            [sourceState.OriginalPath, targetState.OriginalPath,
             sourceState.WorkingPath, targetState.WorkingPath], out string warning))
        {
            MessageBox.Show(this, warning, "Копирование страницы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
            $"Вставить страницу {sourcePageIndex + 1} из «{System.IO.Path.GetFileName(sourceState.OriginalPath)}» " +
            $"после страницы {targetState.SelectedPageIndex + 1} документа «{System.IO.Path.GetFileName(targetState.OriginalPath)}»? " +
            "Будет создан отдельный PDF. Подписи, формы и интерактивные элементы " +
            "могут не сохраниться; оба оригинала останутся неизменными.",
            "Новый PDF из двух вкладок", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        using var cancellation = new CancellationTokenSource();
        bool reservedSource = false;
        bool reservedTarget = false;
        bool success = false;
        try
        {
            // Freeze both workspaces while their scratch input copies are read.
            source.SetCrossTabExportBusy(cancellation);
            reservedSource = true;
            destination.SetCrossTabExportBusy(cancellation);
            reservedTarget = true;
            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                await Task.Run(() =>
                    PdfDocumentWriter.SaveMixed(dialog.FileName, plan, token), token);
                return true;
            }, cancellation.Token);
            success = true;
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "Копирование страницы отменено. Оригиналы не изменены.",
                "Копирование страницы", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-cross-tab-page-copy", ex);
            MessageBox.Show(this, "Не удалось создать PDF. Возможны ограничения структуры " +
                "документа, нехватка места или отсутствие доступа к файлу. " +
                "Оригиналы не изменены.", "Копирование страницы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (reservedTarget) destination.SetCrossTabExportBusy(null);
            if (reservedSource) source.SetCrossTabExportBusy(null);
        }
        if (success)
            await OpenPathsAsync([dialog.FileName]);
    }
}
