using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MigrationStudio.Application.Errors;
using MigrationStudio.Application.Navigation;
using MigrationStudio.Application.Settings;
using MigrationStudio.Desktop.Errors;
using MigrationStudio.Desktop.Dialogs;
using MigrationStudio.Desktop.Navigation;
using MigrationStudio.Desktop.Theming;
using MigrationStudio.Desktop.Threading;
using MigrationStudio.Desktop.ViewModels;

namespace MigrationStudio.Desktop;

public static class DependencyInjection
{
    public static IServiceCollection AddMigrationStudioDesktop(this IServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher>(
            _ => new WpfUiDispatcher(System.Windows.Application.Current.Dispatcher));
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IErrorPresenter, WpfErrorPresenter>();
        services.AddSingleton<GlobalExceptionHandler>();
        services.AddSingleton<IFileDialogService, FileDialogService>();

        services.AddSingleton<NavigationService>();
        services.AddSingleton<IDesktopNavigationService>(provider => provider.GetRequiredService<NavigationService>());
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<NavigationService>());

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddSingleton<PostgreSqlConnectionViewModel>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<MigrationWizardViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<PluginsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
