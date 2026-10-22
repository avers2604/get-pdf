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
            GetMessageBox.Show(this, "Для переноса нужны открытые PDF без несохранённых изменений. " +
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
            var insert = new MenuItem { Header = "Вставить в черновик (Ctrl+Z)" };
            insert.Click += async (_, _) =>
                await InsertPageToTabAsync(source, target);
            var saveCopy = new MenuItem { Header = "Создать отдельный PDF…" };
            saveCopy.Click += async (_, _) => await CopyPageToTabAsync(source, target);
            item.Items.Add(insert);
            item.Items.Add(saveCopy);
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

    private async Task InsertPageToTabAsync(PdfViewerWindow source,
        PdfViewerWindow destination, Guid? draggedPageId = null)
    {
        if (_shutdownPending || ReferenceEquals(source, destination)) return;
        try { await destination.EnsureInitialDocumentLoadedAsync(); }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-cross-insert-target-load", ex);
            GetMessageBox.Show(this, "Не удалось открыть PDF назначения.",
                "Вставка страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var from = source.GetCleanTransferSnapshot();
        var into = destination.GetWritableTransferSnapshot();
        if (from is null || into is null)
        {
            GetMessageBox.Show(this, "Исходный PDF должен быть без несохранённых правок, " +
                "а документ назначения — доступен для редактирования.",
                "Вставка страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int at = draggedPageId is Guid id ?
            PdfCrossTabPageTransfer.FindPageById(from.Pages, id) : from.SelectedPageIndex;
        if (at < 0 || at >= from.Pages.Length)
        {
            GetMessageBox.Show(this, "Исходная страница была удалена или изменена.",
                "Вставка страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (GetMessageBox.Show(this,
            $"Вставить страницу {at + 1} из «{System.IO.Path.GetFileName(from.OriginalPath)}» " +
            $"после страницы {into.SelectedPageIndex + 1} «{System.IO.Path.GetFileName(into.OriginalPath)}»? " +
            "Это изменит только черновик вкладки назначения. Ctrl+Z отменит вставку, " +
            "а исходные PDF не изменятся. Структурные подписи, формы и ссылки " +
            "могут быть утрачены при последующем сохранении новой копии.",
            "Вставить в черновик PDF", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        // Revalidate snapshots after modal confirmation, before reserving
        // both workspaces. A stale drag cannot insert the wrong PDF page.
        var latestFrom = source.GetCleanTransferSnapshot();
        var latestInto = destination.GetWritableTransferSnapshot();
        if (latestFrom is null || latestInto is null ||
            !from.Pages.SequenceEqual(latestFrom.Pages) ||
            !into.Pages.SequenceEqual(latestInto.Pages) ||
            from.WorkingPath != latestFrom.WorkingPath ||
            into.WorkingPath != latestInto.WorkingPath ||
            latestFrom.SelectedPageIndex < 0 ||
            latestInto.SelectedPageIndex != into.SelectedPageIndex)
        {
            GetMessageBox.Show(this, "Один из документов изменился. Повторите перенос.",
                "Вставка страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        bool sourceLocked = false, targetLocked = false;
        try
        {
            source.SetCrossTabExportBusy(cancellation);
            sourceLocked = true;
            destination.SetCrossTabExportBusy(cancellation);
            targetLocked = true;
            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                await destination.InsertExternalPageInDraftAsync(
                    latestFrom, latestFrom.Pages[at].Id, latestInto, token);
                return true;
            }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            GetMessageBox.Show(this, "Вставка отменена. Оригиналы не изменены.",
                "Вставка страницы", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-cross-insert", ex);
            GetMessageBox.Show(this, "Не удалось вставить страницу. Черновик сохранён " +
                "без изменений, если подготовка была прервана.",
                "Вставка страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (targetLocked) destination.SetCrossTabExportBusy(null);
            if (sourceLocked) source.SetCrossTabExportBusy(null);
        }
    }

    private async Task CopyPageToTabAsync(PdfViewerWindow source,
        PdfViewerWindow destination, Guid? draggedPageId = null)
    {
        // Target tabs are lazy-loaded until selected. An explicit copy
        // operation may require opening the target without tab switching.
        try { await destination.EnsureInitialDocumentLoadedAsync(); }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-cross-tab-target-open", ex);
            GetMessageBox.Show(this, "Не удалось открыть PDF назначения.",
                "Копирование страницы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Recheck BOTH snapshots after the await: a user may have edited the
        // source while the inactive target was imported.
        var sourceState = source.GetCleanTransferSnapshot();
        var targetState = destination.GetCleanTransferSnapshot();
        if (sourceState is null || targetState is null)
        {
            GetMessageBox.Show(this, "Оба PDF должны быть открыты без несохранённых изменений. " +
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
            GetMessageBox.Show(this, "Выбранная страница больше не существует.",
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
            GetMessageBox.Show(this, ex.Message, "Копирование страницы",
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
            GetMessageBox.Show(this, warning, "Копирование страницы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (GetMessageBox.Show(this,
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
            GetMessageBox.Show(this, "Копирование страницы отменено. Оригиналы не изменены.",
                "Копирование страницы", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-cross-tab-page-copy", ex);
            GetMessageBox.Show(this, "Не удалось создать PDF. Возможны ограничения структуры " +
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
