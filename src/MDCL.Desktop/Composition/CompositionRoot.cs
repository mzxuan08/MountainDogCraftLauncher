using MDCL.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MDCL.Desktop.Composition;

public static class CompositionRoot
{
    public static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
