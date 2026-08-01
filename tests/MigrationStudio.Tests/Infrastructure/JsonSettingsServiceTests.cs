using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Settings;
using MigrationStudio.Infrastructure.Settings;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "MigrationStudio.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndInitialize_RoundTripsNormalizedSettings()
    {
        var paths = new TestApplicationPaths(_testDirectory);
        using (var writer = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance))
        {
            await writer.SaveAsync(
                new ApplicationSettings
                {
                    Theme = ThemeMode.Dark,
                    MaximumConcurrentOperations = 6
                },
                CancellationToken.None);
        }

        using var reader = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance);
        await reader.InitializeAsync(CancellationToken.None);

        Assert.Equal(ThemeMode.Dark, reader.Current.Theme);
        Assert.Equal(6, reader.Current.MaximumConcurrentOperations);
        Assert.Equal(ApplicationSettings.CurrentSchemaVersion, reader.Current.SchemaVersion);
    }

    [Fact]
    public async Task Initialize_WithInvalidJson_UsesDefaults()
    {
        var paths = new TestApplicationPaths(_testDirectory);
        Directory.CreateDirectory(paths.ApplicationDataDirectory);
        await File.WriteAllTextAsync(paths.SettingsFilePath, "{ invalid");

        using var service = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance);
        await service.InitializeAsync(CancellationToken.None);

        Assert.Equal(new ApplicationSettings(), service.Current);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private sealed class TestApplicationPaths(string root) : IApplicationPaths
    {
        public string ApplicationDataDirectory { get; } = root;

        public string LogsDirectory { get; } = Path.Combine(root, "Logs");

        public string PluginsDirectory { get; } = Path.Combine(root, "Plugins");

        public string SettingsFilePath { get; } = Path.Combine(root, "settings.json");
    }
}
