using System.Text.Json;
using GetDocument.Collaboration;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

/// <summary>
/// Ensures a signed Docs API launch is not accidentally treated as a LibreOffice
/// UNO capability. A real remote adapter must pass separate API compatibility tests.
/// </summary>
[TestClass]
public sealed class EuroOfficeFeatureBoundaryTests
{
    private static OnlyOfficeSessionOptions Session() => new(
        DocumentId: "test-document",
        RevisionId: "revision-one",
        DisplayFileName: "Ведомость.xlsx",
        UserId: "employee-1",
        UserDisplayName: "Тестовый пользователь",
        StorageOrigin: new Uri("https://files.get.test/"),
        DocumentUrl: new Uri("https://files.get.test/document"),
        CallbackUrl: new Uri("https://files.get.test/callback"),
        DocumentServerOrigin: new Uri("https://office.get.test/"),
        CanEdit: true,
        IsWormOriginal: false);

    [TestMethod]
    public void SignedEditableEuroOfficeSessionNeverEnablesLocalUnoCommands()
    {
        var bootstrap = EuroOfficeSessionFactory.Create(
            Session(), Enumerable.Repeat((byte)0x6c, 32).ToArray(),
            immutableRevisionSinkReady: true);
        using var configuration = JsonDocument.Parse(bootstrap.ConfigurationJson);
        Assert.AreEqual("edit",
            configuration.RootElement.GetProperty("editorConfig").GetProperty("mode").GetString());

        foreach (string kind in new[] { "writer", "calc", "impress" })
            Assert.AreEqual(0, OfficeFeatureCatalog.ForProvider(
                kind, OfficeFeatureProvider.EuroOfficeDocumentServer).Count);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OfficeFeatureCatalog.Require("calc", "calc-pivot-sum", readOnly: false,
                provider: OfficeFeatureProvider.EuroOfficeDocumentServer));
        Assert.IsTrue(OfficeFeatureCatalog.ForProvider(
            "calc", OfficeFeatureProvider.LibreOfficeUno).Count > 0);
    }

    [TestMethod]
    public void DenyByDefaultEuroOfficeLaunchMustNotClaimFeatureParity()
    {
        var bootstrap = OfficeServerEnginePolicy.Create(
            Session(), Enumerable.Repeat((byte)0x71, 32).ToArray());
        using var configuration = JsonDocument.Parse(bootstrap.ConfigurationJson);
        Assert.AreEqual("view",
            configuration.RootElement.GetProperty("editorConfig").GetProperty("mode").GetString());

        foreach (var provider in new[]
                 {
                     OfficeFeatureProvider.EuroOfficeDocumentServer,
                     OfficeFeatureProvider.OnlyOfficeLegacy,
                     OfficeFeatureProvider.CollaboraWopi
                 })
            Assert.AreEqual(0, OfficeFeatureCatalog.ForProvider("calc", provider).Count);
    }
}
