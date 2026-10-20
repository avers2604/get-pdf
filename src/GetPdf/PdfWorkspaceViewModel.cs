using CommunityToolkit.Mvvm.ComponentModel;

namespace GetPdf;

/// <summary>
/// Immutable view of PDF capabilities and transient state. No document content,
/// source paths or page pixels enter the WPF command model.
/// </summary>
internal sealed record PdfWorkspaceState
{
    public int PageCount { get; init; }
    public int PageIndex { get; init; }
    public int MaxDocumentPages { get; init; } = 1000;
    public bool IsLoading { get; init; }
    public bool IsRendering { get; init; }
    public bool CanModify { get; init; }
    public bool HasChanges { get; init; }
    public bool CanUndo { get; init; }
    public bool CanRedo { get; init; }
    public bool CanDrawOnCurrentPage { get; init; }
    public bool HasRectangles { get; init; }
    public bool HasNotes { get; init; }
    public bool HasSearchHits { get; init; }
    public bool AllowPrinting { get; init; }
    public bool IsJobActive { get; init; }
    public bool CanCancelJob { get; init; }
    public bool HasNativeDrafts { get; init; }
    public bool IsNativeTextMode { get; init; }
}

/// <summary>
/// Single source of truth for the PDF window's command availability.
/// WPF keeps file dialogs and visual page rendering; no PDF I/O is performed
/// by this state machine. Read-only documents may be printed and byte-copied
/// but never structurally edited, irrespective of shortcut dispatch.
/// </summary>
internal sealed class PdfWorkspaceViewModel : ObservableObject
{
    private PdfWorkspaceState _state = new();

    public bool IsReady => _state.PageCount > 0 &&
        _state.PageIndex >= 0 && _state.PageIndex < _state.PageCount &&
        !_state.IsLoading && !_state.IsRendering;
    public bool CanEdit => IsReady && _state.CanModify && !_state.HasNativeDrafts;
    public bool CanSelectPage => IsReady;
    public bool CanNavigatePrevious => IsReady && _state.PageIndex > 0;
    public bool CanNavigateNext => IsReady && _state.PageIndex + 1 < _state.PageCount;
    public bool CanZoom => IsReady;
    public bool CanRotate => CanEdit && !_state.HasRectangles && !_state.HasNotes;
    public bool CanMoveUp => CanEdit && _state.PageIndex > 0;
    public bool CanMoveDown => CanEdit && _state.PageIndex + 1 < _state.PageCount;
    public bool CanDelete => CanEdit && _state.PageCount > 1;
    public bool CanDuplicate => CanEdit && _state.PageCount < _state.MaxDocumentPages;
    public bool CanUndo => CanEdit && _state.CanUndo;
    public bool CanRedo => CanEdit && _state.CanRedo;
    public bool CanSaveAs => IsReady && !_state.HasNativeDrafts && (_state.CanModify || !_state.HasChanges);
    public string SaveAsLabel => _state.HasChanges ? "Сохранить изменения…" : "Сохранить копию…";
    public bool CanSearch => IsReady;
    public bool CanFindNext => IsReady && _state.HasSearchHits;
    public bool CanPrint => IsReady && !_state.HasNativeDrafts && _state.AllowPrinting;
    public bool CanSetPageRange => IsReady;
    public bool CanOcr => CanEdit;
    public bool CanExtract => CanEdit;
    public bool CanMerge => CanEdit;
    public bool CanStamp => CanEdit;
    public bool CanDrawMarkup => CanEdit && _state.CanDrawOnCurrentPage;
    public bool CanClearRectangles => CanDrawMarkup && _state.HasRectangles;
    public bool CanClearNotes => CanDrawMarkup && _state.HasNotes;
    public bool CanWriteNotes => CanEdit || (IsReady && _state.CanModify &&
        _state.CanDrawOnCurrentPage && _state.IsNativeTextMode);
    public bool IsJobActive => _state.IsJobActive;
    public bool CanCancelJob => IsJobActive && _state.CanCancelJob;

    public void Update(PdfWorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.MaxDocumentPages <= 0 ||
            state.PageCount < 0 || state.PageCount > state.MaxDocumentPages)
            throw new ArgumentOutOfRangeException(nameof(state),
                "PDF page count exceeds the configured workstation policy.");
        if (_state == state) return;
        _state = state;
        foreach (var name in Properties)
            OnPropertyChanged(name);
    }

    private static readonly string[] Properties =
    [
        nameof(IsReady), nameof(CanEdit), nameof(CanSelectPage),
        nameof(CanNavigatePrevious), nameof(CanNavigateNext), nameof(CanZoom),
        nameof(CanRotate), nameof(CanMoveUp), nameof(CanMoveDown),
        nameof(CanDelete), nameof(CanDuplicate), nameof(CanUndo),
        nameof(CanRedo), nameof(CanSaveAs), nameof(SaveAsLabel),
        nameof(CanSearch), nameof(CanFindNext), nameof(CanPrint),
        nameof(CanSetPageRange), nameof(CanOcr), nameof(CanExtract),
        nameof(CanMerge), nameof(CanStamp), nameof(CanDrawMarkup),
        nameof(CanClearRectangles), nameof(CanClearNotes),
        nameof(CanWriteNotes), nameof(IsJobActive), nameof(CanCancelJob)
    ];
}
