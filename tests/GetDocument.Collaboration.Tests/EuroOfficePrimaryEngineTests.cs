using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class EuroOfficePrimaryEngineTests
{
    private static readonly byte[] SigningKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("euro-office-local-regression-test-key"));

    private static OnlyOfficeSessionOptions Session(string fileName = "Приказ.docx", bool worm = false) =>
        new(
            DocumentId: "corporate-document-42",
            RevisionId: "revision-1",
            DisplayFileName: fileName,
            UserId: "employee-23",
            UserDisplayName: "Тестовый сотрудник",
            StorageOrigin: new Uri("https://files.intra.get.test/"),
            DocumentUrl: new Uri("https://files.intra.get.test/office/42/content"),
            CallbackUrl: new Uri("https://files.intra.get.test/office/42/callback"),
            DocumentServerOrigin: new Uri("https://office.intra.get.test/"),
            CanEdit: true,
            IsWormOriginal: worm);

    [TestMethod]
    public void EuroOfficeIsThePrimaryServerEditor()
    {
        Assert.AreEqual(OfficeServerEngine.EuroOffice, OfficeServerEnginePolicy.Primary);
    }

    [DataTestMethod]
    [DataRow("Приказ.docx", "word")]
    [DataRow("Ведомость.xlsx", "cell")]
    [DataRow("Слайды.pptx", "slide")]
    public void DefaultServerBootstrapBuildsSignedEuroOfficeDocsApiConfiguration(
        string fileName, string expectedType)
    {
        var bootstrap = OfficeServerEnginePolicy.Create(Session(fileName), SigningKey);
        using var json = JsonDocument.Parse(bootstrap.ConfigurationJson);
        var config = json.RootElement;

        Assert.AreEqual(expectedType, config.GetProperty("documentType").GetString());
        Assert.AreEqual("fast", config.GetProperty("editorConfig")
            .GetProperty("coEditing").GetProperty("mode").GetString());
        Assert.AreEqual("ru", config.GetProperty("editorConfig").GetProperty("lang").GetString());
        Assert.IsFalse(config.GetRawText().Contains("smb://", StringComparison.OrdinalIgnoreCase));
        var verified = Hs256Jwt.Validate(config.GetProperty("token").GetString()!, SigningKey);
        Assert.AreEqual(bootstrap.DocumentKey,
            verified.GetProperty("document").GetProperty("key").GetString());
    }

    [TestMethod]
    public void EuroOfficeCannotModifyWormOriginal()
    {
        var bootstrap = OfficeServerEnginePolicy.Create(Session(worm: true), SigningKey);
        using var json = JsonDocument.Parse(bootstrap.ConfigurationJson);
        var root = json.RootElement;
        Assert.AreEqual("view", root.GetProperty("editorConfig").GetProperty("mode").GetString());
        Assert.IsFalse(root.GetProperty("document").GetProperty("permissions")
            .GetProperty("edit").GetBoolean());
    }

    [TestMethod]
    public void OnlyOfficeLegacyMustBeSelectedExplicitly()
    {
        var primary = OfficeServerEnginePolicy.Create(Session(), SigningKey);
        var legacy = OfficeServerEnginePolicy.Create(
            Session(), SigningKey, OfficeServerEngine.OnlyOfficeLegacy);
        Assert.AreEqual(primary.DocumentKey, legacy.DocumentKey);
        Assert.AreEqual(primary.ConfigurationJson, legacy.ConfigurationJson);
    }

    [TestMethod]
    public void CollaboraWopiDoesNotSilentlyUseDocsApi()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OfficeServerEnginePolicy.Create(
                Session(), SigningKey, OfficeServerEngine.CollaboraWopi));
    }

    [TestMethod]
    public void UnknownEngineFailsClosed()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OfficeServerEnginePolicy.Create(
                Session(), SigningKey, (OfficeServerEngine)999));
    }

    [TestMethod]
    public void RejectedEuroOfficeInputsAreNotConvertedIntoFallbackSessions()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeServerEnginePolicy.Create(Session() with
            {
                DocumentUrl = new Uri("http://files.intra.get.test/office/42/content")
            }, SigningKey));
    }
}
