namespace MDCL.Application.Ports;

public interface ICredentialVault
{
    Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken ct);
    Task ReplaceAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}
