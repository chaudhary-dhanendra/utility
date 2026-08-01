using Microsoft.Extensions.DependencyInjection;
using MigrationStudio.Application.Navigation;
using MigrationStudio.Application.Plugins;
using MigrationStudio.Desktop.Navigation;
using MigrationStudio.Desktop.ViewModels;

namespace MigrationStudio.Tests.Desktop;

public sealed class NavigationServiceTests
{
    [Fact]
    public void NavigateAndGoBack_RestoresPreviousRoute()
    {
        var services = new ServiceCollection()
            .AddTransient<HomeViewModel>()
            .AddTransient<PluginsViewModel>()
            .AddSingleton<IPluginCatalog, EmptyPluginCatalog>()
            .BuildServiceProvider();
        var navigation = new NavigationService(services);

        navigation.Initialize();
        navigation.Navigate(NavigationRoute.Plugins);

        Assert.Equal(NavigationRoute.Plugins, navigation.CurrentRoute);
        Assert.IsType<PluginsViewModel>(navigation.CurrentViewModel);
        Assert.True(navigation.CanGoBack);

        var moved = navigation.GoBack();

        Assert.True(moved);
        Assert.Equal(NavigationRoute.Home, navigation.CurrentRoute);
        Assert.IsType<HomeViewModel>(navigation.CurrentViewModel);
    }

    private sealed class EmptyPluginCatalog : IPluginCatalog
    {
        public IReadOnlyList<PluginDescriptor> Plugins { get; } = [];
    }
}
