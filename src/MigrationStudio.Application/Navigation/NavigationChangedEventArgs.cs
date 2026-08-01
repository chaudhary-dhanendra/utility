namespace MigrationStudio.Application.Navigation;

public sealed class NavigationChangedEventArgs(NavigationRoute route) : EventArgs
{
    public NavigationRoute Route { get; } = route;
}
