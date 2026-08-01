using CommunityToolkit.Mvvm.ComponentModel;
using MigrationStudio.Application.Plugins;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class PluginsViewModel(IPluginCatalog pluginCatalog) : ObservableObject
{
    public string Title { get; } = "Plugins";

    public IReadOnlyList<PluginDescriptor> Plugins => pluginCatalog.Plugins;

    public string Summary => Plugins.Count == 0
        ? "No plugins were discovered."
        : $"{Plugins.Count} plugin package(s) discovered.";
}
