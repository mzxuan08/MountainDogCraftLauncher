using MDCL.Application.Ports;
using MDCL.Application.Versions;
using MDCL.Core;
using MDCL.Core.Versions;
using MDCL.Desktop.Composition;
using MDCL.Desktop.ViewModels;
using MDCL.Infrastructure.Installers;
using MDCL.Infrastructure.Launcher;
using MDCL.Infrastructure.Metadata;
using MDCL.Infrastructure.Mods;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MDCL.Desktop.Tests;

public sealed class WorkflowInteractionTests
{
    [Fact]
    public void FourSectionNavigation_KeepsExistingFeatureRoutesReachable()
    {
        using var context = new Context();
        var vm = context.ViewModel;
        Assert.Equal(new[] { "启动", "下载", "设置", "更多" }, vm.Navigation);
        vm.SelectedSectionIndex = 1;
        Assert.Equal(7, vm.SelectedTabIndex);
        vm.SelectedSidebar = vm.SidebarItems.Single(x => x.PageIndex == 2);
        Assert.Equal(2, vm.SelectedTabIndex);
        vm.OpenPacksCommand.Execute(null);
        Assert.Equal(1, vm.SelectedSectionIndex);
        Assert.Equal(3, vm.SelectedSidebar?.PageIndex);
        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal(2, vm.SelectedSectionIndex);
        vm.SelectedSectionIndex = 3;
        vm.SelectedSidebar = vm.SidebarItems.Single(x => x.PageIndex == 5);
        Assert.Equal(5, vm.SelectedTabIndex);
        vm.OpenVersionsCommand.Execute(null);
        Assert.True(vm.IsLaunchSection);
        Assert.Equal(8, vm.SelectedTabIndex);
    }

    [Fact]
    public async Task EmptyLauncherPrimaryAction_OpensDownloadWithoutStartingProcess()
    {
        using var context = new Context();
        var vm = context.ViewModel;
        Assert.Equal("下载游戏", vm.PrimaryActionLabel);
        await vm.PrimaryActionCommand.ExecuteAsync(null);
        Assert.Equal(7, vm.SelectedTabIndex);
        Assert.False(vm.IsGameRunning);
    }

    [Fact]
    public async Task AccountMode_NeverUsesLocalIdentityForMicrosoftMode()
    {
        using var context = new Context();
        var vm = context.ViewModel;
        await vm.InitializeAsync();
        Assert.Single(vm.ModeAccounts);
        vm.IsLocalMode = false;
        Assert.True(vm.IsMicrosoftMode);
        Assert.Null(vm.SelectedAccount);
        Assert.Empty(vm.ModeAccounts);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsMicrosoftMode);
        Assert.Null(vm.SelectedAccount);
        vm.IsLocalMode = true;
        Assert.NotNull(vm.SelectedAccount);
    }

    [Fact]
    public async Task JavaDiscovery_UsesBindingOfInstanceSelectedAfterAwait()
    {
        using var context = new Context();
        var vm = context.ViewModel;
        await vm.InitializeAsync();
        await vm.CreateCommand.ExecuteAsync(null);
        vm.SelectedJavaRuntime = new("java-a", "A");
        await vm.SaveInstanceSettingsCommand.ExecuteAsync(null);
        var first = vm.SelectedInstance;
        vm.NewInstanceName = "B";
        await vm.CreateCommand.ExecuteAsync(null);
        vm.SelectedJavaRuntime = new("java-b", "B");
        await vm.SaveInstanceSettingsCommand.ExecuteAsync(null);
        var second = vm.SelectedInstance;
        vm.SelectedInstance = first;
        var discovering = vm.RefreshJavaCommand.ExecuteAsync(null);
        await context.Discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedInstance = second;
        context.Discovery.Release.TrySetResult([]);
        await discovering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("java-b", vm.SelectedJavaRuntime?.RuntimeId);
        await vm.SaveInstanceSettingsCommand.ExecuteAsync(null);
        Assert.Equal("java-b", vm.SelectedInstance?.Manifest.JavaPolicy.BoundRuntimeId);
    }

    [Fact]
    public void ImportWithoutReviewedPreview_IsDisabled()
    {
        using var context = new Context();
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mrpack");
        File.WriteAllText(file, "unreviewed input");
        try
        {
            context.ViewModel.MrpackPath = file;
            Assert.False(context.ViewModel.ImportMrpackCommand.CanExecute(null));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task MissingInstance_DisablesInstallLaunchAndStop()
    {
        using var context = new Context();
        var vm = context.ViewModel;
        await vm.InitializeAsync();
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.False(vm.LaunchCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstallCancellation_ReleasesBusyStateAndCanRetry()
    {
        using var context = new Context();
        var vm = context.ViewModel;
        await vm.InitializeAsync();
        await vm.CreateCommand.ExecuteAsync(null);
        var installing = vm.InstallCommand.ExecuteAsync(null);
        await context.Resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(vm.InstallCommand.CanBeCanceled);
            vm.InstallCommand.Cancel();
            await installing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { context.Resolver.Release.Cancel(); }
        Assert.False(vm.IsBusy);
        Assert.Contains("取消", vm.Status, StringComparison.Ordinal);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    private sealed class BlockingResolver : IVersionResolver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Release { get; } = new();
        public async Task<ResolvedVersion> ResolveAsync(VersionSelection selection, TargetPlatform target, CancellationToken ct)
        {
            Started.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Release.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
            throw new InvalidOperationException();
        }
    }

    private sealed class Context : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MDCL-ui-tests", Guid.NewGuid().ToString("N"));
        private readonly string? _previous = Environment.GetEnvironmentVariable("MDCL_DATA_ROOT");
        private readonly ServiceProvider _services;
        public BlockingResolver Resolver { get; } = new();
        public BlockingJavaDiscovery Discovery { get; } = new();
        public MainWindowViewModel ViewModel { get; }
        public Context()
        {
            Environment.SetEnvironmentVariable("MDCL_DATA_ROOT", _root);
            _services = CompositionRoot.CreateServiceProvider();
            var workflow = new LauncherWorkflow(Get<IAppPaths>(), Get<IInstanceRepository>(), Get<IInstanceIndex>(), Get<IInstanceLeaseManager>(),
                Resolver, Get<IVersionMetadataSource>(), Get<IInstallPlanner>(), Get<IOperationCoordinator>(),
                Discovery, Get<IJavaSelector>(), Get<IJavaRuntimeInstaller>(), Get<IAccountService>(), Get<IGameProcessService>(), Get<ForgeInstallerService>());
            ViewModel = new MainWindowViewModel(workflow, Get<ModrinthProvider>(), Get<ModrinthInstallService>(), Get<LocalModService>(), Get<MrpackImportService>(), Get<MDCL.Infrastructure.Storage.InstanceLifecycleService>(), Get<MDCL.Infrastructure.Diagnostics.DiagnosticArchiveService>());
        }
        private T Get<T>() where T : notnull => _services.GetRequiredService<T>();
        public void Dispose()
        {
            Resolver.Release.Cancel();
            _services.Dispose();
            Environment.SetEnvironmentVariable("MDCL_DATA_ROOT", _previous);
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    private sealed class BlockingJavaDiscovery : IJavaDiscovery
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<JavaProbeResult>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<JavaProbeResult>> DiscoverAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            return Release.Task.WaitAsync(ct);
        }
    }
}
