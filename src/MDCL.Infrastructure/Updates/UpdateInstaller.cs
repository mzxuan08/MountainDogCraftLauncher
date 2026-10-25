using MDCL.Core.Errors;
using MDCL.Core.Updates;
using MDCL.Infrastructure.Archives;
using System.Text.Json;

namespace MDCL.Infrastructure.Updates;

public sealed record UpdateApplyResult(UpdateManifest Manifest, string? BackupDirectory, string? StatePath = null);
public sealed record UpdateTransactionState(string Target, string Backup, string Staging, string State, UpdateManifest Manifest);

public sealed class UpdateInstaller
{
    private readonly UpdateManifestVerifier _verifier;
    private readonly SafeArchiveExtractor _extractor = new();
    private readonly Func<string, CancellationToken, Task<bool>>? _healthCheck;

    public UpdateInstaller(UpdateManifestVerifier verifier, Func<string, CancellationToken, Task<bool>>? healthCheck = null)
    { _verifier = verifier; _healthCheck = healthCheck; }

    public async Task<UpdateApplyResult> ApplyAsync(
        string envelopeJson, string packagePath, string targetDirectory, DateTimeOffset now, CancellationToken ct)
    {
        var manifest = _verifier.Verify(envelopeJson, now);
        await _verifier.VerifyPackageAsync(packagePath, manifest, ct);
        var target = Path.GetFullPath(targetDirectory);
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Target must have a parent directory.", nameof(targetDirectory));
        var staging = Path.Combine(parent, ".mdcl-update-staging-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ".mdcl-update-backup-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(parent, ".mdcl-update-state-" + Guid.NewGuid().ToString("N") + ".json");
        ValidateTarget(target);
        var transaction = new UpdateTransactionState(target, backup, staging, "Prepared", manifest);
        try
        {
            await _extractor.ExtractAsync(packagePath, staging, new ArchiveExtractionPolicy(RestoreUnixExecutable: true), ct);
            var mac = manifest.Rid is "osx-arm64" or "osx-x64";
            var payload = mac ? Path.Combine(staging, "MDCL.app") : staging;
            if ((mac && !target.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(Path.Combine(payload, mac ? "Contents/MacOS/MDCL.Desktop" : "MDCL.Desktop.exe")) ||
                (mac && !File.Exists(Path.Combine(payload, "Contents/Info.plist"))))
                throw new MdclException(MdclError.Of("UPDATE_PACKAGE_CONTENT_INVALID", "Update", "更新包缺少 MDCL 桌面入口。", false));
            await SaveStateAsync(statePath, transaction, ct);
            string? createdBackup = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(target))
                {
                    Directory.Move(target, backup);
                    createdBackup = backup;
                }
                await SaveStateAsync(statePath, transaction with { State = "BackupCreated" }, CancellationToken.None);
                Directory.Move(payload, target);
                await SaveStateAsync(statePath, transaction with { State = "NewPlaced" }, CancellationToken.None);
                if (_healthCheck is not null && !await _healthCheck(target, ct))
                    throw new MdclException(MdclError.Of("UPDATE_HEALTHCHECK_FAILED", "Update", "新版本健康检查失败，正在恢复旧版本。", true));
                ct.ThrowIfCancellationRequested();
                await SaveStateAsync(statePath, transaction with { State = "Completed" }, CancellationToken.None);
            }
            catch
            {
                if (createdBackup is not null)
                    await RollbackAsync(new UpdateApplyResult(manifest, createdBackup, statePath), target, CancellationToken.None);
                throw;
            }
            return new UpdateApplyResult(manifest, createdBackup, statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MdclException(MdclError.Of("UPDATE_APPLY_FAILED", "Update", "更新应用失败，旧版本保留。", true), ex);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
        }
    }

    public async Task RollbackAsync(UpdateApplyResult result, string targetDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (result.BackupDirectory is null || !Directory.Exists(result.BackupDirectory))
            throw new MdclException(MdclError.Of("UPDATE_ROLLBACK_UNAVAILABLE", "Update", "没有可用的旧版本备份。", false));
        var target = Path.GetFullPath(targetDirectory);
        ValidateTarget(target);
        ValidateSibling(target, result.BackupDirectory, ".mdcl-update-backup-");
        var failed = target + ".failed-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(target)) Directory.Move(target, failed);
            Directory.Move(result.BackupDirectory, target);
            try { if (Directory.Exists(failed)) Directory.Delete(failed, true); } catch (IOException) { }
            if (result.StatePath is { } statePath && File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<UpdateTransactionState>(await File.ReadAllTextAsync(statePath, ct))!;
                await SaveStateAsync(statePath, state with { State = "RolledBack" }, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(target) && Directory.Exists(failed)) Directory.Move(failed, target);
            throw new MdclException(MdclError.Of("UPDATE_ROLLBACK_FAILED", "Update", "回退失败，当前目录未删除。", false), ex);
        }
    }

    public async Task RecoverAsync(string statePath, CancellationToken ct)
    {
        var state = JsonSerializer.Deserialize<UpdateTransactionState>(await File.ReadAllTextAsync(statePath, ct))
            ?? throw new MdclException(MdclError.Of("UPDATE_STATE_INVALID", "Update", "更新恢复记录无效。", false));
        ValidateTarget(state.Target);
        ValidateSibling(state.Target, state.Backup, ".mdcl-update-backup-");
        ValidateSibling(state.Target, state.Staging, ".mdcl-update-staging-");
        if (state.State is "Completed" or "RolledBack") return;
        if (Directory.Exists(state.Backup))
            await RollbackAsync(new UpdateApplyResult(state.Manifest, state.Backup, statePath), state.Target, ct);
        else if (!Directory.Exists(state.Target))
            throw new MdclException(MdclError.Of("UPDATE_RECOVERY_UNAVAILABLE", "Update", "没有可恢复的应用或备份。", false));
        else await SaveStateAsync(statePath, state with { State = "RolledBack" }, ct);
    }

    private static void ValidateTarget(string target)
    {
        target = Path.GetFullPath(target);
        if (string.Equals(target.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(target)!.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(target, "accounts.json")) || File.Exists(Path.Combine(target, "mdcl.db")))
            throw new MdclException(MdclError.Of("UPDATE_TARGET_INVALID", "Update", "更新目标不能是卷根或用户数据根。", false));
        for (string? current = target; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new MdclException(MdclError.Of("UPDATE_TARGET_INVALID", "Update", "更新目标包含链接或重解析点。", false));
    }

    private static void ValidateSibling(string target, string sibling, string prefix)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var path = Path.GetFullPath(sibling);
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(Path.GetFullPath(target)), comparison) ||
            !Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
            throw new MdclException(MdclError.Of("UPDATE_STATE_INVALID", "Update", "更新备份不在受控邻接目录。", false));
        ValidateTarget(path);
    }

    private static async Task SaveStateAsync(string path, UpdateTransactionState state, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: ct);
            await stream.FlushAsync(ct); stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
