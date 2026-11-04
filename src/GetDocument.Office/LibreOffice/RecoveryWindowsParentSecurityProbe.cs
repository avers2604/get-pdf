using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bq: Windows READ-ONLY owner SID and DACL presence from an ALREADY
/// OPEN file/directory handle. Does not traverse a pathname or edit any ACL.
/// A present DACL is NOT a vetted DACL; Windows writes remain unauthorized
/// until an explicit handle-based ACE/owner/privilege policy and OS tests.
/// </summary>
internal static class RecoveryWindowsParentSecurityProbe
{
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int FileObject = 1; // SE_FILE_OBJECT

    internal static RecoveryWindowsParentEvidence ObserveExistingHandle(
        SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsClosed || handle.IsInvalid)
            throw new ObjectDisposedException(nameof(handle));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Windows owner SID/DACL можно читать только на Windows.");

        // Read the native ID from the same open object, not FileInfo.
        RecoveryNativeFileId fileId =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(handle);
        nint securityDescriptor = 0;
        nint owner = 0;
        nint dacl = 0;
        uint error = GetSecurityInfo(handle, FileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out owner, 0, out dacl, 0, out securityDescriptor);
        try
        {
            if (error != 0)
                throw new IOException(
                    "Windows не предоставила owner/DACL открытого дескриптора.",
                    new Win32Exception(unchecked((int)error)));
            if (owner == 0)
                throw new InvalidDataException("Owner SID отсутствует.");

            nint buffer = 0;
            try
            {
                if (!ConvertSidToStringSidW(owner, out buffer))
                    throw new IOException(
                        "Нельзя прочитать Owner SID открытого дескриптора.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                string? sid = Marshal.PtrToStringUni(buffer);
                if (string.IsNullOrEmpty(sid))
                    throw new InvalidDataException("Owner SID пуст.");
                return new RecoveryWindowsParentEvidence(sid, dacl != 0, fileId);
            }
            finally
            {
                if (buffer != 0) LocalFree(buffer);
            }
        }
        finally
        {
            if (securityDescriptor != 0) LocalFree(securityDescriptor);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo",
        ExactSpelling = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle, int objectType, uint securityInformation,
        out nint ownerSid, nint groupSid, out nint dacl,
        nint sacl, out nint securityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(
        nint sid, out nint sidText);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree",
        ExactSpelling = true)]
    private static extern nint LocalFree(nint allocation);
}

internal readonly record struct RecoveryWindowsParentEvidence(
    string OwnerSid, bool DaclPresent, RecoveryNativeFileId FileId);

/// <summary>
/// Pure DENIAL-ONLY gate. A matching owner SID and non-null DACL do NOT
/// prove that arbitrary principals lack DELETE_CHILD, WRITE_DAC or
/// FILE_ADD_FILE rights; effective access depends on ACEs and privileges.
/// Keep the production Windows native identity adapter disabled.
/// </summary>
internal static class RecoveryWindowsParentSecurityGate
{
    internal static RecoveryWindowsParentDecision Evaluate(
        RecoveryWindowsParentEvidence evidence, string expectedOwnerSid)
    {
        if (evidence.FileId.Platform != RecoveryNativePlatform.Windows ||
            !evidence.FileId.IsComplete)
            return RecoveryWindowsParentDecision.IncompleteFileIdentity;
        if (!LooksLikeCanonicalSid(evidence.OwnerSid) ||
            !LooksLikeCanonicalSid(expectedOwnerSid))
            return RecoveryWindowsParentDecision.MissingOrInvalidOwner;
        if (!string.Equals(evidence.OwnerSid, expectedOwnerSid,
                StringComparison.OrdinalIgnoreCase))
            return RecoveryWindowsParentDecision.OwnerMismatch;
        if (!evidence.DaclPresent)
            return RecoveryWindowsParentDecision.NullOrUnavailableDacl;

        // This deliberately never produces an authorization result:
        // explicit per-ACE audit + enabled/deny-only SIDs + privileges
        // + integrity labels require a separate carefully tested design.
        return RecoveryWindowsParentDecision.RequiresExplicitAceAudit;
    }

    internal static bool CanAuthorizeCapture(RecoveryWindowsParentDecision finding) =>
        false;

    private static bool LooksLikeCanonicalSid(string? sid) =>
        sid is { Length: >= 7 and <= 256 } &&
        sid.StartsWith("S-1-", StringComparison.Ordinal) &&
        sid[4..].All(c => char.IsAsciiDigit(c) || c == '-') &&
        sid[^1] != '-' && !sid.Contains("--", StringComparison.Ordinal);
}

internal enum RecoveryWindowsParentDecision
{
    IncompleteFileIdentity,
    MissingOrInvalidOwner,
    OwnerMismatch,
    NullOrUnavailableDacl,
    RequiresExplicitAceAudit
}
