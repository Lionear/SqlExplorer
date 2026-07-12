using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lionear.SqlExplorer.Core.Connections;

namespace Lionear.SqlExplorer.Infrastructure.Secrets;

/// <summary>
/// macOS backend over the login Keychain (Security.framework generic-password APIs).
/// NOT yet runtime-verified; test on macOS before shipping.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainStore : ISecretStore
{
    private const string Service = "com.lionear.sqlexplorer";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public void Set(string key, string secret)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);
        var password = Encoding.UTF8.GetBytes(secret);

        var status = SecKeychainFindGenericPassword(IntPtr.Zero,
            (uint)service.Length, service, (uint)account.Length, account,
            out _, out _, out var itemRef);

        if (status == 0 && itemRef != IntPtr.Zero)
        {
            var modify = SecKeychainItemModifyAttributesAndData(itemRef, IntPtr.Zero,
                (uint)password.Length, password);
            CFRelease(itemRef);
            if (modify != 0)
            {
                throw new InvalidOperationException($"Keychain modify failed (OSStatus {modify}).");
            }

            return;
        }

        var add = SecKeychainAddGenericPassword(IntPtr.Zero,
            (uint)service.Length, service, (uint)account.Length, account,
            (uint)password.Length, password, out _);
        if (add != 0)
        {
            throw new InvalidOperationException($"Keychain add failed (OSStatus {add}).");
        }
    }

    public string? Get(string key)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);

        var status = SecKeychainFindGenericPassword(IntPtr.Zero,
            (uint)service.Length, service, (uint)account.Length, account,
            out var length, out var dataPtr, out var itemRef);
        if (status != 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[length];
            Marshal.Copy(dataPtr, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, dataPtr);
            if (itemRef != IntPtr.Zero)
            {
                CFRelease(itemRef);
            }
        }
    }

    public void Delete(string key)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);

        var status = SecKeychainFindGenericPassword(IntPtr.Zero,
            (uint)service.Length, service, (uint)account.Length, account,
            out _, out _, out var itemRef);
        if (status == 0 && itemRef != IntPtr.Zero)
        {
            SecKeychainItemDelete(itemRef);
            CFRelease(itemRef);
        }
    }

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList,
        uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
