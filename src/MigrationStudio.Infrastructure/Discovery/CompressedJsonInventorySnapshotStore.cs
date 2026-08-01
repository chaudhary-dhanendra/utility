using System.IO.Compression;
using System.Text.Json;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Discovery;

public sealed class CompressedJsonInventorySnapshotStore : IInventorySnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public async Task SaveAsync(
        InventorySnapshot snapshot,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The snapshot path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false))
            {
                await JsonSerializer.SerializeAsync(gzip, snapshot, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<InventorySnapshot> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var file = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        var snapshot = await JsonSerializer.DeserializeAsync<InventorySnapshot>(
            gzip,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {
            throw new InvalidDataException("The inventory snapshot is empty.");
        }

        if (snapshot.FormatVersion > InventorySnapshot.CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"Inventory format {snapshot.FormatVersion} is newer than supported format {InventorySnapshot.CurrentFormatVersion}.");
        }

        return snapshot;
    }
}
