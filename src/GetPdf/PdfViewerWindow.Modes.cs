using System.Windows;
using NAPS2.Images;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private DocumentSession _documentSession = new();
    private string? _displayName;
    internal event Action? DocumentReady;
    internal void MarkModeSnapshotSaved(string? fingerprint)
    {
        if (fingerprint == WorkspaceFingerprint)
        {
            _savedPlanFingerprint = CurrentPlanFingerprint();
            _documentSession.MarkSaved(_documentSession.Revision);
            PersistPdfDraft();
        }
    }
    internal bool WorkspaceHasChanges => HasPendingChanges;
    internal string WorkspaceFingerprint => _sourcePath + ":" + CurrentPlanFingerprint();
    internal DocumentSession Session => _documentSession;
    internal int SelectedPageIndex => _pageIndex;
    internal double WorkspaceZoom => _zoom;
    internal double WorkspaceOffset => DocumentView.RelativePageOffset;
    internal string DisplayDocumentName => DocumentTitle.Text;
    private void SynchronizePdfSession()
    {
        if (_plan is null || _sourcePath is null) return;
        var pages = _plan.Pages.Select(p => new DocumentPageEntry(p.Id, new(DocumentSourceKind.Pdf, _sourcePath), p.SourcePage, p.Rotation)).ToArray();
        _documentSession.Synchronize(pages, pages[_pageIndex].Id);
        _documentSession.Mode = DocumentMode.PdfEditor;
    }
}
