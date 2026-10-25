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
        int copies, PrintDuplex duplex, PrintPaper paper, string range)
    {
        PrinterName = printerName;
        SourcePageCount = sourcePageCount;
        Pages = pages;
        Copies = copies;
        Duplex = duplex;
        Paper = paper;
        CupsPageRange = range;
    }

    public string PrinterName { get; }
    public int SourcePageCount { get; }
    public IReadOnlyList<int> Pages { get; }
    public int Copies { get; }
    public PrintDuplex Duplex { get; }
    public PrintPaper Paper { get; }
    public string CupsPageRange { get; }
    public int PageCountToPrint => checked(Pages.Count * Copies);

    public static PrintJobPlan Create(
        string printerName, int sourcePageCount, string? pageRange, int copies,
        PrintDuplex duplex, PrintPaper paper)
    {
        if (string.IsNullOrWhiteSpace(printerName) || printerName.Length > 160 ||
            printerName[0] == '-' || printerName.Any(c => char.IsControl(c)))
            throw new ArgumentException("Выберите допустимый принтер.", nameof(printerName));
        if (sourcePageCount is < 1 or > 1000)
            throw new ArgumentException("Нельзя распечатать PDF с некорректным числом страниц.",
                nameof(sourcePageCount));
        if (copies is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(copies), "Разрешено от 1 до 99 копий.");
        if (!Enum.IsDefined(duplex) || !Enum.IsDefined(paper))
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
            Array.AsReadOnly(chosen), copies, duplex, paper, compact.ToString());
    }
}

public enum PrintDuplex { OneSided = 0, LongEdge = 1, ShortEdge = 2 }
public enum PrintPaper { A4 = 0, A3 = 1 }
