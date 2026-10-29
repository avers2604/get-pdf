using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeBindingReceiptTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";

    private static string Valid(bool binding = true, bool cef = true, bool pathMatch = true) =>
        JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-binding-lab/1",
            sessionId = Session.ToString("N"),
            tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            cefViewLoaded = cef,
            editorPathMatched = pathMatch,
            bindingObserved = binding,
            documentVerified = false,
            saveVerified = false,
            pdfVerified = false,
            printApproved = false,
            laboratoryOnly = true
        });

    [TestMethod]
    public void ExactNativeBindingEvidenceNeverGrantsDocumentSaveOrPrint()
    {
        var evidence = EuroOfficeNativeBindingReceipt.Parse(Valid(), Session, Tab, Fixture);
        Assert.IsTrue(evidence.BindingObserved);
        Assert.IsTrue(evidence.EditorPathMatched);
        Assert.IsTrue(evidence.CefViewLoaded);
        Assert.IsFalse(evidence.DocumentVerified);
        Assert.IsFalse(evidence.SaveCompleted);
        Assert.IsFalse(evidence.PrintApproved);
        Assert.IsFalse(evidence.ApprovedForProduction);
    }

    [TestMethod]
    public void RejectsReplayedOrCrossTabEvidence()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeBindingReceipt.Parse(Valid(), Guid.NewGuid(), Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeBindingReceipt.Parse(Valid(), Session, Guid.NewGuid(), Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeBindingReceipt.Parse(Valid(), Session, Tab, "gd31j6_calc.xlsx"));
    }

    [TestMethod]
    public void RejectsImpossibleAndForgedSuccessStates()
    {
        string valid = Valid();
        foreach (string invalid in new[]
        {
            Valid(binding: true, cef: false),
            Valid(binding: true, pathMatch: false),
            valid.Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            valid.Replace("\"printApproved\":false", "\"printApproved\":true"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            valid.Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":"),
            valid.Replace("\"fixtureName\":", "\"unexpected\":true,\"fixtureName\":"),
            "{}",
            new string('Z', 4096)
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeBindingReceipt.Parse(invalid, Session, Tab, Fixture));
    }

    [TestMethod]
    public void NegativeEvidenceIsDiagnosticOnly()
    {
        var evidence = EuroOfficeNativeBindingReceipt.Parse(
            Valid(binding: false, cef: false, pathMatch: false), Session, Tab, Fixture);
        Assert.IsFalse(evidence.BindingObserved);
        Assert.IsFalse(evidence.DocumentVerified);
    }
}
