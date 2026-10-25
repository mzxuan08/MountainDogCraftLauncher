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
    private readonly string[] _prerelease;
    public SemanticVersion(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value ?? "",
            @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z");
        if (!match.Success || value!.Length > 128 ||
            !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch))
            throw new FormatException($"Invalid version: {value}");
        Major = major; Minor = minor; Patch = patch;
        _prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (_prerelease.Any(x => x.Length > 1 && x[0] == '0' && x.All(char.IsAsciiDigit)))
            throw new FormatException("Numeric prerelease identifiers cannot have leading zeroes.");
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
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
            return _prerelease.Length == other._prerelease.Length ? 0 : _prerelease.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i]; var right = other._prerelease[i];
            var leftNumeric = left.All(char.IsAsciiDigit); var rightNumeric = right.All(char.IsAsciiDigit);
            if (leftNumeric && rightNumeric)
            {
                result = left.Length.CompareTo(right.Length);
                if (result == 0) result = string.CompareOrdinal(left, right);
            }
            else result = leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : string.CompareOrdinal(left, right);
            if (result != 0) return result;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }
}
