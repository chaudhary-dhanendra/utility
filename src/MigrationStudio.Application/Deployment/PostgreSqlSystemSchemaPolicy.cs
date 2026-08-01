namespace MigrationStudio.Application.Deployment;

public static class PostgreSqlSystemSchemaPolicy
{
    public static bool IsSystemSchema(string? schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return false;
        }

        return schemaName.Equals("pg_catalog", StringComparison.OrdinalIgnoreCase) ||
            schemaName.Equals("information_schema", StringComparison.OrdinalIgnoreCase) ||
            schemaName.Equals("pg_toast", StringComparison.OrdinalIgnoreCase) ||
            schemaName.StartsWith("pg_temp_", StringComparison.OrdinalIgnoreCase) ||
            schemaName.StartsWith("pg_toast_temp_", StringComparison.OrdinalIgnoreCase);
    }

    public static string CatalogPredicate(string schemaExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaExpression);
        return $"{schemaExpression} NOT IN ('pg_catalog', 'information_schema', 'pg_toast') " +
            $"AND {schemaExpression} NOT LIKE 'pg_temp_%' " +
            $"AND {schemaExpression} NOT LIKE 'pg_toast_temp_%'";
    }
}
