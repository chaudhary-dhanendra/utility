using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Plugins;
using MigrationStudio.Application.Settings;
using MigrationStudio.Desktop.Errors;
using MigrationStudio.Desktop.Logging;
using MigrationStudio.Infrastructure;
using MigrationStudio.Infrastructure.Platform;
using MigrationStudio.Infrastructure.Plugins;
using MigrationStudio.Infrastructure.Security;
using MigrationStudio.Reporting;
using MigrationStudio.Deployment;
using MigrationStudio.Validation;
using Serilog;
using Serilog.Events;

namespace MigrationStudio.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private GlobalExceptionHandler? _exceptionHandler;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
#if DEBUG
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
#endif
            var paths = new ApplicationPaths();
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables("MIGRATIONSTUDIO_");

            builder.Logging.ClearProviders();
            var redactor = new SensitiveDataRedactor();
            var configuredLevel = builder.Configuration[
                $"{ProductionOptions.SectionName}:LoggingLevel"];
            var minimumLevel = Enum.TryParse<LogEventLevel>(
                configuredLevel, true, out var parsedLevel)
                ? parsedLevel
                : LogEventLevel.Information;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    new RedactingJsonFormatter(redactor),
                    Path.Combine(paths.LogsDirectory, "migration-studio-.jsonl"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 52_428_800,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 31,
                    shared: true)
                .CreateLogger();
            builder.Services.AddSerilog(Log.Logger, dispose: true);

            builder.Services.AddSingleton<IApplicationPaths>(paths);
            builder.Services.AddMigrationStudioReporting();
            builder.Services.AddMigrationStudioValidation();
            builder.Services.AddMigrationStudioDeployment();
            builder.Services.AddMigrationStudioInfrastructure(builder.Configuration);
            builder.Services.AddMigrationStudioDesktop();

            var hostVersion = typeof(App).Assembly.GetName().Version ?? new Version(1, 0);
            var pluginOptions = builder.Configuration
                .GetSection(PluginLoadingOptions.SectionName)
                .Get<PluginLoadingOptions>() ?? new PluginLoadingOptions();
            var pluginCatalog = PluginLoader.DiscoverAndInitialize(
                paths.PluginsDirectory,
                hostVersion,
                pluginOptions);
            builder.Services.AddSingleton<IPluginCatalog>(pluginCatalog);

            _host = builder.Build();

            var settings = _host.Services.GetRequiredService<ISettingsService>();
            await settings.InitializeAsync(CancellationToken.None);
            _host.Services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);

            _exceptionHandler = _host.Services.GetRequiredService<GlobalExceptionHandler>();
            _exceptionHandler.Attach(this);

            await _host.StartAsync();

            MainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Migration Studio could not start.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Migration Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _exceptionHandler?.Detach(this);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                _host.StopAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Application host shutdown exceeded the five-second timeout.");
            }

            _host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
