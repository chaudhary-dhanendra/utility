using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed class SchemaConverter : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType == InventoryObjectType.Schema;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = context.Identifiers.MapObject(source);
        return Task.FromResult(ConversionRuleSupport.Success(
            $"CREATE SCHEMA IF NOT EXISTS {target.Schema};",
            "SCHEMA.CREATE"));
    }
}
