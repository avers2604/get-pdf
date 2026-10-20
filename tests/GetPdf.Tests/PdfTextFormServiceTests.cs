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
    public void RequiresFreshOutputName()
    {
        string path = Path.Combine(Path.GetTempPath(), "sample.pdf");
        Assert.IsFalse(PdfSaveSafety.TryValidateNewCopy(path, [path], out _));
    }
}
