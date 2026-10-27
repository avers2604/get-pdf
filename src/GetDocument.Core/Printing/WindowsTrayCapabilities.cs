using System.Xml;
using System.Xml.Linq;

namespace GetDocument.Core.Printing;

/// <summary>A paper source as the Windows driver names it in its PrintCapabilities.</summary>
public sealed record WindowsTrayChoice(string Feature, string NamespaceUri, string LocalName, string Label);

/// <summary>
/// Reads paper sources from the driver's Print Schema XML. The WPF <c>InputBin</c> enum only knows
/// five generic keywords, so real MFP trays ("Main Tray", "Лоток 2", "Обходной лоток"), which drivers
/// publish as vendor options such as <c>ns0000:_1</c>, would be lost; here they are kept with the
/// driver's own display name. "Automatically select" is left out: the wizard offers it anyway.
/// </summary>
public static class WindowsTrayCapabilities
{
    public const string FrameworkNamespace = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    public const string KeywordsNamespace = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";
    private const int MaxTrays = 32;
    // Job-level first: it applies to the whole document, which is what the wizard sets.
    private static readonly string[] Features = ["JobInputBin", "DocumentInputBin", "PageInputBin"];

    public static IReadOnlyList<WindowsTrayChoice> Parse(string? capabilitiesXml)
    {
        if (string.IsNullOrEmpty(capabilitiesXml) || capabilitiesXml.Length > 8 * 1024 * 1024) return [];
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(capabilitiesXml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return [];
        }
        XNamespace psf = FrameworkNamespace;
        var features = document.Root?.Elements(psf + "Feature").ToList() ?? [];
        foreach (string wanted in Features)
        {
            var feature = features.FirstOrDefault(f => Resolve(f, (string?)f.Attribute("name")) is { } name &&
                name.Namespace == KeywordsNamespace && name.Local == wanted);
            if (feature is null) continue;
            var choices = new List<WindowsTrayChoice>();
            foreach (var option in feature.Elements(psf + "Option"))
            {
                if (Resolve(option, (string?)option.Attribute("name")) is not { } name) continue;
                if (name.Namespace == KeywordsNamespace && name.Local == "AutoSelect") continue;
                if (!PrintTrayOptions.IsValidValue(name.Local) || choices.Any(c => c.LocalName == name.Local)) continue;
                string label = option.Elements(psf + "Property")
                    .Where(p => Resolve(p, (string?)p.Attribute("name")) is { Local: "DisplayName" })
                    .Select(p => (string?)p.Element(psf + "Value"))
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? name.Local;
                choices.Add(new WindowsTrayChoice(wanted, name.Namespace, name.Local, PrintTrayOptions.CleanLabel(label)));
                if (choices.Count == MaxTrays) break;
            }
            return choices;
        }
        return [];
    }

    /// <summary>A PrintTicket fragment selecting <paramref name="choice"/>, to merge with the job ticket.</summary>
    public static string TicketDelta(WindowsTrayChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        XNamespace psf = FrameworkNamespace, psk = KeywordsNamespace, vendor = choice.NamespaceUri;
        bool keyword = choice.NamespaceUri == KeywordsNamespace;
        var root = new XElement(psf + "PrintTicket",
            new XAttribute(XNamespace.Xmlns + "psf", FrameworkNamespace),
            new XAttribute(XNamespace.Xmlns + "psk", KeywordsNamespace),
            new XAttribute("version", "1"),
            new XElement(psf + "Feature", new XAttribute("name", "psk:" + choice.Feature),
                new XElement(psf + "Option", new XAttribute("name", (keyword ? "psk:" : "getbin:") + choice.LocalName))));
        if (!keyword) root.Add(new XAttribute(XNamespace.Xmlns + "getbin", vendor.NamespaceName));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>True when the validated ticket still selects <paramref name="choice"/> (the driver did not drop it).</summary>
    public static bool TicketSelects(string? ticketXml, WindowsTrayChoice choice)
    {
        if (string.IsNullOrEmpty(ticketXml)) return false;
        try
        {
            XNamespace psf = FrameworkNamespace;
            var root = XDocument.Parse(ticketXml).Root;
            return root?.Elements(psf + "Feature").Any(f =>
                Resolve(f, (string?)f.Attribute("name")) is { Local: var feature } && feature == choice.Feature &&
                f.Elements(psf + "Option").Any(o => Resolve(o, (string?)o.Attribute("name")) is { } name &&
                    name.Namespace == choice.NamespaceUri && name.Local == choice.LocalName)) == true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static (string Namespace, string Local)? Resolve(XElement scope, string? qualifiedName)
    {
        if (string.IsNullOrEmpty(qualifiedName)) return null;
        int colon = qualifiedName.IndexOf(':');
        if (colon <= 0) return null;
        XNamespace? ns = scope.GetNamespaceOfPrefix(qualifiedName[..colon]);
        return ns is null ? null : (ns.NamespaceName, qualifiedName[(colon + 1)..]);
    }
}
