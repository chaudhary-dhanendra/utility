using System.Text.Json;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Settings;

namespace MigrationStudio.Infrastructure.Settings;

public sealed class JsonSettingsService(
    IApplicationPaths paths,
    ILogger<JsonSettingsService> logger) : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ApplicationSettings _current = new();

    public ApplicationSettings Current => _current;

    public event EventHandler<ApplicationSettings>? SettingsChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(paths.SettingsFilePath))
            {
                await SaveCoreAsync(_current, cancellationToken).ConfigureAwait(false);
                return;
            }

            await using var stream = new FileStream(
                paths.SettingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var loaded = await JsonSerializer.DeserializeAsync<ApplicationSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            _current = (loaded ?? new ApplicationSettings()).Normalize();
            SettingsLog.Loaded(logger, _current.SchemaVersion);
        }
        catch (JsonException exception)
        {
            SettingsLog.Invalid(logger, exception);
            _current = new ApplicationSettings();
        }
        catch (IOException exception)
        {
            SettingsLog.ReadFailed(logger, exception);
            _current = new ApplicationSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(normalized, cancellationToken).ConfigureAwait(false);
            _current = normalized;
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, normalized);
    }

    private static void ReplaceFile(string temporaryPath, string destinationPath)
    {
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private async Task SaveCoreAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.ApplicationDataDirectory);
        var temporaryPath = paths.SettingsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ReplaceFile(temporaryPath, paths.SettingsFilePath);
            SettingsLog.Saved(logger);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal static partial class SettingsLog
{
    [LoggerMessage(1000, LogLevel.Information, "Application settings schema {SchemaVersion} loaded.")]
    public static partial void Loaded(ILogger logger, int schemaVersion);

    [LoggerMessage(1001, LogLevel.Error, "The settings file is invalid. Defaults will be used.")]
    public static partial void Invalid(ILogger logger, Exception exception);

    [LoggerMessage(1002, LogLevel.Error, "The settings file could not be read. Defaults will be used.")]
    public static partial void ReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(1003, LogLevel.Information, "Application settings saved.")]
    public static partial void Saved(ILogger logger);
}
