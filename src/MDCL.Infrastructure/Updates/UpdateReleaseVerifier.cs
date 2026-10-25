using System.Security.Cryptography;
using MDCL.Core.Updates;

namespace MDCL.Infrastructure.Updates;

public static class UpdateReleaseVerifier
{
    public static UpdateManifest VerifyEnvelope(
        string envelope, string publicKeyPem, string channel, string rid, string currentVersion, DateTimeOffset now)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        var verifier = new UpdateManifestVerifier(rsa, channel, rid, currentVersion);
        return verifier.Verify(envelope, now);
    }
}
