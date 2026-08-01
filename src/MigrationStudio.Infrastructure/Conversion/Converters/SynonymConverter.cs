using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed class SynonymConverter : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType == InventoryObjectType.Synonym;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var synonym = context.Inventory.Synonyms.FirstOrDefault(item => item.ObjectId == source.Id);
        if (synonym is null)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "Synonym metadata is unavailable.",
                $"-- Manual synonym mapping required for {source.QualifiedSourceName}.",
                "missing synonym metadata"));
        }

        if (context.Options.SynonymStrategy != SynonymConversionStrategy.View)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                $"Synonym strategy is {context.Options.SynonymStrategy}.",
                $"-- Source synonym target: {synonym.BaseObjectName}",
                synonym.IsLinkedServerReference ? "linked server" :
                synonym.IsCrossDatabaseReference ? "cross-database synonym" : "synonym"));
        }

        if (synonym.IsLinkedServerReference || synonym.IsCrossDatabaseReference)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "Cross-database or linked-server synonyms cannot become local views without an FDW mapping.",
                $"-- Configure an FDW for {synonym.BaseObjectName}.",
                "external synonym"));
        }

        var targetSource = context.Inventory.Objects.FirstOrDefault(item =>
            item.SourceSchema.Equals(synonym.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals(synonym.ObjectName, StringComparison.OrdinalIgnoreCase));
        if (targetSource is null ||
            targetSource.ObjectType is not (InventoryObjectType.Table or InventoryObjectType.View))
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "Synonym target is unresolved or is not safely representable as a view.",
                $"-- Manual synonym target: {synonym.BaseObjectName}",
                "unresolved synonym"));
        }

        var target = context.Identifiers.MapObject(source);
        var referenced = context.Identifiers.MapObject(targetSource);
        return Task.FromResult(ConversionRuleSupport.Success(
            $"CREATE OR REPLACE VIEW {target.QualifiedName} AS SELECT * FROM {referenced.QualifiedName};",
            "SYNONYM.VIEW",
            [
                ConversionRuleSupport.Finding(
                    source,
                    "SYNONYM.VIEW_SEMANTICS",
                    FindingSeverity.Warning,
                    "The synonym was converted to a view; DML/updatability and permission behavior require validation.")
            ],
            classification: ConversionClassification.AutomaticWithWarning,
            confidence: 0.7m));
    }
}
