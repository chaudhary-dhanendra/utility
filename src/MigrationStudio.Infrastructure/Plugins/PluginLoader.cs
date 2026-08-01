using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MigrationStudio.Application.Plugins;

namespace MigrationStudio.Infrastructure.Plugins;

public static class PluginLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static PluginCatalog DiscoverAndInitialize(
        string pluginsDirectory,
        Version hostVersion)
        => DiscoverAndInitialize(
            pluginsDirectory,
            hostVersion,
            new PluginLoadingOptions
            {
                Enabled = true,
                RequireAuthenticodeSignature = false
            });

    public static PluginCatalog DiscoverAndInitialize(
        string pluginsDirectory,
        Version hostVersion,
        PluginLoadingOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        ArgumentNullException.ThrowIfNull(hostVersion);
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(pluginsDirectory);
        var descriptors = new List<PluginDescriptor>();
        var contexts = new List<AssemblyLoadContext>();
        if (!options.Enabled)
        {
            return new PluginCatalog(descriptors, contexts);
        }

        var trustedPublishers = AuthenticodeSignatureVerifier.NormalizeThumbprints(
            options.TrustedPublisherThumbprints);

        foreach (var manifestPath in Directory.EnumerateFiles(
                     pluginsDirectory,
                     "plugin.json",
                     SearchOption.AllDirectories))
        {
            LoadPlugin(
                manifestPath,
                hostVersion,
                options.RequireAuthenticodeSignature,
                trustedPublishers,
                descriptors,
                contexts);
        }

        return new PluginCatalog(descriptors, contexts);
    }

    private static void LoadPlugin(
        string manifestPath,
        Version hostVersion,
        bool requireAuthenticodeSignature,
        IReadOnlySet<string> trustedPublisherThumbprints,
        List<PluginDescriptor> descriptors,
        List<AssemblyLoadContext> contexts)
    {
        PluginLoadContext? loadContext = null;
        string? assemblyPath = null;

        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath),
                SerializerOptions) ?? throw new InvalidDataException("Plugin manifest is empty.");

            ArgumentException.ThrowIfNullOrWhiteSpace(manifest.EntryAssembly);
            var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
                ?? throw new InvalidDataException("The plugin manifest has no parent directory.");
            assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly));

            if (!assemblyPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(assemblyPath))
            {
                throw new InvalidDataException("The plugin entry assembly must exist inside its plugin directory.");
            }

            if (requireAuthenticodeSignature)
            {
                AuthenticodeSignatureVerifier.Verify(assemblyPath, trustedPublisherThumbprints);
            }

            loadContext = new PluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var pluginTypes = assembly.GetTypes()
                .Where(type => typeof(IMigrationStudioPlugin).IsAssignableFrom(type) &&
                               type is { IsAbstract: false, IsInterface: false })
                .ToArray();

            if (pluginTypes.Length != 1)
            {
                throw new InvalidDataException("A plugin assembly must expose exactly one IMigrationStudioPlugin.");
            }

            var plugin = (IMigrationStudioPlugin?)Activator.CreateInstance(pluginTypes[0])
                ?? throw new InvalidDataException("The plugin entry type could not be created.");

            if (plugin.Metadata.MinimumHostVersion > hostVersion)
            {
                descriptors.Add(new PluginDescriptor(
                    assemblyPath,
                    plugin.Metadata,
                    PluginLoadState.Incompatible,
                    $"Requires host {plugin.Metadata.MinimumHostVersion} or later."));
                loadContext.Unload();
                return;
            }

            plugin.Initialize(new PluginContext(hostVersion, directory));
            contexts.Add(loadContext);
            descriptors.Add(new PluginDescriptor(assemblyPath, plugin.Metadata, PluginLoadState.Loaded, null));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            ReflectionTypeLoadException or
            BadImageFormatException or
            InvalidDataException or
            ArgumentException or
            TargetInvocationException)
        {
            loadContext?.Unload();
            descriptors.Add(new PluginDescriptor(
                assemblyPath ?? manifestPath,
                null,
                PluginLoadState.Failed,
                exception.GetBaseException().Message));
        }
    }

    private sealed class PluginLoadContext(string entryAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "MigrationStudio.Application" or "MigrationStudio.Domain")
            {
                return null;
            }

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
        }
    }

    private sealed class PluginManifest
    {
        public string EntryAssembly { get; init; } = string.Empty;
    }

    private sealed record PluginContext(
        Version HostVersion,
        string PluginDirectory) : IPluginContext;
}
