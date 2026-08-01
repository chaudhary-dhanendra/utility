namespace MigrationStudio.Application.Platform;

public interface IApplicationPaths
{
    string ApplicationDataDirectory { get; }

    string LogsDirectory { get; }

    string PluginsDirectory { get; }

    string SettingsFilePath { get; }
}
