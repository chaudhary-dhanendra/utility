using Npgsql;

namespace MigrationStudio.Deployment;

internal static class PostgreSqlDeploymentErrorClassifier
{
    private static readonly HashSet<string> TransientStates =
    [
        "40001", "40P01", "53300", "53400", "57P01", "57P02", "57P03",
        "08000", "08001", "08003", "08004", "08006", "08007", "08P01"
    ];

    public static bool IsTransient(PostgresException exception) =>
        TransientStates.Contains(exception.SqlState);

    public static bool IsTransient(string? sqlState) =>
        sqlState is not null && TransientStates.Contains(sqlState);

    public static bool IsPermanent(string sqlState) =>
        sqlState.StartsWith("42", StringComparison.Ordinal) ||
        sqlState.StartsWith("28", StringComparison.Ordinal) ||
        sqlState is "42501" or "42703" or "42P01";
}
