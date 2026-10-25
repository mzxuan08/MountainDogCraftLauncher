using System.IO.Compression;
using System.Text;
using MDCL.Core.Errors;
using MDCL.Infrastructure.Installers;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

public sealed class MrpackOverrideTests
{
    [Fact]
    public async Task OverridesAndClientOverrides_AreMergedIntoNewInstance()
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "pack.mrpack");
        using (var stream = File.Create(pack))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Add(zip, "modrinth.index.json", "{}");
            Add(zip, "overrides/config/base.toml", "a=1");
            Add(zip, "client-overrides/config/client.toml", "b=2");
        }
        var game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(game);
        await new MrpackOverrideExtractor().ExtractAsync(pack, game, Path.Combine(temp.Path, "staging"), CancellationToken.None);
        Assert.Equal("a=1", await File.ReadAllTextAsync(Path.Combine(game, "config", "base.toml")));
        Assert.Equal("b=2", await File.ReadAllTextAsync(Path.Combine(game, "config", "client.toml")));
    }

    [Fact]
    public async Task OverrideConflict_IsRejectedWithoutOverwritingUserFile()
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "pack.mrpack");
        using (var stream = File.Create(pack))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            Add(zip, "overrides/config/existing.toml", "new");
        var game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "config"));
        await File.WriteAllTextAsync(Path.Combine(game, "config", "existing.toml"), "old");
        var error = await Assert.ThrowsAsync<MdclException>(() =>
            new MrpackOverrideExtractor().ExtractAsync(pack, game, Path.Combine(temp.Path, "staging"), CancellationToken.None));
        Assert.Equal("MRPACK_OVERRIDE_CONFLICT", error.Error.ErrorCode);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(game, "config", "existing.toml")));
    }

    private static void Add(ZipArchive zip, string name, string body)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(body);
    }
}
