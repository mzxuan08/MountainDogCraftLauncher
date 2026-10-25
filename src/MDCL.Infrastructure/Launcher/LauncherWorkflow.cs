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
using MDCL.Infrastructure.Installers;
using MDCL.Infrastructure.Processes;
using MDCL.Infrastructure.Storage;

namespace MDCL.Infrastructure.Launcher;

public sealed record InstallOutcome(InstanceManifest Manifest, ResolvedVersion Resolved);
public sealed record LaunchOutcome(IGameSession Session, JavaRuntime Java);
public sealed record LauncherInstance(InstanceManifest Manifest, bool IsInstalled);

public sealed class LauncherWorkflow
{
    public const string MicrosoftClientEnvironmentVariable = "MDCL_MICROSOFT_CLIENT_ID";
    private readonly IAppPaths _paths;
    private readonly IInstanceRepository _repository;
    private readonly IInstanceIndex _index;
    private readonly IInstanceLeaseManager _leases;
    private readonly IVersionResolver _resolver;
    private readonly IVersionMetadataSource _metadata;
    private readonly IInstallPlanner _planner;
    private readonly IOperationCoordinator _coordinator;
    private readonly IJavaDiscovery _javaDiscovery;
    private readonly IJavaSelector _javaSelector;
    private readonly IJavaRuntimeInstaller _javaInstaller;
    private readonly IAccountService _accounts;
    private readonly IGameProcessService _processes;
    private readonly ForgeInstallerService _forgeInstaller;
    private readonly ForgeStagingMerger _forgeMerger = new();
    private readonly NativeLibraryExtractor _natives = new();
    private readonly ResolvedVersionStore _resolvedStore = new();

