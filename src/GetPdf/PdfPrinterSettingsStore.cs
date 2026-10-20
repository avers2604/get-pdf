using System.Printing;
using System.Text.Json;
using System.Windows.Controls;

namespace GetPdf;

internal enum PdfPaperChoice { Default, A4, A3 }
internal enum PdfOrientationChoice { Default, Portrait, Landscape }
internal enum PdfDuplexChoice { Default, OneSided, LongEdge, ShortEdge }

internal sealed record PdfPrinterSettings(
    PdfPaperChoice Paper, PdfOrientationChoice Orientation,
    PdfDuplexChoice Duplex, int Copies)
{
    internal static PdfPrinterSettings Default { get; } = new(
        PdfPaperChoice.Default, PdfOrientationChoice.Default,
        PdfDuplexChoice.Default, 1);
}

/// <summary>Per-user printer preferences; no printer name or PDF content stored.</summary>
internal static class PdfPrinterSettingsStore
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GET-PDF", "printing.json");

    internal static void Validate(PdfPrinterSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Paper) ||
            !Enum.IsDefined(value.Orientation) ||
            !Enum.IsDefined(value.Duplex) ||
            value.Copies is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(value), "Неподдерживаемые параметры печати PDF.");
    }

    internal static PdfPrinterSettings Read() => ReadFrom(DefaultPath);
    internal static PdfPrinterSettings ReadFrom(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096)
                return PdfPrinterSettings.Default;
            var stored = JsonSerializer.Deserialize<PdfPrinterSettings>(File.ReadAllText(path));
            if (stored is null) return PdfPrinterSettings.Default;
            Validate(stored);
            return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or JsonException or NotSupportedException)
        { return PdfPrinterSettings.Default; }
    }

    internal static void Save(PdfPrinterSettings value) => SaveTo(DefaultPath, value);
    internal static void SaveTo(string path, PdfPrinterSettings value)
    {
        Validate(value);
        string target = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,
            ".printing-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// Apply only supported PrintTicket choices to the driver-selected queue.
    /// Do not claim duplex/A3 support unless the driver advertises it.
    /// </summary>
    internal static void ApplyTo(PrintDialog dialog, PdfPrinterSettings settings)
    {
        Validate(settings);
        var ticket = dialog.PrintTicket ?? new System.Printing.PrintTicket();
        var capabilities = dialog.PrintQueue.GetPrintCapabilities(ticket);
        PageMediaSizeName? media = settings.Paper switch
        {
            PdfPaperChoice.A4 => PageMediaSizeName.ISOA4,
            PdfPaperChoice.A3 => PageMediaSizeName.ISOA3,
            _ => null
        };
        if (media is { } choice)
        {
            if (!capabilities.PageMediaSizeCapability.Any(x => x.PageMediaSizeName == choice))
                throw new NotSupportedException("Выбранная бумага A3/A4 не поддерживается драйвером принтера.");
            ticket.PageMediaSize = new PageMediaSize(choice);
        }
        PageOrientation? orientation = settings.Orientation switch
        {
            PdfOrientationChoice.Portrait => PageOrientation.Portrait,
            PdfOrientationChoice.Landscape => PageOrientation.Landscape,
            _ => null
        };
        if (orientation is { } oriented)
        {
            if (!capabilities.PageOrientationCapability.Contains(oriented))
                throw new NotSupportedException("Выбранная ориентация не поддерживается драйвером принтера.");
            ticket.PageOrientation = oriented;
        }
        Duplexing? duplex = settings.Duplex switch
        {
            PdfDuplexChoice.OneSided => Duplexing.OneSided,
            PdfDuplexChoice.LongEdge => Duplexing.TwoSidedLongEdge,
            PdfDuplexChoice.ShortEdge => Duplexing.TwoSidedShortEdge,
            _ => null
        };
        if (duplex is { } selected)
        {
            if (!capabilities.DuplexingCapability.Contains(selected))
                throw new NotSupportedException("Выбранный дуплекс не поддерживается драйвером принтера.");
            ticket.Duplexing = selected;
        }
        ticket.CopyCount = settings.Copies;
        dialog.PrintTicket = ticket;
    }
}
