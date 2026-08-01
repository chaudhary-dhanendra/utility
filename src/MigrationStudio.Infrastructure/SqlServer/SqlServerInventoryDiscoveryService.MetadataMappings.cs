using Microsoft.Data.SqlClient;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.SqlServer;

public sealed partial class SqlServerInventoryDiscoveryService
{
    private static async Task ReadProgrammableObjectsAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var parametersByObject = new Dictionary<int, List<ModuleParameterInventory>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!parametersByObject.TryGetValue(objectId, out var parameters))
            {
                parameters = [];
                parametersByObject[objectId] = parameters;
            }

            parameters.Add(new ModuleParameterInventory(
                reader.Int32("parameter_id"),
                reader.Text("name"),
                reader.Text("type_schema"),
                reader.Text("type_name"),
                reader.Int16("max_length"),
                reader.Byte("precision"),
                reader.Byte("scale"),
                reader.Boolean("is_output"),
                reader.Boolean("has_default_value"),
                reader.NullableText("default_value"),
                reader.Boolean("is_readonly"),
                reader.Boolean("is_table_type")));
        }

        for (var index = 0; index < accumulator.Modules.Count; index++)
        {
            var module = accumulator.Modules[index];
            var sqlObjectId = accumulator.ObjectsBySqlId.FirstOrDefault(pair => pair.Value.Id == module.ObjectId).Key;
            var resultColumns = accumulator.Columns.Where(column => column.ParentObjectId == module.ObjectId).ToArray();
            accumulator.Modules[index] = module with
            {
                Parameters = parametersByObject.GetValueOrDefault(sqlObjectId) ?? [],
                ResultColumns = resultColumns
            };
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!accumulator.ObjectsBySqlId.ContainsKey(objectId))
            {
                continue;
            }

            accumulator.Sequences.Add(new SequenceInventory(
                accumulator.GetObject(objectId).Id,
                reader.Text("type_schema"),
                reader.Text("type_name"),
                reader.Decimal("start_value"),
                reader.Decimal("increment"),
                reader.Decimal("minimum_value"),
                reader.Decimal("maximum_value"),
                reader.Boolean("is_cycling"),
                reader.Int32("cache_size"),
                reader.NullableDecimal("current_value"),
                reader.Boolean("is_exhausted")));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.Text("schema_name");
            var name = reader.Text("name");
            var isTableType = reader.Boolean("is_table_type");
            var typeObject = accumulator.AddSyntheticObject(
                isTableType ? InventoryObjectType.TableType : InventoryObjectType.UserDefinedType,
                schema,
                name,
                null,
                reader.Boolean("is_assembly_type")
                    ? ConversionClassification.ManualConversion
                    : ConversionClassification.AutomaticWithWarning,
                new
                {
                    UserTypeId = reader.Int32("user_type_id"),
                    BaseType = reader.NullableText("base_type_name"),
                    IsNullable = reader.Boolean("is_nullable"),
                    IsAssemblyType = reader.Boolean("is_assembly_type"),
                    IsTableType = isTableType
                });
            var tableObjectId = reader.NullableInt32("type_table_object_id");
            var tableColumns = tableObjectId is { } typeTableObjectId &&
                               accumulator.ObjectsBySqlId.TryGetValue(typeTableObjectId, out var tableObject)
                ? accumulator.Columns.Where(column => column.ParentObjectId == tableObject.Id).ToArray()
                : [];
            accumulator.UserDefinedTypes.Add(new UserDefinedTypeInventory(
                typeObject.Id,
                isTableType ? "TABLE_TYPE" : reader.Boolean("is_assembly_type") ? "CLR" : "ALIAS",
                reader.NullableText("base_type_schema"),
                reader.NullableText("base_type_name"),
                reader.Boolean("is_nullable"),
                reader.Boolean("is_assembly_type"),
                null,
                tableColumns));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!accumulator.ObjectsBySqlId.ContainsKey(objectId))
            {
                continue;
            }

            var baseObjectName = reader.Text("base_object_name");
            var parts = ParseMultipartName(baseObjectName);
            var item = accumulator.GetObject(objectId);
            var server = parts.Count >= 4 ? parts[^4] : null;
            var database = parts.Count >= 3 ? parts[^3] : null;
            var schema = parts.Count >= 2 ? parts[^2] : null;
            var name = parts.Count >= 1 ? parts[^1] : null;
            accumulator.Synonyms.Add(new SynonymInventory(
                item.Id,
                baseObjectName,
                server,
                database,
                schema,
                name,
                server is not null,
                database is not null && !string.Equals(database, accumulator.DatabaseName, StringComparison.OrdinalIgnoreCase)));
            accumulator.Dependencies.Add(new InventoryDependency(
                item.Id,
                null,
                DependencyKind.Synonym,
                baseObjectName,
                false,
                false,
                server,
                database,
                baseObjectName));
        }
    }

    private static async Task ReadDependenciesAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sourceSqlId = reader.Int32("referencing_id");
            if (!accumulator.ObjectsBySqlId.TryGetValue(sourceSqlId, out var source))
            {
                continue;
            }

            var target = accumulator.TryGetObjectId(reader.NullableInt32("referenced_id"));
            var server = reader.NullableText("referenced_server_name");
            var database = reader.NullableText("referenced_database_name");
            var schema = reader.NullableText("referenced_schema_name");
            var entity = reader.NullableText("referenced_entity_name");
            var nameResolutionAmbiguous = false;
            if (target is null &&
                server is null &&
                (database is null ||
                 database.Equals(accumulator.DatabaseName, StringComparison.OrdinalIgnoreCase)))
            {
                target = accumulator.TryResolveObjectId(
                    schema,
                    entity,
                    source.SourceSchema,
                    out nameResolutionAmbiguous);
            }
            var referencedName = string.Join(
                ".",
                new[] { server, database, schema, entity }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var kind = server is not null
                ? DependencyKind.LinkedServer
                : database is not null && !string.Equals(database, accumulator.DatabaseName, StringComparison.OrdinalIgnoreCase)
                    ? DependencyKind.CrossDatabase
                    : DependencyKind.SqlExpression;
            accumulator.Dependencies.Add(new InventoryDependency(
                source.Id,
                target,
                kind,
                referencedName,
                target is not null,
                reader.Boolean("is_ambiguous") || nameResolutionAmbiguous,
                server,
                database,
                reader.Boolean("is_caller_dependent") ? "Caller-dependent binding" : null));

            if (target is null)
            {
                AddExternalDependency(
                    accumulator,
                    source.Id,
                    kind.ToString(),
                    referencedName,
                    server,
                    database,
                    schema,
                    evidence: referencedName);
            }
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var childId = reader.Int32("object_id");
            var parentId = reader.Int32("parent_object_id");
            if (accumulator.ObjectsBySqlId.TryGetValue(childId, out var child) &&
                accumulator.ObjectsBySqlId.TryGetValue(parentId, out var parent))
            {
                accumulator.Dependencies.Add(new InventoryDependency(
                    child.Id,
                    parent.Id,
                    DependencyKind.ParentChild,
                    parent.QualifiedSourceName,
                    true,
                    false));
            }
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sourceId = reader.Int32("parent_object_id");
            var targetId = reader.Int32("referenced_object_id");
            if (accumulator.ObjectsBySqlId.TryGetValue(sourceId, out var source))
            {
                var target = accumulator.TryGetObjectId(targetId);
                accumulator.Dependencies.Add(new InventoryDependency(
                    source.Id,
                    target,
                    DependencyKind.ForeignKey,
                    target?.ToString() ?? targetId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    target is not null,
                    false));
            }
        }
    }

    private static async Task ReadExtendedPropertiesAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var classDescription = reader.Text("class_desc");
            var majorId = reader.Int32("major_id");
            var minorId = reader.Int32("minor_id");
            InventoryObjectId? targetId = classDescription switch
            {
                "OBJECT_OR_COLUMN" when minorId > 0 => accumulator.TryGetColumnId(majorId, minorId),
                "OBJECT_OR_COLUMN" => accumulator.TryGetObjectId(majorId),
                "SCHEMA" when accumulator.SchemasById.TryGetValue(majorId, out var schema) => schema.InventoryObject.Id,
                "TYPE" => null,
                _ => null
            };

            if (targetId is null)
            {
                continue;
            }

            var property = new ExtendedProperty(
                reader.Text("name"),
                reader.NullableText("property_value"),
                reader.Text("target_level"),
                targetId.Value,
                minorId > 0 ? FindColumnName(accumulator, majorId, minorId) : null);
            accumulator.AddExtendedProperty(targetId.Value, property);
        }
    }

    private static async Task ReadSecurityAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var principals = new List<PrincipalRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            principals.Add(new PrincipalRow(
                reader.Int32("principal_id"),
                reader.Text("name"),
                reader.Text("type_desc"),
                reader.Text("authentication_type"),
                reader.NullableText("default_schema_name"),
                reader.Boolean("is_fixed_role"),
                reader.Boolean("is_orphaned")));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var rolesByMember = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var member = reader.Text("member_name");
            if (!rolesByMember.TryGetValue(member, out var roles))
            {
                roles = [];
                rolesByMember[member] = roles;
            }

            roles.Add(reader.Text("role_name"));
        }

        foreach (var principal in principals)
        {
            var objectType = principal.TypeDescription.Contains("ROLE", StringComparison.OrdinalIgnoreCase)
                ? principal.TypeDescription.Contains("APPLICATION", StringComparison.OrdinalIgnoreCase)
                    ? InventoryObjectType.ApplicationRole
                    : InventoryObjectType.Role
                : InventoryObjectType.User;
            var item = accumulator.AddSyntheticObject(
                objectType,
                string.Empty,
                principal.Name,
                null,
                ConversionClassification.ManualConversion,
                principal);
            accumulator.SecurityPrincipals.Add(new SecurityPrincipalInventory(
                item.Id,
                principal.PrincipalId,
                principal.Name,
                principal.TypeDescription,
                principal.AuthenticationType,
                principal.DefaultSchema,
                principal.IsFixedRole,
                principal.IsOrphaned,
                rolesByMember.GetValueOrDefault(principal.Name) ?? []));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var targetSchema = reader.NullableText("target_schema");
            var targetName = reader.NullableText("target_object");
            var target = targetName is null
                ? null
                : accumulator.ObjectsBySqlId.Values.FirstOrDefault(item =>
                    string.Equals(item.SourceSchema, targetSchema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.SourceName, targetName, StringComparison.OrdinalIgnoreCase));
            var permissionName = reader.Text("permission_name");
            var grantee = reader.Text("grantee");
            var permissionObject = accumulator.AddSyntheticObject(
                InventoryObjectType.Permission,
                targetSchema ?? string.Empty,
                $"{grantee}:{permissionName}:{reader.Int32("major_id")}:{reader.Int32("minor_id")}",
                target?.Id,
                ConversionClassification.ManualConversion,
                new { permissionName, grantee, Target = targetName });
            accumulator.Permissions.Add(new PermissionInventory(
                permissionObject.Id,
                reader.Text("state_desc"),
                permissionName,
                reader.Text("class_desc"),
                grantee,
                reader.Text("grantor"),
                target?.Id,
                reader.NullableText("column_name")));
        }
    }

    private static void AddExternalDependency(
        InventoryAccumulator accumulator,
        InventoryObjectId sourceId,
        string referenceKind,
        string referencedName,
        string? server,
        string? database,
        string? schema,
        string evidence)
    {
        var item = accumulator.AddSyntheticObject(
            InventoryObjectType.ExternalDataSource,
            schema ?? string.Empty,
            referencedName.Length == 0 ? "Unresolved reference" : referencedName,
            sourceId,
            ConversionClassification.ManualConversion,
            new { referenceKind, referencedName, server, database });
        accumulator.ExternalDependencies.Add(new ExternalDependencyInventory(
            item.Id,
            sourceId,
            referenceKind,
            referencedName,
            server,
            database,
            schema,
            false,
            evidence));
        accumulator.Findings.Add(new InventoryFinding(
            "DEPENDENCY.UNRESOLVED",
            FindingSeverity.Warning,
            $"Dependency '{referencedName}' could not be resolved inside the source database.",
            sourceId,
            evidence));
    }

    private static List<string> ParseMultipartName(string value)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var bracketed = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (bracketed && character == ']' && index + 1 < value.Length && value[index + 1] == ']')
            {
                current.Append(']');
                index++;
            }
            else if (character == '[' && !bracketed)
            {
                bracketed = true;
            }
            else if (character == ']' && bracketed)
            {
                bracketed = false;
            }
            else if (character == '.' && !bracketed)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        parts.Add(current.ToString().Trim());
        return parts;
    }

    private sealed record PrincipalRow(
        int PrincipalId,
        string Name,
        string TypeDescription,
        string AuthenticationType,
        string? DefaultSchema,
        bool IsFixedRole,
        bool IsOrphaned);
}
