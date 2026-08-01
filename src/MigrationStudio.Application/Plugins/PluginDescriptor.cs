using MigrationStudio.Domain.Plugins;

namespace MigrationStudio.Application.Plugins;

public enum PluginLoadState
{
    Loaded,
    Incompatible,
    Failed
}

public sealed record PluginDescriptor(
    string AssemblyPath,
    PluginMetadata? Metadata,
    PluginLoadState State,
    string? Diagnostic);
