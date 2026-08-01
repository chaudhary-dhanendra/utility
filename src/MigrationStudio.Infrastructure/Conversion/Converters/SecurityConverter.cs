using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed class SecurityConverter : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType is InventoryObjectType.User or InventoryObjectType.Role or
            InventoryObjectType.ApplicationRole or InventoryObjectType.Permission;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Options.SecurityStrategy == SecurityConversionStrategy.ReportOnly)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "Security strategy is ReportOnly; no role or grant statement is emitted.",
                $"-- Review SQL Server security principal or permission {source.QualifiedSourceName}.",
                "report-only security"));
        }

        if (source.ObjectType == InventoryObjectType.Permission)
        {
            return Task.FromResult(ConvertPermission(source, context));
        }

        var principal = context.Inventory.SecurityPrincipals.FirstOrDefault(item => item.ObjectId == source.Id);
        if (principal is null)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source, "Security principal metadata is missing.", $"-- Manual role mapping required for {source.SourceName}.", "missing principal"));
        }

        var role = context.Identifiers.MapObject(source).Name;
        var statements = new List<string> { $"CREATE ROLE {role} NOLOGIN;" };
        statements.AddRange(principal.RoleMemberships.Select(membership =>
            $"GRANT {context.Identifiers.QuoteIdentifier(membership)} TO {role};"));
        var findings = new List<InventoryFinding>
        {
            ConversionRuleSupport.Finding(
                source,
                "SECURITY.NO_PASSWORDS",
                FindingSeverity.Information,
                "Authentication passwords are intentionally never migrated.")
        };
        if (context.Options.SecurityStrategy == SecurityConversionStrategy.ExternalIdentityMapping)
        {
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "SECURITY.EXTERNAL_IDENTITY",
                FindingSeverity.Warning,
                "The generated NOLOGIN role requires external identity provisioning."));
        }
        return Task.FromResult(ConversionRuleSupport.Success(
            string.Join(Environment.NewLine, statements),
            "SECURITY.ROLE",
            findings,
            classification: ConversionClassification.AutomaticWithWarning,
            confidence: 0.7m));
    }

    private static ConversionResult<string> ConvertPermission(
        InventoryObject source,
        ConversionContext context)
    {
        var permission = context.Inventory.Permissions.FirstOrDefault(item => item.ObjectId == source.Id);
        if (permission is null || permission.State.Equals("DENY", StringComparison.OrdinalIgnoreCase))
        {
            return ConversionRuleSupport.Manual(
                source,
                permission?.State.Equals("DENY", StringComparison.OrdinalIgnoreCase) == true
                    ? "PostgreSQL has no general SQL Server DENY equivalent."
                    : "Permission metadata is incomplete.",
                $"-- Manual permission mapping required for {source.QualifiedSourceName}.",
                permission?.State ?? "missing permission");
        }

        var privilege = MapPrivilege(permission.PermissionName);
        if (privilege is null || permission.TargetObjectId is not { } targetId ||
            !context.ObjectsById.TryGetValue(targetId, out var targetSource))
        {
            return ConversionRuleSupport.Manual(
                source,
                $"Permission '{permission.PermissionName}' has no safe target mapping.",
                $"-- Manual permission mapping required for {source.QualifiedSourceName}.",
                permission.PermissionName);
        }
        var target = context.Identifiers.MapObject(targetSource);
        var grantee = context.Identifiers.QuoteIdentifier(permission.Grantee);
        var column = permission.ColumnName is null
            ? string.Empty
            : $" ({context.Identifiers.MapChildIdentifier(targetSource.Id, "column", targetSource.SourceSchema, permission.ColumnName)})";
        return ConversionRuleSupport.Success(
            $"GRANT {privilege}{column} ON {target.QualifiedName} TO {grantee};",
            "SECURITY.GRANT",
            classification: ConversionClassification.AutomaticWithWarning,
            confidence: 0.8m);
    }

    private static string? MapPrivilege(string permission) =>
        permission.ToUpperInvariant() switch
        {
            "SELECT" => "SELECT",
            "INSERT" => "INSERT",
            "UPDATE" => "UPDATE",
            "DELETE" => "DELETE",
            "REFERENCES" => "REFERENCES",
            "EXECUTE" => "EXECUTE",
            "USAGE" => "USAGE",
            _ => null
        };
}
