using System.Globalization;
using System.Text;

namespace GetDocument.Core.Printing;

/// <summary>
/// Immutable, validated print settings from the GET-owned wizard.
/// A printer backend may only receive a plan built by Create().
/// Page indices are one-based as in the physical document.
/// </summary>
public sealed class PrintJobPlan
{
    private PrintJobPlan(string printerName, int sourcePageCount, IReadOnlyList<int> pages,
        int copies, PrintDuplex duplex, PrintPaper paper, string range,
        PrintOrientation orientation, PrintColorMode color, PrintPagesPerSheet pagesPerSheet,
        PrintTrayOption? tray)
    {
        PrinterName = printerName;
        SourcePageCount = sourcePageCount;
        Pages = pages;
        Copies = copies;
        Duplex = duplex;
        Paper = paper;
        CupsPageRange = range;
        Orientation = orientation;
        Color = color;
        PagesPerSheet = pagesPerSheet;
        Tray = tray;
    }

    public string PrinterName { get; }
    public int SourcePageCount { get; }
    public IReadOnlyList<int> Pages { get; }
    public int Copies { get; }
    public PrintDuplex Duplex { get; }
    public PrintPaper Paper { get; }
    public string CupsPageRange { get; }
    public PrintOrientation Orientation { get; }
    public PrintColorMode Color { get; }
    public PrintPagesPerSheet PagesPerSheet { get; }
    public PrintTrayOption? Tray { get; }
    public int SheetSidesToPrint => checked(((Pages.Count + (int)PagesPerSheet - 1) / (int)PagesPerSheet) * Copies);
    /// <summary>Estimate only: printer may insert blank sides or covers.</summary>
    public int EstimatedPhysicalSheets
    {
        get
        {
            int sidesPerCopy = (Pages.Count + (int)PagesPerSheet - 1) / (int)PagesPerSheet;
            int sheetsPerCopy = Duplex == PrintDuplex.OneSided
                ? sidesPerCopy : (sidesPerCopy + 1) / 2;
            return checked(sheetsPerCopy * Copies);
        }
    }
    public int PageCountToPrint => checked(Pages.Count * Copies);

    public static PrintJobPlan Create(
        string printerName, int sourcePageCount, string? pageRange, int copies,
        PrintDuplex duplex, PrintPaper paper,
        PrintOrientation orientation = PrintOrientation.Automatic,
        PrintColorMode color = PrintColorMode.PrinterDefault,
        PrintPagesPerSheet pagesPerSheet = PrintPagesPerSheet.One,
        PrintTrayOption? tray = null)
    {
        if (string.IsNullOrWhiteSpace(printerName) || printerName.Length > 160 ||
            printerName[0] == '-' || printerName.Any(c => char.IsControl(c)))
            throw new ArgumentException("Выберите допустимый принтер.", nameof(printerName));
        if (sourcePageCount is < 1 or > 1000)
            throw new ArgumentException("Нельзя распечатать PDF с некорректным числом страниц.",
                nameof(sourcePageCount));
        if (copies is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(copies), "Разрешено от 1 до 99 копий.");
        if (tray is not null)
        {
            var validated = PrintTrayOptions.Make(tray.Option, tray.Value);
            if (tray != validated)
                throw new ArgumentException("Лоток содержит некорректное наименование.", nameof(tray));
        }
        if (!Enum.IsDefined(duplex) || !Enum.IsDefined(paper) ||
            !Enum.IsDefined(orientation) || !Enum.IsDefined(color) ||
            !Enum.IsDefined(pagesPerSheet))
            throw new ArgumentException("Неизвестная настройка печати.");

        string source = pageRange?.Trim() ?? "";
        if (source.Length > 256)
            throw new ArgumentException("Диапазон слишком длинный.", nameof(pageRange));
        if (source.Length > 0 && source.Any(c =>
                c is not (>= '0' and <= '9' or '-' or ',' or ' ')))
            throw new ArgumentException("Страницы задаются в виде 1,3-5.", nameof(pageRange));

        var pages = new SortedSet<int>();
        if (source.Length == 0)
        {
            for (int index = 1; index <= sourcePageCount; index++) pages.Add(index);
        }
        else
        {
            foreach (string raw in source.Split(','))
            {
                string piece = raw.Trim();
                if (piece.Length == 0)
                    throw new ArgumentException("Пустое значение страницы.", nameof(pageRange));
                string[] parts = piece.Split('-');
                if (parts.Length is < 1 or > 2 ||
                    parts.Any(p => p.Length == 0 || p.Length > 4 ||
                        p.Any(c => c is < '0' or > '9')))
                    throw new ArgumentException("Неверный диапазон страниц.", nameof(pageRange));

                if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture,
                        out int first) ||
                    first < 1 || first > sourcePageCount)
                    throw new ArgumentException("Номер страницы вне документа.", nameof(pageRange));
                int last = first;
                if (parts.Length == 2 &&
                    (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture,
                        out last) || last < first || last > sourcePageCount))
                    throw new ArgumentException("Некорректный конец диапазона.", nameof(pageRange));
                for (int number = first; number <= last; number++) pages.Add(number);
            }
        }

        var chosen = pages.ToArray();
        if (pagesPerSheet != PrintPagesPerSheet.One && chosen.Length != sourcePageCount)
            throw new ArgumentException(
                "Для 2/4 страниц на листе выберите все страницы: CUPS может считать диапазон по физическим листам.",
                nameof(pageRange));
        if (chosen.Length == 0) throw new ArgumentException("Нет страниц для печати.");
        var compact = new StringBuilder();
        int i = 0;
        while (i < chosen.Length)
        {
            int start = chosen[i];
            int finish = start;
            while (i + 1 < chosen.Length && chosen[i + 1] == finish + 1)
            {
                i++;
                finish = chosen[i];
            }
            if (compact.Length > 0) compact.Append(',');
            compact.Append(start.ToString(CultureInfo.InvariantCulture));
            if (finish != start)
                compact.Append('-').Append(finish.ToString(CultureInfo.InvariantCulture));
            i++;
        }
        return new PrintJobPlan(printerName, sourcePageCount,
            Array.AsReadOnly(chosen), copies, duplex, paper, compact.ToString(),
            orientation, color, pagesPerSheet, tray);
    }
}

public enum PrintDuplex { OneSided = 0, LongEdge = 1, ShortEdge = 2 }
public enum PrintPaper { A4 = 0, A3 = 1 }

public enum PrintOrientation { Automatic = 0, Portrait = 1, Landscape = 2 }
public enum PrintColorMode { PrinterDefault = 0, Color = 1, Monochrome = 2 }
public enum PrintPagesPerSheet { One = 1, Two = 2, Four = 4 }
