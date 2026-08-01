using System.Text;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed class UserDefinedTypeConverter : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType is InventoryObjectType.UserDefinedType or InventoryObjectType.TableType;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = context.Inventory.UserDefinedTypes.FirstOrDefault(item => item.ObjectId == source.Id);
        if (type is null || type.IsAssemblyType)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                type?.IsAssemblyType == true ? "CLR types require an explicit extension mapping." : "Type metadata is unavailable.",
                $"-- Manual PostgreSQL type required for {context.Identifiers.MapObject(source).QualifiedName}.",
                "CLR or missing type"));
        }

        var target = context.Identifiers.MapObject(source);
        if (type.TableTypeColumns.Count > 0)
        {
            var fields = type.TableTypeColumns.OrderBy(item => item.OrdinalPosition).Select(column =>
            {
                var mapping = context.TypeMappings.Map(
                    column.SystemTypeName,
                    column.MaximumLength,
                    column.Precision,
                    column.Scale,
                    context.Options);
                return $"    {context.Identifiers.MapChildIdentifier(source.Id, "field", source.SourceSchema, column.Name)} {mapping.TargetType}";
            });
            return Task.FromResult(ConversionRuleSupport.Success(
                $"CREATE TYPE {target.QualifiedName} AS ({Environment.NewLine}{string.Join($",{Environment.NewLine}", fields)}{Environment.NewLine});",
                "TYPE.COMPOSITE"));
        }

        var baseMapping = context.TypeMappings.Map(
            type.BaseTypeName ?? "sql_variant",
            -1,
            0,
            0,
            context.Options);
        if (baseMapping.Classification == ConversionClassification.ManualConversion)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "Alias type base type is not safely mapped.",
                $"-- Manual domain required for {target.QualifiedName}.",
                type.BaseTypeName ?? "unknown base type"));
        }

        return context.Options.UserDefinedTypeStrategy switch
        {
            UserDefinedTypeStrategy.Domain => Task.FromResult(ConversionRuleSupport.Success(
                $"CREATE DOMAIN {target.QualifiedName} AS {baseMapping.TargetType};",
                "TYPE.DOMAIN")),
            UserDefinedTypeStrategy.BaseType => Task.FromResult(ConversionRuleSupport.Success(
                $"-- Alias type {source.QualifiedSourceName} maps directly to {baseMapping.TargetType}.",
                "TYPE.BASE")),
            _ => Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "A scalar alias type cannot be emitted as a composite type.",
                $"-- Choose domain or base-type strategy for {target.QualifiedName}.",
                "invalid UDT strategy"))
        };
    }
}
