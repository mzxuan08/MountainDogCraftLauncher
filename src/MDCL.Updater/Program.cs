using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Core.Updates;
using MDCL.Infrastructure.Updates;

if (args.Length == 0) return Usage();
try
{
    var options = Parse(args.Skip(1).ToArray());
    return args[0] switch
    {
        "apply" => await ApplyAsync(options),
        "rollback" => await RollbackAsync(options),
        "verify" => await VerifyAsync(options),
        _ => Usage()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> ApplyAsync(Dictionary<string, string> options)
{
    var envelope = await File.ReadAllTextAsync(Required(options, "envelope"));
    var package = Path.GetFullPath(Required(options, "package"));
    var target = Path.GetFullPath(Required(options, "target"));
    using var rsa = RSA.Create();
    rsa.ImportFromPem(await File.ReadAllTextAsync(Required(options, "public-key")));
    var verifier = new UpdateManifestVerifier(rsa, Required(options, "channel"), Required(options, "rid"), Required(options, "current"));
    var installer = new UpdateInstaller(verifier);
    var result = await installer.ApplyAsync(envelope, package, target, DateTimeOffset.UtcNow, CancellationToken.None);
    var statePath = Path.Combine(Path.GetDirectoryName(target)!, "mdcl-update-state.json");
    await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new
    {
        target,
        backup = result.BackupDirectory,
        manifest = result.Manifest
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"applied {result.Manifest.Version}; backup={result.BackupDirectory}; state={statePath}");
    return 0;
}

static async Task<int> VerifyAsync(Dictionary<string, string> options)
{
    var envelope = await File.ReadAllTextAsync(Required(options, "envelope"));
    var publicKey = await File.ReadAllTextAsync(Required(options, "public-key"));
    var manifest = UpdateReleaseVerifier.VerifyEnvelope(envelope, publicKey,
        Required(options, "channel"), Required(options, "rid"), Required(options, "current"), DateTimeOffset.UtcNow);
    Console.WriteLine($"verified {manifest.Version} {manifest.Rid} {manifest.Channel} size={manifest.Size} sha256={manifest.Sha256}");
    return 0;
}

static async Task<int> RollbackAsync(Dictionary<string, string> options)
{
    var statePath = Path.GetFullPath(Required(options, "state"));
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
    var target = document.RootElement.GetProperty("target").GetString() ?? throw new InvalidOperationException("state target missing");
    var backup = document.RootElement.GetProperty("backup").GetString();
    using var rsa = RSA.Create();
    // Rollback does not need to verify a manifest anew; it only restores the recorded backup.
    var verifier = new UpdateManifestVerifier(rsa, "rollback", "rollback", "0.0.0");
    var installer = new UpdateInstaller(verifier);
    var manifest = new UpdateManifest("0.0.0", "rollback", "rollback", new Uri("https://example.invalid/"),
        0, new string('0', 64), DateTimeOffset.UtcNow, "0.0.0");
    await installer.RollbackAsync(new UpdateApplyResult(manifest, backup), target, CancellationToken.None);
    Console.WriteLine($"rolled back {target}");
    return 0;
}

static string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"missing --{name}");

static Dictionary<string, string> Parse(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= values.Length)
            throw new ArgumentException("arguments must be --name value pairs");
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

static int Usage()
{
    Console.Error.WriteLine("MDCL.Updater apply --envelope <json> --package <zip> --target <dir> --public-key <pem> --channel <c> --rid <rid> --current <version>");
    Console.Error.WriteLine("MDCL.Updater verify --envelope <json> --public-key <pem> --channel <c> --rid <rid> --current <version>");
    Console.Error.WriteLine("MDCL.Updater rollback --state <mdcl-update-state.json>");
    return 2;
}
