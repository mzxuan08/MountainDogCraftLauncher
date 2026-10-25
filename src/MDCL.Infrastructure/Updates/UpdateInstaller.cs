using MDCL.Core.Errors;
using MDCL.Core.Updates;
using MDCL.Infrastructure.Archives;

namespace MDCL.Infrastructure.Updates;

public sealed record UpdateApplyResult(UpdateManifest Manifest, string? BackupDirectory);

public sealed class UpdateInstaller
{
    private readonly UpdateManifestVerifier _verifier;
    private readonly SafeArchiveExtractor _extractor = new();

    public UpdateInstaller(UpdateManifestVerifier verifier) => _verifier = verifier;

    public async Task<UpdateApplyResult> ApplyAsync(
        string envelopeJson, string packagePath, string targetDirectory, DateTimeOffset now, CancellationToken ct)
    {
        var manifest = _verifier.Verify(envelopeJson, now);
        await _verifier.VerifyPackageAsync(packagePath, manifest, ct);
        var target = Path.GetFullPath(targetDirectory);
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Target must have a parent directory.", nameof(targetDirectory));
        var staging = Path.Combine(parent, ".mdcl-update-staging-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ".mdcl-update-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await _extractor.ExtractAsync(packagePath, staging, null, ct);
            if (!Directory.Exists(staging) || !Directory.EnumerateFiles(staging, "MDCL.Desktop*", SearchOption.TopDirectoryOnly).Any())
                throw new MdclException(MdclError.Of("UPDATE_PACKAGE_CONTENT_INVALID", "Update", "更新包缺少 MDCL 桌面入口。", false));
            string? createdBackup = null;
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                createdBackup = backup;
            }
            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (createdBackup is not null && !Directory.Exists(target)) Directory.Move(createdBackup, target);
                throw;
            }
            return new UpdateApplyResult(manifest, createdBackup);
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

    public Task RollbackAsync(UpdateApplyResult result, string targetDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (result.BackupDirectory is null || !Directory.Exists(result.BackupDirectory))
            throw new MdclException(MdclError.Of("UPDATE_ROLLBACK_UNAVAILABLE", "Update", "没有可用的旧版本备份。", false));
        var target = Path.GetFullPath(targetDirectory);
        var failed = target + ".failed-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(target)) Directory.Move(target, failed);
            Directory.Move(result.BackupDirectory, target);
            try { if (Directory.Exists(failed)) Directory.Delete(failed, true); } catch (IOException) { }
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(target) && Directory.Exists(failed)) Directory.Move(failed, target);
            throw new MdclException(MdclError.Of("UPDATE_ROLLBACK_FAILED", "Update", "回退失败，当前目录未删除。", false), ex);
        }
    }
}
