namespace MigrationStudio.Infrastructure.SqlServer;

internal static class SqlServerCatalogQueries
{
    public const string ServerMetadata = """
        SELECT
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS product_version,
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')) AS product_level,
            CONVERT(nvarchar(256), SERVERPROPERTY('Edition')) AS edition,
            CONVERT(int, SERVERPROPERTY('EngineEdition')) AS engine_edition;
        """;

    public const string DatabaseMetadata = """
        SELECT
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS product_version,
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')) AS product_level,
            CONVERT(nvarchar(256), SERVERPROPERTY('Edition')) AS edition,
            CONVERT(int, SERVERPROPERTY('EngineEdition')) AS engine_edition,
            d.name, d.database_id, SUSER_SNAME(d.owner_sid) AS owner_name,
            d.compatibility_level, d.collation_name, d.containment_desc, d.recovery_model_desc,
            d.is_read_only, d.snapshot_isolation_state_desc, d.is_read_committed_snapshot_on,
            d.is_ansi_null_default_on, d.is_ansi_nulls_on, d.is_ansi_padding_on,
            d.is_ansi_warnings_on, d.is_quoted_identifier_on, d.is_recursive_triggers_on,
            d.is_trustworthy_on, d.is_broker_enabled, d.is_encrypted,
            CASE WHEN OBJECT_ID(N'sys.change_tracking_databases') IS NULL THEN CONVERT(bit, 0)
                 WHEN EXISTS (SELECT 1 FROM sys.change_tracking_databases ctd WHERE ctd.database_id = d.database_id)
                 THEN CONVERT(bit, 1) ELSE CONVERT(bit, 0) END AS is_change_tracking_enabled,
            COALESCE((SELECT TOP (1) actual_state_desc FROM sys.database_query_store_options), N'OFF') AS query_store_state
        FROM sys.databases d
        WHERE d.database_id = DB_ID();

        SELECT name, value, value_for_secondary, is_value_default
        FROM sys.database_scoped_configurations
        ORDER BY name;

        SELECT
            df.file_id, df.name, df.physical_name, df.type_desc,
            COALESCE(ds.name, N'') AS data_space_name,
            CONVERT(bigint, df.size) * 8192 AS size_bytes,
            CASE WHEN df.type = 0 THEN CONVERT(bigint, FILEPROPERTY(df.name, 'SpaceUsed')) * 8192 ELSE NULL END AS used_bytes,
            df.is_percent_growth, CONVERT(bigint, df.growth) AS growth,
            CASE WHEN df.max_size = -1 THEN CONVERT(bigint, -1)
                 ELSE CONVERT(bigint, df.max_size) * 8192 END AS max_size_bytes,
            df.state_desc
        FROM sys.database_files df
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id = df.data_space_id
        ORDER BY df.file_id;

        SELECT
            fg.data_space_id, fg.name, fg.is_default, fg.is_read_only, fg.type_desc,
            COUNT(df.file_id) AS file_count
        FROM sys.filegroups fg
        LEFT JOIN sys.database_files df ON df.data_space_id = fg.data_space_id
        GROUP BY fg.data_space_id, fg.name, fg.is_default, fg.is_read_only, fg.type_desc
        ORDER BY fg.data_space_id;
        """;

    public const string Schemas = """
        SELECT
            s.schema_id, s.name, USER_NAME(s.principal_id) AS owner_name,
            COUNT(o.object_id) AS object_count,
            CASE WHEN s.name IN (
                N'sys', N'INFORMATION_SCHEMA', N'guest', N'db_owner',
                N'db_accessadmin', N'db_securityadmin', N'db_ddladmin',
                N'db_backupoperator', N'db_datareader', N'db_datawriter',
                N'db_denydatareader', N'db_denydatawriter')
              THEN CONVERT(bit, 1) ELSE CONVERT(bit, 0) END AS is_system_schema
        FROM sys.schemas s
        LEFT JOIN sys.objects o ON o.schema_id = s.schema_id AND o.is_ms_shipped = 0
        GROUP BY s.schema_id, s.name, s.principal_id
        ORDER BY s.name;
        """;

