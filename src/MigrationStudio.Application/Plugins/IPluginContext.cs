namespace MigrationStudio.Application.Plugins;

public interface IPluginContext
{
    Version HostVersion { get; }

    string PluginDirectory { get; }
}
