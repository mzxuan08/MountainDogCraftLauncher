using MDCL.Application.Ports;
using MDCL.Core.Instances;
using Microsoft.Data.Sqlite;

namespace MDCL.Infrastructure.Storage;

public sealed class SqliteInstanceIndex : IInstanceIndex
{
    private readonly string _databasePath;
    private readonly IInstanceRepository _repository;

    public SqliteInstanceIndex(string databasePath, IInstanceRepository repository)
    {
        _databasePath = databasePath;
        _repository = repository;
    }

    public async Task RebuildAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var manifests = await _repository.ListAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, "DELETE FROM instances;", ct);
        foreach (var manifest in manifests)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO instances(instance_id, display_name, base_version, revision) VALUES($id, $name, $version, $revision);";
            command.Parameters.AddWithValue("$id", manifest.InstanceId.ToString());
            command.Parameters.AddWithValue("$name", manifest.DisplayName);
            command.Parameters.AddWithValue("$version", manifest.BaseVersion);
            command.Parameters.AddWithValue("$revision", manifest.Revision);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<InstanceManifest>> ListAsync(CancellationToken ct)
    {
        if (!File.Exists(_databasePath)) return [];
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT instance_id FROM instances ORDER BY display_name COLLATE NOCASE, instance_id;";
        var ids = new List<InstanceId>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(new InstanceId(reader.GetGuid(0)));
        await reader.DisposeAsync();
        var result = new List<InstanceManifest>();
        foreach (var id in ids) result.Add(await _repository.LoadAsync(id, ct));
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared;Pooling=false");
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", ct);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS instances (instance_id TEXT PRIMARY KEY, display_name TEXT NOT NULL, base_version TEXT NOT NULL, revision INTEGER NOT NULL);", ct);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS operation_journal (operation_id TEXT PRIMARY KEY, plan_hash TEXT NOT NULL, state TEXT NOT NULL, payload_json TEXT NOT NULL, updated_at TEXT NOT NULL);", ct);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
