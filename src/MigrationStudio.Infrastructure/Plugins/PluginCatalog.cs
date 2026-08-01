using System.Runtime.Loader;
using MigrationStudio.Application.Plugins;

namespace MigrationStudio.Infrastructure.Plugins;

public sealed class PluginCatalog : IPluginCatalog, IDisposable
{
    private readonly IReadOnlyList<AssemblyLoadContext> _loadContexts;

    internal PluginCatalog(
        IReadOnlyList<PluginDescriptor> plugins,
        IReadOnlyList<AssemblyLoadContext> loadContexts)
    {
        Plugins = plugins;
        _loadContexts = loadContexts;
    }

    public IReadOnlyList<PluginDescriptor> Plugins { get; }

    public void Dispose()
    {
        foreach (var context in _loadContexts)
        {
            context.Unload();
        }
    }
}
