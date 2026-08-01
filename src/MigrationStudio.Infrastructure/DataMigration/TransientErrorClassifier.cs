using Microsoft.Data.SqlClient;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.DataMigration;
using Npgsql;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class TransientErrorClassifier : ITransientErrorClassifier
{
    private static readonly HashSet<int> TransientSqlServerNumbers =
    [
        -2, 20, 64, 233, 1205, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613
    ];

    private static readonly HashSet<string> TransientPostgreSqlStates =
    [
        "40001", "40P01", "53300", "53400", "57P01", "57P02", "57P03", "08000",
        "08001", "08003", "08004", "08006", "08007", "08P01"
    ];

    public FailureCategory Classify(Exception exception) =>
        exception switch
        {
            OperationCanceledException => FailureCategory.Cancellation,
            SqlException sql when sql.Errors.Cast<SqlError>()
                .Any(error => TransientSqlServerNumbers.Contains(error.Number)) =>
                FailureCategory.TransientSqlServer,
            PostgresException postgres when TransientPostgreSqlStates.Contains(postgres.SqlState) =>
                FailureCategory.TransientPostgreSql,
            NpgsqlException npgsql when npgsql.IsTransient => FailureCategory.TransientPostgreSql,
            SqlException => FailureCategory.PermanentDatabase,
            NpgsqlException => FailureCategory.PermanentDatabase,
            InvalidCastException or FormatException or OverflowException => FailureCategory.Conversion,
            _ => FailureCategory.Configuration
        };

    public bool IsTransient(Exception exception) =>
        Classify(exception) is FailureCategory.TransientSqlServer or FailureCategory.TransientPostgreSql;
}
