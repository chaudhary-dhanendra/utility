using MigrationStudio.Domain.Plugins;

namespace MigrationStudio.Application.Plugins;

public interface IMigrationStudioPlugin
{
    PluginMetadata Metadata { get; }

    void Initialize(IPluginContext context);
}
