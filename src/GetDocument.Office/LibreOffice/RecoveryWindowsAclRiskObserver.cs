using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bs: Windows DACL read-only diagnostic from an ALREADY OPEN handle.
/// Finds obvious broad-group ALLOW-write ACEs; it does NOT evaluate effective
/// token rights, privileges, owner implicit rights, deny precedence, nested
/// groups, inheritance semantics, mandatory labels, or ancestor DACLs.
/// Consequently no observation can grant capture, publication or deletion.
/// Not registered with production OfficeRecoveryStore or the volume coordinator.
/// </summary>
internal static class RecoveryWindowsAclRiskObserver
{
    private const int FileObject = 1; // SE_FILE_OBJECT
    private const uint DaclSecurityInformation = 0x00000004;
    private const int AclSizeInformation = 2;
    private const byte AccessAllowedAceType = 0;
    private const byte AccessDeniedAceType = 1;
    private const int MaxAceCount = 256;
    private const uint PotentialWriteMask = 0x00000002 // FILE_ADD_FILE / FILE_WRITE_DATA
                                            | 0x00000004 // FILE_ADD_SUBDIRECTORY / APPEND
                                            | 0x00000010 // FILE_WRITE_EA
                                            | 0x00000040 // FILE_DELETE_CHILD
                                            | 0x00000100 // FILE_WRITE_ATTRIBUTES
                                            | 0x00010000 // DELETE
                                            | 0x00040000 // WRITE_DAC
                                            | 0x00080000 // WRITE_OWNER
                                            | 0x10000000 // GENERIC_ALL
                                            | 0x40000000; // GENERIC_WRITE

    internal static RecoveryWindowsAceRisk ClassifyAce(RecoveryWindowsAclAce ace)
    {
        // Other ACE types (callback, object, conditional, resource, audit...)
        // remain unreviewed instead of being treated as effective deny/safety.
        if (ace.Type == AccessDeniedAceType)
            return RecoveryWindowsAceRisk.DenyNotEvaluated;
        if (ace.Type != AccessAllowedAceType ||
            !LooksLikeCanonicalSid(ace.TrusteeSid))
            return RecoveryWindowsAceRisk.UnreviewedAce;
        if ((ace.AccessMask & PotentialWriteMask) == 0)
            return RecoveryWindowsAceRisk.NoBroadWriteObserved;
        return ace.TrusteeSid is "S-1-1-0" // Everyone
            or "S-1-5-11"                 // Authenticated Users
            or "S-1-5-32-545"             // BUILTIN\Users
            or "S-1-5-32-546"             // BUILTIN\Guests
            or "S-1-5-2"                  // NETWORK
            ? RecoveryWindowsAceRisk.BroadGroupWriteAllow
            : RecoveryWindowsAceRisk.NoBroadWriteObserved;
    }

    internal static RecoveryWindowsAclRiskEvidence Summarize(
        IReadOnlyList<RecoveryWindowsAclAce> aces)
    {
        ArgumentNullException.ThrowIfNull(aces);
        if (aces.Count > MaxAceCount)
            throw new InvalidDataException(
                "Слишком много ACE для ограниченного аудита DACL.");
        int broadWrite = 0, unreviewed = 0, deny = 0;
        foreach (RecoveryWindowsAclAce ace in aces)
        {
            switch (ClassifyAce(ace))
            {
                case RecoveryWindowsAceRisk.BroadGroupWriteAllow:
                    broadWrite++;
                    break;
                case RecoveryWindowsAceRisk.UnreviewedAce:
                    unreviewed++;
                    break;
                case RecoveryWindowsAceRisk.DenyNotEvaluated:
                    deny++;
                    break;
            }
        }
        return new RecoveryWindowsAclRiskEvidence(
            aces.Count, broadWrite, unreviewed, deny);
    }

    internal static RecoveryWindowsAclRiskEvidence ObserveExistingHandle(
        SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsClosed || handle.IsInvalid)
            throw new ObjectDisposedException(nameof(handle));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Native DACL inspector предназначен только для Windows.");

