using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Validation;
using MigrationStudio.Validation;

namespace MigrationStudio.Tests.Validation;

public sealed class StructuralValidationTests
{
    [Fact]
    public async Task MissingMappedTableIsDetectedWithoutDirectNameFallback()
    {
        var tableId = InventoryObjectId.Create("fixture", InventoryObjectType.Table, "dbo", "Orders", 1);
        var table = new InventoryObject(
            tableId, "fixture", "dbo", "Orders", "[dbo].[Orders]", InventoryObjectType.Table,
            1, null, null, null, false, true, SelectionReason.CompleteDatabase, 0, 0, [],
            ConversionClassification.Automatic, null, null, "table-hash", [],
            DiscoveryStatus.Discovered);
        var snapshot = TestInventory.CreateSnapshot([table]);
        var conversion = new ConversionRun(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "fixture", new PostgreSqlVersion(18),
            new ConversionOptions(),
            [
                new IdentifierMappingEntry(
                    tableId, "Table", "dbo", "Orders", "[dbo].[Orders]",
                    "sales", "orders_v2", "sales.orders_v2",
                    6, 9, false, false, null, "configured mapping")
            ],
            [], [], [], [], "test");
        var engine = new PostMigrationValidationEngine(
            new FixedMetadataReader(new TargetDatabaseSnapshot
            {
                Identity = "fixture",
                Objects =
                [
                    new TargetObjectMetadata("sales", "Orders", "Table", null, null)
                ]
            }),
            new CanonicalValueSerializer(),
            new CanonicalChecksumService());

        var result = await engine.ValidateAsync(
            new ValidationRequest(
                snapshot,
                conversion,
                new ValidationConnectionOptions(string.Empty, string.Empty),
                new ValidationConfiguration { Level = ValidationLevel.InventoryOnly }),
            null,
            CancellationToken.None);

        var comparison = Assert.Single(result.ObjectComparisons, item => item.SourceName == "[dbo].[Orders]");
        Assert.Equal("sales.orders_v2", comparison.TargetName);
        Assert.Equal(ComparisonClassification.Missing, comparison.Classification);
        Assert.Contains(result.Findings, item =>
            item.RuleId == "STRUCTURE.MISSING_TABLE" &&
            item.Severity == ValidationSeverity.Critical);
    }

    private sealed class FixedMetadataReader(TargetDatabaseSnapshot snapshot)
        : IPostgreSqlValidationMetadataReader
    {
        public Task<TargetDatabaseSnapshot> ReadAsync(
            string connectionString,
            ValidationScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }
}
