using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bt: read-only Windows TOKEN_GROUPS diagnostics from an ALREADY HELD
/// token handle. This is not Windows AccessCheck and cannot authorize capture,
/// file publication, deletion, or native EuroOffice SaveAs.
/// No process/thread token is opened here; caller owns the supplied handle.
/// </summary>
internal static class RecoveryWindowsTokenGroupProbe
{
    private const int TokenGroupsClass = 2; // TOKEN_INFORMATION_CLASS.TokenGroups
    private const int ErrorInsufficientBuffer = 122;
    private const int MaxGroupCount = 256;
    private const uint MaxBufferBytes = 128 * 1024;
    private const uint GroupEnabled = 0x00000004;
    private const uint GroupDenyOnly = 0x00000010;
    private const uint GroupIntegrity = 0x00000020;
    private const uint GroupIntegrityEnabled = 0x00000040;
    // Flags defined by TOKEN_GROUPS: mandatory, enabled-by-default, enabled,
    // owner, deny-only, integrity, integrity-enabled, resource and logon-id.
    private const uint KnownGroupFlags = 0xE000007F;
    private const uint PotentialWriteMask = 0x00000002 | 0x00000004 |
        0x00000010 | 0x00000040 | 0x00000100 | 0x00010000 |
        0x00040000 | 0x00080000 | 0x10000000 | 0x40000000;

    internal static RecoveryWindowsTokenGroupState ClassifyAttributes(uint flags)
    {
        if ((flags & ~KnownGroupFlags) != 0 ||
            (flags & (GroupIntegrity | GroupIntegrityEnabled)) != 0 ||
            ((flags & GroupDenyOnly) != 0 && (flags & GroupEnabled) != 0))
            return RecoveryWindowsTokenGroupState.Unreviewed;

        if ((flags & GroupDenyOnly) != 0)
            return RecoveryWindowsTokenGroupState.DenyOnly;
        if ((flags & GroupEnabled) != 0)
            return RecoveryWindowsTokenGroupState.Enabled;
        return RecoveryWindowsTokenGroupState.Disabled;
    }

    internal static RecoveryWindowsTokenAceFinding CompareAllow(
        RecoveryWindowsAclAce ace, IReadOnlyList<RecoveryWindowsTokenGroup> groups)
    {
        // Validate all supplied evidence even if this ACE itself is harmless.
        _ = Summarize(groups);
        if (ace.Type != 0 || (ace.AccessMask & PotentialWriteMask) == 0)
            return RecoveryWindowsTokenAceFinding.NoRelevantAllowWrite;
        if ((ace.Flags & 0x08) != 0) // INHERIT_ONLY_ACE, excludes this object
            return RecoveryWindowsTokenAceFinding.InheritOnlyNotApplicableToObject;
        if (!IsSidGrammar(ace.TrusteeSid))
            return RecoveryWindowsTokenAceFinding.UnresolvedTokenCoverage;

        foreach (RecoveryWindowsTokenGroup group in groups)
        {
            if (!string.Equals(group.Sid, ace.TrusteeSid, StringComparison.OrdinalIgnoreCase))
                continue;
            return ClassifyAttributes(group.Attributes) switch
            {
                RecoveryWindowsTokenGroupState.Enabled =>
                    RecoveryWindowsTokenAceFinding.EnabledGroupWriteAllowObserved,
                RecoveryWindowsTokenGroupState.DenyOnly =>
                    RecoveryWindowsTokenAceFinding.DenyOnlyAllowIgnored,
                RecoveryWindowsTokenGroupState.Disabled =>
                    RecoveryWindowsTokenAceFinding.DisabledGroupAllowIgnored,
                _ => RecoveryWindowsTokenAceFinding.UnresolvedTokenCoverage
            };
        }

        // TOKEN_GROUPS omits the user SID. Restricted SIDs, group nesting,
        // privileges, MIC, ancestor ACLs and other tokens are not modeled.
        return RecoveryWindowsTokenAceFinding.UnresolvedTokenCoverage;
    }

    internal static RecoveryWindowsTokenAceFinding CompareAllow(
        RecoveryWindowsAclAce ace, RecoveryWindowsTokenGroupEvidence evidence) =>
        CompareAllow(ace, evidence.Groups ??
            Array.Empty<RecoveryWindowsTokenGroup>());

    internal static RecoveryWindowsTokenGroupEvidence Summarize(
        IReadOnlyList<RecoveryWindowsTokenGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count > MaxGroupCount)
            throw new InvalidDataException("Слишком много Windows token groups.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int enabled = 0, denyOnly = 0, disabled = 0, unreviewed = 0;
        foreach (RecoveryWindowsTokenGroup group in groups)
        {
            if (!IsSidGrammar(group.Sid) || !seen.Add(group.Sid))
                throw new InvalidDataException("Недостоверный или повторный SID token group.");
            switch (ClassifyAttributes(group.Attributes))
            {
                case RecoveryWindowsTokenGroupState.Enabled: enabled++; break;
                case RecoveryWindowsTokenGroupState.DenyOnly: denyOnly++; break;
                case RecoveryWindowsTokenGroupState.Disabled: disabled++; break;
                default: unreviewed++; break;
            }
        }
        return new RecoveryWindowsTokenGroupEvidence(groups.Count,
            enabled, denyOnly, disabled, unreviewed,
            Array.AsReadOnly(groups.ToArray()));
    }

