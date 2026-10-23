using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class OnlyOfficeBridgeTests
{
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("local-only-test-secret"));

    private static OnlyOfficeSessionOptions Options(bool editable = true, bool worm = false,
        string fileName = "Документ.docx") => new(
            DocumentId: "get-document-42",
            RevisionId: "2026-10-23-r1",
            DisplayFileName: fileName,
            UserId: "employee-17",
            UserDisplayName: "Иван Иванов",
            StorageOrigin: new Uri("https://files.intra.get.test/"),
            DocumentUrl: new Uri("https://files.intra.get.test/session/42/content"),
            CallbackUrl: new Uri("https://files.intra.get.test/api/docs/callback"),
            DocumentServerOrigin: new Uri("https://office.intra.get.test/"),
            CanEdit: editable,
            IsWormOriginal: worm);

    [DataTestMethod]
    [DataRow("Дело.docx", "word")]
    [DataRow("Таблица.xlsx", "cell")]
    [DataRow("Презентация.pptx", "slide")]
    public void BuildsSignedOfficeConfigurationForThreeFormats(string fileName, string expectedType)
    {
        var config = OnlyOfficeSessionFactory.Create(Options(fileName: fileName), Key);
        using var signedJson = JsonDocument.Parse(config.ConfigurationJson);
        var root = signedJson.RootElement;
        Assert.AreEqual(expectedType, root.GetProperty("documentType").GetString());
        Assert.AreEqual("edit", root.GetProperty("editorConfig").GetProperty("mode").GetString());
        Assert.IsTrue(root.GetProperty("document").GetProperty("permissions").GetProperty("edit").GetBoolean());
        Assert.AreEqual(fileName, root.GetProperty("document").GetProperty("title").GetString());
        var authenticated = Hs256Jwt.Validate(root.GetProperty("token").GetString()!, Key);
        Assert.AreEqual(config.DocumentKey, authenticated.GetProperty("document").GetProperty("key").GetString());
        Assert.AreEqual("employee-17",
            authenticated.GetProperty("editorConfig").GetProperty("user").GetProperty("id").GetString());
    }

    [TestMethod]
    public void WormOriginalCanOnlyBeOpenedReadOnlyEvenWhenCanEditTrue()
    {
        var config = OnlyOfficeSessionFactory.Create(Options(editable: true, worm: true), Key);
        using var root = JsonDocument.Parse(config.ConfigurationJson);
        Assert.AreEqual("view", root.RootElement.GetProperty("editorConfig").GetProperty("mode").GetString());
        Assert.IsFalse(root.RootElement.GetProperty("document").GetProperty("permissions").GetProperty("edit").GetBoolean());
    }

    [TestMethod]
    public void RevisionIdChangesTheEditorKeyButSameRevisionIsStable()
    {
        var a = OnlyOfficeSessionFactory.Create(Options(), Key);
        var b = OnlyOfficeSessionFactory.Create(Options(), Key);
        var c = OnlyOfficeSessionFactory.Create(Options() with { RevisionId = "r2" }, Key);
        Assert.AreEqual(a.DocumentKey, b.DocumentKey);
        Assert.AreNotEqual(a.DocumentKey, c.DocumentKey);
    }

    [TestMethod]
    public void RejectsExternalAndInsecureDocumentUrls()
    {
        Assert.ThrowsExactly<ArgumentException>(() => OnlyOfficeSessionFactory.Create(
            Options() with { DocumentUrl = new Uri("http://files.intra.get.test/content") }, Key));
        Assert.ThrowsExactly<ArgumentException>(() => OnlyOfficeSessionFactory.Create(
            Options() with { CallbackUrl = new Uri("https://evil.example/callback") }, Key));
        Assert.ThrowsExactly<ArgumentException>(() => OnlyOfficeSessionFactory.Create(
            Options() with { DocumentServerOrigin = new Uri("http://office.intra.get.test/") }, Key));
        Assert.ThrowsExactly<ArgumentException>(() => OnlyOfficeSessionFactory.Create(
            Options() with { DocumentUrl = new Uri("https://u:p@files.intra.get.test/content") }, Key));
    }

    [TestMethod]
    public void RejectsShortSigningKeysAndUnsupportedFormats()
    {
        Assert.ThrowsExactly<ArgumentException>(() => OnlyOfficeSessionFactory.Create(Options(), new byte[12]));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OnlyOfficeSessionFactory.Create(Options(fileName: "x.pdf"), Key));
    }

    [DataTestMethod]
    [DataRow(2)]
    [DataRow(6)]
    public void SignedCallbackCanPlanNewRevisionDownload(int status)
    {
        var session = OnlyOfficeSessionFactory.Create(Options(), Key);
        string token = Hs256Jwt.Sign(new {
            operation = "callback",
            payload = new {
                key = session.DocumentKey,
                status,
                url = "https://office.intra.get.test/cache/download/42"
            }
        }, Key);
        var candidate = OnlyOfficeCallbackVerifier.Plan(token,
            session.DocumentKey, new Uri("https://office.intra.get.test/"), Key);
        Assert.IsNotNull(candidate);
        Assert.AreEqual(status, candidate.Status);
        Assert.AreEqual("office.intra.get.test", candidate.DownloadUrl.Host);
    }

    [TestMethod]
    public void SignedNoChangesStatusDoesNotPublishRevision()
    {
        var session = OnlyOfficeSessionFactory.Create(Options(), Key);
        string token = Hs256Jwt.Sign(new {
            operation = "callback",
            payload = new { key = session.DocumentKey, status = 4 }
        }, Key);
        Assert.IsNull(OnlyOfficeCallbackVerifier.Plan(token, session.DocumentKey,
            new Uri("https://office.intra.get.test/"), Key));
    }

    [TestMethod]
    public void TamperedJwtWrongKeyAndWrongOperationAreRejected()
    {
        var session = OnlyOfficeSessionFactory.Create(Options(), Key);
        string token = Hs256Jwt.Sign(new {
            operation = "callback",
            payload = new {
                key = session.DocumentKey, status = 2,
                url = "https://office.intra.get.test/cache/42"
            }
        }, Key);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            OnlyOfficeCallbackVerifier.Plan(token + "x", session.DocumentKey,
                new Uri("https://office.intra.get.test/"), Key));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            OnlyOfficeCallbackVerifier.Plan(token, "wrong-session",
                new Uri("https://office.intra.get.test/"), Key));
        string differentOperation = Hs256Jwt.Sign(new {
            operation = "download",
            payload = new { key = session.DocumentKey, status = 2,
                url = "https://office.intra.get.test/cache/42" }
        }, Key);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            OnlyOfficeCallbackVerifier.Plan(differentOperation, session.DocumentKey,
                new Uri("https://office.intra.get.test/"), Key));
    }

    [TestMethod]
    public void SignedCallbackCannotRedirectDownloaderToExternalOrCredentialsUrl()
    {
        var session = OnlyOfficeSessionFactory.Create(Options(), Key);
        foreach (string url in new[] {
            "https://evil.example/cache/42",
            "http://office.intra.get.test/cache/42",
            "https://u:p@office.intra.get.test/cache/42"
        })
        {
            string token = Hs256Jwt.Sign(new { operation = "callback",
                payload = new { key = session.DocumentKey, status = 2, url } }, Key);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                OnlyOfficeCallbackVerifier.Plan(token, session.DocumentKey,
                    new Uri("https://office.intra.get.test/"), Key));
        }
    }

    [TestMethod]
    public void CallbackDoesNotContainAnyFilesystemWriteOrAutomaticDownload()
    {
        var session = OnlyOfficeSessionFactory.Create(Options(), Key);
        string token = Hs256Jwt.Sign(new { operation = "callback",
            payload = new { key = session.DocumentKey, status = 2,
                url = "https://office.intra.get.test/cache/42" } }, Key);
        var candidate = OnlyOfficeCallbackVerifier.Plan(token, session.DocumentKey,
            new Uri("https://office.intra.get.test/"), Key);
        Assert.IsNotNull(candidate);
        Assert.IsTrue(candidate.RequiresApprovedRevisionSink);
    }
}
