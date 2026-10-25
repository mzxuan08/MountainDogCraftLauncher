using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Instances;

namespace MDCL.Infrastructure.Storage;

public sealed class FileInstanceLeaseManager : IInstanceLeaseManager
{
    private readonly string _instancesRoot;
    public FileInstanceLeaseManager(string instancesRoot) => _instancesRoot = Path.GetFullPath(instancesRoot);

    public Task<IInstanceLease> AcquireAsync(InstanceId id, string ownerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Stable lock identity must survive moving an instance into/out of trash.
        var directory = Path.Combine(_instancesRoot, ".leases");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id.ToString() + ".lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var owner = Encoding.UTF8.GetBytes($"{ownerId}\n{DateTimeOffset.UtcNow:O}\n");
            stream.Write(owner);
            stream.Flush(true);
            return Task.FromResult<IInstanceLease>(new Lease(id, ownerId, stream));
        }
        catch (IOException ex)
        {
            string owner = "unknown";
            try { owner = File.ReadAllText(path); } catch (IOException) { }
            throw new MdclException(MdclError.Of("INSTANCE_BUSY", "Storage", "实例正被另一个修改者或游戏会话占用。", true, owner), ex);
        }
    }

    private sealed class Lease(InstanceId id, string ownerId, FileStream stream) : IInstanceLease
    {
        public InstanceId InstanceId { get; } = id;
        public string OwnerId { get; } = ownerId;
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
