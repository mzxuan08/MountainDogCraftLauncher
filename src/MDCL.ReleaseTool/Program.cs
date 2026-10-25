using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Core.Updates;

if (args.Length != 7 || args[0] != "sign")
{
    Console.Error.WriteLine("MDCL.ReleaseTool sign --manifest <json> --key <private.pem> --out <envelope.json>");
    return 2;
}
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 1; index < args.Length; index += 2)
{
    if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length) return 2;
    options[args[index][2..]] = args[index + 1];
}
var manifestJson = await File.ReadAllTextAsync(options["manifest"]);
var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestJson, UpdateSignature.Options)
    ?? throw new InvalidOperationException("manifest is empty");
using var rsa = RSA.Create();
rsa.ImportFromPem(await File.ReadAllTextAsync(options["key"]));
var envelope = UpdateSignature.Sign(manifest, rsa);
await File.WriteAllTextAsync(options["out"], JsonSerializer.Serialize(envelope, UpdateSignature.Options));
Console.WriteLine($"signed {manifest.Version} {manifest.Rid} {manifest.Channel}");
return 0;
