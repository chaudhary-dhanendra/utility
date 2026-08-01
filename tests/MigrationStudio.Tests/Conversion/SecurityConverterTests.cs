using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Conversion.Converters;

namespace MigrationStudio.Tests.Conversion;

public sealed class SecurityConverterTests
{
    [Fact]
    public async Task GeneratesNoLoginRoleWithoutPassword()
    {
        var role = Object(InventoryObjectType.Role, "AppReaders");
        var inventory = TestInventory.CreateSnapshot([role]) with
        {
            SecurityPrincipals =
            [
                new SecurityPrincipalInventory(
                    role.Id, 5, role.SourceName, "DATABASE_ROLE", "NONE", null, false, false, [])
            ]
        };
        var options = new ConversionOptions
        {
            SecurityStrategy = SecurityConversionStrategy.GenerateRolesWithoutPasswords
        };
        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(inventory, options);
        var byId = inventory.Objects.ToDictionary(item => item.Id);
        var context = new ConversionContext(
            inventory, options, mapper, new PostgreSqlTypeMappingRegistry(),
            new StructuredSqlExpressionTranslator(), byId,
            byId.ToDictionary(item => item.Key, item => mapper.MapObject(item.Value)));

        var result = await new SecurityConverter().ConvertAsync(role, context, CancellationToken.None);

        Assert.Contains("CREATE ROLE", result.Target, StringComparison.Ordinal);
        Assert.Contains("NOLOGIN", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSWORD", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    private static InventoryObject Object(InventoryObjectType type, string name)
    {
        var id = InventoryObjectId.Create("fixture", type, "dbo", name, 1);
        return new InventoryObject(
            id, "fixture", "dbo", name, $"[dbo].[{name}]", type, 1, null, null, null, false, true,
            SelectionReason.CompleteDatabase, 0, 0, [], InventoryClassification.ForObject(type), null,
            null, "hash", [], DiscoveryStatus.Discovered);
    }
}
