using System.Globalization;
using GetDocument.Office.EuroOfficeDesktop;

namespace GetDocWindowsPipeDenialLab;

/// <summary>
/// Manual, separately launched Windows-only diagnostic roles. Never a service,
/// installer, process launcher, native SaveAs adapter or production integration.
/// Always exits 2; even successful denial observation is NOT release evidence.
/// </summary>
public static class Program
{
    private const string EnvName = "GETDOC_GD31J27AK_VM";
    private const string EnvValue = "OFFLINE_DISPOSABLE_NO_DOCS";
    private const string OperatorAck =
        "I_APPROVE_DISPOSABLE_WINDOWS_VM_NO_DOCUMENTS_DENIAL_ONLY";

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable(EnvName) != EnvValue ||
            args.Length < 3 ||
            !Guid.TryParseExact(args[1], "N", out Guid runId) ||
            runId == Guid.Empty ||
            !string.Equals(runId.ToString("N"), args[1], StringComparison.Ordinal))
            return 2;

        try
        {
            if (args.Length == 3 &&
                args[0] == "client" && args[2] == OperatorAck)
            {
                // Start this role first in a separate console, then manually
                // pass this exact process ID to the second (server) console.
                Console.WriteLine("GD31J27AK_CLIENT_PID=" + Environment.ProcessId);
                await WindowsNamedPipeDenialLab.ReceiveDenialOnceAsync(runId);
                Console.WriteLine("GD31J27AK_CLIENT_DENIAL_OBSERVED_NOT_ATTESTED");
            }
            else if (args.Length == 4 &&
                     args[0] == "server" && args[3] == OperatorAck &&
                     int.TryParse(args[2], NumberStyles.None,
                         CultureInfo.InvariantCulture, out int pinnedPid))
            {
                await WindowsNamedPipeDenialLab.ServeDeniedOnceAsync(
                    runId, pinnedPid);
                Console.WriteLine("GD31J27AK_SERVER_DENIAL_SENT_NOT_ATTESTED");
            }
        }
        catch (Exception ex) when (ex is IOException or
                                   InvalidDataException or
                                   UnauthorizedAccessException or
                                   OperationCanceledException or
                                   PlatformNotSupportedException or
                                   ArgumentException)
        {
            // Never write OS identities, paths, payloads or exception details
            // to logs. Distinguish denial observation from failure by marker,
            // not the always-denied exit code.
        }
        return 2;
    }
}
