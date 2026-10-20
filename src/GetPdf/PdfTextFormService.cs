using AsposeDocument = Aspose.Pdf.Document;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace GetPdf;

/// <summary>
/// Conservative offline AcroForm text editing. Refuses signatures, XFA,
/// JavaScript/actions, nested/complex fields and password controls.
/// Writes an atomic separate PDF, never overwrites the imported original.
/// </summary>
internal static class PdfTextFormService
{
    internal static IReadOnlyList<string> ReadEditableFieldNames(string source)
    {
        if (new FileInfo(source).Length > PdfFormFillLabPolicy.MaxPdfBytes)
            throw new InvalidDataException("Форма превышает предел 32 МиБ.");
        using var pdf = PdfReader.Open(Path.GetFullPath(source), PdfDocumentOpenMode.Import);
        var form = pdf.Internals.Catalog.Elements.GetDictionary("/AcroForm")
            ?? throw new NotSupportedException("Этот PDF не содержит текстовой AcroForm.");
        var fields = form.Elements.GetObject("/Fields") as PdfArray
            ?? throw new InvalidDataException("Повреждено дерево AcroForm.");
        if (fields.Elements.Count is < 1 or > PdfFormFillLabPolicy.MaxFields)
            throw new NotSupportedException("Число полей формы вне поддерживаемого диапазона.");
        var names = new List<string>();
        foreach (var item in fields.Elements)
        {
            var dict = item is PdfReference reference
                ? reference.Value as PdfDictionary : item as PdfDictionary;
            if (dict is null) throw new InvalidDataException("Повреждён объект поля.");
            string name = dict.Elements.GetString("/T");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120 ||
                name.Any(char.IsControl) || names.Contains(name, StringComparer.Ordinal))
                throw new InvalidDataException("Некорректные либо повторные имена полей.");
            names.Add(name);
        }
        PdfFormFillLabPolicy.ValidateDocument(pdf,
            names.ToDictionary(n => n, _ => "", StringComparer.Ordinal));
        return names;
    }

    internal static void ValidateValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > PdfFormFillLabPolicy.MaxFields)
            throw new ArgumentException("Выберите от 1 до 32 текстовых полей.");
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120 ||
                name.Any(char.IsControl) || value is null ||
                value.Length > PdfFormFillLabPolicy.MaxValueLength ||
                PdfFormFillLabPolicy.HasUnsafeText(value))
                throw new ArgumentException("Некорректное имя или значение поля.");
        }
    }

    internal static void FillNewCopy(string source, string output,
        IReadOnlyDictionary<string, string> values,
        CancellationToken token = default)
    {
        ValidateValues(values);
        if (!PdfSaveSafety.TryValidateNewCopy(output, [source], out string reason))
            throw new IOException(reason);
        var known = ReadEditableFieldNames(source);
        if (values.Keys.Except(known, StringComparer.Ordinal).Any())
            throw new InvalidDataException("Обнаружено неизвестное поле AcroForm.");
        token.ThrowIfCancellationRequested();
        using (var pdf = PdfReader.Open(source, PdfDocumentOpenMode.Import))
            PdfFormFillLabPolicy.ValidateDocument(pdf, values);
        string path = Path.GetFullPath(output);
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder,
            ".getpdf-form-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            using (var doc = AsposeDocument.Open(source))
            {
                // Explicitly installed font, not a silent PDF base-14 fallback.
                string fontPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
                if (!File.Exists(fontPath))
                    throw new NotSupportedException("Для формы требуется системный шрифт Arial.");
                var font = Aspose.Pdf.Text.FontRepository.OpenFont(fontPath);
                foreach (var (name, value) in values)
                {
                    token.ThrowIfCancellationRequested();
                    if (doc.Form[name] is not Aspose.Pdf.Forms.TextBoxField textbox)
                        throw new InvalidDataException("Тип текстового поля изменился.");
                    textbox.DefaultAppearance = new Aspose.Pdf.Annotations.DefaultAppearance(
                        font, 10, System.Drawing.Color.Black);
                    textbox.Value = value;
                    // Persist a real /AP /N visual appearance. Setting /V
                    // alone often displays a blank field in other readers.
                    textbox.UpdateAppearances();
                }
                doc.Save(temporary);
            }
            token.ThrowIfCancellationRequested();
            using (var check = AsposeDocument.Open(temporary))
            {
                foreach (var (name, value) in values)
                    if (check.Form[name] is not Aspose.Pdf.Forms.TextBoxField filled ||
                        filled.Value != value || filled.NormalAppearance is null)
                        throw new InvalidDataException(
                            "Значение или визуальное представление текстового поля не сохранилось.");
            }
            using (var original = PdfReader.Open(source, PdfDocumentOpenMode.Import))
                PdfOutputVerifier.Verify(temporary, original.PageCount);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
