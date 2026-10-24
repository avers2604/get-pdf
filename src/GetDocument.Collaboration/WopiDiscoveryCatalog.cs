using System.Xml;
using System.Xml.Linq;

namespace GetDocument.Collaboration;

/// <summary>Parses authenticated Collabora discovery XML; never fetches untrusted URLs.</summary>
public static class WopiDiscoveryCatalog
{
    public static Uri SelectViewAction(string xml, string extension, Uri officeOrigin)
    {
        OnlyOfficeUriPolicy.RequireHttpsOrigin(officeOrigin);
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 1024 * 1024)
            throw new InvalidDataException("Invalid WOPI discovery document.");
        extension = extension.TrimStart('.').ToLowerInvariant();
        if (extension is not ("docx" or "xlsx" or "pptx"))
            throw new NotSupportedException("Unsupported WOPI extension.");

        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024,
            IgnoreComments = true
        });
        var document = XDocument.Load(reader);
        var matches = document.Descendants("app")
            .Where(app => string.Equals((string?)app.Attribute("name"), extension,
                StringComparison.OrdinalIgnoreCase) || app.Elements("action").Any(a =>
                string.Equals((string?)a.Attribute("ext"), extension, StringComparison.OrdinalIgnoreCase)))
            .Elements("action")
            .Where(action => (string?)action.Attribute("name") == "view" &&
                ((string?)action.Attribute("ext") is null ||
                 string.Equals((string?)action.Attribute("ext"), extension, StringComparison.OrdinalIgnoreCase)))
            .Select(action => (string?)action.Attribute("urlsrc"))
            .Where(url => !string.IsNullOrWhiteSpace(url)).ToArray();
        if (matches.Length != 1 ||
            !Uri.TryCreate(matches[0], UriKind.Absolute, out var actionUrl))
            throw new InvalidDataException("No unique WOPI view action.");
        // Discovery urlsrc may contain template placeholders in the query.
        // Require a strict HTTPS authority; no credentials, fragment or external host.
        OnlyOfficeUriPolicy.RequireTrustedUrl(actionUrl, officeOrigin);
        return actionUrl;
    }
}
