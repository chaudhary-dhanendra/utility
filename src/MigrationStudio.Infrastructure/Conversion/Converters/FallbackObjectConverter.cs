using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed class FallbackObjectConverter : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) => true;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = context.Identifiers.MapObject(source);
        var classification = source.ObjectType is InventoryObjectType.ServiceBrokerObject or
            InventoryObjectType.SqlAgentJob or InventoryObjectType.ReplicationObject or InventoryObjectType.ServerTrigger
            ? ConversionClassification.Unsupported
            : ConversionClassification.ManualConversion;
        var finding = ConversionRuleSupport.Finding(
            source,
            classification == ConversionClassification.Unsupported ? "CONVERSION.UNSUPPORTED" : "CONVERSION.MANUAL",
            classification == ConversionClassification.Unsupported ? FindingSeverity.Error : FindingSeverity.Warning,
            $"No safe automatic PostgreSQL conversion is registered for {source.ObjectType}.");
        var sourceSql = (source.SourceDefinition ?? "Definition unavailable")
            .Replace("*/", "* /", StringComparison.Ordinal);
        var sql = $"-- {classification}: {target.QualifiedName}{Environment.NewLine}" +
                  $"/* Preserved source definition:{Environment.NewLine}{sourceSql}{Environment.NewLine}*/";
        return Task.FromResult(new ConversionResult<string>(
            sql,
            classification,
            "OBJECT.FALLBACK",
            0m,
            [finding],
            [source.ObjectType.ToString()],
            true));
    }
}
