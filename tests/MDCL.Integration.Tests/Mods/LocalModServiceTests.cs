using System.IO.Compression;
using System.Text;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Mods;
using MDCL.Infrastructure.Storage;
using MDCL.Platform.Windows.Paths;
using Xunit;

namespace MDCL.Integration.Tests.Mods;

public sealed class LocalModServiceTests
{
    [Fact]
    public async Task ListsMetadataAndTogglesWithoutChangingOtherMods()
    {
        using var temp = new TestDirectory();
        var paths = new WindowsAppPaths(temp.Path);
        var id = InstanceId.New();
        var instanceRoot = Path.Combine(paths.InstancesRoot, id.ToString());
        var mods = Path.Combine(instanceRoot, ".minecraft", "mods");
        Directory.CreateDirectory(mods);
        CreateJar(Path.Combine(mods, "fabric-mod.jar"),
            ("fabric.mod.json", """{ "id": "demo", "name": "Demo Mod", "version": "1.2.3" }"""));
        CreateJar(Path.Combine(mods, "forge-mod.jar.disabled"),
            ("META-INF/mods.toml", "modId=\"forge_demo\"\ndisplayName=\"Forge Demo\"\nversion=\"2.0\"\n"));
        var manager = new FileInstanceLeaseManager(paths.InstancesRoot);
        var service = new LocalModService(paths, manager);
        var manifest = new InstanceManifest
        {
            InstanceId = id, DisplayName = "mods", BaseVersion = "1.20.1",
            GameDirectory = ".minecraft", CreatedAt = DateTimeOffset.UtcNow
        };
        var list = await service.ListAsync(manifest, CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, x => x.Name == "Demo Mod" && x.Enabled);
        Assert.Contains(list, x => x.Name == "Forge Demo" && !x.Enabled);
        await service.SetEnabledAsync(manifest, "fabric-mod.jar", false, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(mods, "fabric-mod.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(mods, "fabric-mod.jar")));
        await service.SetEnabledAsync(manifest, "fabric-mod.jar.disabled", true, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(mods, "fabric-mod.jar")));
    }

    [Fact]
    public async Task RunningInstanceLease_PreventsModification()
    {
        using var temp = new TestDirectory();
        var paths = new WindowsAppPaths(temp.Path);
        var id = InstanceId.New();
        var mods = Path.Combine(paths.InstancesRoot, id.ToString(), ".minecraft", "mods");
        Directory.CreateDirectory(mods);
        CreateJar(Path.Combine(mods, "demo.jar"), ("fabric.mod.json", """{ "id": "demo" }"""));
        var manager = new FileInstanceLeaseManager(paths.InstancesRoot);
        var service = new LocalModService(paths, manager);
        var manifest = new InstanceManifest
        {
            InstanceId = id, DisplayName = "mods", BaseVersion = "1.20.1",
            GameDirectory = ".minecraft", CreatedAt = DateTimeOffset.UtcNow
        };
        await using var running = await manager.AcquireAsync(id, "game", CancellationToken.None);
        await Assert.ThrowsAsync<MDCL.Core.Errors.MdclException>(() =>
            service.SetEnabledAsync(manifest, "demo.jar", false, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(mods, "demo.jar")));
    }

    private static void CreateJar(string path, params (string Name, string Body)[] entries)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(body);
        }
    }
}
