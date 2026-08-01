using System.Globalization;
using System.Text;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed class SequenceConverter : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType == InventoryObjectType.Sequence;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = context.Inventory.Sequences.FirstOrDefault(item => item.ObjectId == source.Id);
        if (sequence is null)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source, "Sequence metadata is unavailable.", $"-- Manual sequence required for {source.QualifiedSourceName}.", "missing sequence metadata"));
        }

        var target = context.Identifiers.MapObject(source);
        var sql = new StringBuilder("CREATE SEQUENCE ")
            .Append(target.QualifiedName)
            .Append(" AS ").Append(MapIntegerType(sequence.TypeName))
            .Append(" START WITH ").Append(sequence.StartValue.ToString(CultureInfo.InvariantCulture))
            .Append(" INCREMENT BY ").Append(sequence.Increment.ToString(CultureInfo.InvariantCulture))
            .Append(" MINVALUE ").Append(sequence.MinimumValue.ToString(CultureInfo.InvariantCulture))
            .Append(" MAXVALUE ").Append(sequence.MaximumValue.ToString(CultureInfo.InvariantCulture))
            .Append(sequence.IsCycling ? " CYCLE" : " NO CYCLE")
            .Append(" CACHE ").Append(Math.Max(1, sequence.CacheSize).ToString(CultureInfo.InvariantCulture))
            .Append(';');
        var owners = context.Inventory.Columns
            .Where(column => !string.IsNullOrWhiteSpace(column.DefaultDefinition) &&
                             ReferencesSequence(column.DefaultDefinition, source.SourceName))
            .Where(column => context.ObjectsById.ContainsKey(column.ParentObjectId))
            .ToArray();
        var findings = new List<InventoryFinding>();
        var classification = ConversionClassification.Automatic;
        if (owners.Length == 1)
        {
            var table = context.ObjectsById[owners[0].ParentObjectId];
            var targetTable = context.Identifiers.MapObject(table);
            var targetColumn = context.Identifiers.MapChildIdentifier(
                table.Id, "column", table.SourceSchema, owners[0].Name);
            sql.AppendLine()
                .Append("ALTER SEQUENCE ").Append(target.QualifiedName)
                .Append(" OWNED BY ").Append(targetTable.QualifiedName).Append('.').Append(targetColumn).Append(';');
        }
        else if (owners.Length > 1)
        {
            classification = ConversionClassification.AutomaticWithWarning;
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "SEQUENCE.MULTIPLE_OWNERS",
                FindingSeverity.Warning,
                "The sequence is referenced by multiple columns and cannot have a single PostgreSQL OWNED BY link."));
        }
        return Task.FromResult(ConversionRuleSupport.Success(
            sql.ToString(),
            "SEQUENCE.CREATE",
            findings,
            classification: classification,
            confidence: classification == ConversionClassification.Automatic ? 1m : 0.8m));
    }

    private static string MapIntegerType(string sourceType) =>
        sourceType.ToLowerInvariant() switch
        {
            "tinyint" or "smallint" => "smallint",
            "int" or "integer" => "integer",
            _ => "bigint"
        };

    private static bool ReferencesSequence(string definition, string sequenceName) =>
        TSqlTokenizer.Tokenize(definition).Any(token =>
            token.Kind is TSqlTokenKind.Word or TSqlTokenKind.QuotedIdentifier &&
            Unquote(token.Text).Equals(sequenceName, StringComparison.OrdinalIgnoreCase));

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '[' || value[0] == '"')
            ? value[1..^1].Replace(value[0] == '[' ? "]]" : "\"\"", value[0] == '[' ? "]" : "\"", StringComparison.Ordinal)
            : value;
}
