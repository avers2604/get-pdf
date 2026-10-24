using System.Xml;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class WopiDiscoveryCatalogTests
{
    private static readonly Uri Office = new("https://office.intra.get.test/");
    private const string Valid = """
        <wopi-discovery>
          <net-zone name="external-https">
            <app name="docx">
              <action name="view" ext="docx" urlsrc="https://office.intra.get.test/browser/view?"/>
              <action name="edit" ext="docx" urlsrc="https://office.intra.get.test/browser/edit?"/>
            </app>
          </net-zone>
        </wopi-discovery>
        """;

    [TestMethod]
    public void SelectsViewAndNeverEdit()
    {
        var uri = WopiDiscoveryCatalog.SelectViewAction(Valid, ".docx", Office);
        Assert.AreEqual("/browser/view", uri.AbsolutePath);
    }

    [TestMethod]
    public void RejectsExternalOfficeHost()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            WopiDiscoveryCatalog.SelectViewAction(
                Valid.Replace("office.intra.get.test", "evil.example"), "docx", Office));
    }

    [TestMethod]
    public void RejectsXmlEntities()
    {
        const string xml = "<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><a>&x;</a>";
        Assert.ThrowsExactly<XmlException>(() =>
            WopiDiscoveryCatalog.SelectViewAction(xml, "docx", Office));
    }

    [TestMethod]
    public void RejectsDuplicateViewActions()
    {
        string xml = Valid.Replace("<action name=\"edit\"", "<action name=\"view\"");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WopiDiscoveryCatalog.SelectViewAction(xml, "docx", Office));
    }

    [TestMethod]
    public void RejectsUnsupportedFormat()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            WopiDiscoveryCatalog.SelectViewAction(Valid, "exe", Office));
    }
}