    private const string ObjectsSql = """
        SELECT
            o.object_id, o.parent_object_id, o.schema_id, s.name AS schema_name,
            o.name, o.type, o.type_desc, o.create_date, o.modify_date, o.is_ms_shipped,
            sm.definition, sm.uses_ansi_nulls, sm.uses_quoted_identifier, sm.is_schema_bound,
            sm.is_recompiled, sm.execute_as_principal_id, sm.uses_native_compilation,
            OBJECTPROPERTYEX(o.object_id, 'IsEncrypted') AS is_encrypted
        FROM sys.objects o
        INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.sql_modules sm ON sm.object_id = o.object_id
        WHERE o.is_ms_shipped = 0
          AND o.type NOT IN ('S', 'IT')
          AND s.name NOT IN (
              N'sys', N'INFORMATION_SCHEMA', N'guest', N'db_owner',
              N'db_accessadmin', N'db_securityadmin', N'db_ddladmin',
              N'db_backupoperator', N'db_datareader', N'db_datawriter',
              N'db_denydatareader', N'db_denydatawriter')
        ORDER BY o.object_id;
        """;

    public static string Objects(int majorVersion)
    {
        EnsureSupportedVersion(majorVersion);
        return ObjectsSql;
    }

    public static string Tables(int majorVersion)
    {
        EnsureSupportedVersion(majorVersion);
        return $"""
        SELECT
            t.object_id, t.is_memory_optimized, t.durability_desc, t.is_filetable,
            t.temporal_type, t.history_table_id, t.is_remote_data_archive_enabled,
            {(majorVersion >= 14 ? "t.is_node" : "CONVERT(bit, 0)")} AS is_node,
            {(majorVersion >= 14 ? "t.is_edge" : "CONVERT(bit, 0)")} AS is_edge,
            {(majorVersion >= 16 ? "CASE WHEN t.ledger_type > 0 THEN CONVERT(bit, 1) ELSE CONVERT(bit, 0) END" : "CONVERT(bit, 0)")} AS is_ledger,
            CASE WHEN t.lock_escalation_desc = N'DISABLE' THEN CONVERT(bit, 1) ELSE CONVERT(bit, 0) END AS lock_escalation_disabled,
            t.lock_on_bulk_load,
            COALESCE(SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END), 0) AS row_count,
            COALESCE(SUM(CONVERT(bigint, au.total_pages)) * 8192, 0) AS reserved_bytes,
            COALESCE(SUM(CONVERT(bigint, au.used_pages)) * 8192, 0) AS used_bytes
        FROM sys.tables t
        INNER JOIN sys.objects user_object
            ON user_object.object_id = t.object_id
           AND user_object.is_ms_shipped = 0
        LEFT JOIN sys.partitions p ON p.object_id = t.object_id
        LEFT JOIN sys.allocation_units au ON au.container_id =
            CASE WHEN au.type IN (1, 3) THEN p.hobt_id ELSE p.partition_id END
        GROUP BY t.object_id, t.is_memory_optimized, t.durability_desc, t.is_filetable,
                 t.temporal_type, t.history_table_id, t.is_remote_data_archive_enabled,
                 {(majorVersion >= 14 ? "t.is_node, t.is_edge," : string.Empty)}
                 {(majorVersion >= 16 ? "t.ledger_type," : string.Empty)}
                 t.lock_escalation_desc, t.lock_on_bulk_load
        ORDER BY t.object_id;

        SELECT
            et.object_id, et.location, eds.name AS data_source_name, eff.name AS file_format_name,
            CASE et.reject_type
                WHEN 0 THEN N'VALUE'
                WHEN 1 THEN N'PERCENTAGE'
                ELSE NULL
            END AS reject_type_desc,
            et.reject_value
        FROM sys.external_tables et
        LEFT JOIN sys.external_data_sources eds ON eds.data_source_id = et.data_source_id
        LEFT JOIN sys.external_file_formats eff ON eff.file_format_id = et.file_format_id
        ORDER BY et.object_id;
        """;
    }

