using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lionear.SqlExplorer.Core.Connections;

namespace Lionear.SqlExplorer.Infrastructure.Secrets;

/// <summary>
/// Windows backend over Credential Manager (advapi32 Cred* APIs) — the Windows equivalent of the
/// macOS Keychain / Linux Secret Service. NOT yet runtime-verified; test on Windows before shipping.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ISecretStore
{
    private const string TargetPrefix = "Lionear.SqlExplorer:";
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    public void Set(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetPrefix + key,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"CredWrite failed (Win32 {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public string? Get(string key)
    {
        if (!CredRead(TargetPrefix + key, CRED_TYPE_GENERIC, 0, out var handle))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Delete(string key) => CredDelete(TargetPrefix + key, CRED_TYPE_GENERIC, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
