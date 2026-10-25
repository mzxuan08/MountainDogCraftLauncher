using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Instances;

namespace MDCL.Infrastructure.Storage;

public sealed class FileInstanceRepository : IInstanceRepository
{
    private readonly string _instancesRoot;

    public FileInstanceRepository(string instancesRoot) => _instancesRoot = Path.GetFullPath(instancesRoot);

    public async Task<InstanceManifest> LoadAsync(InstanceId id, CancellationToken ct)
    {
        var path = ManifestPath(id);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var manifest = InstanceManifestSerializer.Deserialize(await new StreamReader(stream).ReadToEndAsync(ct));
            if (manifest.InstanceId != id)
                throw new MdclException(MdclError.Of("INSTANCE_ID_MISMATCH", "Storage", "实例清单身份与目录不一致。", false));
            return manifest;
        }
        catch (FileNotFoundException ex)
        {
            throw new MdclException(MdclError.Of("INSTANCE_NOT_FOUND", "Storage", "找不到实例清单。", false, id.ToString()), ex);
        }
        catch (JsonException ex)
        {
            throw new MdclException(MdclError.Of("INSTANCE_MANIFEST_INVALID", "Storage", "实例清单不是有效 JSON。", false, id.ToString()), ex);
        }
    }

    public async Task<IReadOnlyList<InstanceManifest>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_instancesRoot)) return [];
        var result = new List<InstanceManifest>();
        foreach (var directory in Directory.EnumerateDirectories(_instancesRoot).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(Path.GetFileName(directory), out var guid)) continue;
            var path = Path.Combine(directory, "instance.json");
            if (File.Exists(path)) result.Add(await LoadAsync(new InstanceId(guid), ct));
        }
        return result;
    }

    public async Task<long> SaveAsync(InstanceManifest manifest, long expectedRevision, CancellationToken ct)
    {
        if (manifest.InstanceId.Value == Guid.Empty) throw new ArgumentException("Instance ID is required.", nameof(manifest));
        _ = InstanceManifestSerializer.Deserialize(InstanceManifestSerializer.Serialize(manifest));
        var directory = Path.Combine(_instancesRoot, manifest.InstanceId.ToString());
        var path = ManifestPath(manifest.InstanceId);
        Directory.CreateDirectory(directory);
        var currentRevision = 0L;
        if (File.Exists(path))
        {
            var current = await LoadAsync(manifest.InstanceId, ct);
            currentRevision = current.Revision;
        }
        if (currentRevision != expectedRevision)
        {
            throw new MdclException(MdclError.Of("INSTANCE_REVISION_CONFLICT", "Storage",
                $"实例 revision 已变化：期望 {expectedRevision}，实际 {currentRevision}。", true, manifest.InstanceId.ToString()));
        }

        var revision = currentRevision + 1;
        var updated = manifest with { Revision = revision };
        var temp = Path.Combine(directory, $".instance.json.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, updated, InstanceManifestSerializer.Options, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            if (File.Exists(path))
            {
                var backupDirectory = Path.Combine(directory, "backups");
                Directory.CreateDirectory(backupDirectory);
                File.Copy(path, Path.Combine(backupDirectory, $"instance.rev{currentRevision}.json"), false);
            }
            File.Move(temp, path, true);
            return revision;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private string ManifestPath(InstanceId id) => Path.Combine(_instancesRoot, id.ToString(), "instance.json");
}
