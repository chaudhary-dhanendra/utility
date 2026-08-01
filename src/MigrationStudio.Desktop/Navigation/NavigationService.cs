using Microsoft.Extensions.DependencyInjection;
using MigrationStudio.Application.Navigation;
using MigrationStudio.Application.Settings;
using MigrationStudio.Desktop.ViewModels;

namespace MigrationStudio.Desktop.Navigation;

public sealed class NavigationService(
    IServiceProvider serviceProvider,
    ISettingsService? settings = null) : IDesktopNavigationService
{
    private readonly Stack<NavigationRoute> _history = new();

    public NavigationRoute CurrentRoute { get; private set; } = NavigationRoute.Home;

    public object CurrentViewModel { get; private set; } = null!;

    public bool CanGoBack => _history.Count > 0;

    public event EventHandler<NavigationChangedEventArgs>? Navigated;

    public void Initialize()
    {
        if (settings is not null)
        {
            CurrentRoute = settings.Current.ExperienceMode == ExperienceMode.Advanced
                ? NavigationRoute.AdvancedWorkspace
                : NavigationRoute.Workspace;
        }
        CurrentViewModel = Resolve(CurrentRoute);
        Navigated?.Invoke(this, new NavigationChangedEventArgs(CurrentRoute));
    }

    public void Navigate(NavigationRoute route)
    {
        if (CurrentViewModel is not null && route == CurrentRoute)
        {
            return;
        }

        if (CurrentViewModel is not null)
        {
            _history.Push(CurrentRoute);
        }

        CurrentRoute = route;
        CurrentViewModel = Resolve(route);
        Navigated?.Invoke(this, new NavigationChangedEventArgs(route));
    }

    public bool GoBack()
    {
        if (!_history.TryPop(out var route))
        {
            return false;
        }

        CurrentRoute = route;
        CurrentViewModel = Resolve(route);
        Navigated?.Invoke(this, new NavigationChangedEventArgs(route));
        return true;
    }

    private object Resolve(NavigationRoute route) => route switch
    {
        NavigationRoute.Home => serviceProvider.GetRequiredService<HomeViewModel>(),
        NavigationRoute.Workspace => serviceProvider.GetRequiredService<MigrationWizardViewModel>(),
        NavigationRoute.AdvancedWorkspace => serviceProvider.GetRequiredService<WorkspaceViewModel>(),
        NavigationRoute.Reports => serviceProvider.GetRequiredService<ReportsViewModel>(),
        NavigationRoute.Plugins => serviceProvider.GetRequiredService<PluginsViewModel>(),
        NavigationRoute.Settings => serviceProvider.GetRequiredService<SettingsViewModel>(),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "The navigation route is not registered.")
    };
}
