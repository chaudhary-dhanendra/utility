namespace MigrationStudio.Application.Settings;

public interface ISettingsService
{
    ApplicationSettings Current { get; }

    event EventHandler<ApplicationSettings>? SettingsChanged;

    Task InitializeAsync(CancellationToken cancellationToken);

    Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken);
}
