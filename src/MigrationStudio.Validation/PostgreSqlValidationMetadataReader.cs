using MigrationStudio.Application.Deployment;
using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Validation;
using Npgsql;

namespace MigrationStudio.Validation;

public sealed class PostgreSqlValidationMetadataReader : IPostgreSqlValidationMetadataReader
{
    public async Task<TargetDatabaseSnapshot> ReadAsync(
        string connectionString,
        ValidationScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var identity = await ReadIdentityAsync(connection, cancellationToken).ConfigureAwait(false);
        var objects = await ReadObjectsAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        var columns = await ReadColumnsAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        var constraints = await ReadConstraintsAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        var sequences = await ReadSequencesAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        var roles = await ReadStringsAsync(
            connection,
            "SELECT rolname FROM pg_roles WHERE rolname !~ '^pg_' ORDER BY rolname",
            cancellationToken).ConfigureAwait(false);
        var memberships = await ReadStringsAsync(
            connection,
            """
            SELECT member.rolname || ' -> ' || role.rolname
            FROM pg_auth_members m
            JOIN pg_roles role ON role.oid = m.roleid
            JOIN pg_roles member ON member.oid = m.member
            ORDER BY 1
            """,
            cancellationToken).ConfigureAwait(false);
        var privileges = await ReadStringsAsync(
            connection,
            $"""
            SELECT grantee || ':' || table_schema || '.' || table_name || ':' || privilege_type
            FROM information_schema.table_privileges
            WHERE {PostgreSqlSystemSchemaPolicy.CatalogPredicate("table_schema")}
            ORDER BY 1
            """,
            cancellationToken).ConfigureAwait(false);

        return new TargetDatabaseSnapshot
        {
            Identity = identity,
            Objects = objects,
            Columns = columns,
            Constraints = constraints,
            Indexes = indexes,
            Sequences = sequences,
            Roles = roles,
            RoleMemberships = memberships,
            Privileges = privileges
        };
    }

