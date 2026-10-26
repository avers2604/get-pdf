namespace GetDocument.Core.Printing;

/// <summary>
/// Local spooler acceptance is not a receipt for a physically printed sheet.
/// Conservative categorization avoids telling the operator to blindly resend
/// after timeout, driver crash, IPC failure or ambiguous CUPS rejection.
/// </summary>
public enum PrintFailureKind
{
    InvalidRequest,
    SourceOrPrinterChanged,
    UnsupportedSettings,
    TransportUnavailable,
    Indeterminate
}

public sealed record PrintFailure(
    PrintFailureKind Kind, string UserMessage, bool MightBeQueued);

public static class PrintSpoolFailurePolicy
{
    public static PrintFailure FromWindowsWorker(int exitCode) => exitCode switch
    {
        2 => new(PrintFailureKind.InvalidRequest,
            "Некорректный запрос к Windows-компоненту печати. Задание не отправлено.", false),
        3 => new(PrintFailureKind.SourceOrPrinterChanged,
            "Исходная PDF-копия или выбранный принтер изменились. Задание не отправлено.", false),
        4 => new(PrintFailureKind.UnsupportedSettings,
            "Принтер не поддерживает выбранные параметры. Задание не отправлено.", false),
        _ => new(PrintFailureKind.Indeterminate,
            "Windows-компонент печати не подтвердил приём задания. Проверьте очередь перед повторной печатью.", true)
    };

    public static PrintFailure FromCupsFailure() =>
        new(PrintFailureKind.Indeterminate,
            "CUPS не подтвердил приём задания в очередь. Перед повторной печатью проверьте очередь.", true);

    public static PrintFailure TimedOut() =>
        new(PrintFailureKind.Indeterminate,
            "Ожидание ответа очереди печати истекло. Задание могло попасть в очередь; проверьте её до повтора.", true);

    public static PrintFailure UnavailableBeforeSubmit() =>
        new(PrintFailureKind.TransportUnavailable,
            "Локальный компонент или служба печати недоступны. Задание не отправлено.", false);
}
