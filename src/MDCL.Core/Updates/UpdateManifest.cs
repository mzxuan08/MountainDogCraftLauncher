using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDCL.Core.Updates;

public sealed record UpdateManifest(
    string Version,
    string Channel,
    string Rid,
    Uri PackageUri,
    long Size,
    string Sha256,
    DateTimeOffset ExpiresAt,
    string MinimumLauncherVersion);

public sealed record SignedUpdateDocument(UpdateManifest Manifest, string Signature);

public static class UpdateSignature
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static byte[] CanonicalBytes(UpdateManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, Options);

    public static SignedUpdateDocument Sign(UpdateManifest manifest, RSA privateKey)
    {
        var signature = privateKey.SignData(CanonicalBytes(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new SignedUpdateDocument(manifest, Convert.ToBase64String(signature));
    }
}

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    public SemanticVersion(string value)
    {
        var core = value.Split('-', 2)[0].Split('+', 2)[0];
        var parts = core.Split('.');
        if (parts.Length < 1 || parts.Length > 4 || parts.Any(x => !int.TryParse(x, out _)))
            throw new FormatException($"Invalid version: {value}");
        Major = int.Parse(parts[0]);
        Minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        Patch = parts.Length > 2 ? int.Parse(parts[2]) : 0;
        Original = value;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string Original { get; }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }
}
