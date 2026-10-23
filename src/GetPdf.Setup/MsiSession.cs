using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GetPdf.Setup;

/// <summary>
/// Thin Windows Installer (msi.dll) boundary: the wizard draws its own GET DS screens and
/// receives progress through an external UI handler, while the MSI keeps doing the actual
/// install, upgrade (MajorUpgrade), repair, rollback and Apps &amp; Features entry.
/// </summary>
internal sealed class MsiSession
{
    // Same UpgradeCode as packaging/GetPdf.wxs: identifies every ГЭТ PDF version.
    internal const string UpgradeCode = "{BE5C1496-701B-4ED4-937E-DA3B41C2A770}";

    private const int InstallUiLevelNone = 2;
    private const uint MessageFatalExit = 0x00000000, MessageError = 0x01000000, MessageWarning = 0x02000000,
        MessageUser = 0x03000000, MessageActionStart = 0x08000000, MessageActionData = 0x09000000,
        MessageProgress = 0x0A000000, MessageCommonData = 0x0B000000;
    private const uint LogModeVerbose = 0x1000, LogAllButVerbose = 0x0FFF;
    private const int IdOk = 1, IdCancel = 2;

    private delegate int InstallUiHandler(IntPtr context, uint messageType,
        [MarshalAs(UnmanagedType.LPWStr)] string? message);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiSetInternalUI(int uiLevel, IntPtr window);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern InstallUiHandler? MsiSetExternalUI(InstallUiHandler? handler, uint filter, IntPtr context);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiInstallProduct(string packagePath, string commandLine);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiConfigureProductEx(string productCode, int installLevel, int installState, string commandLine);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnableLog(uint logMode, string? logFile, uint attributes);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfo(string productCode, string property, StringBuilder value, ref uint length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>Installed ГЭТ PDF (any version) or null.</summary>
    internal static (string ProductCode, Version Version, string Location)? FindInstalled()
    {
        var code = new StringBuilder(39);
        if (MsiEnumRelatedProducts(UpgradeCode, 0, 0, code) != 0) return null;
        string product = code.ToString();
        string version = Info(product, "VersionString");
        string location = Info(product, "InstallLocation");
        return (product, Version.TryParse(version, out var v) ? v : new Version(0, 0), location);
    }

    private static string Info(string product, string property)
    {
        uint length = 512;
        var value = new StringBuilder((int)length);
        return MsiGetProductInfo(product, property, value, ref length) == 0 ? value.ToString() : "";
    }

    private readonly InstallUiHandler _handler;   // kept alive for the whole native call
    private readonly IntPtr _owner;
    private volatile bool _cancel;
    private int _phase;
    private long _total, _done, _step;
    private bool _forward = true, _countActionData;

    /// <summary>0…1 overall fraction, and a human description of the current action.</summary>
    internal event Action<double, string?>? Progress;

    internal MsiSession(IntPtr owner)
    {
        _owner = owner;
        _handler = Handle;
    }

    internal void Cancel() => _cancel = true;

    internal uint Install(string msiPath, string commandLine, string logPath) =>
        Run(logPath, () => MsiInstallProduct(msiPath, commandLine));

    internal uint Uninstall(string productCode, string logPath) =>
        Run(logPath, () => MsiConfigureProductEx(productCode, 0, 2 /* INSTALLSTATE_ABSENT */, "REBOOT=ReallySuppress"));

    private uint Run(string logPath, Func<uint> action)
    {
        MsiEnableLog(LogModeVerbose | LogAllButVerbose, logPath, 0);
        MsiSetInternalUI(InstallUiLevelNone, IntPtr.Zero);
        var previous = MsiSetExternalUI(_handler,
            MessageFatalExit | MessageError | MessageWarning | MessageUser | MessageActionStart |
            MessageActionData | MessageProgress | MessageCommonData, IntPtr.Zero);
        try { return action(); }
        finally
        {
            MsiSetExternalUI(previous, 0, IntPtr.Zero);
            MsiEnableLog(0, null, 0);
        }
    }

    private int Handle(IntPtr context, uint messageType, string? message)
    {
        if (_cancel) return IdCancel;
        uint kind = messageType & 0xFF000000;
        switch (kind)
        {
            case MessageProgress:
                OnProgress(message);
                return IdOk;
            case MessageActionStart:
                Progress?.Invoke(Fraction(), DescribeAction(message));
                return IdOk;
            case MessageActionData:
                if (_countActionData) { _done += _forward ? _step : -_step; Progress?.Invoke(Fraction(), null); }
                return IdOk;
            case MessageError:
            case MessageWarning:
            case MessageUser:
            case MessageFatalExit:
                if (string.IsNullOrEmpty(message)) return 0;
                // Low bits carry MB_* buttons/icons; the native box returns the matching ID*.
                return MessageBoxW(_owner, message!, "Установка ГЭТ PDF", messageType & 0x00FFFFFF);
            default:
                return 0;
        }
    }

    private void OnProgress(string? message)
    {
        var f = ProgressFields(message);
        if (f.Length < 2) return;
        switch (f[0])
        {
            case 0: // reset: new phase (script generation, then execution)
                _phase++;
                _total = Math.Max(1, f[1]);
                _forward = f.Length < 3 || f[2] == 0;
                _done = _forward ? 0 : _total;
                _countActionData = false;
                break;
            case 1: // action info: optional ticks per ActionData message
                _step = f[1];
                _countActionData = f.Length > 2 && f[2] == 1;
                break;
            case 2: // explicit progress
                _done += _forward ? f[1] : -f[1];
                break;
            case 3: // the action adds to the expected total
                _total += f[1];
                break;
        }
        Progress?.Invoke(Fraction(), null);
    }

    private double Fraction()
    {
        double inPhase = _total <= 0 ? 0 : Math.Max(0, Math.Min(1, (double)_done / _total));
        // Phase 1 (planning) is short; execution carries almost all of the visible bar.
        return _phase <= 1 ? 0.08 * inPhase : 0.08 + 0.92 * inPhase;
    }

    /// <summary>"1: 0 2: 12345 3: 0 4: 0" → [0, 12345, 0, 0].</summary>
    internal static long[] ProgressFields(string? message)
    {
        if (string.IsNullOrEmpty(message)) return Array.Empty<long>();
        var values = new System.Collections.Generic.List<long>();
        foreach (var part in message!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (!part.EndsWith(":", StringComparison.Ordinal) && long.TryParse(part, out long value))
                values.Add(value);
        return values.ToArray();
    }

    /// <summary>"Action 12:01:02: InstallFiles. Копирование новых файлов" → "Копирование новых файлов".</summary>
    internal static string? DescribeAction(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        int dot = message!.IndexOf(". ", StringComparison.Ordinal);
        string text = dot >= 0 ? message.Substring(dot + 2).Trim() : "";
        text = text.TrimEnd('.');
        return text.Length > 0 ? text : null;
    }
}
