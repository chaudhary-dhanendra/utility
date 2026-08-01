using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Conversion;

/// <summary>
/// Renders PostgreSQL names exclusively from the final, published identifier mapping.
/// The mapped components already reflect the configured quoting and case policy.
/// </summary>
public static class MappedPostgreSqlIdentifierRenderer
{
    private const char ReferenceSeparator = '\u001f';

    private static readonly HashSet<InventoryObjectType> SqlReferenceTypes =
    [
        InventoryObjectType.Table,
        InventoryObjectType.ExternalTable,
        InventoryObjectType.View,
        InventoryObjectType.StoredProcedure,
        InventoryObjectType.Function,
        InventoryObjectType.Sequence,
        InventoryObjectType.UserDefinedType,
        InventoryObjectType.TableType,
        InventoryObjectType.Synonym
    ];

    public static string RenderQualifiedName(
        IIdentifierMapper identifiers,
        InventoryObject source)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(source);
        return RenderQualifiedName(identifiers.MapObject(source));
    }

    public static string RenderQualifiedName(TargetObjectIdentifier mapped)
    {
        ArgumentNullException.ThrowIfNull(mapped);
        if (string.IsNullOrWhiteSpace(mapped.Schema) || string.IsNullOrWhiteSpace(mapped.Name))
        {
            throw new InvalidOperationException(
                "A mapped PostgreSQL qualified name requires both schema and object components.");
        }

        return $"{mapped.Schema}.{mapped.Name}";
    }

    public static string CreateObjectReferenceKey(string sourceSchema, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        return string.Concat(sourceSchema, ReferenceSeparator, sourceName);
    }

    public static IReadOnlyDictionary<string, string> CreateObjectReferenceMap(
        IEnumerable<InventoryObject> sourceObjects,
        IReadOnlyDictionary<InventoryObjectId, TargetObjectIdentifier> targetsBySource)
    {
        ArgumentNullException.ThrowIfNull(sourceObjects);
        ArgumentNullException.ThrowIfNull(targetsBySource);

        var candidates = sourceObjects
            .Where(item => item.IsIncluded && SqlReferenceTypes.Contains(item.ObjectType))
            .Where(item => targetsBySource.ContainsKey(item.Id))
            .GroupBy(
                item => CreateObjectReferenceKey(item.SourceSchema, item.SourceName),
                StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var mappedNames = candidate
                .Select(item => RenderQualifiedName(targetsBySource[item.Id]))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (mappedNames.Length == 1)
            {
                result.Add(candidate.Key, mappedNames[0]);
            }
        }

        return result;
    }
}
