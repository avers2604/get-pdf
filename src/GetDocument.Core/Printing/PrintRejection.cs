namespace GetDocument.Core.Printing;

/// <summary>
/// The print job was refused BEFORE anything reached the queue (unsupported setting,
/// printer or document changed). Unlike a failure during spooling this is not ambiguous:
/// the user can change the setting and send again without risking duplicate copies.
/// </summary>
public sealed class PrintNotSubmittedException(string message) : InvalidOperationException(message);

/// <summary>Russian wording for print settings and for refusals of the Windows print worker.</summary>
public static class PrintRejection
{
    /// <summary>Worker exit codes that are returned before XpsDocumentWriter starts writing.</summary>
    public const int BadRequest = 2, Changed = 3, Unsupported = 4;
    /// <summary>Prefix of the worker's stdout when it refuses an unsupported setting.</summary>
    public const string UnsupportedPrefix = "unsupported:";

    /// <summary>Message for a refusal before submission, or null when the outcome is ambiguous.</summary>
    public static string? Describe(int exitCode, string? output, string printerName) => exitCode switch
    {
        Unsupported => $"Принтер «{printerName}» не поддерживает {Feature(output)}. " +
                       "Задание не отправлено: измените настройку на шаге 2 и отправьте снова.",
        Changed => "Принтер или документ изменились с момента открытия мастера. " +
                   "Задание не отправлено: обновите список принтеров и отправьте снова.",
        BadRequest => "Некорректные параметры печати. Задание не отправлено.",
        _ => null
    };

    private static string Feature(string? output)
    {
        string feature = output is not null && output.StartsWith(UnsupportedPrefix, StringComparison.Ordinal)
            ? output[UnsupportedPrefix.Length..].Trim() : "";
        return feature switch
        {
            "duplex" => "выбранную двустороннюю печать",
            "paper" => "выбранный размер бумаги",
            "orientation" => "выбранную ориентацию",
            "color" => "выбранный цветовой режим",
            "nup" => "выбранное число страниц на листе",
            "tray" => "выбранный лоток",
            _ => "одну из выбранных настроек"
        };
    }

    public static string Orientation(PrintOrientation value) => value switch
    {
        PrintOrientation.Portrait => "книжная",
        PrintOrientation.Landscape => "альбомная",
        _ => "автоматически (по документу)"
    };

    public static string Color(PrintColorMode value) => value switch
    {
        PrintColorMode.Color => "цветная",
        PrintColorMode.Monochrome => "чёрно-белая",
        _ => "как настроено в принтере"
    };

    public static string Paper(PrintPaper value) => value == PrintPaper.A3 ? "A3" : "A4";
}
