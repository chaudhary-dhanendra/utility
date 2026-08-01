using System.IO;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Discovery;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class InventorySnapshotStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsVersionedCompressedInventory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.msinventory");
        try
        {
            var snapshot = TestInventory.CreateSnapshot([]);
            var store = new CompressedJsonInventorySnapshotStore();

            await store.SaveAsync(snapshot, path, CancellationToken.None);
            var loaded = await store.LoadAsync(path, CancellationToken.None);

            Assert.Equal(InventorySnapshot.CurrentFormatVersion, loaded.FormatVersion);
            Assert.Equal("fixture", loaded.Database.DatabaseName);
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
