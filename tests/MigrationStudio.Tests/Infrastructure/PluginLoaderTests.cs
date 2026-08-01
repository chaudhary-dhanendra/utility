using System.IO;
using MigrationStudio.Application.Plugins;
using MigrationStudio.Infrastructure;
using MigrationStudio.Infrastructure.Plugins;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "MigrationStudio.PluginTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoverAndInitialize_InvalidManifestProducesFailedDescriptor()
    {
        var pluginDirectory = Path.Combine(_testDirectory, "invalid-plugin");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "plugin.json"), "{ invalid");

        using var catalog = PluginLoader.DiscoverAndInitialize(_testDirectory, new Version(1, 0));

        var descriptor = Assert.Single(catalog.Plugins);
        Assert.Equal(PluginLoadState.Failed, descriptor.State);
        Assert.NotEmpty(descriptor.Diagnostic ?? string.Empty);
    }

    [Fact]
    public void DiscoverAndInitialize_DisabledPluginsAreNotEnumeratedOrLoaded()
    {
        var pluginDirectory = Path.Combine(_testDirectory, "disabled-plugin");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "plugin.json"), "{ invalid");

        using var catalog = PluginLoader.DiscoverAndInitialize(
            _testDirectory,
            new Version(1, 0),
            new PluginLoadingOptions
            {
                Enabled = false,
                RequireAuthenticodeSignature = true
            });

        Assert.Empty(catalog.Plugins);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