    public LauncherWorkflow(
        IAppPaths paths, IInstanceRepository repository, IInstanceIndex index, IInstanceLeaseManager leases,
        IVersionResolver resolver, IVersionMetadataSource metadata, IInstallPlanner planner, IOperationCoordinator coordinator,
        IJavaDiscovery javaDiscovery, IJavaSelector javaSelector, IJavaRuntimeInstaller javaInstaller,
        IAccountService accounts, IGameProcessService processes, ForgeInstallerService forgeInstaller)
    {
        _forgeInstaller = forgeInstaller;
        _paths = paths;
        _repository = repository;
        _index = index;
        _leases = leases;
        _resolver = resolver;
        _metadata = metadata;
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

    public async Task<IReadOnlyList<LauncherInstance>> ListInstanceStatusesAsync(CancellationToken ct) =>
        (await ListInstancesAsync(ct)).Select(manifest => new LauncherInstance(manifest,
            File.Exists(Path.Combine(_paths.InstancesRoot, manifest.InstanceId.ToString(), "resolved-version.json")) &&
            !File.Exists(Path.Combine(_paths.InstancesRoot, manifest.InstanceId.ToString(), MrpackImportService.PendingFileName)))).ToArray();

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

    public async Task<InstallOutcome> InstallFabricAsync(
        InstanceId instanceId, string? loaderVersion, IProgress<OperationEvent>? progress, CancellationToken ct)
    {
        var manifest = await _repository.LoadAsync(instanceId, ct);
        await using var lease = await _leases.AcquireAsync(instanceId, "install-fabric", ct);
        var selection = new VersionSelection(manifest.BaseVersion, LoaderKind.Fabric, string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim());
        var resolved = await _resolver.ResolveAsync(selection, Target, ct);
        var plan = await _planner.CreateAsync(new InstallRequest(instanceId, manifest.BaseVersion, new LoaderDescriptor(LoaderKind.Fabric, resolved.LoaderVersion), Target), ct);
        var result = await _coordinator.RunAsync(plan, _paths.SharedRoot, progress, ct);
        if (result.State != OperationState.Completed)
            throw new MdclException(result.Error ?? MdclError.Of("FABRIC_INSTALL_FAILED", "Fabric", "Fabric 安装未完成。", true));
        var instanceDirectory = Path.Combine(_paths.InstancesRoot, instanceId.ToString());
        await _resolvedStore.SaveAsync(instanceDirectory, resolved, ct);
        await _repository.SaveAsync(manifest with { Loader = new LoaderDescriptor(LoaderKind.Fabric, resolved.LoaderVersion) }, manifest.Revision, ct);
        await _index.RebuildAsync(ct);
        return new InstallOutcome(await _repository.LoadAsync(instanceId, ct), resolved);
    }

    public async Task<InstallOutcome> InstallForgeAsync(
        InstanceId instanceId, LoaderKind kind, string loaderVersion, IProgress<OperationEvent>? progress, CancellationToken ct)
    {
        if (kind is not (LoaderKind.Forge or LoaderKind.NeoForge))
            throw new MdclException(MdclError.Of("LOADER_KIND_INVALID", "Forge", "不是 Forge/NeoForge 安装请求。", false));
        var manifest = await _repository.LoadAsync(instanceId, ct);
        await using var lease = await _leases.AcquireAsync(instanceId, "install-forge", ct);
        var vanilla = await _resolver.ResolveAsync(new VersionSelection(manifest.BaseVersion), Target, ct);
        var java = await EnsureJavaAsync(manifest, vanilla, ct);
        var versionManifest = await _metadata.GetManifestAsync(ct);
        var entry = versionManifest.Versions.FirstOrDefault(x => string.Equals(x.Id, manifest.BaseVersion, StringComparison.Ordinal))
            ?? throw new MdclException(MdclError.Of("VERSION_NOT_FOUND", "Forge", $"Mojang 版本列表没有 {manifest.BaseVersion}。", true));
        var baseJson = (await _metadata.GetVersionAsync(entry, ct)).RawJson;
        if (!string.IsNullOrWhiteSpace(baseJson))
        {
            var baseVersionDirectory = Path.Combine(_paths.SharedRoot, "versions", SafeVersionDirectory(manifest.BaseVersion));
            Directory.CreateDirectory(baseVersionDirectory);
            File.WriteAllText(Path.Combine(baseVersionDirectory, manifest.BaseVersion + ".json"), baseJson);
        }
        progress?.Report(new OperationEvent("forge-install", "installer", 1, OperationState.Running, null, null, "installer-started", DateTimeOffset.UtcNow));
        var installed = await _forgeInstaller.InstallAsync(
            new ForgeInstallRequest(kind, manifest.BaseVersion, loaderVersion.Trim(), baseJson), java.Executable, _paths.StagingRoot, null, ct);
        await _forgeMerger.MergeAsync(installed.StagingDirectory, _paths.SharedRoot, ct);
        var resolved = await _resolver.ResolveAsync(
            new VersionSelection(manifest.BaseVersion, kind, loaderVersion.Trim()), Target, ct);
        var instanceDirectory = Path.Combine(_paths.InstancesRoot, instanceId.ToString());
        await _resolvedStore.SaveAsync(instanceDirectory, resolved, ct);
        await _repository.SaveAsync(manifest with { Loader = new LoaderDescriptor(kind, loaderVersion.Trim()) }, manifest.Revision, ct);
        await _index.RebuildAsync(ct);
        try { Directory.Delete(installed.StagingDirectory, true); } catch (IOException) { }
        progress?.Report(new OperationEvent("forge-install", "installer", 2, OperationState.Completed, null, null, "installer-completed", DateTimeOffset.UtcNow));
        return new InstallOutcome(await _repository.LoadAsync(instanceId, ct), resolved);
    }

    private static string SafeVersionDirectory(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

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
            if (File.Exists(Path.Combine(instanceDirectory, MrpackImportService.PendingFileName)))
                throw new MdclException(MdclError.Of("MRPACK_IMPORT_INCOMPLETE", "Launch", "整合包尚未导入完成，请用同一包重新导入以恢复。", true));
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
