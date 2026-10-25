using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Core.Errors;
using MDCL.Core.Updates;

namespace MDCL.Infrastructure.Updates;

public sealed class UpdateManifestVerifier
{
    private readonly RSA _publicKey;
    private readonly string _expectedChannel;
    private readonly string _expectedRid;
    private readonly SemanticVersion _currentVersion;

    public UpdateManifestVerifier(RSA publicKey, string expectedChannel, string expectedRid, string currentVersion)
    {
        _publicKey = publicKey;
        _expectedChannel = expectedChannel;
        _expectedRid = expectedRid;
        _currentVersion = new SemanticVersion(currentVersion);
    }

    public UpdateManifest Verify(string envelopeJson, DateTimeOffset now)
    {
        SignedUpdateDocument envelope;
        try { envelope = JsonSerializer.Deserialize<SignedUpdateDocument>(envelopeJson, UpdateSignature.Options)!; }
        catch (JsonException ex)
        {
            throw new MdclException(MdclError.Of("UPDATE_MANIFEST_INVALID", "Update", "更新清单不是有效 JSON。", false), ex);
        }
        if (envelope?.Manifest is null || string.IsNullOrWhiteSpace(envelope.Signature) ||
            !_publicKey.VerifyData(UpdateSignature.CanonicalBytes(envelope.Manifest), Convert.FromBase64String(envelope.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new MdclException(MdclError.Of("UPDATE_SIGNATURE_INVALID", "Update", "更新清单签名无效。", false));
        var manifest = envelope.Manifest;
        if (manifest.ExpiresAt <= now)
            throw new MdclException(MdclError.Of("UPDATE_EXPIRED", "Update", "更新清单已过期。", false));
        if (!string.Equals(manifest.Channel, _expectedChannel, StringComparison.Ordinal))
            throw new MdclException(MdclError.Of("UPDATE_CHANNEL_MISMATCH", "Update", "更新通道不匹配。", false));
        if (!string.Equals(manifest.Rid, _expectedRid, StringComparison.Ordinal))
            throw new MdclException(MdclError.Of("UPDATE_RID_MISMATCH", "Update", "更新包架构不匹配。", false));
        if (manifest.PackageUri.Scheme != Uri.UriSchemeHttps || manifest.Sha256.Length != 64 ||
            manifest.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new MdclException(MdclError.Of("UPDATE_PACKAGE_INVALID", "Update", "更新包地址或摘要无效。", false));
        SemanticVersion offered;
        try { offered = new SemanticVersion(manifest.Version); }
        catch (FormatException ex) { throw new MdclException(MdclError.Of("UPDATE_VERSION_INVALID", "Update", "更新版本号无效。", false), ex); }
        if (offered.CompareTo(_currentVersion) < 0)
            throw new MdclException(MdclError.Of("UPDATE_DOWNGRADE_REFUSED", "Update", "拒绝未授权的版本降级。", false));
        return manifest;
    }

    public async Task VerifyPackageAsync(string path, UpdateManifest manifest, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        if (!string.Equals(Convert.ToHexString(hash), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new MdclException(MdclError.Of("UPDATE_PACKAGE_HASH_MISMATCH", "Update", "更新包摘要不匹配。", false));
    }
}
