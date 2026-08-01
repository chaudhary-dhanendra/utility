using Microsoft.Data.SqlClient;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.SqlServer;

public sealed partial class SqlServerInventoryDiscoveryService
{
    private static async Task ReadAdvancedAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!accumulator.ObjectsBySqlId.TryGetValue(objectId, out var currentTable))
            {
                continue;
            }

            accumulator.TemporalTables.Add(new TemporalTableInventory(
                currentTable.Id,
                accumulator.TryGetObjectId(reader.NullableInt32("history_table_id")),
                reader.NullableText("period_start_column"),
                reader.NullableText("period_end_column"),
                null,
                null,
                reader.Int32("temporal_type") == 2));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            var tableId = accumulator.TryGetObjectId(objectId);
            accumulator.ChangeData.Add(new ChangeDataInventory(
                tableId,
                "CHANGE_TRACKING",
                true,
                null,
                null,
                null,
                reader.Boolean("is_track_columns_updated_on"),
                []));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var policies = new Dictionary<int, (InventoryObject Item, bool Enabled, bool SchemaBound)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!policies.TryGetValue(objectId, out var policy))
            {
                var item = accumulator.AddSyntheticObject(
                    InventoryObjectType.SecurityPolicy,
                    string.Empty,
                    reader.Text("name"),
                    null,
                    ConversionClassification.ManualConversion,
                    new
                    {
                        IsEnabled = reader.Boolean("is_enabled"),
                        IsSchemaBound = reader.Boolean("is_schema_bound")
                    });
                policy = (item, reader.Boolean("is_enabled"), reader.Boolean("is_schema_bound"));
                policies[objectId] = policy;
            }

            var target = accumulator.TryGetObjectId(reader.NullableInt32("target_object_id"));
            if (target is { } targetId)
            {
                accumulator.Dependencies.Add(new InventoryDependency(
                    policy.Item.Id,
                    targetId,
                    DependencyKind.SecurityPolicy,
                    reader.NullableText("predicate_definition") ?? targetId.ToString(),
                    true,
                    false,
                    Evidence: $"{reader.NullableText("predicate_type_desc")} {reader.NullableText("operation_desc")}"));
            }
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.Text("name");
            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.FullTextCatalog,
                string.Empty,
                name,
                null,
                ConversionClassification.ManualConversion,
                new
                {
                    FullTextCatalogId = reader.Int32("fulltext_catalog_id"),
                    IsDefault = reader.Boolean("is_default"),
                    AccentSensitive = reader.Boolean("is_accent_sensitivity_on")
                });
            accumulator.FullText.Add(new FullTextInventory(item.Id, "CATALOG", name, null, null, null, []));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var fullTextRows = new List<FullTextRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fullTextRows.Add(new FullTextRow(
                reader.Int32("object_id"),
                reader.Text("change_tracking_state_desc"),
                reader.NullableText("stoplist_name"),
                reader.NullableText("column_name")));
        }

        foreach (var group in fullTextRows.GroupBy(row => row.ObjectId))
        {
            if (!accumulator.ObjectsBySqlId.TryGetValue(group.Key, out var target))
            {
                continue;
            }

            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.FullTextIndex,
                target.SourceSchema,
                $"FullText:{target.SourceName}",
                target.Id,
                ConversionClassification.ManualConversion,
                new { Target = target.QualifiedSourceName });
            var first = group.First();
            accumulator.FullText.Add(new FullTextInventory(
                item.Id,
                "INDEX",
                item.SourceName,
                target.Id,
                first.ChangeTracking,
                first.Stoplist,
                group.Where(row => row.ColumnName is not null).Select(row => row.ColumnName!).ToArray()));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = reader.Text("broker_kind");
            var name = reader.Text("name");
            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.ServiceBrokerObject,
                string.Empty,
                $"{kind}:{name}",
                null,
                ConversionClassification.Unsupported,
                new { kind, name });
            accumulator.ServiceBroker.Add(new ServiceBrokerInventory(
                item.Id,
                kind,
                name,
                reader.Boolean("is_enabled"),
                reader.NullableText("related_object")));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.Text("name");
            accumulator.AddSyntheticObject(
                InventoryObjectType.Assembly,
                string.Empty,
                name,
                null,
                ConversionClassification.ManualConversion,
                new
                {
                    PermissionSet = reader.Text("permission_set_desc"),
                    IsVisible = reader.Boolean("is_visible"),
                    Created = reader.NullableDateTimeOffset("create_date")
                });
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.Text("name");
            accumulator.AddSyntheticObject(
                InventoryObjectType.DatabaseScopedCredential,
                string.Empty,
                name,
                null,
                ConversionClassification.ManualConversion,
                new { Name = name, IdentityPresent = !string.IsNullOrWhiteSpace(reader.NullableText("credential_identity")) });
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.Text("name");
            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.EncryptionKey,
                string.Empty,
                $"CMK:{name}",
                null,
                ConversionClassification.ManualConversion,
                new { Provider = reader.NullableText("key_store_provider_name"), PathPresent = !string.IsNullOrWhiteSpace(reader.NullableText("key_path")) });
            accumulator.Encryption.Add(new EncryptionInventory(
                item.Id,
                "COLUMN_MASTER_KEY",
                name,
                null,
                reader.NullableText("key_store_provider_name"),
                false,
                "METADATA_ONLY"));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.Text("name");
            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.EncryptionKey,
                string.Empty,
                $"CEK:{name}",
                null,
                ConversionClassification.ManualConversion,
                new { ColumnMasterKeyId = reader.Int32("column_master_key_id"), Algorithm = reader.NullableText("algorithm_name") });
            accumulator.Encryption.Add(new EncryptionInventory(
                item.Id,
                "COLUMN_ENCRYPTION_KEY",
                name,
                reader.NullableText("algorithm_name"),
                null,
                false,
                "METADATA_ONLY"));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var triggerRows = new List<TriggerRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            triggerRows.Add(new TriggerRow(
                reader.Int32("object_id"),
                reader.Text("parent_class_desc"),
                reader.Int32("parent_id"),
                reader.Text("name"),
                reader.Boolean("is_instead_of_trigger"),
                reader.Boolean("is_disabled"),
                reader.Boolean("is_not_for_replication"),
                reader.NullableText("event_name"),
                reader.Boolean("is_first"),
                reader.Boolean("is_last"),
                reader.NullableText("definition"),
                reader.NullableInt32("execute_as_principal_id")));
        }

        foreach (var group in triggerRows.GroupBy(row => row.ObjectId))
        {
            var first = group.First();
            var parent = accumulator.TryGetObjectId(first.ParentId);
            InventoryObject triggerObject;
            if (accumulator.ObjectsBySqlId.TryGetValue(first.ObjectId, out var existing))
            {
                triggerObject = existing;
            }
            else
            {
                triggerObject = accumulator.AddObject(
                    first.ObjectId,
                    0,
                    string.Empty,
                    first.Name,
                    InventoryObjectType.DatabaseTrigger,
                    null,
                    null,
                    false,
                    first.Definition,
                    first.Definition is null ? DiscoveryStatus.DefinitionUnavailable : DiscoveryStatus.Discovered,
                    ConversionClassification.AutomaticWithWarning,
                    first);
            }

            var events = group.Where(row => row.EventName is not null).Select(row => row.EventName!).Distinct().ToArray();
            accumulator.Triggers.Add(new TriggerInventory(
                triggerObject.Id,
                parent,
                first.ParentClass,
                first.IsInsteadOf,
                first.IsDisabled,
                first.IsNotForReplication,
                FormatExecuteAs(first.ExecuteAsPrincipalId),
                events,
                group.Where(row => row.IsFirst && row.EventName is not null).Select(row => row.EventName!).Distinct().ToArray(),
                group.Where(row => row.IsLast && row.EventName is not null).Select(row => row.EventName!).Distinct().ToArray()));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!accumulator.ObjectsBySqlId.TryGetValue(objectId, out var source))
            {
                continue;
            }

            var details = string.Join(
                ", ",
                new[]
                {
                    reader.Boolean("is_replicated") ? "transactional publication" : null,
                    reader.Boolean("is_merge_published") ? "merge publication" : null,
                    reader.Boolean("is_sync_tran_subscribed") ? "synchronous subscription" : null,
                    reader.Boolean("has_replication_filter") ? "replication filter" : null,
                    reader.Boolean("is_tracked_by_cdc") ? "CDC" : null
                }.Where(value => value is not null));
            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.ReplicationObject,
                source.SourceSchema,
                $"Replication:{source.SourceName}",
                source.Id,
                ConversionClassification.Unsupported,
                new { source.SourceName, Details = details });
            accumulator.Replication.Add(new ReplicationInventory(
                item.Id,
                source.Id,
                "TABLE",
                source.QualifiedSourceName,
                true,
                details));

            if (reader.Boolean("is_tracked_by_cdc"))
            {
                accumulator.ChangeData.Add(new ChangeDataInventory(
                    source.Id,
                    "CDC",
                    true,
                    null,
                    null,
                    null,
                    null,
                    []));
            }
        }

        if (accumulator.Database.IsEncrypted)
        {
            accumulator.Encryption.Add(new EncryptionInventory(
                null,
                "TRANSPARENT_DATA_ENCRYPTION",
                accumulator.Database.DatabaseName,
                null,
                null,
                true,
                "ENCRYPTED"));
        }
    }

    private static async Task ReadExternalAndPartitioningAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var partitionFunctions = new Dictionary<int, (InventoryObject Item, bool RangeRight, List<string> Boundaries)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var functionId = reader.Int32("function_id");
            if (!partitionFunctions.TryGetValue(functionId, out var function))
            {
                var name = reader.Text("name");
                var item = accumulator.AddSyntheticObject(
                    InventoryObjectType.PartitionFunction,
                    string.Empty,
                    name,
                    null,
                    ConversionClassification.ManualConversion,
                    new { functionId, RangeRight = reader.Boolean("boundary_value_on_right") });
                function = (item, reader.Boolean("boundary_value_on_right"), []);
                partitionFunctions[functionId] = function;
            }

            if (reader.NullableText("boundary_value") is { } boundary)
            {
                function.Boundaries.Add(boundary);
            }
        }

        accumulator.PartitionFunctions.AddRange(partitionFunctions.Values.Select(function =>
            new PartitionFunctionInventory(function.Item.Id, function.RangeRight, function.Boundaries)));

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var schemes = new Dictionary<int, (InventoryObject Item, string FunctionName, List<string> Destinations)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dataSpaceId = reader.Int32("data_space_id");
            if (!schemes.TryGetValue(dataSpaceId, out var scheme))
            {
                var name = reader.Text("name");
                var item = accumulator.AddSyntheticObject(
                    InventoryObjectType.PartitionScheme,
                    string.Empty,
                    name,
                    null,
                    ConversionClassification.ManualConversion,
                    new { dataSpaceId, Function = reader.Text("function_name") });
                scheme = (item, reader.Text("function_name"), []);
                schemes[dataSpaceId] = scheme;
            }

            if (reader.NullableText("destination_name") is { } destination)
            {
                scheme.Destinations.Add(destination);
            }
        }

        accumulator.PartitionSchemes.AddRange(schemes.Values.Select(scheme =>
            new PartitionSchemeInventory(scheme.Item.Id, scheme.FunctionName, scheme.Destinations)));

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.Text("name");
            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.ExternalDataSource,
                string.Empty,
                name,
                null,
                ConversionClassification.ManualConversion,
                new
                {
                    Location = reader.NullableText("location"),
                    Type = reader.NullableText("type_desc"),
                    HasConnectionOptions = !string.IsNullOrWhiteSpace(reader.NullableText("connection_options"))
                });
            accumulator.ExternalDependencies.Add(new ExternalDependencyInventory(
                item.Id,
                null,
                "EXTERNAL_DATA_SOURCE",
                name,
                null,
                null,
                null,
                false,
                reader.NullableText("location") ?? name));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            accumulator.AddSyntheticObject(
                InventoryObjectType.ExternalFileFormat,
                string.Empty,
                reader.Text("name"),
                null,
                ConversionClassification.ManualConversion,
                new
                {
                    Format = reader.NullableText("format_type"),
                    Compression = reader.NullableText("data_compression_desc")
                });
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = accumulator.TryGetObjectId(reader.NullableInt32("referencing_id"));
            if (source is null)
            {
                continue;
            }

            var referencedName = string.Join(
                ".",
                new[]
                {
                    reader.NullableText("referenced_server_name"),
                    reader.NullableText("referenced_database_name"),
                    reader.NullableText("referenced_schema_name"),
                    reader.NullableText("referenced_entity_name")
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            AddExternalDependency(
                accumulator,
                source.Value,
                "FOUR_PART_REFERENCE",
                referencedName,
                reader.NullableText("referenced_server_name"),
                reader.NullableText("referenced_database_name"),
                reader.NullableText("referenced_schema_name"),
                referencedName);
        }
    }

    private static async Task ReadServerTriggersAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var rows = new List<ServerTriggerRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ServerTriggerRow(
                reader.Int32("object_id"),
                reader.Text("name"),
                reader.Boolean("is_disabled"),
                reader.Boolean("is_instead_of_trigger"),
                reader.NullableText("event_name"),
                reader.NullableText("definition"),
                reader.NullableInt32("execute_as_principal_id")));
        }

        foreach (var group in rows.GroupBy(row => row.ObjectId))
        {
            var first = group.First();
            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.ServerTrigger,
                string.Empty,
                first.Name,
                null,
                ConversionClassification.Unsupported,
                first);
            accumulator.Triggers.Add(new TriggerInventory(
                item.Id,
                null,
                "SERVER",
                first.IsInsteadOf,
                first.IsDisabled,
                false,
                FormatExecuteAs(first.ExecuteAsPrincipalId),
                group.Where(row => row.EventName is not null).Select(row => row.EventName!).Distinct().ToArray(),
                [],
                []));
        }
    }

    private static async Task ReadSqlAgentAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var jobs = new Dictionary<Guid, AgentJobRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = reader.Guid("job_id");
            jobs[jobId] = new AgentJobRow(
                jobId,
                reader.Text("name"),
                reader.Boolean("enabled"),
                reader.Text("owner_name"),
                reader.Text("category_name"),
                [],
                []);
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = reader.Guid("job_id");
            if (jobs.TryGetValue(jobId, out var job))
            {
                job.Steps.Add(new SqlAgentStepInventory(
                    reader.Int32("step_id"),
                    reader.Text("step_name"),
                    reader.Text("subsystem"),
                    reader.NullableText("database_name"),
                    reader.NullableText("command") ?? string.Empty,
                    reader.NullableText("proxy_name")));
            }
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = reader.Guid("job_id");
            if (jobs.TryGetValue(jobId, out var job))
            {
                job.Schedules.Add(
                    $"{reader.Text("name")} (type {reader.Int32("freq_type")}, interval {reader.Int32("freq_interval")})");
            }
        }

        foreach (var job in jobs.Values)
        {
            var item = accumulator.AddSyntheticObject(
                InventoryObjectType.SqlAgentJob,
                string.Empty,
                job.Name,
                null,
                ConversionClassification.Unsupported,
                new { job.JobId, job.Enabled, job.Owner, job.Category });
            accumulator.SqlAgentJobs.Add(new SqlAgentJobInventory(
                item.Id,
                job.JobId,
                job.Name,
                job.Enabled,
                job.Owner,
                job.Category,
                job.Steps,
                job.Schedules));
        }
    }

    private static string? FormatExecuteAs(int? principalId) =>
        principalId switch
        {
            null => null,
            -2 => "OWNER",
            _ => principalId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

    private sealed record FullTextRow(int ObjectId, string ChangeTracking, string? Stoplist, string? ColumnName);

    private sealed record TriggerRow(
        int ObjectId,
        string ParentClass,
        int ParentId,
        string Name,
        bool IsInsteadOf,
        bool IsDisabled,
        bool IsNotForReplication,
        string? EventName,
        bool IsFirst,
        bool IsLast,
        string? Definition,
        int? ExecuteAsPrincipalId);

    private sealed record ServerTriggerRow(
        int ObjectId,
        string Name,
        bool IsDisabled,
        bool IsInsteadOf,
        string? EventName,
        string? Definition,
        int? ExecuteAsPrincipalId);

    private sealed record AgentJobRow(
        Guid JobId,
        string Name,
        bool Enabled,
        string Owner,
        string Category,
        List<SqlAgentStepInventory> Steps,
        List<string> Schedules);
}
