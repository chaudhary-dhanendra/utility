using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.DataMigration;
using Npgsql;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class DataMigrationValidator(
    ICanonicalValueFormatter canonicalFormatter) : IDataMigrationValidator
{
    public async Task<TableValidationResult> ValidateAsync(
        DataMigrationRequest request,
        TableLoadPlan table,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var sourceConnectionString = CreateSourceConnectionString(request.SourceConnection);
        await using var source = new SqlConnection(sourceConnectionString);
        await using var target = new NpgsqlConnection(request.TargetConnectionString);
        await Task.WhenAll(
            source.OpenAsync(cancellationToken),
            target.OpenAsync(cancellationToken)).ConfigureAwait(false);

        var sourceCount = await CountSourceAsync(source, table, request.Options, cancellationToken)
            .ConfigureAwait(false);
        var targetCount = await CountTargetAsync(target, table, request.Options, cancellationToken)
            .ConfigureAwait(false);
        var columns = new List<ColumnValidationResult>();
        if (request.Options.Validation.CompareNullCounts)
        {
            foreach (var column in table.Columns.Where(item => item.IsIncluded))
            {
                var sourceNulls = await ScalarLongAsync(
                    source,
                    $"SELECT COUNT_BIG(*) FROM {QuoteSql(table.SourceSchema)}.{QuoteSql(table.SourceTable)} " +
                    $"WHERE {QuoteSql(column.SourceName)} IS NULL" +
                    SourcePredicate(table),
                    request.Options.CommandTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                var targetNulls = await ScalarLongAsync(
                    target,
                    $"SELECT COUNT(*) FROM {QuotePg(table.TargetSchema)}.{QuotePg(table.TargetTable)} " +
                    $"WHERE {QuotePg(column.TargetName)} IS NULL",
                    request.Options.CommandTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                columns.Add(new ColumnValidationResult(
                    column.SourceName,
                    sourceNulls,
                    targetNulls,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    sourceNulls == targetNulls ? ValidationOutcome.Passed : ValidationOutcome.Failed,
                    sourceNulls == targetNulls ? null : "Null counts differ."));
            }
        }

        string? sourceChecksum = null;
        string? targetChecksum = null;
        var checksumOutcome = ValidationOutcome.NotRun;
        string? checksumMessage = null;
        if (request.Options.Validation.ChecksumMode != ChecksumMode.None)
        {
            if (table.StableResumeKey is null)
            {
                checksumOutcome = ValidationOutcome.Inconclusive;
                checksumMessage = "Logical checksum requires a stable ordering key.";
            }
            else
            {
                var sample = request.Options.Validation.ChecksumMode == ChecksumMode.Sample
                    ? request.Options.Validation.SampleSize
                    : (int?)null;
                sourceChecksum = await ComputeSourceChecksumAsync(
                    source, table, request.Options, sample, cancellationToken).ConfigureAwait(false);
                targetChecksum = await ComputeTargetChecksumAsync(
                    target, table, request.Options, sample, cancellationToken).ConfigureAwait(false);
                checksumOutcome = sourceChecksum == targetChecksum
                    ? ValidationOutcome.Passed
                    : ValidationOutcome.Failed;
                checksumMessage = checksumOutcome == ValidationOutcome.Failed
                    ? "Canonical logical checksums differ."
                    : null;
            }
        }

        var rowOutcome = sourceCount == targetCount
            ? ValidationOutcome.Passed
            : ValidationOutcome.Failed;
        var outcome = rowOutcome == ValidationOutcome.Failed ||
            checksumOutcome == ValidationOutcome.Failed ||
            columns.Any(item => item.Outcome == ValidationOutcome.Failed)
            ? ValidationOutcome.Failed
            : checksumOutcome == ValidationOutcome.Inconclusive
                ? ValidationOutcome.Warning
                : ValidationOutcome.Passed;
        return new TableValidationResult(
            table.SourceQualifiedName,
            sourceCount,
            targetCount,
            sourceChecksum,
            targetChecksum,
            columns,
            outcome,
            started.Elapsed,
            rowOutcome == ValidationOutcome.Failed
                ? "Source and target row counts differ."
                : checksumMessage);
    }

    private static Task<long> CountSourceAsync(
        SqlConnection connection,
        TableLoadPlan table,
        DataMigrationOptions options,
        CancellationToken cancellationToken) =>
        ScalarLongAsync(
            connection,
            $"SELECT COUNT_BIG(*) FROM {QuoteSql(table.SourceSchema)}.{QuoteSql(table.SourceTable)}" +
            PredicateOnly(table),
            options.CommandTimeoutSeconds,
            cancellationToken);

    private static Task<long> CountTargetAsync(
        NpgsqlConnection connection,
        TableLoadPlan table,
        DataMigrationOptions options,
        CancellationToken cancellationToken) =>
        ScalarLongAsync(
            connection,
            $"SELECT COUNT(*) FROM {QuotePg(table.TargetSchema)}.{QuotePg(table.TargetTable)}",
            options.CommandTimeoutSeconds,
            cancellationToken);

    private async Task<string> ComputeSourceChecksumAsync(
        SqlConnection connection,
        TableLoadPlan table,
        DataMigrationOptions options,
        int? sample,
        CancellationToken cancellationToken)
    {
        var columns = table.Columns.Where(item => item.IsIncluded).ToArray();
        var top = sample is null ? string.Empty : $"TOP ({sample.Value}) ";
        var sql = $"SELECT {top}{string.Join(", ", columns.Select(item => QuoteSql(item.SourceName)))} " +
            $"FROM {QuoteSql(table.SourceSchema)}.{QuoteSql(table.SourceTable)}" +
            PredicateOnly(table) +
            $" ORDER BY {QuoteSql(table.StableResumeKey!)}";
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds
        };
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken).ConfigureAwait(false);
        return await HashRowsAsync(reader, columns, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ComputeTargetChecksumAsync(
        NpgsqlConnection connection,
        TableLoadPlan table,
        DataMigrationOptions options,
        int? sample,
        CancellationToken cancellationToken)
    {
        var columns = table.Columns.Where(item => item.IsIncluded).ToArray();
        var limit = sample is null ? string.Empty : $" LIMIT {sample.Value}";
        var stable = columns.First(item =>
            item.SourceName.Equals(table.StableResumeKey, StringComparison.OrdinalIgnoreCase));
        var sql = $"SELECT {string.Join(", ", columns.Select(item => QuotePg(item.TargetName)))} " +
            $"FROM {QuotePg(table.TargetSchema)}.{QuotePg(table.TargetTable)} " +
            $"ORDER BY {QuotePg(stable.TargetName)}{limit}";
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds
        };
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken).ConfigureAwait(false);
        return await HashRowsAsync(reader, columns, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> HashRowsAsync(
        IDataReader reader,
        ColumnMapping[] columns,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader is System.Data.Common.DbDataReader dbReader &&
               await dbReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new (object?, DataTransportKind)[columns.Length];
            for (var index = 0; index < columns.Length; index++)
            {
                var value = dbReader.IsDBNull(index) ? null : dbReader.GetValue(index);
                values[index] = (DataTransportConverter.ConvertValue(value, columns[index]),
                    columns[index].TransportKind);
            }

            var rowHash = canonicalFormatter.ComputeRowHash(values);
            hash.AppendData(Encoding.ASCII.GetBytes(rowHash));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<long> ScalarLongAsync(
        System.Data.Common.DbConnection connection,
        string sql,
        int timeout,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /* private static string CreateSourceConnectionString(SqlServerConnectionOptions options)
     {
         var builder = new SqlConnectionStringBuilder
         {
             DataSource = options.Port is null ? options.Server : $"{options.Server},{options.Port}",
             InitialCatalog = options.Database,
             IntegratedSecurity = options.AuthenticationMode == SqlServerAuthenticationMode.Windows,
             UserID = options.Username,
             Password = options.Password,
             Encrypt = options.Encrypt,
             TrustServerCertificate = options.TrustServerCertificate,
             ConnectTimeout = options.ConnectionTimeoutSeconds,
             ApplicationName = "SQL Server to PostgreSQL Migration Studio Validation"
         };
         return builder.ConnectionString;
     }
 */

    private static string CreateSourceConnectionString(SqlServerConnectionOptions options)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = options.Port is null
                ? options.Server
                : $"{options.Server},{options.Port}",

            InitialCatalog = options.Database,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,
            ConnectTimeout = options.ConnectionTimeoutSeconds
        };

        if (options.AuthenticationMode == SqlServerAuthenticationMode.Windows)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = options.Username ?? throw new InvalidOperationException(
                "SQL username is required.");

            if (options.Password != null)
                builder.Password = options.Password;
        }

        return builder.ConnectionString;
    }
    private static string PredicateOnly(TableLoadPlan table) =>
        table.SourcePredicate is null ? string.Empty : $" WHERE ({table.SourcePredicate})";

    private static string SourcePredicate(TableLoadPlan table) =>
        table.SourcePredicate is null ? string.Empty : $" AND ({table.SourcePredicate})";

    private static string QuoteSql(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string QuotePg(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);
}
