using System.Text.Json;
using MDCL.Core.Installation;

namespace MDCL.Infrastructure.Storage;

public sealed record InstallJournalStep(string StepId, string State, string? ArtifactKey, string? Path, string? Hash, string? ErrorCode);
public sealed record InstallJournalDocument(
    string OperationId,
    string PlanHash,
    long Revision,
    string State,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InstallJournalStep> Steps);

public sealed class InstallJournalStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public InstallJournalStore(string path) => _path = System.IO.Path.GetFullPath(path);
    public string Path => _path;

    public async Task<InstallJournalDocument?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<InstallJournalDocument>(stream, Options, ct);
    }

    public async Task SaveAsync(InstallJournalDocument document, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document with { UpdatedAt = DateTimeOffset.UtcNow }, Options, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static InstallJournalDocument Create(string operationId, string planHash, IEnumerable<string> stepIds) => new(
        operationId, planHash, 1, "Running", DateTimeOffset.UtcNow,
        stepIds.Select(id => new InstallJournalStep(id, "Pending", null, null, null, null)).ToArray());
}
