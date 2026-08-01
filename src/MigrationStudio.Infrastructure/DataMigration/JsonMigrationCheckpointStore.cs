using System.Text.Json;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Platform;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class JsonMigrationCheckpointStore(IApplicationPaths paths) :
    IMigrationCheckpointStore,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> SaveAsync(
        MigrationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var directory = GetDirectory();
        Directory.CreateDirectory(directory);
        var path = GetPath(checkpoint.RunId);
        var temporary = path + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    checkpoint,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MigrationCheckpoint?> LoadAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var path = GetPath(runId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checkpoint = await JsonSerializer.DeserializeAsync<MigrationCheckpoint>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (checkpoint?.FormatVersion != MigrationCheckpoint.CurrentFormatVersion)
        {
            throw new InvalidDataException("The checkpoint format is unsupported.");
        }

        return checkpoint;
    }

    public async Task DeleteTableAsync(
        Guid runId,
        InventoryObjectId tableId,
        CancellationToken cancellationToken)
    {
        var checkpoint = await LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return;
        }

        await SaveAsync(
            checkpoint with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Tables = checkpoint.Tables.Where(item => item.TableId != tableId).ToArray()
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(runId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetDirectory() => Path.Combine(paths.ApplicationDataDirectory, "Checkpoints");

    private string GetPath(Guid runId) => Path.Combine(GetDirectory(), $"{runId:N}.json");

    public void Dispose() => _gate.Dispose();
}
