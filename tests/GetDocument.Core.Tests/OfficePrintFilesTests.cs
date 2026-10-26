using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class OfficePrintFilesTests
{
    [TestMethod]
    public void ExportsUseUniqueOpaquePdfNamesInsideDedicatedFolder()
    {
        string first = OfficePrintFiles.NewExportPath();
        string second = OfficePrintFiles.NewExportPath();
        Assert.AreNotEqual(first, second);
        Assert.IsTrue(OfficePrintFiles.IsOwnedExport(first));
        Assert.IsTrue(OfficePrintFiles.IsOwnedExport(second));
        Assert.AreEqual("office-", Path.GetFileName(first)[..7]);
        Assert.AreEqual(OfficePrintFiles.ExportFolder, Path.GetDirectoryName(first));
    }

    [TestMethod]
    public void CleanupDeletesOnlyRecognizedOfficeExport()
    {
        string owned = OfficePrintFiles.NewExportPath();
        string userDocument = Path.Combine(OfficePrintFiles.ExportFolder,
            "user-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllText(owned, "%PDF-1.4\n");
            // A file in the same folder must not be deleted without a trusted
            // GUID-qualified export name.
            File.WriteAllText(userDocument, "%PDF-1.4\n");
            OfficePrintFiles.DeleteOwnedExport(userDocument);
            Assert.IsTrue(File.Exists(userDocument));
            OfficePrintFiles.DeleteOwnedExport(owned);
            Assert.IsFalse(File.Exists(owned));
            Assert.IsFalse(OfficePrintFiles.IsOwnedExport("../original.pdf"));
        }
        finally
        {
            File.Delete(owned);
            File.Delete(userDocument);
        }
    }

    [TestMethod]
    public void ForgedNamesAndOtherDirectoriesCannotBeTreatedAsTemporaryExports()
    {
        string root = OfficePrintFiles.ExportFolder;
        foreach (string candidate in new[]
        {
            Path.Combine(root, "office-" + new string('z', 32) + ".pdf"),
            Path.Combine(root, "office-" + Guid.NewGuid().ToString("N") + ".docx"),
            Path.Combine(root, "user.pdf"),
            Path.Combine(Path.GetTempPath(), "office-" + Guid.NewGuid().ToString("N") + ".pdf")
        })
            Assert.IsFalse(OfficePrintFiles.IsOwnedExport(candidate));
    }
}
