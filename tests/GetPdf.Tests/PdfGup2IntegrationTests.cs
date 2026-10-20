using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfGup2IntegrationTests
{
    [TestMethod]
    public void PortalIsDisabledUntilItPolicyExplicitlyOptsIn()
    {
        var policy = WorkstationPolicy.Parse("{\"Version\":1}");
        Assert.IsFalse(policy.AllowGup2Portal);
        Assert.AreEqual("", policy.Gup2PortalUrl);
        Assert.IsNull(Gup2PortalLink.FromPolicy(policy));
    }

    [TestMethod]
    public void AllowedPortalIsHttpsAndNeverContainsCredentials()
    {
        var policy = WorkstationPolicy.Parse(
            "{\"Version\":1,\"AllowGup2Portal\":true," +
            "\"Gup2PortalUrl\":\"https://gup2.example.internal/documents/\"}");
        Uri link = Gup2PortalLink.FromPolicy(policy)!;
        Assert.AreEqual("https", link.Scheme);
        Assert.AreEqual("gup2.example.internal", link.Host);
        Assert.AreEqual("/documents/", link.AbsolutePath);
        Assert.AreEqual("", link.UserInfo);
        Assert.AreEqual("", link.Query);
    }

    [DataTestMethod]
    [DataRow("http://gup2.internal/documents/")]
    [DataRow("https://user:password@gup2.internal/documents/")]
    [DataRow("file:///C:/secrets")]
    [DataRow("https://gup2.internal/documents/?token=secret")]
    [DataRow("https://gup2.internal/documents/#secret")]
    [DataRow("javascript:alert(1)")]
    [DataRow("gup2.internal")]
    public void RejectsCredentialsInUrlAndUnapprovedSchemes(string url)
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2PortalLink.Validate(url));
    }

    [TestMethod]
    public void EnabledWithoutHttpsUrlIsInvalidCorporatePolicy()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WorkstationPolicy.Parse(
                "{\"Version\":1,\"AllowGup2Portal\":true}"));
    }

    [TestMethod]
    public void Gup2SettingsDoNotOverrideIndependentGetSystemSettings()
    {
        var policy = WorkstationPolicy.Parse(
            "{\"Version\":1,\"ExportGetSystemStatus\":false," +
            "\"AllowGup2Portal\":true," +
            "\"Gup2PortalUrl\":\"https://gup2.internal/documents/\"}");
        Assert.IsFalse(policy.ExportGetSystemStatus);
        Assert.IsNotNull(Gup2PortalLink.FromPolicy(policy));
    }
}
