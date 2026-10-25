using System.IO.Compression;
using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Accounts;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Core.Instances;
using MDCL.Core.Versions;
using MDCL.Infrastructure.Installers;
using MDCL.Infrastructure.Launcher;
using MDCL.Infrastructure.Storage;
using MDCL.Platform.Windows.Paths;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

public sealed class MrpackRecoveryTests
{
    [Fact]
    public async Task FailedImport_IsNotLaunchableAndRetryReusesInstance()
    {
        using var temp = new TestDirectory();
        var pack = Path.Combine(temp.Path, "pack.mrpack");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        await using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
            await writer.WriteAsync("""{"formatVersion":1,"game":"minecraft","dependencies":{"minecraft":"1.20.1"},"files":[]}""");
        var paths = new WindowsAppPaths(temp.Path);
        paths.EnsureDirectories();
        var repo = new FileInstanceRepository(paths.InstancesRoot);
        var coordinator = new FailOnceCoordinator();
        var workflow = new LauncherWorkflow(paths, repo, new SqliteInstanceIndex(paths.DatabasePath, repo),
            new FileInstanceLeaseManager(paths.InstancesRoot), new Resolver(), null!, new Planner(), coordinator,
            null!, null!, null!, null!, null!, null!);
        var service = new MrpackImportService(workflow, new MrpackReader(), coordinator);
        await Assert.ThrowsAsync<MdclException>(() => service.ImportAsync(pack, "pack", null, CancellationToken.None));
        var before = Assert.Single(await workflow.ListInstanceStatusesAsync(CancellationToken.None));
        Assert.False(before.IsInstalled);
        var error = await Assert.ThrowsAsync<MdclException>(() => workflow.LaunchAsync(
            before.Manifest.InstanceId, AccountId.New(), null, CancellationToken.None));
        Assert.Equal("MRPACK_IMPORT_INCOMPLETE", error.Error.ErrorCode);
        var outcome = await service.ImportAsync(pack, "pack", null, CancellationToken.None);
        Assert.Equal(before.Manifest.InstanceId, outcome.Instance.InstanceId);
        Assert.True(Assert.Single(await workflow.ListInstanceStatusesAsync(CancellationToken.None)).IsInstalled);
    }

    private sealed class Resolver : IVersionResolver
    {
        public Task<ResolvedVersion> ResolveAsync(VersionSelection selection, TargetPlatform target, CancellationToken ct) =>
            Task.FromResult(new ResolvedVersion("1.20.1", "Main", [], [], [], [], null, null, null,
                new MDCL.Core.Java.JavaRequirement(17,17,null,CpuArchitecture.X64,[],[]), [], "fixture"));
    }
    private sealed class Planner : IInstallPlanner
    {
        public Task<InstallPlan> CreateAsync(InstallRequest request, CancellationToken ct) =>
            Task.FromResult(new InstallPlan("base", request.InstanceId, "base-hash", 1, [], "fixture"));
    }
    private sealed class FailOnceCoordinator : IOperationCoordinator
    {
        private bool _failed;
        public Task<OperationResult> RunAsync(InstallPlan plan, string target, IProgress<OperationEvent>? progress, CancellationToken ct)
        {
            if (plan.PlanId.StartsWith("mrpack-", StringComparison.Ordinal) && !_failed)
            {
                _failed = true;
                return Task.FromResult(new OperationResult(OperationState.Failed, MdclError.Of("INJECTED", "Test", "Injected pack download failure")));
            }
            return Task.FromResult(new OperationResult(OperationState.Completed));
        }
    }
}
