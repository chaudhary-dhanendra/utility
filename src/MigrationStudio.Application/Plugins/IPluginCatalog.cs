namespace MigrationStudio.Application.Plugins;

public interface IPluginCatalog
{
    IReadOnlyList<PluginDescriptor> Plugins { get; }
}
