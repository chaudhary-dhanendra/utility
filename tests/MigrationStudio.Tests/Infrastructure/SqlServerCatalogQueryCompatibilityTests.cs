using System.Data;
using MigrationStudio.Infrastructure.SqlServer;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class SqlServerCatalogQueryCompatibilityTests
{
    [Fact]
    public void ProductionCatalogReader_AllowsNamedColumnsToBeReadOutOfOrdinalOrder()
    {
        Assert.Equal(
            CommandBehavior.Default,
            SqlServerInventoryDiscoveryService.CatalogReaderBehavior);
    }

    [Fact]
    public void RequiredQueries_RejectSqlServerVersionsBefore2016()
    {
        Assert.Throws<NotSupportedException>(() => SqlServerCatalogQueries.Objects(12));
        Assert.Throws<NotSupportedException>(() => SqlServerCatalogQueries.Tables(12));
        Assert.Throws<NotSupportedException>(() => SqlServerCatalogQueries.Columns(12));
        Assert.Throws<NotSupportedException>(() => SqlServerCatalogQueries.Advanced(12));
        Assert.Throws<NotSupportedException>(() =>
            SqlServerCatalogQueries.ExternalAndPartitioning(12));
    }

    [Fact]
    public void Tables_SelectsGraphColumnsOnlyWhenSupported()
    {
        var sql2016 = SqlServerCatalogQueries.Tables(13);
        var sql2017 = SqlServerCatalogQueries.Tables(14);

        Assert.DoesNotContain("t.is_node AS", sql2016, StringComparison.Ordinal);
        Assert.Contains("CONVERT(bit, 0) AS is_node", sql2016, StringComparison.Ordinal);
        Assert.Contains("t.is_node AS is_node", sql2017, StringComparison.Ordinal);
        Assert.Contains("t.is_edge AS is_edge", sql2017, StringComparison.Ordinal);
    }

    [Fact]
    public void Tables_SelectsLedgerMetadataOnlyOnSqlServer2022()
    {
        var sql2019 = SqlServerCatalogQueries.Tables(15);
        var sql2022 = SqlServerCatalogQueries.Tables(16);

        Assert.DoesNotContain("t.ledger_type > 0", sql2019, StringComparison.Ordinal);
        Assert.Contains("t.ledger_type > 0", sql2022, StringComparison.Ordinal);
    }

    [Fact]
    public void Tables_DerivesExternalTableRejectDescriptionFromDocumentedNumericColumn()
    {
        var sql2022 = SqlServerCatalogQueries.Tables(16);

        Assert.DoesNotContain("et.reject_type_desc", sql2022, StringComparison.Ordinal);
        Assert.Contains("CASE et.reject_type", sql2022, StringComparison.Ordinal);
        Assert.Contains("END AS reject_type_desc", sql2022, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerTriggers_UsesAStableFalseValueForUnsupportedInsteadOfMetadata()
    {
        Assert.DoesNotContain(
            "tr.is_instead_of_trigger",
            SqlServerCatalogQueries.ServerTriggers,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONVERT(bit, 0) AS is_instead_of_trigger",
            SqlServerCatalogQueries.ServerTriggers,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Advanced_ReadsColumnEncryptionKeyValueMetadataFromItsOwningCatalog()
    {
        var sql2022 = SqlServerCatalogQueries.Advanced(16);

        Assert.Contains("sys.column_encryption_key_values cekv", sql2022, StringComparison.Ordinal);
        Assert.Contains("cekv.column_master_key_id", sql2022, StringComparison.Ordinal);
        Assert.Contains(
            "cekv.encryption_algorithm_name AS algorithm_name",
            sql2022,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalFileFormats_AliasesDocumentedCompressionColumnForTheMapper()
    {
        var sql2022 = SqlServerCatalogQueries.ExternalAndPartitioning(16);

        Assert.DoesNotContain(
            "SELECT name, format_type, data_compression_desc",
            sql2022,
            StringComparison.Ordinal);
        Assert.Contains(
            "data_compression AS data_compression_desc",
            sql2022,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalDataSourceConnectionOptions_IsVersionGated()
    {
        var sql2019 = SqlServerCatalogQueries.ExternalAndPartitioning(15);
        var sql2022 = SqlServerCatalogQueries.ExternalAndPartitioning(16);

        Assert.Contains(
            "CONVERT(nvarchar(4000), NULL) AS connection_options",
            sql2019,
            StringComparison.Ordinal);
        Assert.Contains("connection_options AS connection_options", sql2022, StringComparison.Ordinal);
    }
}
