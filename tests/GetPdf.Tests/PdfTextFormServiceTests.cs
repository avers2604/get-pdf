using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfTextFormServiceTests
{
    [TestMethod]
    public void InventoriesOnlyFlatUnsignedTextFieldsWithoutReadingValues()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-form-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "form.pdf");
            using (var doc = new PdfDocument())
            {
                doc.AddPage();
                var root = new PdfDictionary(doc);
                var fields = new PdfArray(doc);
                var field = new PdfDictionary(doc);
                field.Elements.SetName("/FT", "/Tx");
                field.Elements.SetString("/T", "ФИО");
                fields.Elements.Add(field);
                root.Elements.SetObject("/Fields", fields);
                doc.Internals.Catalog.Elements.SetObject("/AcroForm", root);
                doc.Save(file);
            }
            var names = PdfTextFormService.ReadEditableFieldNames(file);
            CollectionAssert.AreEqual(new[] { "ФИО" }, names.ToArray());
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void RejectsDuplicateNamesAndUnapprovedInputValues()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PdfTextFormService.ValidateValues(
            new Dictionary<string, string> { ["name"] = "X\nY" }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfTextFormService.ValidateValues(
            new Dictionary<string, string> { ["name"] = new string('Ж', 513) }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfTextFormService.ValidateValues(
            new Dictionary<string, string> { [""] = "valid" }));
    }

    [TestMethod]
    public void FormFillNeverOverwritesOriginalEvenWhenDestinationIsSameFile()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-worm-form-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string original = Path.Combine(folder, "original.pdf");
            using (var doc = new PdfDocument())
            {
                doc.AddPage();
                doc.Save(original);
            }
            byte[] before = File.ReadAllBytes(original);
            Assert.ThrowsExactly<IOException>(() =>
                PdfTextFormService.FillNewCopy(original, original,
                    new Dictionary<string, string> { ["ФИО"] = "Тест" }));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(original));
            Assert.AreEqual(1, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void RejectsInvisibleBidiControlsInFormsAndJsonRequests()
    {
        string spoofed = "ФИО" + '\u202e' + "123";
        Assert.IsTrue(PdfFormFillLabPolicy.HasUnsafeText(spoofed));
        Assert.IsFalse(PdfFormFillLabPolicy.HasUnsafeText("Иван Иванов"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PdfTextFormService.ValidateValues(
                new Dictionary<string, string> { ["ФИО"] = spoofed }));
        var request = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(
                new { version = 1, fields = new Dictionary<string, string> { ["ФИО"] = spoofed } }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfFormFillLabPolicy.ReadRequest(request));
    }

    [TestMethod]
    public void RejectsInvisibleFormattingInFieldNames()
    {
        string disguised = "ФИО" + '\u202e';
        Assert.ThrowsExactly<ArgumentException>(() =>
            PdfTextFormService.ValidateValues(
                new Dictionary<string, string> { [disguised] = "Иван Иванов" }));
        var request = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(
                new { version = 1, fields = new Dictionary<string, string> { [disguised] = "Тест" } }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfFormFillLabPolicy.ReadRequest(request));
    }

    [TestMethod]
    public void PdfAcroFormValidationRejectsSpoofedFieldIdentifier()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var form = new PdfDictionary(doc);
        var fields = new PdfArray(doc);
        var field = new PdfDictionary(doc);
        field.Elements.SetName("/FT", "/Tx");
        string disguised = "ФИО" + '\u202e';
        field.Elements.SetString("/T", disguised);
        fields.Elements.Add(field);
        form.Elements.SetObject("/Fields", fields);
        doc.Internals.Catalog.Elements.SetObject("/AcroForm", form);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfFormFillLabPolicy.ValidateDocument(doc,
                new Dictionary<string, string> { [disguised] = "Тест" }));
    }

    [TestMethod]
    public void RequiresFreshOutputName()
    {
        string path = Path.Combine(Path.GetTempPath(), "sample.pdf");
        Assert.IsFalse(PdfSaveSafety.TryValidateNewCopy(path, [path], out _));
    }
}
