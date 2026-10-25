using System.IO.Compression;
using System.Text;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Installers;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

public sealed class MrpackTests
{
    [Fact]
    public async Task ClientUnsupportedFile_IsExcludedFromPlan()
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "server-only.mrpack");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        await using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
            await writer.WriteAsync($$"""{"formatVersion":1,"game":"minecraft","dependencies":{"minecraft":"1.20.1"},"files":[{"path":"mods/server.jar","env":{"client":"unsupported","server":"required"},"hashes":{"sha1":"{{new string('a',40)}}"},"fileSize":1,"downloads":["https://example.invalid/server.jar"]}]}""");
        var preview = await new MrpackReader().ReadAsync(pack, CancellationToken.None);
        Assert.Empty(preview.Files);
        Assert.Empty(new MrpackReader().CreatePlan(preview, InstanceId.New()).Steps.Single().Artifacts);
    }

    [Fact]
    public async Task UnsafeOverride_IsRejectedDuringPreview()
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "bad-overrides.mrpack");
        await CreatePack(pack, "mods/safe.jar", "../../escape");
        var error = await Assert.ThrowsAsync<MdclException>(() => new MrpackReader().ReadAsync(pack, CancellationToken.None));
        Assert.Equal("MRPACK_PATH_UNSAFE", error.Error.ErrorCode);
    }

    [Fact]
    public async Task PreviewAndPlan_CaptureComponentsHashesAndOverrides()
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "pack.mrpack");
        await CreatePack(pack, "mods/example.jar", "overrides/config/example.toml");
        var reader = new MrpackReader();
        var preview = await reader.ReadAsync(pack, CancellationToken.None);
        Assert.Equal("fabric", preview.LoaderKind);
        Assert.Equal("0.16.10", preview.LoaderVersion);
        Assert.Equal("1.20.1", preview.GameVersion);
        Assert.Single(preview.Files);
        Assert.Equal(1, preview.OverrideFileCount);
        var plan = reader.CreatePlan(preview, InstanceId.New());
        Assert.Equal("mods/example.jar", Assert.Single(plan.Steps).Artifacts[0].RelativeTargetPath);
        Assert.Equal("SHA512", plan.Steps[0].Artifacts[0].HashAlgorithm);
    }

    [Fact]
    public async Task VanillaPackWithoutLoader_IsAccepted()
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "vanilla.mrpack");
        using (var stream = File.Create(pack))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("modrinth.index.json");
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            await writer.WriteAsync("""{"formatVersion":1,"game":"minecraft","name":"Vanilla","versionId":"1.0.0","dependencies":{"minecraft":"1.20.1"},"files":[]}""");
        }
        var preview = await new MrpackReader().ReadAsync(pack, CancellationToken.None);
        Assert.Equal("vanilla", preview.LoaderKind);
        Assert.Equal("1.20.1", preview.GameVersion);
        Assert.Empty(preview.Files);
    }

    [Theory]
    [InlineData("forge", "47.2.0", "forge")]
    [InlineData("neo-forge", "47.1.106", "neoforge")]
    public async Task LoaderDependencies_MapToInstallerKinds(string dependency, string version, string expected)
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "loader.mrpack");
        using (var stream = File.Create(pack))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("modrinth.index.json");
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            await writer.WriteAsync($$"""{"formatVersion":1,"game":"minecraft","name":"Loader","versionId":"1.0.0","dependencies":{"minecraft":"1.20.1","{{dependency}}":"{{version}}"},"files":[]}""");
        }
        var preview = await new MrpackReader().ReadAsync(pack, CancellationToken.None);
        Assert.Equal(expected, preview.LoaderKind);
        Assert.Equal(version, preview.LoaderVersion);
    }

    [Theory]
    [InlineData("../evil.jar")]
    [InlineData("/absolute.jar")]
    [InlineData("mods\\..\\evil.jar")]
    public async Task UnsafePath_IsRejected(string path)
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "bad.mrpack");
        await CreatePack(pack, path, null);
        var error = await Assert.ThrowsAsync<MdclException>(() => new MrpackReader().ReadAsync(pack, CancellationToken.None));
        Assert.Equal("MRPACK_PATH_UNSAFE", error.Error.ErrorCode);
    }

    private static async Task CreatePack(string path, string filePath, string? overridePath)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var index = zip.CreateEntry("modrinth.index.json");
        await using (var writer = new StreamWriter(index.Open(), new UTF8Encoding(false)))
        {
            await writer.WriteAsync($$"""
                {
                  "formatVersion": 1, "game": "minecraft", "name": "Demo Pack", "versionId": "1.0.0",
                  "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.16.10" },
                  "files": [{
                    "path": {{System.Text.Json.JsonSerializer.Serialize(filePath)}},
                    "hashes": { "sha512": "{{new string('a', 128)}}", "sha1": "{{new string('b', 40)}}" },
                    "fileSize": 12,
                    "downloads": ["https://example.invalid/mod.jar"]
                  }]
                }
                """);
        }
        if (overridePath is not null)
        {
            var entry = zip.CreateEntry("overrides/" + overridePath);
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            await writer.WriteAsync("value=1");
        }
    }
}
