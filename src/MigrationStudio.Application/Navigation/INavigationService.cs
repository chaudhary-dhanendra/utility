namespace MigrationStudio.Application.Navigation;

public interface INavigationService
{
    NavigationRoute CurrentRoute { get; }

    bool CanGoBack { get; }

    event EventHandler<NavigationChangedEventArgs>? Navigated;

    void Navigate(NavigationRoute route);

    bool GoBack();
}
