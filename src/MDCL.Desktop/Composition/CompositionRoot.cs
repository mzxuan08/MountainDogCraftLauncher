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
    public static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        var dataRootOverride = Environment.GetEnvironmentVariable("MDCL_DATA_ROOT");
        var paths = OperatingSystem.IsWindows()
            ? (IAppPaths)new WindowsAppPaths(dataRootOverride)
            : new MacOSAppPaths(dataRootOverride);
        pathsConfig(paths);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MDCL", "0.1.0-alpha"));

        services.AddSingleton(paths);
        services.AddSingleton(http);
        services.AddSingleton<IInstanceRepository>(new FileInstanceRepository(paths.InstancesRoot));
        services.AddSingleton<IInstanceIndex>(sp => new SqliteInstanceIndex(paths.DatabasePath, sp.GetRequiredService<IInstanceRepository>()));
        services.AddSingleton<IInstanceLeaseManager>(new FileInstanceLeaseManager(paths.InstancesRoot));
        var metadata = new MojangMetadataProvider(http, Path.Combine(paths.CacheRoot, "metadata"));
        services.AddSingleton<IVersionMetadataSource>(metadata);
        services.AddSingleton<IAssetIndexSource>(metadata);
        services.AddSingleton<IComponentVersionProvider, MDCL.Infrastructure.Installers.FabricComponentVersionProvider>();
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
        services.AddSingleton<IJavaRuntimePackageSource>(new AdoptiumJavaPackageSource(http));
        services.AddSingleton<IJavaRuntimeInstaller>(sp => new JavaRuntimeInstaller(
            sp.GetRequiredService<IJavaRuntimePackageSource>(), sp.GetRequiredService<IArtifactDownloader>(),
            sp.GetRequiredService<IJavaProbe>(), paths.RuntimesRoot, paths.StagingRoot));
        services.AddSingleton<IAccountStore>(new JsonAccountStore(Path.Combine(paths.ConfigRoot, "accounts.json")));
        services.AddSingleton<ICredentialVault>(_ => CreateCredentialVault(paths));
        services.AddSingleton(new MicrosoftAuthOptions(
            Environment.GetEnvironmentVariable(LauncherWorkflow.MicrosoftClientEnvironmentVariable) ?? ""));
        services.AddSingleton<MicrosoftAuthProtocol>();
        services.AddSingleton<IAuthorizationCodeProvider, LoopbackAuthorizationCodeProvider>();
        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IAccountService, MicrosoftAccountService>();
        services.AddSingleton<IGameProcessService>(new GameProcessService(Path.Combine(paths.ConfigRoot, "session-temp")));
        services.AddSingleton<LauncherWorkflow>();
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
