namespace MigrationStudio.Application.Conversion;

public static class PostgreSqlIdentifierQuoter
{
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public static string Unquote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"'
            ? identifier[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : identifier;
    }
}
