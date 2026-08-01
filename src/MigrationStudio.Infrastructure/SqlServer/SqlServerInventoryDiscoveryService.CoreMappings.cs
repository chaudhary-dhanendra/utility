using Microsoft.Data.SqlClient;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.SqlServer;

public sealed partial class SqlServerInventoryDiscoveryService
{
    private static async Task ReadServerMetadataAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQL Server did not return server metadata.");
        }

        var productVersion = reader.Text("product_version");
        accumulator.SqlServerMajorVersion = ParseMajorVersion(productVersion);
        if (accumulator.SqlServerMajorVersion == 0)
        {
            throw new InvalidDataException(
                $"SQL Server returned an unrecognized product version '{productVersion}'.");
        }
    }

    private static async Task ReadDatabaseMetadataAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQL Server did not return database metadata.");
        }

        var productVersion = reader.Text("product_version");
        accumulator.SqlServerMajorVersion = ParseMajorVersion(productVersion);
        var scopedConfigurations = new List<DatabaseScopedConfiguration>();
        var files = new List<DatabaseFileMetadata>();
        var filegroups = new List<FilegroupMetadata>();
        var values = new
        {
            ProductVersion = productVersion,
            ProductLevel = reader.Text("product_level"),
            Edition = reader.Text("edition"),
            EngineEdition = reader.Int32("engine_edition"),
            Name = reader.Text("name"),
            DatabaseId = reader.Int32("database_id"),
            Owner = reader.NullableText("owner_name"),
            CompatibilityLevel = reader.Int32("compatibility_level"),
            Collation = reader.Text("collation_name"),
            Containment = reader.Text("containment_desc"),
            Recovery = reader.Text("recovery_model_desc"),
            IsReadOnly = reader.Boolean("is_read_only"),
            SnapshotIsolation = reader.Text("snapshot_isolation_state_desc"),
            IsReadCommittedSnapshot = reader.Boolean("is_read_committed_snapshot_on"),
            IsAnsiNullDefault = reader.Boolean("is_ansi_null_default_on"),
            IsAnsiNulls = reader.Boolean("is_ansi_nulls_on"),
            IsAnsiPadding = reader.Boolean("is_ansi_padding_on"),
            IsAnsiWarnings = reader.Boolean("is_ansi_warnings_on"),
            IsQuotedIdentifier = reader.Boolean("is_quoted_identifier_on"),
            IsRecursiveTriggers = reader.Boolean("is_recursive_triggers_on"),
            IsTrustworthy = reader.Boolean("is_trustworthy_on"),
            IsBrokerEnabled = reader.Boolean("is_broker_enabled"),
            IsEncrypted = reader.Boolean("is_encrypted"),
            IsChangeTracking = reader.Boolean("is_change_tracking_enabled"),
            QueryStore = reader.Text("query_store_state")
        };

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scopedConfigurations.Add(new DatabaseScopedConfiguration(
                reader.Text("name"),
                reader.NullableText("value") ?? string.Empty,
                reader.NullableText("value_for_secondary"),
                reader.Boolean("is_value_default")));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            files.Add(new DatabaseFileMetadata(
                reader.Int32("file_id"),
                reader.Text("name"),
                reader.NullableText("physical_name"),
                reader.Text("type_desc"),
                reader.Text("data_space_name"),
                reader.Int64("size_bytes"),
                reader.NullableInt64("used_bytes"),
                reader.Boolean("is_percent_growth"),
                reader.Int64("growth"),
                reader.Int64("max_size_bytes"),
                reader.Text("state_desc")));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            filegroups.Add(new FilegroupMetadata(
                reader.Int32("data_space_id"),
                reader.Text("name"),
                reader.Boolean("is_default"),
                reader.Boolean("is_read_only"),
                reader.Text("type_desc"),
                reader.Int32("file_count")));
        }

        accumulator.Database = new DatabaseMetadata(
            values.ProductVersion,
            values.ProductLevel,
            values.Edition,
            values.EngineEdition,
            values.Name,
            values.DatabaseId,
            values.Owner,
            values.CompatibilityLevel,
            values.Collation,
            values.Containment,
            values.Recovery,
            values.IsReadOnly,
            values.SnapshotIsolation,
            values.IsReadCommittedSnapshot,
            values.IsAnsiNullDefault,
            values.IsAnsiNulls,
            values.IsAnsiPadding,
            values.IsAnsiWarnings,
            values.IsQuotedIdentifier,
            values.IsRecursiveTriggers,
            values.IsTrustworthy,
            values.IsBrokerEnabled,
            values.IsChangeTracking,
            values.IsEncrypted,
            values.QueryStore,
            scopedConfigurations,
            files,
            filegroups,
            new Dictionary<string, string?>
            {
                ["CompatibilityLevel"] = values.CompatibilityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Collation"] = values.Collation,
                ["Containment"] = values.Containment,
                ["RecoveryModel"] = values.Recovery
            });
    }

    private static async Task ReadSchemasAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            accumulator.AddSchema(
                reader.Int32("schema_id"),
                reader.Text("name"),
                reader.NullableText("owner_name"),
                reader.Int32("object_count"),
                reader.Boolean("is_system_schema"));
        }
    }

    private static async Task ReadObjectsAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // sys.objects.type is char(2), so one-character codes are space padded.
            // Treating the raw value as an enum discriminator classified ordinary
            // user tables ("U ") as Unknown.
            var sqlType = reader.Text("type").Trim();
            var objectType = MapObjectType(sqlType);
            var moduleKind = MapModuleKind(sqlType);
            var definition = reader.NullableText("definition");
            var isEncrypted = reader.Boolean("is_encrypted");
            var status = definition is null && (moduleKind is not null || isEncrypted)
                ? DiscoveryStatus.DefinitionUnavailable
                : DiscoveryStatus.Discovered;
            var item = accumulator.AddObject(
                reader.Int32("object_id"),
                reader.Int32("parent_object_id"),
                reader.Text("schema_name"),
                reader.Text("name"),
                objectType,
                reader.NullableDateTimeOffset("create_date"),
                reader.NullableDateTimeOffset("modify_date"),
                reader.Boolean("is_ms_shipped"),
                definition,
                status,
                moduleKind is null
                    ? InventoryClassification.ForObject(objectType)
                    : InventoryClassification.ForObject(objectType, moduleKind: moduleKind),
                new
                {
                    SqlType = sqlType,
                    TypeDescription = reader.Text("type_desc"),
                    UsesAnsiNulls = reader.NullableBoolean("uses_ansi_nulls"),
                    UsesQuotedIdentifier = reader.NullableBoolean("uses_quoted_identifier"),
                    IsSchemaBound = reader.NullableBoolean("is_schema_bound"),
                    IsRecompiled = reader.NullableBoolean("is_recompiled"),
                    ExecuteAsPrincipalId = reader.NullableInt32("execute_as_principal_id"),
                    UsesNativeCompilation = reader.NullableBoolean("uses_native_compilation"),
                    IsEncrypted = isEncrypted
                });

            if (moduleKind is { } kind)
            {
                var source = definition ?? string.Empty;
                accumulator.Modules.Add(new ModuleInventory(
                    item.Id,
                    kind,
                    reader.Boolean("uses_ansi_nulls"),
                    reader.Boolean("uses_quoted_identifier"),
                    reader.Boolean("is_schema_bound"),
                    reader.Boolean("is_recompiled"),
                    isEncrypted,
                    reader.Boolean("uses_native_compilation"),
                    reader.NullableInt32("execute_as_principal_id") is { } executeAs
                        ? executeAs == -2 ? "OWNER" : executeAs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : null,
                    ContainsAny(source, "EXEC(", "EXEC (", "sp_executesql"),
                    ContainsAny(source, "#", "tempdb.."),
                    ContainsAny(source, "BEGIN TRAN", "COMMIT", "ROLLBACK", "SAVE TRAN"),
                    ContainsAny(source, "TRY", "CATCH", "RAISERROR", "THROW"),
                    [],
                    []));
            }

            if (definition is not null)
            {
                foreach (var indicator in new[] { "OPENQUERY", "OPENROWSET", "OPENDATASOURCE", "EXECUTE AT", "EXEC AT" })
                {
                    if (definition.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                    {
                        AddExternalDependency(
                            accumulator,
                            item.Id,
                            indicator,
                            indicator,
                            null,
                            null,
                            item.SourceSchema,
                            $"Module contains {indicator}.");
                    }
                }
            }
        }
    }

    private static int ParseMajorVersion(string productVersion)
    {
        var firstSegment = productVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(firstSegment, out var major) ? major : 0;
    }

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    internal static InventoryObjectType MapObjectType(string sqlType) => sqlType.Trim() switch
    {
        "U" => InventoryObjectType.Table,
        "V" => InventoryObjectType.View,
        "P" or "PC" or "X" => InventoryObjectType.StoredProcedure,
        "FN" or "IF" or "TF" or "FS" or "FT" or "AF" => InventoryObjectType.Function,
        "TR" => InventoryObjectType.Trigger,
        "TA" => InventoryObjectType.Trigger,
        "PK" => InventoryObjectType.PrimaryKey,
        "UQ" => InventoryObjectType.UniqueConstraint,
        "C" => InventoryObjectType.CheckConstraint,
        "F" => InventoryObjectType.ForeignKey,
        "D" => InventoryObjectType.DefaultConstraint,
        "SO" => InventoryObjectType.Sequence,
        "SN" => InventoryObjectType.Synonym,
        "TT" => InventoryObjectType.TableType,
        _ => InventoryObjectType.Unknown
    };

    private static ModuleKind? MapModuleKind(string sqlType) => sqlType.Trim() switch
    {
        "V" => ModuleKind.View,
        "P" or "X" => ModuleKind.StoredProcedure,
        "PC" => ModuleKind.ClrProcedure,
        "FN" => ModuleKind.ScalarFunction,
        "IF" => ModuleKind.InlineTableValuedFunction,
        "TF" => ModuleKind.MultiStatementTableValuedFunction,
        "FS" => ModuleKind.ClrScalarFunction,
        "FT" => ModuleKind.ClrTableValuedFunction,
        "AF" => ModuleKind.AggregateFunction,
        "TR" or "TA" => ModuleKind.DmlTrigger,
        _ => null
    };
}