    private const string ColumnsSql = """
        SELECT
            c.object_id, c.column_id, c.name,
            st.name AS system_type_name, ut.name AS user_type_name, uts.name AS type_schema,
            c.max_length, c.precision, c.scale, c.collation_name, c.is_nullable,
            c.is_identity, ic.seed_value, ic.increment_value, ic.last_value, ic.is_not_for_replication,
            c.is_computed, cc.definition AS computed_definition, cc.is_persisted,
            COLUMNPROPERTY(c.object_id, c.name, 'IsDeterministic') AS is_deterministic,
            c.is_sparse, c.is_column_set, c.is_rowguidcol, c.is_filestream,
            c.generated_always_type, c.is_hidden,
            CASE WHEN mc.column_id IS NULL THEN CONVERT(bit, 0) ELSE CONVERT(bit, 1) END AS is_masked,
            mc.masking_function,
            c.encryption_type_desc, c.encryption_algorithm_name, cek.name AS column_encryption_key,
            xsc.name AS xml_schema_collection,
            dc.name AS default_constraint_name, dc.definition AS default_definition,
            OBJECT_SCHEMA_NAME(c.rule_object_id) + N'.' + OBJECT_NAME(c.rule_object_id) AS rule_name
        FROM sys.columns c
        INNER JOIN sys.types ut ON ut.user_type_id = c.user_type_id
        INNER JOIN sys.schemas uts ON uts.schema_id = ut.schema_id
        INNER JOIN sys.types st ON st.user_type_id = c.system_type_id AND st.user_type_id = st.system_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.masked_columns mc ON mc.object_id = c.object_id AND mc.column_id = c.column_id AND mc.is_masked = 1
        LEFT JOIN sys.column_encryption_keys cek ON cek.column_encryption_key_id = c.column_encryption_key_id
        LEFT JOIN sys.xml_schema_collections xsc ON xsc.xml_collection_id = c.xml_collection_id
        LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        WHERE OBJECTPROPERTY(c.object_id, 'IsMSShipped') = 0
        ORDER BY c.object_id, c.column_id;
        """;

    public static string Columns(int majorVersion)
    {
        EnsureSupportedVersion(majorVersion);
        return ColumnsSql;
    }

    public const string Constraints = """
        SELECT
            kc.object_id, kc.parent_object_id, kc.name, kc.type,
            ic.key_ordinal AS ordinal, c.name AS column_name, ic.is_descending_key,
            i.type_desc, i.fill_factor, ds.name AS data_space_name, i.filter_definition
        FROM sys.key_constraints kc
        INNER JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
        INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
        INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
        ORDER BY kc.object_id, ic.key_ordinal;

        SELECT
            cc.object_id, cc.parent_object_id, cc.name, cc.parent_column_id, cc.definition,
            cc.is_disabled, cc.is_not_trusted, cc.is_not_for_replication
        FROM sys.check_constraints cc
        ORDER BY cc.object_id;

        SELECT
            fk.object_id, fk.parent_object_id, fk.referenced_object_id, fk.name,
            fkc.constraint_column_id AS ordinal,
            pc.name AS parent_column, rc.name AS referenced_column,
            fk.delete_referential_action_desc, fk.update_referential_action_desc,
            fk.is_disabled, fk.is_not_trusted, fk.is_not_for_replication
        FROM sys.foreign_keys fk
        INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        INNER JOIN sys.columns pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fkc.parent_column_id
        INNER JOIN sys.columns rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fk.object_id, fkc.constraint_column_id;

        SELECT object_id, parent_object_id, name, parent_column_id, definition
        FROM sys.default_constraints
        ORDER BY object_id;
        """;

    public const string Indexes = """
        SELECT
            i.object_id, i.index_id, COALESCE(i.name, N'HEAP') AS name, i.type, i.type_desc,
            i.is_unique, i.is_primary_key, i.is_unique_constraint, i.is_disabled,
            i.has_filter, i.filter_definition, i.fill_factor, ds.name AS data_space_name,
            ic.key_ordinal, c.name AS column_name, ic.is_descending_key, ic.is_included_column
        FROM sys.indexes i
        INNER JOIN sys.objects o ON o.object_id = i.object_id AND o.is_ms_shipped = 0
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
        LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.is_hypothetical = 0
        ORDER BY i.object_id, i.index_id, ic.key_ordinal, ic.index_column_id;

        SELECT
            p.object_id, p.index_id, p.partition_number, p.rows,
            p.data_compression_desc, ds.name AS data_space_name,
            ps.name AS partition_scheme, pc.name AS partition_column
        FROM sys.partitions p
        INNER JOIN sys.objects o ON o.object_id = p.object_id AND o.is_ms_shipped = 0
        LEFT JOIN sys.indexes i ON i.object_id = p.object_id AND i.index_id = p.index_id
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
        LEFT JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
        LEFT JOIN sys.index_columns pic ON pic.object_id = i.object_id AND pic.index_id = i.index_id AND pic.partition_ordinal = 1
        LEFT JOIN sys.columns pc ON pc.object_id = pic.object_id AND pc.column_id = pic.column_id
        ORDER BY p.object_id, p.index_id, p.partition_number;
        """;

