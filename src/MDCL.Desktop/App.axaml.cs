using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MDCL.Desktop.Composition;
using MDCL.Desktop.ViewModels;
using MDCL.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MDCL.Desktop;

public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = CompositionRoot.CreateServiceProvider();
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => services.Dispose();
            var healthArgs = desktop.Args ?? [];
            if (healthArgs.Length == 4 && healthArgs[0] == "--health-check" && healthArgs[2] == "--health-nonce")
            {
                desktop.MainWindow.ShowInTaskbar = false;
                desktop.MainWindow.Opacity = 0;
                desktop.MainWindow.Opened += async (_, _) =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        await services.GetRequiredService<MDCL.Infrastructure.Launcher.LauncherWorkflow>().InitializeAsync(timeout.Token);
                        await File.WriteAllTextAsync(healthArgs[1], System.Text.Json.JsonSerializer.Serialize(new
                        { nonce = healthArgs[3], version = MDCL.Core.BuildInfo.Version }), timeout.Token);
                        desktop.Shutdown(0);
                    }
                    catch (Exception) { desktop.Shutdown(1); }
                };
            }
            else _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
