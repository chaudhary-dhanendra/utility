using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Discovery;

public interface ISourceObjectScopePolicy
{
    bool IsSystemSchema(string? schemaName);

    bool IsBuiltInPrincipal(string? principalName);

    bool IsUserMigrationObject(InventoryObject item);
}

public sealed class SqlServerUserObjectScopePolicy : ISourceObjectScopePolicy
{
    private static readonly HashSet<string> SystemSchemas = new(
        [
            "sys",
            "INFORMATION_SCHEMA",
            "guest",
            "db_owner",
            "db_accessadmin",
            "db_securityadmin",
            "db_ddladmin",
            "db_backupoperator",
            "db_datareader",
            "db_datawriter",
            "db_denydatareader",
            "db_denydatawriter"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BuiltInPrincipals = new(
        [
            "dbo",
            "guest",
            "INFORMATION_SCHEMA",
            "sys",
            "public",
            "db_owner",
            "db_accessadmin",
            "db_securityadmin",
            "db_ddladmin",
            "db_backupoperator",
            "db_datareader",
            "db_datawriter",
            "db_denydatareader",
            "db_denydatawriter"
        ],
        StringComparer.OrdinalIgnoreCase);

    public bool IsSystemSchema(string? schemaName) =>
        !string.IsNullOrWhiteSpace(schemaName) && SystemSchemas.Contains(schemaName);

    public bool IsBuiltInPrincipal(string? principalName) =>
        !string.IsNullOrWhiteSpace(principalName) && BuiltInPrincipals.Contains(principalName);

    public bool IsUserMigrationObject(InventoryObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsSystemObject || IsSystemSchema(item.SourceSchema))
        {
            return false;
        }

        return item.ObjectType switch
        {
            InventoryObjectType.Schema => !IsSystemSchema(item.SourceName),
            InventoryObjectType.Role or InventoryObjectType.User or
                InventoryObjectType.ApplicationRole => !IsBuiltInPrincipal(item.SourceName),
            InventoryObjectType.Permission => !string.IsNullOrWhiteSpace(item.SourceSchema) &&
                !IsSystemSchema(item.SourceSchema),
            InventoryObjectType.ExternalTable or InventoryObjectType.SqlAgentJob or
                InventoryObjectType.ServerTrigger or
                InventoryObjectType.ReplicationObject or InventoryObjectType.ServiceBrokerObject or
                InventoryObjectType.FullTextCatalog or InventoryObjectType.FullTextIndex or
                InventoryObjectType.PartitionFunction or InventoryObjectType.PartitionScheme or
                InventoryObjectType.Assembly or InventoryObjectType.ExternalDataSource or
                InventoryObjectType.ExternalFileFormat or
                InventoryObjectType.DatabaseScopedCredential or InventoryObjectType.EncryptionKey or
                InventoryObjectType.Certificate =>
                false,
            _ => !string.IsNullOrWhiteSpace(item.SourceSchema) ||
                item.ObjectType == InventoryObjectType.DatabaseTrigger
        };
    }
}
