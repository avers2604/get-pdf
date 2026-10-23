using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GetDocument.Core.Workspace;

namespace GetDocument.Desktop.Preview;

internal static class PreviewFactory
{
    public static Control Create(WorkspaceDocumentTab item, Func<WorkspaceDocumentTab, Task> saveDraft,
        Func<string, Task> openSavedCopy,
        Func<WorkspaceDocumentTab, string, string[][], Task<bool>> saveXlsxRevision)
    {
        if (item.IsDraft)
            return new OfficeDraftPane(item, saveDraft);
        if (item.FilePath is null)
            return Message(item.Title, "Не указан источник документа.");

        if (item.Kind == WorkspaceDocumentKind.Text &&
            Path.GetExtension(item.FilePath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return new SimpleDocxEditorPane(item.FilePath, openSavedCopy);
        if (item.Kind == WorkspaceDocumentKind.Spreadsheet &&
            Path.GetExtension(item.FilePath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return new SimpleXlsxEditPane(item, saveXlsxRevision);
        if (item.Kind == WorkspaceDocumentKind.Pdf)
            return new PdfPreviewPane(item.FilePath);
        if (item.Kind == WorkspaceDocumentKind.Image)
            return new ImagePreviewPane(item.FilePath);
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
                new TextBlock { Text = title, FontSize = 24 },
                new TextBlock { Text = explanation, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
            }
        };
    }
}
