using MDCL.Core.Instances;

namespace MDCL.Application.Ports;

public interface IAppPaths
{
    string ConfigRoot { get; }
    string CacheRoot { get; }
    string LogRoot { get; }
    string DataRoot { get; }
    string InstancesRoot { get; }
    string SharedRoot { get; }
    string RuntimesRoot { get; }
    string StagingRoot { get; }
    string TrashRoot { get; }
    string DatabasePath { get; }
}

public interface IInstanceRepository
{
    Task<InstanceManifest> LoadAsync(InstanceId id, CancellationToken ct);
    Task<IReadOnlyList<InstanceManifest>> ListAsync(CancellationToken ct);
    Task<long> SaveAsync(InstanceManifest manifest, long expectedRevision, CancellationToken ct);
}

public interface IInstanceLease : IAsyncDisposable
{
    InstanceId InstanceId { get; }
    string OwnerId { get; }
}

public interface IInstanceLeaseManager
{
    Task<IInstanceLease> AcquireAsync(InstanceId id, string ownerId, CancellationToken ct);
}

public interface IInstanceIndex
{
    Task RebuildAsync(CancellationToken ct);
    Task<IReadOnlyList<InstanceManifest>> ListAsync(CancellationToken ct);
}
