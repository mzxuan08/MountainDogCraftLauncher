using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Errors;

namespace MDCL.Platform.MacOS.Credentials;

[SupportedOSPlatform("macos")]
public sealed class KeychainCredentialVault : ICredentialVault
{
    private const string Service = "MDCL";
    private const int ItemNotFound = -25300;
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";

    public Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out var length, out var data, out var item);
        if (status == ItemNotFound) return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        try
        {
            if (status != 0) throw Error(status);
            var result = new byte[length];
            Marshal.Copy(data, result, 0, (int)length);
            return Task.FromResult<ReadOnlyMemory<byte>?>(result);
        }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public Task ReplaceAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);
        var password = value.ToArray();
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out _, out var oldData, out var item);
        if (oldData != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, oldData);
        try
        {
            if (status == 0 && item != IntPtr.Zero)
            {
                var modify = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password);
                if (modify != 0) throw Error(modify);
            }
            else if (status == ItemNotFound)
            {
                var add = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                    (uint)password.Length, password, out var newItem);
                if (newItem != IntPtr.Zero) CFRelease(newItem);
                if (add != 0) throw Error(add);
            }
            else throw Error(status);
        }
        finally { if (item != IntPtr.Zero) CFRelease(item); }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out _, out var data, out var item);
        if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
        try
        {
            if (status == ItemNotFound) return Task.CompletedTask;
            if (status != 0) throw Error(status);
            var deleted = SecKeychainItemDelete(item);
            if (deleted != 0) throw Error(deleted);
        }
        finally { if (item != IntPtr.Zero) CFRelease(item); }
        return Task.CompletedTask;
    }

    private static MdclException Error(int status) => new(MdclError.Of("CREDENTIAL_KEYCHAIN_ERROR", "Credentials",
        $"macOS Keychain 操作失败，状态 {status}。", true));

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}
