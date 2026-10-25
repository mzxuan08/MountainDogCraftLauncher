using MDCL.Application.Launch;
using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Accounts;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Core.Instances;
using MDCL.Core.Java;
using MDCL.Core.Launch;
using MDCL.Core.Versions;
using MDCL.Infrastructure.Processes;
using MDCL.Infrastructure.Storage;

namespace MDCL.Infrastructure.Launcher;

public sealed record InstallOutcome(InstanceManifest Manifest, ResolvedVersion Resolved);
public sealed record LaunchOutcome(IGameSession Session, JavaRuntime Java);

public sealed class LauncherWorkflow
{
    public const string MicrosoftClientEnvironmentVariable = "MDCL_MICROSOFT_CLIENT_ID";
    private readonly IAppPaths _paths;
    private readonly IInstanceRepository _repository;
    private readonly IInstanceIndex _index;
    private readonly IInstanceLeaseManager _leases;
    private readonly IVersionResolver _resolver;
    private readonly IInstallPlanner _planner;
    private readonly IOperationCoordinator _coordinator;
    private readonly IJavaDiscovery _javaDiscovery;
    private readonly IJavaSelector _javaSelector;
    private readonly IJavaRuntimeInstaller _javaInstaller;
    private readonly IAccountService _accounts;
    private readonly IGameProcessService _processes;
    private readonly NativeLibraryExtractor _natives = new();
    private readonly ResolvedVersionStore _resolvedStore = new();

