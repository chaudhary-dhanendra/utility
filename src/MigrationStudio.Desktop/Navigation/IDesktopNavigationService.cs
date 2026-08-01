using MigrationStudio.Application.Navigation;

namespace MigrationStudio.Desktop.Navigation;

public interface IDesktopNavigationService : INavigationService
{
    object CurrentViewModel { get; }

    void Initialize();
}
