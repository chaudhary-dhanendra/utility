using MigrationStudio.Application.Settings;

namespace MigrationStudio.Tests.Application;

public sealed class ApplicationSettingsTests
{
    [Fact]
    public void Normalize_ClampsConcurrencyAndDockDimensions()
    {
        var settings = new ApplicationSettings
        {
            MaximumConcurrentOperations = 100,
            DockLayout = new DockLayoutSettings
            {
                ExplorerWidth = 10,
                InspectorWidth = 2_000,
                OutputHeight = 5
            }
        };

        var normalized = settings.Normalize();

        Assert.Equal(16, normalized.MaximumConcurrentOperations);
        Assert.Equal(180, normalized.DockLayout.ExplorerWidth);
        Assert.Equal(700, normalized.DockLayout.InspectorWidth);
        Assert.Equal(100, normalized.DockLayout.OutputHeight);
    }
}
