using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core.Accounts;

namespace MDCL.Infrastructure.Authentication;

public sealed class JsonAccountStore : IAccountStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonAccountStore(string path) => _path = Path.GetFullPath(path);

    public async Task<IReadOnlyList<AccountRecord>> ListAsync(CancellationToken ct) => (await LoadAsync(ct)).Accounts;

    public async Task<AccountRecord?> GetAsync(AccountId id, CancellationToken ct) =>
        (await LoadAsync(ct)).Accounts.FirstOrDefault(x => x.Profile.Id == id);

    public async Task SaveAsync(AccountRecord account, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var document = await LoadUnlockedAsync(ct);
            var accounts = document.Accounts.Where(x => x.Profile.Id != account.Profile.Id).Append(account).ToArray();
            await WriteAsync(new AccountStoreDocument(accounts), ct);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(AccountId id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var document = await LoadUnlockedAsync(ct);
            await WriteAsync(new AccountStoreDocument(document.Accounts.Where(x => x.Profile.Id != id).ToArray()), ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<AccountStoreDocument> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await LoadUnlockedAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<AccountStoreDocument> LoadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new AccountStoreDocument([]);
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<AccountStoreDocument>(stream, Options, ct) ?? new AccountStoreDocument([]);
    }

    private async Task WriteAsync(AccountStoreDocument document, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, document, Options, ct);
            await stream.FlushAsync(ct);
            stream.Flush(true);
        }
        File.Move(temp, _path, true);
    }
}