    public const string ProgrammableObjects = """
        SELECT
            p.object_id, p.parameter_id, p.name, ts.name AS type_schema, t.name AS type_name,
            p.max_length, p.precision, p.scale, p.is_output, p.has_default_value,
            CONVERT(nvarchar(4000), p.default_value) AS default_value,
            p.is_readonly, t.is_table_type
        FROM sys.parameters p
        INNER JOIN sys.types t ON t.user_type_id = p.user_type_id
        INNER JOIN sys.schemas ts ON ts.schema_id = t.schema_id
        WHERE p.object_id > 0
        ORDER BY p.object_id, p.parameter_id;

        SELECT
            seq.object_id, ts.name AS type_schema, t.name AS type_name,
            CONVERT(decimal(38, 0), seq.start_value) AS start_value,
            CONVERT(decimal(38, 0), seq.increment) AS increment,
            CONVERT(decimal(38, 0), seq.minimum_value) AS minimum_value,
            CONVERT(decimal(38, 0), seq.maximum_value) AS maximum_value,
            seq.is_cycling, seq.cache_size,
            CONVERT(decimal(38, 0), seq.current_value) AS current_value,
            seq.is_exhausted
        FROM sys.sequences seq
        INNER JOIN sys.types t ON t.user_type_id = seq.user_type_id
        INNER JOIN sys.schemas ts ON ts.schema_id = t.schema_id
        ORDER BY seq.object_id;

        SELECT
            t.user_type_id, t.name, s.name AS schema_name, t.is_nullable,
            t.is_assembly_type, t.is_table_type,
            bt.name AS base_type_name, bts.name AS base_type_schema,
            at.assembly_id, tt.type_table_object_id
        FROM sys.types t
        INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN sys.types bt ON bt.user_type_id = t.system_type_id AND bt.user_type_id = bt.system_type_id
        LEFT JOIN sys.schemas bts ON bts.schema_id = bt.schema_id
        LEFT JOIN sys.assembly_types at ON at.user_type_id = t.user_type_id
        LEFT JOIN sys.table_types tt ON tt.user_type_id = t.user_type_id
        WHERE t.is_user_defined = 1 OR t.is_table_type = 1 OR t.is_assembly_type = 1
        ORDER BY s.name, t.name;

        SELECT object_id, base_object_name
        FROM sys.synonyms
        ORDER BY object_id;
        """;

    public const string Dependencies = """
        SELECT
            sed.referencing_id, sed.referenced_id, sed.referenced_server_name,
            sed.referenced_database_name, sed.referenced_schema_name, sed.referenced_entity_name,
            sed.is_ambiguous, sed.is_caller_dependent
        FROM sys.sql_expression_dependencies sed
        WHERE sed.referencing_id IS NOT NULL
        ORDER BY sed.referencing_id;

        SELECT object_id, parent_object_id
        FROM sys.objects
        WHERE parent_object_id <> 0 AND is_ms_shipped = 0;

        SELECT object_id, parent_object_id, referenced_object_id
        FROM sys.foreign_keys;
        """;

    public const string ExtendedProperties = """
        SELECT
            ep.class_desc, ep.major_id, ep.minor_id, ep.name,
            CONVERT(nvarchar(max), ep.value) AS property_value,
            CASE ep.class
                WHEN 0 THEN N'DATABASE'
                WHEN 1 THEN CASE WHEN ep.minor_id = 0 THEN N'OBJECT' ELSE N'COLUMN' END
                WHEN 3 THEN N'SCHEMA'
                WHEN 6 THEN N'TYPE'
                WHEN 7 THEN N'INDEX'
                ELSE ep.class_desc
            END AS target_level
        FROM sys.extended_properties ep
        ORDER BY ep.class, ep.major_id, ep.minor_id, ep.name;
        """;

    public const string Security = """
        SELECT
            dp.principal_id, dp.name, dp.type_desc,
            COALESCE(dp.authentication_type_desc, N'NONE') AS authentication_type,
            dp.default_schema_name, dp.is_fixed_role,
            CASE WHEN dp.sid IS NOT NULL AND dp.type IN ('S', 'U', 'G')
                       AND SUSER_SNAME(dp.sid) IS NULL AND dp.authentication_type <> 2
                 THEN CONVERT(bit, 1) ELSE CONVERT(bit, 0) END AS is_orphaned
        FROM sys.database_principals dp
        WHERE dp.principal_id > 4 AND dp.type NOT IN ('C', 'K')
        ORDER BY dp.principal_id;

        SELECT
            USER_NAME(drm.member_principal_id) AS member_name,
            USER_NAME(drm.role_principal_id) AS role_name
        FROM sys.database_role_members drm;

        SELECT
            p.class_desc, p.major_id, p.minor_id, p.state_desc, p.permission_name,
            USER_NAME(p.grantee_principal_id) AS grantee,
            USER_NAME(p.grantor_principal_id) AS grantor,
            CASE WHEN p.class = 1 THEN OBJECT_SCHEMA_NAME(p.major_id) END AS target_schema,
            CASE WHEN p.class = 1 THEN OBJECT_NAME(p.major_id) END AS target_object,
            CASE WHEN p.class = 1 AND p.minor_id > 0 THEN COL_NAME(p.major_id, p.minor_id) END AS column_name
        FROM sys.database_permissions p
        ORDER BY p.class, p.major_id, p.minor_id, p.grantee_principal_id;
        """;

