using System.IO;

namespace GetDocument.Core.Printing;

/// <summary>An actionable, sanitized spooler status (never includes driver stderr or file paths).</summary>
public sealed class PrintSubmissionException : IOException
{
    public PrintSubmissionException(PrintFailure failure)
        : base(failure.UserMessage)
    {
        Failure = failure;
    }

    public PrintFailure Failure { get; }
}