    private static async Task<string> ReadIdentityAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_database() || '@' || inet_server_addr()::text || ':' || inet_server_port()::text",
            connection);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    }

    private static async Task<IReadOnlyList<TargetObjectMetadata>> ReadObjectsAsync(
        NpgsqlConnection connection,
        ValidationScope scope,
        CancellationToken cancellationToken)
    {
        var systemSchemaPredicate = PostgreSqlSystemSchemaPolicy.CatalogPredicate("n.nspname");
        var sql =
            $"""
            SELECT n.nspname, c.relname,
                   CASE c.relkind WHEN 'r' THEN 'Table' WHEN 'p' THEN 'Table'
                     WHEN 'v' THEN 'View' WHEN 'm' THEN 'View' WHEN 'S' THEN 'Sequence'
                     WHEN 'i' THEN 'Index' WHEN 'I' THEN 'Index' ELSE c.relkind::text END,
                   CASE WHEN c.relkind IN ('v','m') THEN pg_get_viewdef(c.oid, true) ELSE NULL END,
                   c.relkind NOT IN ('i','I') OR i.indisvalid,
                   true,
                   owner.rolname, obj_description(c.oid, 'pg_class')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_roles owner ON owner.oid = c.relowner
            LEFT JOIN pg_index i ON i.indexrelid = c.oid
            WHERE {systemSchemaPredicate}
              AND c.relkind IN ('r','p','v','m','S','i','I')
            UNION ALL
            SELECT n.nspname, n.nspname, 'Schema', NULL, true, true, owner.rolname,
                   obj_description(n.oid, 'pg_namespace')
            FROM pg_namespace n
            JOIN pg_roles owner ON owner.oid = n.nspowner
            WHERE {systemSchemaPredicate}
            UNION ALL
            SELECT n.nspname, p.proname,
                   CASE p.prokind WHEN 'p' THEN 'StoredProcedure' ELSE 'Function' END,
                   pg_get_functiondef(p.oid), true, true, owner.rolname,
                   obj_description(p.oid, 'pg_proc')
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_roles owner ON owner.oid = p.proowner
            WHERE {systemSchemaPredicate}
            UNION ALL
            SELECT n.nspname, t.tgname, 'Trigger', pg_get_triggerdef(t.oid, true),
                   true, NOT t.tgisinternal AND t.tgenabled <> 'D', owner.rolname,
                   obj_description(t.oid, 'pg_trigger')
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_roles owner ON owner.oid = c.relowner
            WHERE NOT t.tgisinternal
              AND {systemSchemaPredicate}
            UNION ALL
            SELECT n.nspname, typ.typname, 'UserDefinedType',
                   format_type(typ.oid, NULL), true, true, owner.rolname,
                   obj_description(typ.oid, 'pg_type')
            FROM pg_type typ
            JOIN pg_namespace n ON n.oid = typ.typnamespace
            JOIN pg_roles owner ON owner.oid = typ.typowner
            WHERE {systemSchemaPredicate}
              AND typ.typtype IN ('d','c','e','r')
            ORDER BY 1, 3, 2
            """;
        var items = new List<TargetObjectMetadata>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var definition = reader.IsDBNull(3) ? null : reader.GetString(3);
            items.Add(new TargetObjectMetadata(
                schema,
                reader.GetString(1),
                reader.GetString(2),
                definition,
                definition is null ? null : Hashing.Sha256(definition),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<TargetColumnMetadata>> ReadColumnsAsync(
        NpgsqlConnection connection,
        ValidationScope scope,
        CancellationToken cancellationToken)
    {
        var sql =
            $"""
            SELECT table_schema, table_name, column_name, ordinal_position,
                   CASE
                     WHEN data_type = 'USER-DEFINED' THEN udt_schema || '.' || udt_name
                     WHEN data_type IN ('character varying','character') AND character_maximum_length IS NOT NULL
                       THEN data_type || '(' || character_maximum_length || ')'
                     WHEN data_type = 'numeric' AND numeric_precision IS NOT NULL
                       THEN data_type || '(' || numeric_precision || ',' || numeric_scale || ')'
                     ELSE data_type
                   END,
                   character_maximum_length, numeric_precision, numeric_scale,
                   is_nullable = 'YES', is_identity = 'YES', is_generated <> 'NEVER', column_default,
                   (SELECT col_description(c.oid, a.attnum)
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = column_name
                    WHERE n.nspname = table_schema AND c.relname = table_name
                    LIMIT 1)
            FROM information_schema.columns
            WHERE {PostgreSqlSystemSchemaPolicy.CatalogPredicate("table_schema")}
            ORDER BY table_schema, table_name, ordinal_position
            """;
        var items = new List<TargetColumnMetadata>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            items.Add(new TargetColumnMetadata(
                schema, reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<TargetConstraintMetadata>> ReadConstraintsAsync(
        NpgsqlConnection connection,
        ValidationScope scope,
        CancellationToken cancellationToken)
    {
        var sql =
            $"""
            SELECT n.nspname, c.relname, con.conname,
                   CASE con.contype WHEN 'p' THEN 'PrimaryKey' WHEN 'u' THEN 'Unique'
                     WHEN 'f' THEN 'ForeignKey' WHEN 'c' THEN 'Check' ELSE con.contype::text END,
                   COALESCE((SELECT array_agg(a.attname ORDER BY k.ordinality)
                     FROM unnest(con.conkey) WITH ORDINALITY k(attnum, ordinality)
                     JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum), ARRAY[]::name[]),
                   rn.nspname, rc.relname,
                   COALESCE((SELECT array_agg(a.attname ORDER BY k.ordinality)
                     FROM unnest(con.confkey) WITH ORDINALITY k(attnum, ordinality)
                     JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum), ARRAY[]::name[]),
                   con.convalidated, pg_get_constraintdef(con.oid, true)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_class rc ON rc.oid = con.confrelid
            LEFT JOIN pg_namespace rn ON rn.oid = rc.relnamespace
            WHERE {PostgreSqlSystemSchemaPolicy.CatalogPredicate("n.nspname")}
            ORDER BY n.nspname, c.relname, con.conname
            """;
        var items = new List<TargetConstraintMetadata>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            items.Add(new TargetConstraintMetadata(
                schema, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<string[]>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<string[]>(7),
                reader.GetBoolean(8), reader.GetString(9)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<TargetIndexMetadata>> ReadIndexesAsync(
        NpgsqlConnection connection,
        ValidationScope scope,
        CancellationToken cancellationToken)
    {
        var sql =
            $"""
            SELECT n.nspname, t.relname, i.relname, ix.indisunique, ix.indisvalid,
                   COALESCE(array_agg(a.attname ORDER BY k.ordinality)
                     FILTER (WHERE k.ordinality <= ix.indnkeyatts), ARRAY[]::name[]),
                   COALESCE(array_agg(a.attname ORDER BY k.ordinality)
                     FILTER (WHERE k.ordinality > ix.indnkeyatts), ARRAY[]::name[]),
                   pg_get_expr(ix.indpred, ix.indrelid)
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            LEFT JOIN unnest(ix.indkey) WITH ORDINALITY k(attnum, ordinality) ON true
            LEFT JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE {PostgreSqlSystemSchemaPolicy.CatalogPredicate("n.nspname")}
            GROUP BY n.nspname, t.relname, i.relname, ix.indisunique, ix.indisvalid,
                     ix.indnkeyatts, ix.indpred, ix.indrelid
            ORDER BY 1, 2, 3
            """;
        var items = new List<TargetIndexMetadata>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            items.Add(new TargetIndexMetadata(
                schema, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4),
                reader.GetFieldValue<string[]>(5), reader.GetFieldValue<string[]>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<TargetSequenceMetadata>> ReadSequencesAsync(
        NpgsqlConnection connection,
        ValidationScope scope,
        CancellationToken cancellationToken)
    {
        var sql =
            $"""
            SELECT schemaname, sequencename, COALESCE(last_value, start_value)::numeric,
                   increment_by::numeric, min_value::numeric, max_value::numeric, cycle
            FROM pg_sequences
            WHERE {PostgreSqlSystemSchemaPolicy.CatalogPredicate("schemaname")}
            ORDER BY schemaname, sequencename
            """;
        var items = new List<TargetSequenceMetadata>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            items.Add(new TargetSequenceMetadata(
                schema, reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.GetBoolean(6)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var items = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(reader.GetString(0));
        }
        return items;
    }
}

internal static class Hashing
{
    public static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