        nint acl = 0, descriptor = 0;
        uint error = GetSecurityInfo(handle, FileObject, DaclSecurityInformation,
            0, 0, out acl, 0, out descriptor);
        try
        {
            if (error != 0)
                throw new IOException("Нельзя прочитать DACL открытого объекта.",
                    new Win32Exception(unchecked((int)error)));
            // A NULL DACL allows almost unrestricted access: reject even
            // reporting it as "empty". Never use this as a positive finding.
            if (acl == 0)
                throw new InvalidDataException(
                    "NULL DACL потенциально открывает всем доступ.");
            if (!GetAclInformation(acl, out AclSizeInfo info,
                    (uint)Marshal.SizeOf<AclSizeInfo>(), AclSizeInformation))
                throw new IOException("Windows не предоставила размер DACL.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            if (info.AceCount > MaxAceCount)
                throw new InvalidDataException(
                    "Количество ACE превышает лимит безопасной диагностики.");

            var entries = new List<RecoveryWindowsAclAce>((int)info.AceCount);
            for (uint index = 0; index < info.AceCount; index++)
            {
                if (!GetAce(acl, index, out nint address) || address == 0)
                    throw new IOException("Windows не предоставила ACE открытого объекта.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                byte type = Marshal.ReadByte(address);
                int size = unchecked((ushort)Marshal.ReadInt16(address, 2));
                if (size < 4)
                    throw new InvalidDataException("Повреждённый заголовок ACE.");
                if (type == AccessAllowedAceType)
                {
                    // ACCESS_ALLOWED_ACE: header[4], mask[4], SID[variable].
                    // Validate SID boundaries *before* passing it to Win32.
                    if (size < 16)
                        throw new InvalidDataException("ACCESS_ALLOWED ACE слишком короткая.");
                    int subAuthorities = Marshal.ReadByte(address, 9);
                    if (subAuthorities > 15 || 16 + subAuthorities * 4 > size)
                        throw new InvalidDataException("SID выходит за границы ACE.");
                    uint access = unchecked((uint)Marshal.ReadInt32(address, 4));
                    string trustee = SidToString(IntPtr.Add(address, 8));
                    entries.Add(new RecoveryWindowsAclAce(type, access, trustee));
                }
                else
                {
                    // Never try to parse object/callback ACEs with the basic
                    // ACCESS_ALLOWED_ACE layout: unknown != safe.
                    entries.Add(new RecoveryWindowsAclAce(type, 0, null));
                }
            }
            return Summarize(entries);
        }
        finally
        {
            if (descriptor != 0) LocalFree(descriptor);
        }
    }

    private static string SidToString(nint sid)
    {
        nint text = 0;
        try
        {
            if (!ConvertSidToStringSidW(sid, out text))
                throw new IOException("Не удалось распознать SID внутри ACE.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            string? value = Marshal.PtrToStringUni(text);
            if (!LooksLikeCanonicalSid(value))
                throw new InvalidDataException("Некорректный SID внутри ACE.");
            return value!;
        }
        finally
        {
            if (text != 0) LocalFree(text);
        }
    }

    private static bool LooksLikeCanonicalSid(string? sid)
    {
        if (sid is null || sid.Length < 7 || sid.Length > 256 ||
            !sid.StartsWith("S-1-", StringComparison.Ordinal))
            return false;
        string[] fields = sid[4..].Split('-');
        return fields.Length >= 2 && fields.All(field =>
            field.Length > 0 && field.All(char.IsAsciiDigit));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSizeInfo
    {
        internal uint AceCount;
        internal uint AclBytesInUse;
        internal uint AclBytesFree;
    }

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle, int objectType, uint info,
        nint owner, nint group, out nint dacl, nint sacl, out nint descriptor);

    [DllImport("advapi32.dll", EntryPoint = "GetAclInformation",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAclInformation(nint acl, out AclSizeInfo info,
        uint bufferLength, int informationClass);

    [DllImport("advapi32.dll", EntryPoint = "GetAce",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAce(nint acl, uint index, out nint ace);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(nint sid, out nint text);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern nint LocalFree(nint pointer);
}

internal readonly record struct RecoveryWindowsAclAce(
    byte Type, uint AccessMask, string? TrusteeSid);

internal enum RecoveryWindowsAceRisk
{
    NoBroadWriteObserved,
    BroadGroupWriteAllow,
    UnreviewedAce,
    DenyNotEvaluated
}

/// <summary>
/// Absence of obvious broad allows is NOT evidence of safe effective access.
/// Even all-zero counts retain CanAuthorizeCapture=false (denial only).
/// </summary>
internal readonly record struct RecoveryWindowsAclRiskEvidence(
    int AceCount, int BroadWriteAllowCount, int UnreviewedAceCount,
    int DenyAceCount)
{
    internal bool CanAuthorizeCapture => false;
}
