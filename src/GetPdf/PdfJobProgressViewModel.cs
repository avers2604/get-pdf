using System.ComponentModel;

namespace GetPdf;

internal enum PdfJobPhase
{
    Idle,
    Queued,
    Preparing,
    Finalizing,
    Cancelling,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Small WPF-bindable state machine for PDF OCR and print preparation.
/// Keeps job progress and transition rules independent of the Window and any
/// scanner/PDF dependency. Percent never describes physical printer progress.
/// </summary>
internal sealed class PdfJobProgressViewModel : INotifyPropertyChanged
{
    private PdfJobPhase _phase = PdfJobPhase.Idle;
    private double _percent;
    private bool _isIndeterminate;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PdfJobPhase Phase => _phase;
    public double Percent => _percent;
    public bool IsIndeterminate => _isIndeterminate;
    public bool IsActive => _phase is PdfJobPhase.Queued or PdfJobPhase.Preparing
        or PdfJobPhase.Finalizing or PdfJobPhase.Cancelling;

    public void Begin()
    {
        if (IsActive) throw new InvalidOperationException("PDF-задание уже выполняется.");
        SetPercent(0);
        SetPhase(PdfJobPhase.Queued);
        SetIndeterminate(true);
    }

    public void Start()
    {
        if (_phase != PdfJobPhase.Queued)
            throw new InvalidOperationException("PDF-задание не находится в очереди.");
        SetPhase(PdfJobPhase.Preparing);
        SetIndeterminate(false);
    }

    public void Report(int completed, int total, int maximumPercent = 100)
    {
        if (completed < 0 || total < 1 || completed > total ||
            maximumPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(completed));
        // A cancellation may arrive during an uninterruptible third-party export.
        // Ignore stale progress rather than reversing the cancellation request.
        if (_phase == PdfJobPhase.Cancelling) return;
        if (_phase != PdfJobPhase.Preparing)
            throw new InvalidOperationException("Отчёт о прогрессе доступен только при подготовке.");
        SetPercent(Math.Max(_percent, maximumPercent * (double)completed / total));
    }

    public void BeginFinalizing()
    {
        if (_phase != PdfJobPhase.Preparing)
            throw new InvalidOperationException("Подготовка PDF ещё не завершена.");
        SetPhase(PdfJobPhase.Finalizing);
        SetIndeterminate(true);
    }

    public void RequestCancellation()
    {
        if (!IsActive || _phase == PdfJobPhase.Cancelling) return;
        SetPhase(PdfJobPhase.Cancelling);
        SetIndeterminate(true);
    }

    public void Complete()
    {
        if (!IsActive)
            throw new InvalidOperationException("Нет активного PDF-задания.");
        SetPercent(100);
        SetPhase(PdfJobPhase.Completed);
        SetIndeterminate(false);
    }

    public void Cancelled()
    {
        if (!IsActive) return;
        SetPhase(PdfJobPhase.Cancelled);
        SetIndeterminate(false);
    }

    public void Fail()
    {
        if (!IsActive) return;
        SetPhase(PdfJobPhase.Failed);
        SetIndeterminate(false);
    }

    private void SetPhase(PdfJobPhase value)
    {
        if (_phase == value) return;
        bool before = IsActive;
        _phase = value;
        Changed(nameof(Phase));
        if (before != IsActive) Changed(nameof(IsActive));
    }

    private void SetPercent(double value)
    {
        if (_percent == value) return;
        _percent = value;
        Changed(nameof(Percent));
    }

    private void SetIndeterminate(bool value)
    {
        if (_isIndeterminate == value) return;
        _isIndeterminate = value;
        Changed(nameof(IsIndeterminate));
    }

    private void Changed(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