    /// <summary>
    /// Read an existing token's TOKEN_GROUPS with bounds checks and no
    /// AdjustTokenGroups/Privileges, access checks, identity impersonation,
    /// filesystem writes or access-control changes.
    /// </summary>
    internal static RecoveryWindowsTokenGroupEvidence ObserveExistingToken(
        SafeFileHandle token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.IsClosed || token.IsInvalid)
            throw new ObjectDisposedException(nameof(token));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("TOKEN_GROUPS доступен только на Windows.");

        // First call is a size request; ERROR_INSUFFICIENT_BUFFER is expected.
        if (GetTokenInformation(token, TokenGroupsClass, 0, 0, out uint required))
            throw new InvalidDataException("Неверный ответ Windows TOKEN_GROUPS size probe.");
        int firstError = Marshal.GetLastPInvokeError();
        if (firstError != ErrorInsufficientBuffer)
            throw new IOException("Windows не предоставила размер TOKEN_GROUPS.",
                new Win32Exception(firstError));
        if (required < 8 || required > MaxBufferBytes)
            throw new InvalidDataException("Размер TOKEN_GROUPS вне лимита.");

        nint buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!GetTokenInformation(token, TokenGroupsClass, buffer, required, out uint actual))
                throw new IOException("Windows не предоставила TOKEN_GROUPS.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            if (actual < 8 || actual > required)
                throw new InvalidDataException("Недопустимый фактический размер TOKEN_GROUPS.");

            uint count = unchecked((uint)Marshal.ReadInt32(buffer));
            int headerBytes = IntPtr.Size == 8 ? 8 : 4;
            int entryBytes = IntPtr.Size == 8 ? 16 : 8;
            if (count > MaxGroupCount ||
                (ulong)headerBytes + (ulong)count * (ulong)entryBytes > actual)
                throw new InvalidDataException("TOKEN_GROUPS содержит чрезмерный или усечённый список.");

            var groups = new List<RecoveryWindowsTokenGroup>(checked((int)count));
            for (int index = 0; index < count; index++)
            {
                int entry = headerBytes + index * entryBytes;
                nint sid = Marshal.ReadIntPtr(buffer, entry);
                uint attributes = unchecked((uint)Marshal.ReadInt32(buffer,
                    entry + IntPtr.Size));
                // SID must point INSIDE the returned owned buffer. Check
                // header and complete fixed-size subauthority data BEFORE any
                // Win32 SID helper is invoked.
                long relative = sid.ToInt64() - buffer.ToInt64();
                if (relative < 0 || relative > actual - 8)
                    throw new InvalidDataException("SID вне буфера TOKEN_GROUPS.");
                byte subAuthorityCount = Marshal.ReadByte(sid, 1);
                int sidBytes = 8 + 4 * subAuthorityCount;
                if (subAuthorityCount > 15 || relative > actual - sidBytes)
                    throw new InvalidDataException("SID выходит за границы TOKEN_GROUPS.");
                groups.Add(new RecoveryWindowsTokenGroup(
                    SidToString(sid), attributes));
            }
            return Summarize(groups);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string SidToString(nint sid)
    {
        nint text = 0;
        try
        {
            if (!ConvertSidToStringSidW(sid, out text))
                throw new IOException("Windows не смогла преобразовать SID токена.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            string? value = Marshal.PtrToStringUni(text);
            if (!IsSidGrammar(value))
                throw new InvalidDataException("Некорректный SID из TOKEN_GROUPS.");
            return value!;
        }
        finally
        {
            if (text != 0) LocalFree(text);
        }
    }

    private static bool IsSidGrammar(string? sid)
    {
        if (sid is null || sid.Length < 7 || sid.Length > 256 ||
            !sid.StartsWith("S-1-", StringComparison.Ordinal))
            return false;
        string[] fields = sid[4..].Split('-');
        return fields.Length >= 2 && fields.All(x =>
            x.Length > 0 && x.All(char.IsAsciiDigit));
    }

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeFileHandle token, int informationClass, nint output,
        uint outputBytes, out uint returnedBytes);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(nint sid, out nint text);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}

internal readonly record struct RecoveryWindowsTokenGroup(string Sid, uint Attributes);

internal enum RecoveryWindowsTokenGroupState
{
    Enabled,
    DenyOnly,
    Disabled,
    Unreviewed
}

internal enum RecoveryWindowsTokenAceFinding
{
    NoRelevantAllowWrite,
    InheritOnlyNotApplicableToObject,
    EnabledGroupWriteAllowObserved,
    DenyOnlyAllowIgnored,
    DisabledGroupAllowIgnored,
    UnresolvedTokenCoverage
}

/// <summary>
/// Aggregated diagnostic only. Zero findings NEVER mean verified access.
/// The token may not be the one used at the moment of a write.
/// </summary>
internal readonly record struct RecoveryWindowsTokenGroupEvidence(
    int GroupCount, int EnabledCount, int DenyOnlyCount,
    int DisabledCount, int UnreviewedCount,
    IReadOnlyList<RecoveryWindowsTokenGroup> Groups)
{
    internal bool CanAuthorizeCapture => false;
}
