using System.Security.Cryptography;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeOsrStreamProcessPolicyTests
{
    [TestMethod]
    public void ExplicitLabPolicySelectsOnlySyntheticWaylandFrameStream()
    {
        if (!OperatingSystem.IsLinux()) return; // Linux-only native program.
        string root = Path.Combine(Path.GetTempPath(), "gd31j13-lab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string program = Path.Combine(root, "approved-eurooffice-lab");
            File.WriteAllBytes(program, [0x47, 0x45, 0x54, 0x31, 0x33]);
            string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(program)));
            File.WriteAllBytes(Path.Combine(root, "gd31j6_writer.docx"), [0x50, 0x4B, 0x03, 0x04]);
            File.WriteAllText(Path.Combine(root, "gd31j6-fixture-manifest.json"), "{\"testOnly\":true}");
            var host = new EuroOfficeNativeLabHostPolicy(root, program, sha);
            Guid session = Guid.NewGuid(), tab = Guid.NewGuid();

            var start = EuroOfficeOsrStreamProcessLab.CreateStartInfo(host,
                root, "gd31j6_writer.docx", session, tab, labApproved: true);

            Assert.AreEqual(program, start.FileName);
            Assert.IsFalse(start.UseShellExecute);
            Assert.IsTrue(start.RedirectStandardOutput);
            Assert.IsFalse(start.RedirectStandardInput);
            CollectionAssert.AreEqual(new[]
            {
                "--open-staged-wayland-stream-lab",
                root, "gd31j6_writer.docx",
                session.ToString("N"), tab.ToString("N")
            }, start.ArgumentList.ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void ExplicitInputLabUsesNewCliAndPipedStdinWithoutWeakeningRegularStream()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "gd31j14-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string executable = Path.Combine(root, "approved-eurooffice-lab");
            File.WriteAllBytes(executable, [0x47, 0x45, 0x54, 0x31, 0x34]);
            string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
            File.WriteAllBytes(Path.Combine(root, "gd31j6_writer.docx"), [0x50, 0x4B, 0x03, 0x04]);
            File.WriteAllText(Path.Combine(root, "gd31j6-fixture-manifest.json"), "{\"testOnly\":true}");
            var policy = new EuroOfficeNativeLabHostPolicy(root, executable, digest);
            var session = Guid.NewGuid();
            var tab = Guid.NewGuid();
            var regular = EuroOfficeOsrStreamProcessLab.CreateStartInfo(
                policy, root, "gd31j6_writer.docx", session, tab, true);
            var interactive = EuroOfficeOsrStreamProcessLab.CreateInputStartInfo(
                policy, root, "gd31j6_writer.docx", session, tab, true);
            Assert.AreEqual("--open-staged-wayland-stream-lab", regular.ArgumentList[0]);
            Assert.IsFalse(regular.RedirectStandardInput);
            Assert.AreEqual("--open-staged-wayland-input-lab", interactive.ArgumentList[0]);
            Assert.IsTrue(interactive.RedirectStandardInput);
            Assert.IsTrue(interactive.RedirectStandardOutput);
            Assert.IsFalse(interactive.UseShellExecute);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void WithoutApprovalProcessCannotBeStarted()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeOsrStreamProcessLab.CreateStartInfo(
                null!, "/tmp/fixtures", "gd31j6_writer.docx",
                Guid.NewGuid(), Guid.NewGuid(), labApproved: false));
    }

    [TestMethod]
    public void RejectsNonSyntheticFixtureOrEmptyIds()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeOsrStreamProcessLab.ValidateFixtureName("../confidential.docx"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeOsrStreamProcessLab.ValidateFixtureName("report.xlsx"));
        EuroOfficeOsrStreamProcessLab.ValidateFixtureName("gd31j6_calc.xlsx");
    }
}
