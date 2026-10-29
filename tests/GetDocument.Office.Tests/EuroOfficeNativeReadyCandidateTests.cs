using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeReadyCandidateTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private const string Hash = "c80a1620d6c4883ad5f814dc9d1c1e54f24b99b9031b9ea60170b96a011e90eb";

    private static string Valid(bool callback = true, bool converted = true,
        bool cef = true, bool matched = true) => JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-ready-candidate-lab/1",
            sessionId = Session.ToString("N"),
            tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            cefViewLoaded = cef,
            editorPathMatched = matched,
            editorContentReadyNotification = callback,
            conversionArtifactObserved = converted,
            editorBinaryLength = converted ? 65536L : 0L,
            editorBinarySha256 = converted ? Hash : "",
            documentVerified = false,
            saveVerified = false,
            pdfVerified = false,
            printApproved = false,
            laboratoryOnly = true
        });

    [TestMethod]
    public void CorrelatedRendererCallbackAndConversionStillDoNotAuthorizeRealEditing()
    {
        var receipt = EuroOfficeNativeReadyCandidate.Parse(Valid(), Session, Tab, Fixture);
        Assert.IsTrue(receipt.ReadyAndConversionCorrelated);
        Assert.IsTrue(receipt.EditorContentReadyNotification);
        Assert.IsFalse(receipt.DocumentVerified);
        Assert.IsFalse(receipt.EditReady);
        Assert.IsFalse(receipt.SaveCompleted);
        Assert.IsFalse(receipt.PdfReady);
        Assert.IsFalse(receipt.ApprovedForProduction);
    }

    [TestMethod]
    public void ACallbackWithoutConverterOrBindingIsNotReadiness()
    {
        var missing = EuroOfficeNativeReadyCandidate.Parse(Valid(converted: false), Session, Tab, Fixture);
        Assert.IsTrue(missing.EditorContentReadyNotification);
        Assert.IsFalse(missing.ReadyAndConversionCorrelated);
        var noView = EuroOfficeNativeReadyCandidate.Parse(
            Valid(callback: true, converted: false, cef: false, matched: false),
            Session, Tab, Fixture);
        Assert.IsFalse(noView.ReadyAndConversionCorrelated);
    }

    [TestMethod]
    public void RejectsForgedOrCrossSessionEvidence()
    {
        var valid = Valid();
        foreach (var candidate in new[]
        {
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            valid.Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            valid.Replace("\"printApproved\":false", "\"printApproved\":true"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            valid.Replace("\"schema\":", "\"schema\":\"foreign\",\"schema\":"),
            valid.Replace("\"fixtureName\":", "\"secret\":\"path\",\"fixtureName\":"),
            Valid(converted: true, cef: false),
            Valid(converted: true, matched: false),
            valid.Replace("\"editorBinaryLength\":65536", "\"editorBinaryLength\":999999999"),
            valid.Replace(Hash, "garbage"),
            "{}"
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeReadyCandidate.Parse(candidate, Session, Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeReadyCandidate.Parse(valid, Guid.NewGuid(), Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeReadyCandidate.Parse(valid, Session, Guid.NewGuid(), Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeReadyCandidate.Parse(valid, Session, Tab, "gd31j6_calc.xlsx"));
    }

    [TestMethod]
    public void AnAbsentReadyCallbackNeverBecomesAnImportAcknowledgement()
    {
        var result = EuroOfficeNativeReadyCandidate.Parse(Valid(callback: false), Session, Tab, Fixture);
        Assert.IsTrue(result.ConversionArtifactObserved);
        Assert.IsFalse(result.ReadyAndConversionCorrelated);
    }
}
