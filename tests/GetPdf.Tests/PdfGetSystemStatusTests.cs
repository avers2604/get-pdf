using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfGetSystemStatusTests
{
    [TestMethod]
    public void DisabledByDefaultAndPolicyCanEnableOptIn()
    {
        var defaults = WorkstationPolicy.Parse("{\"Version\":1}");
        Assert.IsFalse(defaults.ExportGetSystemStatus);
        var enabled = WorkstationPolicy.Parse(
            "{\"Version\":1,\"ExportGetSystemStatus\":true,\"RestoreTabs\":false}");
        Assert.IsTrue(enabled.ExportGetSystemStatus);
        Assert.IsFalse(enabled.RestoreTabs);
    }

    [TestMethod]
    public void ContractContainsOnlyNonIdentifyingCountsAndCapabilities()
    {
        var instant = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var payload = PdfGetSystemStatusWriter.Create(instant, 3, 12, true);
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("get-system.pdf.status.v1",
            document.RootElement.GetProperty("Schema").GetString());
        Assert.AreEqual(12, document.RootElement.GetProperty("OpenDocuments").GetInt32());
        // The public schema ID deliberately includes ".pdf", so checking
        // that substring would yield a false positive. Assert an explicit
        // minimal allowlist instead of guessing from incidental text.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Schema", "Product", "ApplicationVersion", "GeneratedUtc",
            "OpenWindows", "OpenDocuments", "PrintingAllowed", "OfflineOnly"
        };
        var actual = document.RootElement.EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(allowed.SetEquals(actual));
        Assert.IsFalse(json.Contains("C:\\", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("\\"SourcePath\\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\\"FileName\\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WritingLatestStatusIsAtomicAndBounded()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "getpdf-status.v1.json");
            PdfGetSystemStatusWriter.WriteTo(file,
                PdfGetSystemStatusWriter.Create(DateTimeOffset.UtcNow, 1, 4, true));
            PdfGetSystemStatusWriter.WriteTo(file,
                PdfGetSystemStatusWriter.Create(DateTimeOffset.UtcNow, 0, 0, false));
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            Assert.AreEqual(0, json.RootElement.GetProperty("OpenDocuments").GetInt32());
            Assert.AreEqual(0, Directory.GetFiles(folder, "*.tmp").Length);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void DisablingExportRemovesStaleLocalSnapshotWithoutDeletingOtherSettings()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-status-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string status = Path.Combine(folder, "getpdf-status.v1.json");
            string other = Path.Combine(folder, "user-policy.json");
            File.WriteAllText(other, "{}");
            PdfGetSystemStatusWriter.WriteTo(status,
                PdfGetSystemStatusWriter.Create(DateTimeOffset.UtcNow, 1, 2, true));
            Assert.IsTrue(File.Exists(status));
            PdfGetSystemStatusWriter.ClearFrom(status);
            Assert.IsFalse(File.Exists(status));
            Assert.IsTrue(File.Exists(other));
            PdfGetSystemStatusWriter.ClearFrom(status);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void RejectsNegativeAndUnreasonablyLargeCounters()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfGetSystemStatusWriter.Create(DateTimeOffset.UtcNow, -1, 1, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfGetSystemStatusWriter.Create(DateTimeOffset.UtcNow, 1, 100000, true));
    }
}
