using System.Diagnostics;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Pinned, explicitly overlaid SDK build only, Linux/Wayland disposable
/// synthetic VM. A real renderer callback is NOT document import or save.
/// Runtime executable SHA must be separately pinned and approved by IT.
/// </summary>
public static class EuroOfficeNativeReadyProbeLab
{
    public static ProcessStartInfo CreateStartInfo(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved)
    {
        ProcessStartInfo info = EuroOfficeOsrStreamProcessLab.CreateStartInfo(
            host, stagingDirectory, fixtureName, sessionId, tabId, labApproved);
        // This command is implemented only by the manually patched, lab-only
        // SDK executable; unchanged pinned binaries reject it.
        info.ArgumentList[0] = "--open-staged-ready-event-lab";
        return info;
    }

    public static async Task<EuroOfficeNativeReadyCandidate> RunAsync(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo info = CreateStartInfo(host, stagingDirectory,
            fixtureName, sessionId, tabId, labApproved);
        string json = await EuroOfficeNativeBindingProbeLab.RunRawLabAsync(
            info, cancellationToken);
        return EuroOfficeNativeReadyCandidate.Parse(json, sessionId, tabId, fixtureName);
    }
}
