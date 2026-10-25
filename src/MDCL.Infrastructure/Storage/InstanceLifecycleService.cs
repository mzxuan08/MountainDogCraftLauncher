using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Instances;

namespace MDCL.Infrastructure.Storage;

public sealed class InstanceLifecycleService(IAppPaths paths, IInstanceRepository repository, IInstanceIndex index, IInstanceLeaseManager leases)
{
    public async Task<InstanceManifest> UpdateSettingsAsync(InstanceId id, string displayName, int memoryMiB, string? boundRuntimeId, CancellationToken ct)
    {
        var name = ValidateName(displayName);
        if (memoryMiB is < 512 or > 131072)
            throw Error("INSTANCE_MEMORY_INVALID", "内存必须在 512—131072 MiB 范围内。");
        await using var lease = await leases.AcquireAsync(id,"settings",ct);
        var instance = await repository.LoadAsync(id,ct);
        await repository.SaveAsync(instance with { DisplayName=name,MemoryMiB=memoryMiB,
            JavaPolicy=instance.JavaPolicy with { BoundRuntimeId=string.IsNullOrWhiteSpace(boundRuntimeId)?null:boundRuntimeId.Trim() } },instance.Revision,ct);
        await index.RebuildAsync(ct);
        return await repository.LoadAsync(id,ct);
    }

    public async Task MoveToTrashAsync(InstanceId id, CancellationToken ct)
    {
        await using var lease=await leases.AcquireAsync(id,"trash",ct);
        _=await repository.LoadAsync(id,ct);
        var source=InstancePath(paths.InstancesRoot,id);
        var destination=InstancePath(paths.TrashRoot,id);
        if (Directory.Exists(destination)) throw Error("INSTANCE_TRASH_CONFLICT","回收区已存在相同实例，未覆盖。");
        Directory.CreateDirectory(paths.TrashRoot);
        Directory.Move(source,destination);
        await index.RebuildAsync(ct);
    }

    public Task<IReadOnlyList<InstanceManifest>> ListTrashAsync(CancellationToken ct) => new FileInstanceRepository(paths.TrashRoot).ListAsync(ct);

    public async Task RestoreAsync(InstanceId id,CancellationToken ct)
    {
        await using var lease=await leases.AcquireAsync(id,"restore",ct);
        _=await new FileInstanceRepository(paths.TrashRoot).LoadAsync(id,ct);
        var destination=InstancePath(paths.InstancesRoot,id);
        if(Directory.Exists(destination)) throw Error("INSTANCE_RESTORE_CONFLICT","目标实例已存在，未覆盖。");
        Directory.Move(InstancePath(paths.TrashRoot,id),destination);
        await index.RebuildAsync(ct);
    }

    public async Task<InstanceManifest> CopyAsync(InstanceId id,string displayName,CancellationToken ct)
    {
        var name=ValidateName(displayName);
        await using var lease=await leases.AcquireAsync(id,"copy",ct);
        var original=await repository.LoadAsync(id,ct);
        if(File.Exists(Path.Combine(InstancePath(paths.InstancesRoot,id),MDCL.Infrastructure.Installers.MrpackImportService.PendingFileName)))
            throw Error("MRPACK_IMPORT_INCOMPLETE","请先恢复完成整合包导入，再复制实例。");
        if (Path.IsPathRooted(original.GameDirectory)) throw Error("INSTANCE_COPY_EXTERNAL_UNSUPPORTED","外部游戏目录需先迁回受管实例后复制。");
        var source=InstancePath(paths.InstancesRoot,id);
        var game=Path.GetFullPath(Path.Combine(source,original.GameDirectory));
        var relative=Path.GetRelativePath(source,game);
        if(relative==".." || relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw Error("INSTANCE_PATH_UNSAFE","游戏目录越过实例边界。");
        var copy=original with { InstanceId=InstanceId.New(),DisplayName=name,Revision=0,CreatedAt=DateTimeOffset.UtcNow };
        var staging=Path.Combine(paths.StagingRoot,"copy-"+Guid.NewGuid().ToString("N"));
        var temporary=InstancePath(staging,copy.InstanceId);
        try
        {
            await new FileInstanceRepository(staging).SaveAsync(copy,0,ct);
            if(Directory.Exists(game)) await CopyTreeAsync(game,Path.Combine(temporary,original.GameDirectory),ct);
            var resolved=Path.Combine(source,"resolved-version.json");
            // Incomplete imports must not become launchable by being copied.
            if(File.Exists(resolved) && !File.Exists(Path.Combine(source,MDCL.Infrastructure.Installers.MrpackImportService.PendingFileName)))
                File.Copy(resolved,Path.Combine(temporary,"resolved-version.json"));
            ct.ThrowIfCancellationRequested();
            Directory.Move(temporary,InstancePath(paths.InstancesRoot,copy.InstanceId));
            await index.RebuildAsync(ct);
            return await repository.LoadAsync(copy.InstanceId,ct);
        }
        finally { try { if(Directory.Exists(staging)) Directory.Delete(staging,true); } catch(IOException) { } }
    }

    private static async Task CopyTreeAsync(string source,string target,CancellationToken ct)
    {
        RejectLink(source);
        Directory.CreateDirectory(target);
        foreach(var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested(); RejectLink(entry);
            var destination=Path.Combine(target,Path.GetFileName(entry));
            if(Directory.Exists(entry)) await CopyTreeAsync(entry,destination,ct);
            else
            {
                await using var input=File.OpenRead(entry);
                await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous);
                await input.CopyToAsync(output,ct);
            }
        }
    }

    private static string InstancePath(string root,InstanceId id)
    {
        var path=Path.GetFullPath(Path.Combine(root,id.ToString()));
        for(string? current=path;current is not null;current=Path.GetDirectoryName(current))
            if(Directory.Exists(current)) RejectLink(current);
        return path;
    }
    private static void RejectLink(string path)
    { if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw Error("INSTANCE_PATH_UNSAFE","实例操作拒绝链接或重解析点。"); }
    private static string ValidateName(string name)
    {
        name=name.Trim();
        if(name.Length is <1 or >40) throw Error("INSTANCE_NAME_INVALID","实例名称必须为 1—40 个字符。");
        return name;
    }
    private static MdclException Error(string code,string message)=>new(MdclError.Of(code,"Instances",message,false));
}
