using System.Globalization;
using Microsoft.Data.SqlClient;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.DataMigration;
using Npgsql;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class SequenceResetService : ISequenceResetService
{
    public async Task<IReadOnlyList<SequenceResetResult>> ResetAsync(
        DataMigrationRequest request,
        IReadOnlyList<TableMigrationMetrics> completedTables,
        CancellationToken cancellationToken)
    {
        var completed = completedTables.Where(item =>
                item.State is TableMigrationState.Completed or TableMigrationState.CompletedWithFailures)
            .Select(item => item.TableId)
            .ToHashSet();
        var plans = new DataMigrationPlanner(new SensitiveColumnClassifier()).CreatePlan(request);
        var results = new List<SequenceResetResult>();
        await using var source = CreateSourceConnection(request);
        await using var target = new NpgsqlConnection(request.TargetConnectionString);
        await Task.WhenAll(
            source.OpenAsync(cancellationToken),
            target.OpenAsync(cancellationToken)).ConfigureAwait(false);
        foreach (var table in plans.Tables.Where(item => completed.Contains(item.SourceTableId)))
        {
            foreach (var column in table.Columns.Where(item => item.IsIncluded && item.IsIdentity))
            {
                var increment = column.IdentityIncrement ?? 1;
                var aggregate = increment >= 0 ? "MAX" : "MIN";
                var sourceValue = await ScalarDecimalAsync(
                    source,
                    $"SELECT {aggregate}({QuoteSql(column.SourceName)}) FROM " +
                    $"{QuoteSql(table.SourceSchema)}.{QuoteSql(table.SourceTable)}",
                    cancellationToken).ConfigureAwait(false);
                var targetValue = await ScalarDecimalAsync(
                    target,
                    $"SELECT {aggregate}({QuotePg(column.TargetName)}) FROM " +
                    $"{QuotePg(table.TargetSchema)}.{QuotePg(table.TargetTable)}",
                    cancellationToken).ConfigureAwait(false);
                var seed = column.IdentitySeed ?? 1;
                var restart = SequenceRestartCalculator.Select(
                    sourceValue,
                    targetValue,
                    seed,
                    increment);
                var tableLiteral = $"{table.TargetSchema}.{table.TargetTable}".Replace(
                    "'",
                    "''",
                    StringComparison.Ordinal);
                var columnLiteral = column.TargetName.Replace("'", "''", StringComparison.Ordinal);
                var called = sourceValue is not null || targetValue is not null;
                var script =
                    $"SELECT setval(pg_get_serial_sequence('{tableLiteral}', '{columnLiteral}'), " +
                    $"{restart.ToString(CultureInfo.InvariantCulture)}, {called.ToString().ToLowerInvariant()});";
                await using var command = new NpgsqlCommand(script, target)
                {
                    CommandTimeout = request.Options.CommandTimeoutSeconds
                };
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                results.Add(new SequenceResetResult(
                    table.TargetQualifiedName,
                    column.TargetName,
                    sourceValue,
                    targetValue,
                    restart,
                    increment,
                    script));
            }
        }

        return results;
    }

    /*  private static SqlConnection CreateSourceConnection(DataMigrationRequest request)
      {
          ArgumentNullException.ThrowIfNull(request);

          // Best option: use the complete validated source connection string
          // already supplied to the data-migration request.
          if (!string.IsNullOrWhiteSpace(request.SourceConnectionString))
          {
              return new SqlConnection(request.SourceConnectionString);
          }

          var builder = new SqlConnectionStringBuilder
          {
              DataSource = request.SourceServer,
              InitialCatalog = request.SourceDatabase,
              Encrypt = request.SourceEncrypt,
              TrustServerCertificate = request.SourceTrustServerCertificate
          };

          if (request.SourceIntegratedSecurity)
          {
              builder.IntegratedSecurity = true;
          }
          else
          {
              if (string.IsNullOrWhiteSpace(request.SourceUserName))
              {
                  throw new InvalidOperationException(
                      "SQL Server username is required when Integrated Security is disabled.");
              }

              builder.UserID = request.SourceUserName;

              if (!string.IsNullOrEmpty(request.SourcePassword))
              {
                  builder.Password = request.SourcePassword;
              }
          }

          return new SqlConnection(builder.ConnectionString);
      }
  */
    private static SqlConnection CreateSourceConnection(DataMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = request.SourceConnection
            ?? throw new InvalidOperationException(
                "SQL Server source connection options are missing.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = options.Port is null
                ? options.Server
                : $"{options.Server},{options.Port}",

            InitialCatalog = options.Database,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,
            ConnectTimeout = options.ConnectionTimeoutSeconds,
            ApplicationName =
                "SQL Server to PostgreSQL Migration Studio Sequence Reset"
        };

        if (options.AuthenticationMode ==
            MigrationStudio.Application.Discovery.SqlServerAuthenticationMode.Windows)
        {
            // Windows authentication:
            // Do not assign UserID or Password because they may be null.
            builder.IntegratedSecurity = true;
        }
        else
        {
            // SQL Server authentication
            if (string.IsNullOrWhiteSpace(options.Username))
            {
                throw new InvalidOperationException(
                    "SQL Server username is required when SQL authentication is selected.");
            }

            builder.IntegratedSecurity = false;
            builder.UserID = options.Username;

            // Assign Password only when non-null.
            // SqlConnectionStringBuilder rejects a null value.
            if (options.Password is not null)
            {
                builder.Password = options.Password;
            }
        }

        return new SqlConnection(builder.ConnectionString);
    }
    private static async Task<decimal?> ScalarDecimalAsync(
        System.Data.Common.DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static string QuoteSql(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string QuotePg(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);
}
