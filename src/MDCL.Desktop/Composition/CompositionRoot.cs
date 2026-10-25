using System.Net.Http.Headers;
using MDCL.Application.Installation;
using MDCL.Application.Ports;
using MDCL.Application.Versions;
using MDCL.Core;
using MDCL.Desktop.ViewModels;
using MDCL.Infrastructure.Authentication;
using MDCL.Infrastructure.Downloads;
using MDCL.Infrastructure.Java;
using MDCL.Infrastructure.Launcher;
using MDCL.Infrastructure.Metadata;
using MDCL.Infrastructure.Operations;
using MDCL.Infrastructure.Processes;
using MDCL.Infrastructure.Storage;
using MDCL.Platform.MacOS.Credentials;
using MDCL.Platform.MacOS.Java;
using MDCL.Platform.MacOS.Paths;
using MDCL.Platform.Windows.Credentials;
using MDCL.Platform.Windows.Java;
using MDCL.Platform.Windows.Paths;
using Microsoft.Extensions.DependencyInjection;

namespace MDCL.Desktop.Composition;

public static class CompositionRoot
{
    // MDCL-owned public application ID. It is not a secret or another launcher's identity.
    public const string MicrosoftApplicationId = "4bdf4b28-00fa-40c3-92bf-d89b0a0148e9";
    public static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        var dataRootOverride = Environment.GetEnvironmentVariable("MDCL_DATA_ROOT");
        var paths = OperatingSystem.IsWindows()
            ? (IAppPaths)new WindowsAppPaths(dataRootOverride)
            : new MacOSAppPaths(dataRootOverride);
        pathsConfig(paths);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MDCL", BuildInfo.Version));

        services.AddSingleton(paths);
        services.AddSingleton(http);
        services.AddSingleton<IInstanceRepository>(new FileInstanceRepository(paths.InstancesRoot));
        services.AddSingleton<IInstanceIndex>(sp => new SqliteInstanceIndex(paths.DatabasePath, sp.GetRequiredService<IInstanceRepository>()));
        services.AddSingleton<IInstanceLeaseManager>(new FileInstanceLeaseManager(paths.InstancesRoot));
        services.AddSingleton<InstanceLifecycleService>();
        services.AddSingleton<MDCL.Infrastructure.Diagnostics.DiagnosticArchiveService>();
        var metadata = new MojangMetadataProvider(http, Path.Combine(paths.CacheRoot, "metadata"));
        services.AddSingleton<IVersionMetadataSource>(metadata);
        services.AddSingleton<IAssetIndexSource>(metadata);
        services.AddSingleton<IComponentVersionProvider, MDCL.Infrastructure.Installers.FabricComponentVersionProvider>();
        services.AddSingleton<IComponentVersionProvider>(_ => new MDCL.Infrastructure.Installers.ForgeInstalledVersionProvider(paths, MDCL.Core.Instances.LoaderKind.Forge));
        services.AddSingleton<IComponentVersionProvider>(_ => new MDCL.Infrastructure.Installers.ForgeInstalledVersionProvider(paths, MDCL.Core.Instances.LoaderKind.NeoForge));
        services.AddSingleton<IVersionResolver, VersionResolver>();
        services.AddSingleton<IArtifactDownloader>(new ArtifactDownloader(http));
        services.AddSingleton<IInstallPlanner, VanillaInstallPlanner>();
        services.AddSingleton<IOperationCoordinator>(sp => new OperationCoordinator(sp.GetRequiredService<IArtifactDownloader>(), paths.StagingRoot));
        services.AddSingleton<IJavaProbe>(new ProcessJavaProbe());
        services.AddSingleton<IJavaDiscovery>(sp =>
        {
            IJavaSearchLocations locations = OperatingSystem.IsWindows()
                ? new WindowsJavaLocations()
                : new MacJavaLocations();
            return new JavaDiscovery(sp.GetRequiredService<IJavaProbe>(), paths.RuntimesRoot, locations);
        });
        services.AddSingleton<IJavaSelector, JavaSelector>();
        services.AddSingleton<MDCL.Infrastructure.Installers.IInstallerProcessRunner, MDCL.Infrastructure.Installers.ProcessInstallerRunner>();
        services.AddSingleton<MDCL.Infrastructure.Installers.ForgeInstallerService>();
        services.AddSingleton<IJavaRuntimePackageSource>(new AdoptiumJavaPackageSource(http));
        services.AddSingleton<IJavaRuntimeInstaller>(sp => new JavaRuntimeInstaller(
            sp.GetRequiredService<IJavaRuntimePackageSource>(), sp.GetRequiredService<IArtifactDownloader>(),
            sp.GetRequiredService<IJavaProbe>(), paths.RuntimesRoot, paths.StagingRoot));
        services.AddSingleton<IAccountStore>(new JsonAccountStore(Path.Combine(paths.ConfigRoot, "accounts.json")));
        services.AddSingleton<ICredentialVault>(_ => CreateCredentialVault(paths));
        var authConfiguration = new MicrosoftAuthConfigurationStore(paths.ConfigRoot,
            Environment.GetEnvironmentVariable(LauncherWorkflow.MicrosoftClientEnvironmentVariable), MicrosoftApplicationId);
        services.AddSingleton(authConfiguration);
        services.AddSingleton(authConfiguration.CurrentOptions);
        services.AddSingleton(sp =>
        {
            // Credential-bearing POST bodies must never follow an HTTP redirect.
            var authHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            authHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MDCL", BuildInfo.Version));
            return new MicrosoftAuthProtocol(authHttp, sp.GetRequiredService<MicrosoftAuthOptions>(), ownsHttpClient: true);
        });
        services.AddSingleton<IAuthorizationCodeProvider, LoopbackAuthorizationCodeProvider>();
        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IAccountService, MicrosoftAccountService>();
        services.AddSingleton<IGameProcessService>(new GameProcessService(Path.Combine(paths.ConfigRoot, "session-temp")));
        services.AddSingleton<LauncherWorkflow>();
        services.AddSingleton<MDCL.Infrastructure.Installers.MrpackReader>();
        services.AddSingleton<MDCL.Infrastructure.Installers.MrpackImportService>();
        services.AddSingleton<ModrinthProvider>();
        services.AddSingleton<MDCL.Infrastructure.Installers.ModrinthInstallService>();
        services.AddSingleton<MDCL.Infrastructure.Mods.LocalModService>();
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }

    private static void pathsConfig(IAppPaths paths)
    {
        if (paths is MDCL.Application.Paths.AppPathsBase concrete) concrete.EnsureDirectories();
    }

    private static ICredentialVault CreateCredentialVault(IAppPaths paths)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiCredentialVault(Path.Combine(paths.ConfigRoot, "credentials"));
        if (OperatingSystem.IsMacOS())
            return new KeychainCredentialVault();
        throw new PlatformNotSupportedException("MDCL supports Windows and macOS only.");
    }
}
