using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Spreadsheet;
using GetDocument.Core.Workspace;

namespace GetDocument.Office;

/// <summary>
/// Creates new, minimal genuine Office Open XML documents.
/// This is NOT a DOCX/XLSX/PPTX visual editor or a legacy-format converter.
/// Never overwrites an existing file; use independently reviewed templates for corporate layouts.
/// </summary>
public static class OfficeDocumentFactory
{
    public static void CreateNew(WorkspaceDocumentKind kind, string destination)
    {
        string extension = kind switch
        {
            WorkspaceDocumentKind.Text => ".docx",
            WorkspaceDocumentKind.Spreadsheet => ".xlsx",
            WorkspaceDocumentKind.Presentation => ".pptx",
            _ => throw new NotSupportedException("Формат не поддерживает создание нового офисного документа.")
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var target = Path.GetFullPath(destination);
        if (!Path.GetExtension(target).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Расширение файла не соответствует типу документа.", nameof(destination));

        // Do not create arbitrary directories in shared corporate folders.
        var folder = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Каталог назначения отсутствует.");
        if (File.Exists(target)) throw new IOException("Файл уже существует. Оригинал не будет перезаписан.");
        string temporary = Path.Combine(folder, ".get-document-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            switch (kind)
            {
                case WorkspaceDocumentKind.Text:
                    using (var package = WordprocessingDocument.Create(temporary, WordprocessingDocumentType.Document))
                    {
                        var main = package.AddMainDocumentPart();
                        main.Document = new Document(new Body(new Paragraph()));
                        main.Document.Save();
                    }
                    break;
                case WorkspaceDocumentKind.Spreadsheet:
                    using (var package = SpreadsheetDocument.Create(temporary, SpreadsheetDocumentType.Workbook))
                    {
                        var workbookPart = package.AddWorkbookPart();
                        workbookPart.Workbook = new Workbook();
                        var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
                        sheetPart.Worksheet = new Worksheet(new SheetData());
                        sheetPart.Worksheet.Save();
                        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
                        sheets.Append(new Sheet()
                        {
                            Id = workbookPart.GetIdOfPart(sheetPart),
                            SheetId = 1U,
                            Name = "Лист1"
                        });
                        workbookPart.Workbook.Save();
                    }
                    break;
                case WorkspaceDocumentKind.Presentation:
                    using (var presentation = Common.PowerPointUtils.CreatePresentation(temporary))
                    {
                        // Uses Microsoft's MIT-licensed complete presentation sample (master/layout/theme).
                    }
                    break;
            }
            // Atomic on a single filesystem; throws if someone created target during generation.
            // In particular, WORM/source files are never silently replaced.
            File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
