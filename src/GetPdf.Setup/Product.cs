namespace GetPdf.Setup;

/// <summary>
/// The setup wizard serves two products from one code base: GET PDF (default) and
/// GET Document (built with -p:SetupProduct=GetDocument, which defines GETDOC).
/// </summary>
internal static class Product
{
#if GETDOC
    public const string Name = "ГЭТ Документ";
    public const string UpgradeCode = "{6D0B7F35-2C0E-4B8E-9C1D-4E5A9B2F7C31}";
    public const string Executable = "GetDocument.Desktop.exe";
    public const string ProcessName = "GetDocument.Desktop";
    public const string RegisteredApp = "GetDocument";
    public const string DefaultFolder = "GET Document";
    public const string DataFolder = "GET-Document";
    public const string Tagline = "Документы ГЭТ в одном окне";
    public const string Description = "PDF, документы Word, таблицы Excel, презентации и изображения во вкладках одного окна. Работает без интернета.";
    public const string DefaultAppsText = "Сделать ГЭТ Документ программой по умолчанию";
    public const string DefaultAppsHint = "После установки откроются параметры Windows — выберите ГЭТ Документ для нужных типов файлов.";
    public static readonly string[] Features =
    [
        "PDF: просмотр, поиск, печать, штампы, нумерация, работа со страницами",
        "Word, Excel, PowerPoint: просмотр, редактирование и печать (LibreOffice входит в установку)",
        "Сжатие PDF без потери качества (qpdf входит в установку)",
        "История открытия и избранные документы"
    ];
#else
    public const string Name = "ГЭТ PDF";
    public const string UpgradeCode = "{BE5C1496-701B-4ED4-937E-DA3B41C2A770}";
    public const string Executable = "GetPdf.exe";
    public const string ProcessName = "GetPdf";
    public const string RegisteredApp = "GetPdf";
    public const string DefaultFolder = "GET PDF";
    public const string DataFolder = "GET-PDF";
    public const string Tagline = "Просмотр и печать PDF";
    public const string Description = "Программа для просмотра, поиска, печати и подготовки PDF-документов. Работает без интернета.";
    public const string DefaultAppsText = "Сделать ГЭТ PDF программой для PDF по умолчанию";
    public const string DefaultAppsHint = "После установки откроются параметры Windows — выберите ГЭТ PDF для .pdf.";
    public static readonly string[] Features =
    [
        "Просмотр, поиск и печать PDF во вкладках",
        "Нумерация, штампы, работа со страницами",
        "Сжатие PDF без потери качества (qpdf входит в установку)",
        "История открытия и избранные документы"
    ];
#endif
}