    private const string AdvancedSql = """
        SELECT
            t.object_id, t.history_table_id,
            start_col.name AS period_start_column, end_col.name AS period_end_column,
            t.temporal_type
        FROM sys.tables t
        LEFT JOIN sys.periods per ON per.object_id = t.object_id
        LEFT JOIN sys.columns start_col ON start_col.object_id = per.object_id AND start_col.column_id = per.start_column_id
        LEFT JOIN sys.columns end_col ON end_col.object_id = per.object_id AND end_col.column_id = per.end_column_id
        WHERE t.temporal_type > 0;

        SELECT object_id, is_track_columns_updated_on
        FROM sys.change_tracking_tables;

        SELECT
            sp.object_id, sp.name, sp.is_enabled, sp.is_schema_bound,
            pred.predicate_type_desc, pred.operation_desc, pred.target_object_id,
            pred.predicate_definition
        FROM sys.security_policies sp
        LEFT JOIN sys.security_predicates pred ON pred.object_id = sp.object_id;

        SELECT
            fc.fulltext_catalog_id, fc.name, fc.is_default, fc.is_accent_sensitivity_on
        FROM sys.fulltext_catalogs fc;

        SELECT
            fi.object_id, fi.change_tracking_state_desc, sl.name AS stoplist_name,
            c.name AS column_name, fic.language_id, fic.statistical_semantics
        FROM sys.fulltext_indexes fi
        LEFT JOIN sys.fulltext_stoplists sl ON sl.stoplist_id = fi.stoplist_id
        LEFT JOIN sys.fulltext_index_columns fic ON fic.object_id = fi.object_id
        LEFT JOIN sys.columns c ON c.object_id = fic.object_id AND c.column_id = fic.column_id
        ORDER BY fi.object_id, fic.column_id;

        SELECT N'MESSAGE_TYPE' AS broker_kind, name, CONVERT(bit, 1) AS is_enabled, NULL AS related_object
        FROM sys.service_message_types
        UNION ALL
        SELECT N'CONTRACT', name, CONVERT(bit, 1), NULL FROM sys.service_contracts
        UNION ALL
        SELECT N'QUEUE', name, is_receive_enabled, OBJECT_SCHEMA_NAME(object_id) FROM sys.service_queues
        UNION ALL
        SELECT N'SERVICE', name, CONVERT(bit, 1), OBJECT_NAME(service_queue_id) FROM sys.services
        UNION ALL
        SELECT N'ROUTE', name, CONVERT(bit, 1), address FROM sys.routes
        UNION ALL
        SELECT N'REMOTE_SERVICE_BINDING', name, CONVERT(bit, 1), remote_service_name FROM sys.remote_service_bindings;

        SELECT name, permission_set_desc, is_visible, create_date
        FROM sys.assemblies
        WHERE is_user_defined = 1;

        SELECT name, credential_identity, create_date, modify_date
        FROM sys.database_scoped_credentials;

        SELECT name, key_store_provider_name, key_path
        FROM sys.column_master_keys;

        SELECT
            cek.name,
            cekv.column_master_key_id,
            cekv.encryption_algorithm_name AS algorithm_name
        FROM sys.column_encryption_keys cek
        INNER JOIN sys.column_encryption_key_values cekv
            ON cekv.column_encryption_key_id = cek.column_encryption_key_id;

        SELECT
            tr.object_id, tr.parent_class_desc, tr.parent_id, tr.name,
            tr.is_instead_of_trigger, tr.is_disabled, tr.is_not_for_replication,
            te.type_desc AS event_name,
            OBJECTPROPERTYEX(tr.object_id, 'ExecIsFirstTrigger') AS is_first,
            OBJECTPROPERTYEX(tr.object_id, 'ExecIsLastTrigger') AS is_last,
            sm.definition, sm.execute_as_principal_id
        FROM sys.triggers tr
        LEFT JOIN sys.trigger_events te ON te.object_id = tr.object_id
        LEFT JOIN sys.sql_modules sm ON sm.object_id = tr.object_id
        WHERE tr.is_ms_shipped = 0
        ORDER BY tr.object_id, te.type_desc;

        SELECT
            t.object_id, t.is_replicated, t.has_replication_filter,
            t.is_merge_published, t.is_sync_tran_subscribed, t.is_tracked_by_cdc
        FROM sys.tables t
        WHERE t.is_replicated = 1 OR t.is_merge_published = 1 OR t.is_sync_tran_subscribed = 1
           OR t.has_replication_filter = 1 OR t.is_tracked_by_cdc = 1;
        """;

