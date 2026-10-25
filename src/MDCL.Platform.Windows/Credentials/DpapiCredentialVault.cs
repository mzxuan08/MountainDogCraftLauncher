using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Errors;

namespace MDCL.Platform.Windows.Credentials;

[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialVault : ICredentialVault
{
    private readonly string _directory;
    public DpapiCredentialVault(string directory) => _directory = Path.GetFullPath(directory);

    public async Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, ct);
            return ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new MdclException(MdclError.Of("CREDENTIAL_UNREADABLE", "Credentials", "Windows 凭据解密失败。", true), ex);
        }
    }

    public async Task ReplaceAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var encrypted = ProtectedData.Protect(value.ToArray(), null, DataProtectionScope.CurrentUser);
        var path = PathFor(key);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ex) { throw new MdclException(MdclError.Of("CREDENTIAL_DELETE_FAILED", "Credentials", "凭据删除失败。", true), ex); }
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_directory, hash + ".bin");
    }
}
