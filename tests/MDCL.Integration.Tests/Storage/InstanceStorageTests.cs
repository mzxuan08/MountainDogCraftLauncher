using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Storage;
using Xunit;

namespace MDCL.Integration.Tests.Storage;

public sealed class InstanceStorageTests
{
    [Fact]
    public async Task NewUnsupportedSchema_IsRejectedBeforeCreatingManifest()
    {
        using var temp = new TestDirectory();
        var root = Path.Combine(temp.Path, "instances");
        var id = InstanceId.New();
        var repository = new FileInstanceRepository(root);
        var error = await Assert.ThrowsAsync<MdclException>(() => repository.SaveAsync(
            TestManifest(id, "future") with { SchemaVersion = 42 }, 0, CancellationToken.None));
        Assert.Equal("INSTANCE_SCHEMA_UNSUPPORTED", error.Error.ErrorCode);
        Assert.False(File.Exists(Path.Combine(root, id.ToString(), "instance.json")));
    }

    [Fact]
    public async Task ManifestIdentityMismatch_IsRejected()
    {
        using var temp = new TestDirectory();
        var root = Path.Combine(temp.Path, "instances");
        var id = InstanceId.New();
        Directory.CreateDirectory(Path.Combine(root, id.ToString()));
        await File.WriteAllTextAsync(Path.Combine(root, id.ToString(), "instance.json"),
            InstanceManifestSerializer.Serialize(TestManifest(InstanceId.New(), "wrong identity")));
        var error = await Assert.ThrowsAsync<MdclException>(() => new FileInstanceRepository(root).LoadAsync(id, CancellationToken.None));
        Assert.Equal("INSTANCE_ID_MISMATCH", error.Error.ErrorCode);
    }

    [Fact]
    public async Task AtomicSave_ChinesePathAndRevisionConflict_AreExplicit()
    {
        using var temp = new TestDirectory();
        var repository = new FileInstanceRepository(Path.Combine(temp.Path, "中文 空间 & data", "instances"));
        var id = InstanceId.New();
        var manifest = TestManifest(id, "实例一");
        Assert.Equal(1, await repository.SaveAsync(manifest, 0, CancellationToken.None));
        var loaded = await repository.LoadAsync(id, CancellationToken.None);
        Assert.Equal(".minecraft", loaded.GameDirectory);
        var error = await Assert.ThrowsAsync<MdclException>(() => repository.SaveAsync(loaded with { DisplayName = "冲突" }, 0, CancellationToken.None));
        Assert.Equal("INSTANCE_REVISION_CONFLICT", error.Error.ErrorCode);
        Assert.Equal("实例一", (await repository.LoadAsync(id, CancellationToken.None)).DisplayName);
    }

    [Fact]
    public async Task RevisionUpgrade_KeepsPreviousManifestBackup()
    {
        using var temp = new TestDirectory();
        var root = Path.Combine(temp.Path, "instances");
        var repository = new FileInstanceRepository(root);
        var id = InstanceId.New();
        await repository.SaveAsync(TestManifest(id, "before"), 0, CancellationToken.None);
        await repository.SaveAsync(TestManifest(id, "after"), 1, CancellationToken.None);
        var backup = Path.Combine(root, id.ToString(), "backups", "instance.rev1.json");
        Assert.Contains("\"displayName\": \"before\"", await File.ReadAllTextAsync(backup), StringComparison.Ordinal);
        Assert.Equal("after", (await repository.LoadAsync(id, CancellationToken.None)).DisplayName);
    }

    [Fact]
    public async Task UnknownNewSchema_IsNeverOverwritten()
    {
        using var temp = new TestDirectory();
        var repository = new FileInstanceRepository(Path.Combine(temp.Path, "instances"));
        var id = InstanceId.New();
        await repository.SaveAsync(TestManifest(id, "future"), 0, CancellationToken.None);
        var path = Path.Combine(temp.Path, "instances", id.ToString(), "instance.json");
        var future = (await File.ReadAllTextAsync(path)).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 42", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, future);
        await Assert.ThrowsAsync<MdclException>(() => repository.SaveAsync(TestManifest(id, "bad overwrite"), 1, CancellationToken.None));
        Assert.Equal(future, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InterruptedTemporaryWrite_DoesNotReplaceValidManifest()
    {
        using var temp = new TestDirectory();
        var root = Path.Combine(temp.Path, "instances");
        var repository = new FileInstanceRepository(root);
        var id = InstanceId.New();
        await repository.SaveAsync(TestManifest(id, "safe"), 0, CancellationToken.None);
        var directory = Path.Combine(root, id.ToString());
        await File.WriteAllTextAsync(Path.Combine(directory, ".instance.json.interrupted.tmp"), "{truncated");
        Assert.Equal("safe", (await repository.LoadAsync(id, CancellationToken.None)).DisplayName);
        Assert.Single(await repository.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Index_CanBeDeletedAndRebuiltFromManifests()
    {
        using var temp = new TestDirectory();
        var root = Path.Combine(temp.Path, "instances");
        var repository = new FileInstanceRepository(root);
        await repository.SaveAsync(TestManifest(InstanceId.New(), "B"), 0, CancellationToken.None);
        await repository.SaveAsync(TestManifest(InstanceId.New(), "A"), 0, CancellationToken.None);
        var database = Path.Combine(temp.Path, "index", "mdcl.db");
        var index = new SqliteInstanceIndex(database, repository);
        await index.RebuildAsync(CancellationToken.None);
        Assert.Equal(2, (await index.ListAsync(CancellationToken.None)).Count);
        File.Delete(database);
        Assert.Empty(await index.ListAsync(CancellationToken.None));
        await index.RebuildAsync(CancellationToken.None);
        Assert.Equal(new[] { "A", "B" }, (await index.ListAsync(CancellationToken.None)).Select(x => x.DisplayName).ToArray());
    }

    [Fact]
    public async Task Lease_RejectsConcurrentWriterAndCanBeReacquired()
    {
        using var temp = new TestDirectory();
        var manager = new FileInstanceLeaseManager(Path.Combine(temp.Path, "instances"));
        var id = InstanceId.New();
        var first = await manager.AcquireAsync(id, "first", CancellationToken.None);
        var error = await Assert.ThrowsAsync<MdclException>(() => manager.AcquireAsync(id, "second", CancellationToken.None));
        Assert.Equal("INSTANCE_BUSY", error.Error.ErrorCode);
        await first.DisposeAsync();
        await using var second = await manager.AcquireAsync(id, "second", CancellationToken.None);
        Assert.Equal("second", second.OwnerId);
    }

    [Fact]
    public async Task SameVersionInstances_RemainIndependent()
    {
        using var temp = new TestDirectory();
        var repository = new FileInstanceRepository(Path.Combine(temp.Path, "instances"));
        var first = InstanceId.New();
        var second = InstanceId.New();
        await repository.SaveAsync(TestManifest(first, "same"), 0, CancellationToken.None);
        await repository.SaveAsync(TestManifest(second, "same"), 0, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "instances", first.ToString(), "config.txt"), "first");
        Assert.False(File.Exists(Path.Combine(temp.Path, "instances", second.ToString(), "config.txt")));
        Assert.Equal(2, (await repository.ListAsync(CancellationToken.None)).Count);
    }

    internal static InstanceManifest TestManifest(InstanceId id, string name) => new()
    {
        InstanceId = id,
        DisplayName = name,
        BaseVersion = "1.20.1",
        GameDirectory = ".minecraft",
        CreatedAt = DateTimeOffset.UnixEpoch
    };
}
