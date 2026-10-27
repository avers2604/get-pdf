using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class LibreOfficeEditorTests
{
    [TestMethod]
    public void SaveCopyFiltersFollowTheTargetExtension()
    {
        Assert.AreEqual("MS Word 2007 XML", EditorDocument.FilterFor("копия.docx"));
        Assert.AreEqual("Calc MS Excel 2007 XML", EditorDocument.FilterFor(@"C:\x\Смета.XLSX"));
        Assert.AreEqual("Impress MS PowerPoint 2007 XML", EditorDocument.FilterFor("slides.pptx"));
        Assert.AreEqual("writer8", EditorDocument.FilterFor("a.odt"));
        Assert.ThrowsExactly<ArgumentException>(() => EditorDocument.FilterFor("a.pdf"));
        Assert.ThrowsExactly<NotSupportedException>(() => EditorDocument.FilterFor("a.exe"));
    }

    [TestMethod]
    public void SeededProfileIsLightRussianAndQuiet()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gdprofile-" + Guid.NewGuid().ToString("N"));
        try
        {
            LibreOfficeEditor.SeedProfile(folder);
            string xcu = File.ReadAllText(Path.Combine(folder, "user", "registrymodifications.xcu"));
            StringAssert.Contains(xcu, "<prop oor:name=\"ApplicationAppearance\" oor:op=\"fuse\"><value>1</value>");
            StringAssert.Contains(xcu, "<prop oor:name=\"ooLocale\" oor:op=\"fuse\"><value>ru-RU</value>");
            StringAssert.Contains(xcu, "<prop oor:name=\"WarnAlienFormat\" oor:op=\"fuse\"><value>false</value>");
            File.WriteAllText(Path.Combine(folder, "user", "registrymodifications.xcu"), "user settings");
            LibreOfficeEditor.SeedProfile(folder);   // never overwrites what the user's LibreOffice has written since
            Assert.AreEqual("user settings", File.ReadAllText(Path.Combine(folder, "user", "registrymodifications.xcu")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task OpensADocumentLeftLockedByACrashedSessionWithoutWaitingForADialog()
    {
        if (!LibreOfficeEditor.IsSupported)
            Assert.Inconclusive("Встроенный редактор LibreOffice недоступен (нет Windows или GETDOC_SOFFICE).");
        string folder = Path.Combine(Path.GetTempPath(), "gdlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string doc = Path.Combine(folder, "Смета.xlsx");
            OfficeDocumentFactory.CreateNew(GetDocument.Core.Workspace.WorkspaceDocumentKind.Spreadsheet, doc);
            // What the editor's LibreOffice leaves behind when it is killed with the document open.
            string user = Environment.UserName, host = Environment.MachineName;
            string lockFile = Path.Combine(folder, ".~lock.Смета.xlsx#");
            File.WriteAllText(lockFile, $",{host}/{user},{host},26.10.2026 16:07,{LibreOfficeRuntime.ConversionProfileUri("libreoffice-editor")};");
            var document = await LibreOfficeEditor.Shared.OpenAsync(doc);
            try
            {
                Assert.AreEqual("calc", document.Kind);
                Assert.IsFalse(document.ReadOnly, "a lock left by the editor's own crash does not block editing");
            }
            finally { await document.CloseAsync(); }

            // Someone else's LibreOffice has the file open: respect it, open read-only, never hang.
            File.WriteAllText(lockFile, ",Иванов И. И.,OTHER-PC,26.10.2026 16:07,file:///C:/Users/ivanov/AppData/Roaming/LibreOffice/4;");
            var shared = await LibreOfficeEditor.Shared.OpenAsync(doc);
            try { Assert.IsTrue(shared.ReadOnly, "a colleague's lock opens the document read-only"); }
            finally { await shared.CloseAsync(); }
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task ConvertsFilesThatAreNotOpenInTheSameLibreOffice()
    {
        if (!LibreOfficeEditor.IsSupported)
            Assert.Inconclusive("Встроенный редактор LibreOffice недоступен (нет Windows или GETDOC_SOFFICE).");
        string folder = Path.Combine(Path.GetTempPath(), "gdconv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string sheet = Path.Combine(folder, "Смета.xlsx");
            OfficeDocumentFactory.CreateNew(GetDocument.Core.Workspace.WorkspaceDocumentKind.Spreadsheet, sheet);
            string pdf = Path.Combine(folder, "Смета.pdf");
            await LibreOfficeEditor.Shared.ConvertToPdfAsync(sheet, pdf);
            byte[] head = new byte[5];
            await using (var stream = File.OpenRead(pdf)) await stream.ReadExactlyAsync(head);
            Assert.AreEqual("%PDF-", System.Text.Encoding.ASCII.GetString(head));
            Assert.IsFalse(File.Exists(Path.Combine(folder, ".~lock.Смета.xlsx#")), "the converted file is closed again");
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task EditsSavesAndExportsARealDocumentWhenLibreOfficeIsInstalled()
    {
        if (!LibreOfficeEditor.IsSupported)
            Assert.Inconclusive("Встроенный редактор LibreOffice недоступен (нет Windows или GETDOC_SOFFICE).");
        string folder = Path.Combine(Path.GetTempPath(), "gdedit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string doc = Path.Combine(folder, "Записка.docx");
            OfficeDocumentFactory.CreateNew(GetDocument.Core.Workspace.WorkspaceDocumentKind.Text, doc);
            var states = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
            var document = await LibreOfficeEditor.Shared.OpenAsync(doc);
            document.StateChanged += (command, enabled, _) => states[command] = enabled;
            try
            {
                Assert.AreEqual("writer", document.Kind);
                Assert.IsFalse(document.ReadOnly);
                await document.DispatchAsync(".uno:InsertText", new Dictionary<string, object> { ["Text"] = "Проверка ГЭТ" }, focus: false);
                await document.DispatchAsync(".uno:SelectAll", focus: false);
                await document.DispatchAsync(".uno:Bold", focus: false);
                for (int i = 0; i < 50 && !document.IsModified; i++) await Task.Delay(100);
                Assert.IsTrue(document.IsModified, "typing marks the document modified");

                string pdf = Path.Combine(folder, "out.pdf");
                await document.ExportPdfAsync(pdf);
                Assert.IsTrue(new FileInfo(pdf).Length > 0);
                string copy = Path.Combine(folder, "копия.odt");
                await document.SaveAsAsync(copy);
                Assert.IsTrue(File.Exists(copy));
                Assert.IsTrue(document.IsModified, "a copy does not save the original");

                // Find and replace through LibreOffice's search, without its dialog.
                Assert.IsTrue((await document.FindAsync("гэт", FindMode.Next)).Found, "case-insensitive by default");
                Assert.IsFalse((await document.FindAsync("гэт", FindMode.Next, matchCase: true)).Found);
                Assert.IsFalse((await document.FindAsync("такого слова нет", FindMode.Next)).Found);
                Assert.AreEqual(1, (await document.FindAsync("Проверка", FindMode.ReplaceAll, "Приёмка")).Replaced);
                Assert.IsTrue((await document.FindAsync("Приёмка ГЭТ", FindMode.Next)).Found);

                // Ctrl+F / Ctrl+H open GET Document's find bar, not LibreOffice's dialog.
                var findShortcut = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                document.CommandRequested += command => { if (command == ".uno:SearchDialog") findShortcut.TrySetResult(command); };
                await document.DispatchAsync(".uno:SearchDialog", focus: false);
                Assert.AreEqual(".uno:SearchDialog", await findShortcut.Task.WaitAsync(TimeSpan.FromSeconds(10)));

                // Ctrl+S inside the document reaches GET Document instead of LibreOffice's own save.
                var intercepted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                document.CommandRequested += command => intercepted.TrySetResult(command);
                await document.DispatchAsync(".uno:Save", focus: false);
                Assert.AreEqual(".uno:Save", await intercepted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.IsTrue(document.IsModified, "the intercepted shortcut itself does not save");

                long before = new FileInfo(doc).Length;
                await document.SaveAsync();
                Assert.IsFalse(document.IsModified);
                Assert.AreNotEqual(before, new FileInfo(doc).Length, "the original file was written");
                Assert.IsTrue(states.ContainsKey(".uno:Bold"), "toolbar state arrives from LibreOffice");
            }
            finally { await document.CloseAsync(); }
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }
}
