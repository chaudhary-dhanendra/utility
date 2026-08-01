using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class SensitiveColumnClassifier : ISensitiveColumnClassifier
{
    public bool IsSensitive(ColumnInventory column, SensitiveDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(options);
        var normalized = Normalize(column.Name);
        if (options.NamePatterns.Any(pattern =>
                normalized.Contains(Normalize(pattern), StringComparison.Ordinal)))
        {
            return true;
        }

        return options.InspectMetadata && column.ExtendedProperties.Any(property =>
            IsSensitiveMetadata(property.Name) || IsSensitiveMetadata(property.Value));
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsSensitiveMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("sensitive", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("credential", StringComparison.OrdinalIgnoreCase);
    }
}
