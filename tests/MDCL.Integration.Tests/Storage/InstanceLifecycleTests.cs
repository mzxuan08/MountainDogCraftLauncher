using MDCL.Core.Instances;
using MDCL.Infrastructure.Storage;
using MDCL.Platform.Windows.Paths;
using Xunit;

namespace MDCL.Integration.Tests.Storage;

public sealed class InstanceLifecycleTests
{
    [Fact]
    public async Task OwnedInstance_CannotBeMovedWhileRunning()
    {
        using var temp=new TestDirectory();
        var paths=new WindowsAppPaths(temp.Path); paths.EnsureDirectories();
        var repository=new FileInstanceRepository(paths.InstancesRoot);
        var leases=new FileInstanceLeaseManager(paths.InstancesRoot);
        var id=InstanceId.New();
        await repository.SaveAsync(InstanceStorageTests.TestManifest(id,"running"),0,CancellationToken.None);
        await using var owned=await leases.AcquireAsync(id,"game-session",CancellationToken.None);
        var service=new InstanceLifecycleService(paths,repository,new SqliteInstanceIndex(paths.DatabasePath,repository),leases);
        var error=await Assert.ThrowsAsync<MDCL.Core.Errors.MdclException>(()=>service.MoveToTrashAsync(id,CancellationToken.None));
        Assert.Equal("INSTANCE_BUSY",error.Error.ErrorCode);
        Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.Empty(await service.ListTrashAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RenameTrashAndRestore_PreserveIdentityDataAndSharedFiles()
    {
        using var temp = new TestDirectory();
        var paths = new WindowsAppPaths(temp.Path); paths.EnsureDirectories();
        var repo = new FileInstanceRepository(paths.InstancesRoot);
        var leases = new FileInstanceLeaseManager(paths.InstancesRoot);
        var service = new InstanceLifecycleService(paths, repo, new SqliteInstanceIndex(paths.DatabasePath, repo), leases);
        var id = InstanceId.New();
        await repo.SaveAsync(InstanceStorageTests.TestManifest(id,"before"),0,CancellationToken.None);
        var game = Path.Combine(paths.InstancesRoot,id.ToString(),".minecraft"); Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game,"save.txt"),"preserved");
        await File.WriteAllTextAsync(Path.Combine(paths.SharedRoot,"shared.txt"),"shared");
        var updated = await service.UpdateSettingsAsync(id,"after",2048,"managed:test",CancellationToken.None);
        Assert.Equal(id,updated.InstanceId); Assert.Equal(2048,updated.MemoryMiB);
        await service.MoveToTrashAsync(id,CancellationToken.None);
        Assert.Empty(await repo.ListAsync(CancellationToken.None));
        Assert.Single(await service.ListTrashAsync(CancellationToken.None));
        await service.RestoreAsync(id,CancellationToken.None);
        Assert.Equal("after",(await repo.LoadAsync(id,CancellationToken.None)).DisplayName);
        Assert.Equal("preserved",await File.ReadAllTextAsync(Path.Combine(game,"save.txt")));
        Assert.Equal("shared",await File.ReadAllTextAsync(Path.Combine(paths.SharedRoot,"shared.txt")));
    }

    [Fact]
    public async Task Copy_CreatesIndependentIdentityAndUserFiles()
    {
        using var temp = new TestDirectory();
        var paths = new WindowsAppPaths(temp.Path); paths.EnsureDirectories();
        var repo = new FileInstanceRepository(paths.InstancesRoot);
        var service = new InstanceLifecycleService(paths,repo,new SqliteInstanceIndex(paths.DatabasePath,repo),new FileInstanceLeaseManager(paths.InstancesRoot));
        var id=InstanceId.New(); await repo.SaveAsync(InstanceStorageTests.TestManifest(id,"original"),0,CancellationToken.None);
        var source=Path.Combine(paths.InstancesRoot,id.ToString(),".minecraft"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source,"config.txt"),"original");
        var copy=await service.CopyAsync(id,"copy",CancellationToken.None);
        Assert.NotEqual(id,copy.InstanceId);
        var copyFile=Path.Combine(paths.InstancesRoot,copy.InstanceId.ToString(),".minecraft/config.txt");
        await File.WriteAllTextAsync(copyFile,"modified");
        Assert.Equal("original",await File.ReadAllTextAsync(Path.Combine(source,"config.txt")));
        Assert.Equal(2,(await repo.ListAsync(CancellationToken.None)).Count);
    }
}
