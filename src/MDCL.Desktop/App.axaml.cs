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
            else if (healthArgs.Length == 2 && healthArgs[0] == "--capture-ui")
            {
                desktop.MainWindow.ShowActivated = false;
                desktop.MainWindow.Opened += async (_, _) =>
                {
                    try
                    {
                        await viewModel.InitializeAsync();
                        var directory = Path.GetFullPath(healthArgs[1]);
                        Directory.CreateDirectory(directory);
                        foreach (var width in new[] { 1200, 1100 })
                        {
                            desktop.MainWindow.Width = width;
                            desktop.MainWindow.Height = width == 1200 ? 800 : 720;
                            for (var page = 0; page < 7; page++)
                            {
                                viewModel.SelectedTabIndex = page;
                                await Task.Delay(200);
                                var size = desktop.MainWindow.ClientSize;
                                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                                    new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
                                bitmap.Render(desktop.MainWindow);
                                bitmap.Save(Path.Combine(directory, $"desktop-{width}-page-{page}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                            }
                        }
                        desktop.Shutdown(0);
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error.Message);
                        desktop.Shutdown(1);
                    }
                };
            }
            else _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
