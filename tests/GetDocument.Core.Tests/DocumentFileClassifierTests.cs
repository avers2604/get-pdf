using GetDocument.Core.DocumentFiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public class DocumentFileClassifierTests
{
    [TestMethod]
    public void RecognizesModernFormatsWithoutConversion()
    {
        var word = DocumentFileClassifier.Classify("Отчёт.DOCX");
        Assert.AreEqual(DocumentFileKind.Text, word.Kind);
        Assert.AreEqual(".docx", word.Extension);
        Assert.IsFalse(word.RequiresConversion);
        Assert.AreEqual(DocumentFileKind.Image, DocumentFileClassifier.Classify("photo.TIFF").Kind);
        Assert.AreEqual(DocumentFileKind.Pdf, DocumentFileClassifier.Classify("акт.pdf").Kind);
    }
    [TestMethod]
    public void LegacyOfficeFormatsRequireWorkingCopyConversion()
    {
        foreach (var name in new[] { "file.doc", "data.xls", "show.ppt", "screen.pps", "legacy.rtf", "binary.xlsb" })
        {
            var format = DocumentFileClassifier.Classify(name);
            Assert.AreNotEqual(DocumentFileKind.Unsupported, format.Kind, name);
            Assert.IsTrue(format.RequiresConversion, name);
        }
    }
    [TestMethod]
    public void UnknownOrAbsentExtensionsAreNotOfficeFiles()
    {
        foreach (var name in new string?[] { null, "", " ", "noextension", "danger.exe", "script.js" })
            Assert.AreEqual(DocumentFileKind.Unsupported, DocumentFileClassifier.Classify(name).Kind);
    }
    [TestMethod]
    public void ClassificationDoesNotReadOrCreateAFile()
    {
        var name = "non-existent-" + Guid.NewGuid().ToString("N") + ".xlsx";
        Assert.AreEqual(DocumentFileKind.Spreadsheet, DocumentFileClassifier.Classify(name).Kind);
        Assert.IsFalse(File.Exists(name));
    }
}