    public static string Advanced(int majorVersion)
    {
        EnsureSupportedVersion(majorVersion);
        return AdvancedSql;
    }

    public const string ServerTriggers = """
        SELECT
            tr.object_id, tr.name, tr.is_disabled,
            CONVERT(bit, 0) AS is_instead_of_trigger,
            te.type_desc AS event_name, sm.definition, sm.execute_as_principal_id
        FROM sys.server_triggers tr
        LEFT JOIN sys.server_trigger_events te ON te.object_id = tr.object_id
        LEFT JOIN sys.server_sql_modules sm ON sm.object_id = tr.object_id
        ORDER BY tr.object_id, te.type_desc;
        """;

    public static string ExternalAndPartitioning(int majorVersion)
    {
        EnsureSupportedVersion(majorVersion);
        return $"""
        SELECT pf.function_id, pf.name, pf.boundary_value_on_right, prv.boundary_id,
               CONVERT(nvarchar(4000), prv.value) AS boundary_value
        FROM sys.partition_functions pf
        LEFT JOIN sys.partition_range_values prv ON prv.function_id = pf.function_id
        ORDER BY pf.function_id, prv.boundary_id;

        SELECT ps.data_space_id, ps.name, pf.name AS function_name,
               dds.destination_id, ds.name AS destination_name
        FROM sys.partition_schemes ps
        INNER JOIN sys.partition_functions pf ON pf.function_id = ps.function_id
        LEFT JOIN sys.destination_data_spaces dds ON dds.partition_scheme_id = ps.data_space_id
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id = dds.data_space_id
        ORDER BY ps.data_space_id, dds.destination_id;

        SELECT name, location, type_desc,
               {(majorVersion >= 16 ? "connection_options" : "CONVERT(nvarchar(4000), NULL)")} AS connection_options
        FROM sys.external_data_sources;

        SELECT name, format_type, data_compression AS data_compression_desc
        FROM sys.external_file_formats;

        SELECT
            sed.referencing_id,
            sed.referenced_server_name, sed.referenced_database_name,
            sed.referenced_schema_name, sed.referenced_entity_name
        FROM sys.sql_expression_dependencies sed
        WHERE sed.referenced_server_name IS NOT NULL OR sed.referenced_database_name IS NOT NULL;
        """;
    }

    public const string SqlAgent = """
        SELECT j.job_id, j.name, j.enabled, SUSER_SNAME(j.owner_sid) AS owner_name,
               c.name AS category_name
        FROM msdb.dbo.sysjobs j
        INNER JOIN msdb.dbo.syscategories c ON c.category_id = j.category_id
        ORDER BY j.name;

        SELECT s.job_id, s.step_id, s.step_name, s.subsystem, s.database_name,
               s.command, p.name AS proxy_name
        FROM msdb.dbo.sysjobsteps s
        LEFT JOIN msdb.dbo.sysproxies p ON p.proxy_id = s.proxy_id
        ORDER BY s.job_id, s.step_id;

        SELECT js.job_id, sch.name, sch.freq_type, sch.freq_interval,
               sch.freq_subday_type, sch.freq_subday_interval,
               sch.active_start_date, sch.active_start_time
        FROM msdb.dbo.sysjobschedules js
        INNER JOIN msdb.dbo.sysschedules sch ON sch.schedule_id = js.schedule_id
        ORDER BY js.job_id, sch.name;
        """;

    private static void EnsureSupportedVersion(int majorVersion)
    {
        if (majorVersion < 13)
        {
            throw new NotSupportedException(
                $"SQL Server major version {majorVersion} is unsupported by catalog discovery.");
        }
    }
}
