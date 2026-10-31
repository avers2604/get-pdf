using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSyntheticRoundTripCandidateTests
{
    private const string Fixture = "gd31j6_writer.docx";
    private const string Source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Output = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string Receipt(bool native = false, bool production = false,
        bool package = true) => JsonSerializer.Serialize(new
        {
            schema = "getdoc-synthetic-ooxml-roundtrip-candidate-lab/1",
            fixtureName = Fixture,
            sourceSha256 = Source,
            outputSha256 = Output,
            syntheticPackageVerified = package,
            nativeSaveProven = native,
            documentVerified = false,
            approvedForProduction = production,
            laboratoryOnly = true
        });

    [TestMethod]
    public void VerifiedSyntheticPackageIsNotAuthenticatedNativeSave()
    {
        var candidate = EuroOfficeSyntheticRoundTripCandidate.Parse(Receipt(), Fixture);
        Assert.IsTrue(candidate.SyntheticPackageVerified);
        Assert.AreEqual(Source, candidate.SourceSha256);
        Assert.AreEqual(Output, candidate.OutputSha256);
        Assert.IsFalse(candidate.NativeSaveProven);
        Assert.IsFalse(candidate.DocumentVerified);
        Assert.IsFalse(candidate.ApprovedForProduction);
    }

    [TestMethod]
    public void DenyBogusFlagsOrMismatchedSource()
    {
        foreach (string payload in new[]
        {
            Receipt(native: true),
            Receipt(production: true),
            Receipt(package: false),
            Receipt().Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            Receipt().Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            Receipt().Replace("\"schema\":", "\"schema\":\"evil\",\"schema\":"),
            Receipt().Replace("\"fixtureName\":", "\"path\":\"private.docx\",\"fixtureName\":"),
            Receipt().Replace(Output, Source),
            Receipt().Replace(Output, "BAD_HASH"),
            "{}"
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeSyntheticRoundTripCandidate.Parse(payload, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticRoundTripCandidate.Parse(Receipt(), "gd31j6_calc.xlsx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticRoundTripCandidate.Parse(Receipt(), "../../secrets.docx"));
    }

    [TestMethod]
    public void TheThreeSyntheticKindsAreExplicitlyAllowlisted()
    {
        foreach (string fixture in new[]
        {
            "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
        })
        {
            string payload = Receipt().Replace(Fixture, fixture);
            var parsed = EuroOfficeSyntheticRoundTripCandidate.Parse(payload, fixture);
            Assert.IsFalse(parsed.NativeSaveProven);
        }
    }
}
