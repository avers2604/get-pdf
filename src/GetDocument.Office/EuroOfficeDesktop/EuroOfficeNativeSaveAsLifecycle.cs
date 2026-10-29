namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// A fail-closed, *diagnostic-only* classification of a single native CEF
/// Save As attempt. This never authorizes a user document, WORM revision or
/// published Office file, even when OOXML ZIP validation passed.
/// Event ordering is enforced in the native Qt owner-thread gate separately.
/// </summary>
public static class EuroOfficeNativeSaveAsLifecycle
{
    public enum State
    {
        DialogNotObserved,
        TargetNotAccepted,
        DuplicateDialog,
        TargetConflict,
        SaveCancelled,
        SaveNotAcknowledged,
        CandidateMissing,
        CandidateInvalid,
        Timeout,
        SavedCandidateValidatedButUntrusted
    }

    public sealed record Result(State Status)
    {
        public bool DocumentVerified => false;
        public bool SaveCompleted => false;
        public bool AuthorizedForPublication => false;
    }

    public static Result Classify(
        EuroOfficeNativeSaveAsReceipt receipt,
        bool candidateValidated,
        bool duplicateDialog = false,
        bool targetConflict = false,
        bool timedOut = false)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SourceBeforeSha256.Length != 64 ||
            receipt.SourceBeforeSha256 != receipt.SourceAfterSha256 ||
            (receipt.SaveAsTargetAccepted && !receipt.SaveAsRequestSeen) ||
            (candidateValidated && !receipt.CandidateWritten) ||
            (receipt.CandidateWritten &&
             (!receipt.SaveAsRequestSeen || !receipt.SaveAsTargetAccepted ||
              !receipt.SaveNotification || receipt.SaveCancelled)))
            throw new InvalidDataException("Неверная или противоречивая Save As квитанция.");

        if (duplicateDialog) return new Result(State.DuplicateDialog);
        if (targetConflict) return new Result(State.TargetConflict);
        if (receipt.SaveCancelled) return new Result(State.SaveCancelled);
        if (timedOut) return new Result(State.Timeout);
        if (!receipt.SaveAsRequestSeen) return new Result(State.DialogNotObserved);
        if (!receipt.SaveAsTargetAccepted) return new Result(State.TargetNotAccepted);
        if (!receipt.SaveNotification) return new Result(State.SaveNotAcknowledged);
        if (!receipt.CandidateWritten) return new Result(State.CandidateMissing);
        return candidateValidated
            ? new Result(State.SavedCandidateValidatedButUntrusted)
            : new Result(State.CandidateInvalid);
    }
}
