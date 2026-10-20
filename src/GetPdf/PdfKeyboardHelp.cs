namespace GetPdf;

/// <summary>
/// Read-only operator help; every shortcut is implemented by the viewer or tab host.
/// No document metadata, local paths or telemetry are included.
/// </summary>
internal static class PdfKeyboardHelp
{
    internal static string Text =>
        "ГЭТ PDF — горячие клавиши\n\n" +
        "Ctrl+O — открыть PDF\n" +
        "Ctrl+T — новая вкладка; Ctrl+W — закрыть вкладку\n" +
        "Ctrl+Tab / Ctrl+Shift+Tab — переключить вкладку\n" +
        "Ctrl+F — поиск; F3 / Shift+F3 — следующее / предыдущее совпадение\n" +
        "Ctrl+G — перейти к странице\n" +
        "Ctrl+Shift+L — показать или скрыть миниатюры\n" +
        "Ctrl+1…Ctrl+5 — переключить группу инструментов редактора\n" +
        "F6 — перейти в следующую область интерфейса\n" +
        "Ctrl+C — копировать выбранный текстовый фрагмент\n" +
        "Ctrl+S — сохранить отдельную копию PDF\n" +
        "Ctrl+P — печать; Esc — отмена ожидающего задания\n" +
        "Ctrl+Z / Ctrl+Y — отменить / повторить изменение\n" +
        "F11 — полный экран; F1 — эта справка\n\n" +
        "Исходные PDF, в том числе WORM-оригиналы GUP-2, не перезаписываются.\n" +
        "Автономная проверка подписи не заменяет юридическую проверку КЭП.\n" +
        "Настоящие аннотации PDF пока доступны только как эскизы.";
}
