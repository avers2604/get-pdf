using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf;

[TestClass]
public sealed class PdfSaveSafetyTests
{
    [TestMethod]
    public void RefusesOriginalAndAdditionalImportedPdf()
    {
        var root = Path.Combine(Path.GetTempPath(), "getpdf-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first.pdf");
            var other = Path.Combine(root, "other.pdf");
            File.WriteAllText(first, "source");
            File.WriteAllText(other, "source");
            Assert.IsFalse(PdfSaveSafety.TryValidateNewCopy(first, [first, other], out _));
            Assert.IsFalse(PdfSaveSafety.TryValidateNewCopy(other.ToUpperInvariant(), [first, other], out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void RefusesExistingUnrelatedFileAndNonPdf()
    {
        var root = Path.Combine(Path.GetTempPath(), "getpdf-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "taken.pdf");
            File.WriteAllText(existing, "do not overwrite");
            Assert.IsFalse(PdfSaveSafety.TryValidateNewCopy(existing, [], out _));
            Assert.IsFalse(PdfSaveSafety.TryValidateNewCopy(Path.Combine(root, "foo.txt"), [], out _));
            Assert.AreEqual("do not overwrite", File.ReadAllText(existing));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void AcceptsNewPdfCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "getpdf-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.pdf");
            File.WriteAllText(source, "source");
            Assert.IsTrue(PdfSaveSafety.TryValidateNewCopy(Path.Combine(root, "new-copy.pdf"), [source], out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