    public LauncherWorkflow(
        IAppPaths paths, IInstanceRepository repository, IInstanceIndex index, IInstanceLeaseManager leases,
        IVersionResolver resolver, IInstallPlanner planner, IOperationCoordinator coordinator,
        IJavaDiscovery javaDiscovery, IJavaSelector javaSelector, IJavaRuntimeInstaller javaInstaller,
        IAccountService accounts, IGameProcessService processes)
    {
        _paths = paths;
        _repository = repository;
        _index = index;
        _leases = leases;
        _resolver = resolver;
        _planner = planner;
        _coordinator = coordinator;
        _javaDiscovery = javaDiscovery;
        _javaSelector = javaSelector;
        _javaInstaller = javaInstaller;
        _accounts = accounts;
        _processes = processes;
        Target = new TargetPlatform(
            OperatingSystem.IsWindows() ? LauncherOperatingSystem.Windows : LauncherOperatingSystem.MacOS,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => CpuArchitecture.Arm64,
                _ => CpuArchitecture.X64
            },
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => CpuArchitecture.Arm64,
                _ => CpuArchitecture.X64
            });
    }

    public TargetPlatform Target { get; }
    public IAccountService Accounts => _accounts;
    public IAppPaths Paths => _paths;

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.ConfigRoot);
        Directory.CreateDirectory(_paths.CacheRoot);
        Directory.CreateDirectory(_paths.LogRoot);
        Directory.CreateDirectory(_paths.DataRoot);
        Directory.CreateDirectory(_paths.InstancesRoot);
        Directory.CreateDirectory(_paths.SharedRoot);
        Directory.CreateDirectory(_paths.RuntimesRoot);
        Directory.CreateDirectory(_paths.StagingRoot);
        Directory.CreateDirectory(_paths.TrashRoot);
        var instances = await _repository.ListAsync(ct);
        foreach (var instance in instances)
        {
            if (!Directory.Exists(Path.Combine(_paths.InstancesRoot, instance.InstanceId.ToString())))
                throw new MdclException(MdclError.Of("INSTANCE_INDEX_INCONSISTENT", "Startup", "实例索引与清单目录不一致。", false));
        }
        await _index.RebuildAsync(ct);
    }

    public async Task<IReadOnlyList<InstanceManifest>> ListInstancesAsync(CancellationToken ct)
    {
        var listed = await _repository.ListAsync(ct);
        return listed.OrderBy(x => x.DisplayName, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<InstanceManifest> CreateInstanceAsync(string displayName, string baseVersion, CancellationToken ct)
    {
        var name = (displayName ?? "").Trim();
        if (name.Length is < 1 or > 40)
            throw new MdclException(MdclError.Of("INSTANCE_NAME_INVALID", "Instances", "实例名称必须为 1—40 个字符。", false));
        if (string.IsNullOrWhiteSpace(baseVersion))
            throw new MdclException(MdclError.Of("VERSION_REQUIRED", "Instances", "请选择游戏版本。", false));
        var manifest = new InstanceManifest
        {
            InstanceId = InstanceId.New(),
            DisplayName = name,
            BaseVersion = baseVersion.Trim(),
            GameDirectory = ".minecraft",
            JavaPolicy = new JavaPolicy(null, true, Target.GameArchitecture),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _repository.SaveAsync(manifest, 0, ct);
        await _index.RebuildAsync(ct);
        return await _repository.LoadAsync(manifest.InstanceId, ct);
    }

    public async Task<InstallOutcome> InstallAsync(
        InstanceId instanceId, IProgress<OperationEvent>? progress, CancellationToken ct)
    {
        var manifest = await _repository.LoadAsync(instanceId, ct);
        await using var lease = await _leases.AcquireAsync(instanceId, "install", ct);
        var resolved = await _resolver.ResolveAsync(
            new VersionSelection(manifest.BaseVersion, manifest.Loader.Kind, manifest.Loader.Version), Target, ct);
        var plan = await _planner.CreateAsync(new InstallRequest(instanceId, manifest.BaseVersion, manifest.Loader, Target), ct);
        var result = await _coordinator.RunAsync(plan, _paths.SharedRoot, progress, ct);
        if (result.State != OperationState.Completed)
            throw new MdclException(result.Error ?? MdclError.Of("INSTALL_FAILED", "Installation", "安装未完成。", true));
        var instanceDirectory = Path.Combine(_paths.InstancesRoot, instanceId.ToString());
        await _resolvedStore.SaveAsync(instanceDirectory, resolved, ct);
        var revision = await _repository.SaveAsync(manifest with { Revision = manifest.Revision }, manifest.Revision, ct);
        await _index.RebuildAsync(ct);
        return new InstallOutcome(await _repository.LoadAsync(instanceId, ct), resolved);
    }

    public async Task<JavaRuntime> EnsureJavaAsync(InstanceManifest manifest, ResolvedVersion resolved, CancellationToken ct)
    {
        var results = await _javaDiscovery.DiscoverAsync(ct);
        var candidates = results.Where(x => x.Runtime is not null).Select(x => x.Runtime!).ToArray();
        var selection = await _javaSelector.SelectAsync(resolved.JavaRequirement, manifest.JavaPolicy, candidates, ct);
        if (selection.Selected is not null) return selection.Selected;
        var installed = await _javaInstaller.InstallAsync(resolved.JavaRequirement, Target, null, ct);
        return installed;
    }

    public async Task<LaunchOutcome> LaunchAsync(
        InstanceId instanceId, AccountId accountId, IProgress<OperationEvent>? progress, CancellationToken ct)
    {
        var manifest = await _repository.LoadAsync(instanceId, ct);
        var lease = await _leases.AcquireAsync(instanceId, "launch", ct);
        try
        {
            var instanceDirectory = Path.Combine(_paths.InstancesRoot, instanceId.ToString());
            var resolved = await _resolvedStore.LoadAsync(instanceDirectory, ct);
            var java = await EnsureJavaAsync(manifest, resolved, ct);
            var sessionDirectory = Path.Combine(_paths.DataRoot, "sessions", Guid.NewGuid().ToString("N"));
            var natives = Path.Combine(sessionDirectory, "natives");
            await _natives.ExtractAsync(resolved.NativeLibraries
                .Where(x => x.Native is not null)
                .Select(x => Path.Combine(_paths.SharedRoot, "libraries", x.Native!.RelativePath)).ToArray(), natives, ct);
            var plan = new LaunchPlanBuilder().Build(manifest, resolved, java, _paths.SharedRoot,
                instanceDirectory, natives, Target);
            var account = await _accounts.GetSessionAsync(accountId, false, ct);
            var session = await _processes.StartAsync(plan, account, ct);
            _ = ReleaseWhenExitAsync(session, lease);
            return new LaunchOutcome(session, java);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private async Task ReleaseWhenExitAsync(IGameSession session, IInstanceLease lease)
    {
        try
        {
            var exit = await session.ExitTask;
            var directory = Path.Combine(_paths.DataRoot, "sessions");
            Directory.CreateDirectory(directory);
            var record = new
            {
                sessionId = session.SessionId,
                instanceId = session.InstanceId.ToString(),
                pid = session.ProcessId,
                processStartedAt = session.ProcessStartedAt,
                exit.ExitCode,
                state = exit.State.ToString(),
                exit.ExitedAt,
                exit.StoppedByUser,
                exit.ErrorCode
            };
            await File.WriteAllTextAsync(Path.Combine(directory, session.SessionId.ToString("N") + ".json"),
                System.Text.Json.JsonSerializer.Serialize(record, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally { await lease.DisposeAsync(); }
    }
}
