namespace GetPdf;

/// <summary>
/// Cooperative single-flight gate shared between PDF windows. Limits simultaneous
/// high-memory PDF jobs without moving WPF-scoped image work off the UI dispatcher.
/// Cancellation while waiting prevents a job from starting; in-progress third-party
/// calls may only stop when their awaited operation returns.
/// </summary>
internal sealed class PdfJobCoordinator
{
    private readonly DocumentJobCoordinator _jobs;
    internal static PdfJobCoordinator Shared { get; } = new(DocumentJobCoordinator.Shared);

    public PdfJobCoordinator() : this(new DocumentJobCoordinator()) { }
    internal PdfJobCoordinator(DocumentJobCoordinator jobs) => _jobs = jobs;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return await _jobs.RunAsync(DocumentJobKind.PdfProcessing,
            job => work(job.Cancellation), cancellation);
    }
}
