using MigrationStudio.Application.Platform;

namespace MigrationStudio.Infrastructure.Platform;

public sealed class ApplicationPaths : IApplicationPaths
{
    public ApplicationPaths()
    {
        ApplicationDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MigrationStudio");
        LogsDirectory = Path.Combine(ApplicationDataDirectory, "Logs");
        PluginsDirectory = Path.Combine(ApplicationDataDirectory, "Plugins");
        SettingsFilePath = Path.Combine(ApplicationDataDirectory, "settings.json");

        Directory.CreateDirectory(ApplicationDataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(PluginsDirectory);
    }

    public string ApplicationDataDirectory { get; }

    public string LogsDirectory { get; }

    public string PluginsDirectory { get; }

    public string SettingsFilePath { get; }
}
