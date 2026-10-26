using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GetDocument.Core.Workspace;

namespace GetDocument.Desktop.Preview;

internal static class PreviewFactory
{
    public static Control Create(WorkspaceDocumentTab item, Func<WorkspaceDocumentTab, Task> saveDraft,
        Func<string, Task> openSavedCopy,
        Func<WorkspaceDocumentTab, string, string[][], Task<bool>> saveXlsxRevision,
        Action<string> status)
    {
        if (item.IsDraft)
            return new OfficeDraftPane(item, saveDraft);
        if (item.FilePath is null)
            return Message(item.Title, "Не указан источник документа.");

        // Office documents: faithful preview + editing + printing through local LibreOffice.
        // The built-in simple DOCX/XLSX editors remain only as a fallback without LibreOffice.
        if (GetDocument.Office.LibreOffice.LibreOfficeRuntime.Handles(item.FilePath) &&
            (GetDocument.Office.LibreOffice.LibreOfficeRuntime.IsAvailable ||
             item.Kind == WorkspaceDocumentKind.Presentation))
            return new OfficeDocumentPane(item.FilePath, status);

        if (item.Kind == WorkspaceDocumentKind.Text &&
            Path.GetExtension(item.FilePath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return new SimpleDocxEditorPane(item.FilePath, openSavedCopy);
        // Legacy Excel workbooks are read-only: do not pretend to convert or edit
        // formulas, chart parts, macros or complex XLSB sheets.
        if (item.Kind == WorkspaceDocumentKind.Spreadsheet)
        {
            string extension = Path.GetExtension(item.FilePath);
            if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsb", StringComparison.OrdinalIgnoreCase))
                return new SpreadsheetReadOnlyPane(item.FilePath);
        }

        if (item.Kind == WorkspaceDocumentKind.Spreadsheet &&
            Path.GetExtension(item.FilePath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return new SimpleXlsxEditPane(item, saveXlsxRevision);
        if (item.Kind == WorkspaceDocumentKind.Pdf)
        {
            try { return new GetDocument.Desktop.Pdf.PdfWorkspacePane(item.FilePath, status, openSavedCopy, isFavorite: false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or
                                       InvalidOperationException or UnauthorizedAccessException)
            {
                return Message(item.Title, "PDF не удалось открыть: " + ex.Message +
                    " Файл может быть повреждён, защищён паролем или превышать лимит 128 МБ / 1000 страниц.");
            }
        }
        if (item.Kind == WorkspaceDocumentKind.Image)
            return new ImagePreviewPane(item.FilePath, openSavedCopy);
        if (Path.GetExtension(item.FilePath) is ".txt" or ".md" or ".csv" or ".TXT" or ".MD" or ".CSV")
            return new TextPreviewPane(item.FilePath);
        return Message(item.Title, "Формат распознан, но встроенный редактор ещё не подключён. " +
            "Содержимое файла не изменено. Для DOCX/XLSX/PPTX продолжается проверка LibreOffice/ProEdit.");
    }

    private static Control Message(string title, string explanation)
    {
        return new StackPanel
        {
            Margin = new Thickness(32),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, Classes = { "title" } },
                new TextBlock { Text = explanation, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
            }
        };
    }
}
