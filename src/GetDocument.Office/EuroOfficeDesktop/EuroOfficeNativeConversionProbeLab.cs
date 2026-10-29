using System.Diagnostics;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Synthetic-only conversion candidate: no user path, editor capability,
/// direct write, WORM authorization or production launcher integration.
/// Runs the pinned, SHA-approved Qt lab with a single bounded JSON receipt.
/// </summary>
public static class EuroOfficeNativeConversionProbeLab
{
    public static ProcessStartInfo CreateStartInfo(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved)
    {
        ProcessStartInfo info = EuroOfficeOsrStreamProcessLab.CreateStartInfo(
            host, stagingDirectory, fixtureName, sessionId, tabId, labApproved);
        info.ArgumentList[0] = "--open-staged-conversion-evidence-lab";
        return info;
    }

    public static async Task<EuroOfficeNativeConversionCandidate> RunAsync(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo info = CreateStartInfo(host, stagingDirectory,
            fixtureName, sessionId, tabId, labApproved);
        string raw = await EuroOfficeNativeBindingProbeLab.RunRawLabAsync(
            info, cancellationToken);
        return EuroOfficeNativeConversionCandidate.Parse(raw, sessionId, tabId, fixtureName);
    }
}
