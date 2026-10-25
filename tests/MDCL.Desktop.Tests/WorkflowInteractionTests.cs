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
        public MainWindowViewModel ViewModel { get; }
        public Context()
        {
            Environment.SetEnvironmentVariable("MDCL_DATA_ROOT", _root);
            _services = CompositionRoot.CreateServiceProvider();
            var workflow = new LauncherWorkflow(Get<IAppPaths>(), Get<IInstanceRepository>(), Get<IInstanceIndex>(), Get<IInstanceLeaseManager>(),
                Resolver, Get<IVersionMetadataSource>(), Get<IInstallPlanner>(), Get<IOperationCoordinator>(),
                Get<IJavaDiscovery>(), Get<IJavaSelector>(), Get<IJavaRuntimeInstaller>(), Get<IAccountService>(), Get<IGameProcessService>(), Get<ForgeInstallerService>());
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
}
