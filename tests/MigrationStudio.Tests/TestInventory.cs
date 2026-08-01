using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests;

internal static class TestInventory
{
    public static InventorySnapshot CreateSnapshot(IReadOnlyList<InventoryObject> objects) =>
        new()
        {
            DiscoveryEngineVersion = "test",
            ApplicationVersion = "test",
            SnapshotTimestamp = DateTimeOffset.UtcNow,
            ScopeMode = MigrationScopeMode.CompleteDatabase,
            Database = new DatabaseMetadata(
                ProductVersion: "16.0",
                ProductLevel: "RTM",
                Edition: "Developer",
                EngineEdition: 3,
                DatabaseName: "fixture",
                DatabaseId: 5,
                Owner: "dbo",
                CompatibilityLevel: 160,
                Collation: "Latin1_General_100_CI_AS",
                ContainmentType: "NONE",
                RecoveryModel: "FULL",
                IsReadOnly: false,
                SnapshotIsolationState: "OFF",
                IsReadCommittedSnapshotOn: false,
                IsAnsiNullDefaultOn: false,
                IsAnsiNullsOn: true,
                IsAnsiPaddingOn: true,
                IsAnsiWarningsOn: true,
                IsQuotedIdentifierOn: true,
                IsRecursiveTriggersOn: false,
                IsTrustworthyOn: false,
                IsBrokerEnabled: false,
                IsChangeTrackingEnabled: false,
                IsEncrypted: false,
                QueryStoreState: "OFF",
                ScopedConfigurations: [],
                Files: [],
                Filegroups: [],
                Options: new Dictionary<string, string?>()),
            Objects = objects
        };
}
