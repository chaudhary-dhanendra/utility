using System.Globalization;
using System.Text;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.DataMigration;
using Npgsql;
using NpgsqlTypes;

namespace MigrationStudio.Infrastructure.DataMigration;


internal static class PostgreSqlWritableColumnResolver
{
    public static async Task<(ColumnMapping[] Columns, int[] SourceIndexes)> ResolveAsync(
        NpgsqlConnection connection,
        TableLoadPlan table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.attname
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c
              ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n
              ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relname = @table
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND a.attgenerated <> '';
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", table.TargetSchema);
        command.Parameters.AddWithValue("table", table.TargetTable);

        var generatedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                generatedColumns.Add(reader.GetString(0));
            }
        }

        var included = table.Columns
            .Where(item => item.IsIncluded)
            .ToArray();

        var selected = included
            .Select((column, index) => new
            {
                Column = column,
                SourceIndex = index
            })
            .Where(item => !generatedColumns.Contains(item.Column.TargetName))
            .ToArray();

        var columns = selected
            .Select(item => item.Column)
            .ToArray();

        var sourceIndexes = selected
            .Select(item => item.SourceIndex)
            .ToArray();

        if (columns.Length == 0)
        {
            throw new InvalidOperationException(
                $"No writable target columns remain for {table.TargetQualifiedName}. " +
                "All included target columns are PostgreSQL generated columns.");
        }

        return (columns, sourceIndexes);
    }
}

public sealed class PostgreSqlBinaryCopyStrategy : IDataTransferStrategy
{
    public DataTransferStrategy Strategy => DataTransferStrategy.PostgreSqlBinaryCopy;

    public bool CanExecute(TableLoadPlan table) =>
        table.Columns.Where(item => item.IsIncluded)
            .All(item => item.TransportKind is not DataTransportKind.Spatial and not DataTransportKind.Opaque);

    public async Task<BatchWriteResult> WriteBatchAsync(
        DataTransferContext context,
        IReadOnlyList<DataRowBuffer> rows,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        await using var connection = new NpgsqlConnection(context.TargetConnectionString);
        await ObserveAsync(
            context,
            StreamingExecutionStage.OpenPostgreSqlConnection,
            "NpgsqlConnection",
            null,
            () => connection.OpenAsync(cancellationToken)).ConfigureAwait(false);

        var (columns, sourceIndexes) =
            await PostgreSqlWritableColumnResolver.ResolveAsync(
                connection,
                context.Table,
                cancellationToken).ConfigureAwait(false);

        var sql = $"COPY {Quote(context.Table.TargetSchema)}.{Quote(context.Table.TargetTable)} " +
            $"({string.Join(", ", columns.Select(item => Quote(item.TargetName)))}) FROM STDIN (FORMAT BINARY)";

        ObserveInstant(context, StreamingExecutionStage.GenerateWritePlan, copySql: sql);
        ObserveInstant(context, StreamingExecutionStage.BeginPostgreSqlTransaction, writer: "COPY transaction");
        ObserveInstant(context, StreamingExecutionStage.CreatePostgreSqlWriter, writer: nameof(NpgsqlBinaryImporter));
        NpgsqlBinaryImporter importer;
        var initialize = Start(context, StreamingExecutionStage.InitializeCopy, nameof(NpgsqlBinaryImporter), sql);
        try
        {
            importer = await connection.BeginBinaryImportAsync(sql, cancellationToken).ConfigureAwait(false);
            context.StageObserver?.Succeed(initialize);
        }
        catch (Exception exception)
        {
            context.StageObserver?.Fail(initialize, exception);
            throw;
        }
        await using var importerScope = importer;
        var first = true;
        foreach (var row in rows)
        {
            var writeFirst = first
                ? Start(context, StreamingExecutionStage.WriteFirstRow, nameof(NpgsqlBinaryImporter), sql)
                : Guid.Empty;
            try
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < columns.Length; index++)
                {
                    var value = row.Values[sourceIndexes[index]];
                    if (value is null)
                    {
                        await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await importer.WriteAsync(
                            value,
                            ToNpgsqlType(columns[index].TransportKind),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                if (first) context.StageObserver?.Succeed(writeFirst);
            }
            catch (Exception exception)
            {
                if (first) context.StageObserver?.Fail(writeFirst, exception);
                throw;
            }
            first = false;
        }

        await ObserveAsync(
            context,
            StreamingExecutionStage.FlushFirstBatch,
            nameof(NpgsqlBinaryImporter),
            sql,
            async () => { await importer.CompleteAsync(cancellationToken).ConfigureAwait(false); })
            .ConfigureAwait(false);
        ObserveInstant(context, StreamingExecutionStage.Commit, writer: nameof(NpgsqlBinaryImporter));
        return new BatchWriteResult(rows.Count, rows.Sum(item => item.ApproximateBytes), started.Elapsed);
    }

    private static string Quote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);

    private static NpgsqlDbType ToNpgsqlType(DataTransportKind kind) =>
        kind switch
        {
            DataTransportKind.Boolean => NpgsqlDbType.Boolean,
            DataTransportKind.Signed16 => NpgsqlDbType.Smallint,
            DataTransportKind.Signed32 => NpgsqlDbType.Integer,
            DataTransportKind.Signed64 => NpgsqlDbType.Bigint,
            DataTransportKind.ExactNumeric => NpgsqlDbType.Numeric,
            DataTransportKind.Floating32 => NpgsqlDbType.Real,
            DataTransportKind.Floating64 => NpgsqlDbType.Double,
            DataTransportKind.Date => NpgsqlDbType.Date,
            DataTransportKind.Time => NpgsqlDbType.Time,
            DataTransportKind.DateTime => NpgsqlDbType.Timestamp,
            DataTransportKind.DateTimeOffset => NpgsqlDbType.TimestampTz,
            DataTransportKind.Text => NpgsqlDbType.Text,
            DataTransportKind.Binary => NpgsqlDbType.Bytea,
            DataTransportKind.Uuid => NpgsqlDbType.Uuid,
            DataTransportKind.Xml => NpgsqlDbType.Xml,
            DataTransportKind.Json => NpgsqlDbType.Jsonb,
            _ => throw new InvalidOperationException($"{kind} is not supported by binary COPY.")
        };

    internal static Guid Start(
        DataTransferContext context,
        StreamingExecutionStage stage,
        string? writer = null,
        string? copySql = null,
        string? insertSql = null) =>
        context.StageObserver?.Start(
            stage,
            context.Table,
            context.CurrentBatch,
            context.RowsRead,
            context.RowsWritten,
            currentWriter: writer,
            copySql: copySql,
            insertSql: insertSql) ?? Guid.Empty;

    internal static void ObserveInstant(
        DataTransferContext context,
        StreamingExecutionStage stage,
        string? writer = null,
        string? copySql = null,
        string? insertSql = null)
    {
        var id = Start(context, stage, writer, copySql, insertSql);
        context.StageObserver?.Succeed(id);
    }

    internal static async Task ObserveAsync(
        DataTransferContext context,
        StreamingExecutionStage stage,
        string? writer,
        string? sql,
        Func<Task> action)
    {
        var id = Start(context, stage, writer, copySql: sql);
        try
        {
            await action().ConfigureAwait(false);
            context.StageObserver?.Succeed(id);
        }
        catch (Exception exception)
        {
            context.StageObserver?.Fail(id, exception);
            throw;
        }
    }
}

public sealed class PostgreSqlTextCopyStrategy(ICanonicalValueFormatter formatter) : IDataTransferStrategy
{
    public DataTransferStrategy Strategy => DataTransferStrategy.PostgreSqlTextCopy;

    public bool CanExecute(TableLoadPlan table) => true;

    public async Task<BatchWriteResult> WriteBatchAsync(
        DataTransferContext context,
        IReadOnlyList<DataRowBuffer> rows,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        await using var connection = new NpgsqlConnection(context.TargetConnectionString);
        await PostgreSqlBinaryCopyStrategy.ObserveAsync(
            context, StreamingExecutionStage.OpenPostgreSqlConnection, "NpgsqlConnection", null,
            () => connection.OpenAsync(cancellationToken)).ConfigureAwait(false);

        var (columns, sourceIndexes) =
            await PostgreSqlWritableColumnResolver.ResolveAsync(
                connection,
                context.Table,
                cancellationToken).ConfigureAwait(false);

        var sql = $"COPY {Quote(context.Table.TargetSchema)}.{Quote(context.Table.TargetTable)} " +
            $"({string.Join(", ", columns.Select(item => Quote(item.TargetName)))}) " +
            "FROM STDIN (FORMAT TEXT, DELIMITER E'\\t', NULL '\\N')";

        PostgreSqlBinaryCopyStrategy.ObserveInstant(
            context, StreamingExecutionStage.GenerateWritePlan, copySql: sql);
        PostgreSqlBinaryCopyStrategy.ObserveInstant(
            context, StreamingExecutionStage.BeginPostgreSqlTransaction, "COPY transaction");
        PostgreSqlBinaryCopyStrategy.ObserveInstant(
            context, StreamingExecutionStage.CreatePostgreSqlWriter, nameof(StreamWriter));
        StreamWriter writer;
        var initialize = PostgreSqlBinaryCopyStrategy.Start(
            context, StreamingExecutionStage.InitializeCopy, nameof(StreamWriter), sql);
        try
        {
            writer = await connection.BeginTextImportAsync(sql, cancellationToken).ConfigureAwait(false);
            context.StageObserver?.Succeed(initialize);
        }
        catch (Exception exception)
        {
            context.StageObserver?.Fail(initialize, exception);
            throw;
        }
        await using var writerScope = writer;
        var first = true;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = string.Join(
                '\t',
                columns.Select((column, index) =>
                {
                    var value = row.Values[sourceIndexes[index]];
                    return value is null
                        ? "\\N"
                        : Escape(ToInvariantText(value, column.TransportKind));
                }));
            var firstWrite = first
                ? PostgreSqlBinaryCopyStrategy.Start(
                    context, StreamingExecutionStage.WriteFirstRow, nameof(StreamWriter), sql)
                : Guid.Empty;
            try
            {
                await writer.WriteLineAsync(line).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (first) context.StageObserver?.Succeed(firstWrite);
            }
            catch (Exception exception)
            {
                if (first) context.StageObserver?.Fail(firstWrite, exception);
                throw;
            }
            first = false;
        }

        await PostgreSqlBinaryCopyStrategy.ObserveAsync(
            context, StreamingExecutionStage.FlushFirstBatch, nameof(StreamWriter), sql,
            () => writer.FlushAsync(cancellationToken)).ConfigureAwait(false);
        PostgreSqlBinaryCopyStrategy.ObserveInstant(
            context, StreamingExecutionStage.Commit, nameof(StreamWriter));
        return new BatchWriteResult(rows.Count, rows.Sum(item => item.ApproximateBytes), started.Elapsed);
    }

    private string ToInvariantText(object value, DataTransportKind kind) =>
        kind switch
        {
            DataTransportKind.Binary => "\\x" + Convert.ToHexString((byte[])value).ToLowerInvariant(),
            DataTransportKind.Text or DataTransportKind.Xml or DataTransportKind.Json =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => formatter.Format(value, kind)[1..]
        };

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Quote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);
}

public sealed class PostgreSqlBatchInsertStrategy : IDataTransferStrategy
{
    public DataTransferStrategy Strategy => DataTransferStrategy.ParameterizedBatchInsert;

    public bool CanExecute(TableLoadPlan table) => true;

    public async Task<BatchWriteResult> WriteBatchAsync(
        DataTransferContext context,
        IReadOnlyList<DataRowBuffer> rows,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        await using var connection = new NpgsqlConnection(context.TargetConnectionString);
        await PostgreSqlBinaryCopyStrategy.ObserveAsync(
            context, StreamingExecutionStage.OpenPostgreSqlConnection, "NpgsqlConnection", null,
            () => connection.OpenAsync(cancellationToken)).ConfigureAwait(false);

        var (columns, sourceIndexes) =
            await PostgreSqlWritableColumnResolver.ResolveAsync(
                connection,
                context.Table,
                cancellationToken).ConfigureAwait(false);

        var parameterNames = columns.Select((_, index) => $"@p{index}").ToArray();
        var sql = $"INSERT INTO {Quote(context.Table.TargetSchema)}.{Quote(context.Table.TargetTable)} " +
            $"({string.Join(", ", columns.Select(item => Quote(item.TargetName)))}) " +
            $"VALUES ({string.Join(", ", parameterNames)})" +
            CreateUpsertClause(context.Table, columns);

        PostgreSqlBinaryCopyStrategy.ObserveInstant(
            context, StreamingExecutionStage.GenerateWritePlan, insertSql: sql);
        NpgsqlTransaction transaction;
        var begin = PostgreSqlBinaryCopyStrategy.Start(
            context, StreamingExecutionStage.BeginPostgreSqlTransaction, nameof(NpgsqlTransaction),
            insertSql: sql);
        try
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            context.StageObserver?.Succeed(begin);
        }
        catch (Exception exception)
        {
            context.StageObserver?.Fail(begin, exception);
            throw;
        }
        await using var transactionScope = transaction;
        await using var batch = new NpgsqlBatch(connection, transaction)
        {
            Timeout = context.CommandTimeoutSeconds
        };
        PostgreSqlBinaryCopyStrategy.ObserveInstant(
            context, StreamingExecutionStage.CreatePostgreSqlWriter, nameof(NpgsqlBatch), insertSql: sql);
        foreach (var row in rows)
        {
            var command = new NpgsqlBatchCommand(sql);
            for (var index = 0; index < columns.Length; index++)
            {
                var value = row.Values[sourceIndexes[index]];
                command.Parameters.AddWithValue(parameterNames[index], value ?? DBNull.Value);
            }

            batch.BatchCommands.Add(command);
        }

        var write = PostgreSqlBinaryCopyStrategy.Start(
            context, StreamingExecutionStage.WriteFirstRow, nameof(NpgsqlBatch), insertSql: sql);
        try
        {
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            context.StageObserver?.Succeed(write);
        }
        catch (Exception exception)
        {
            context.StageObserver?.Fail(write, exception);
            throw;
        }
        PostgreSqlBinaryCopyStrategy.ObserveInstant(
            context, StreamingExecutionStage.FlushFirstBatch, nameof(NpgsqlBatch), insertSql: sql);
        await PostgreSqlBinaryCopyStrategy.ObserveAsync(
            context, StreamingExecutionStage.Commit, nameof(NpgsqlTransaction), null,
            () => transaction.CommitAsync(cancellationToken)).ConfigureAwait(false);
        return new BatchWriteResult(rows.Count, rows.Sum(item => item.ApproximateBytes), started.Elapsed);
    }

    private static string Quote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);

    private static string CreateUpsertClause(
        TableLoadPlan table,
        IReadOnlyList<ColumnMapping> columns)
    {
        if (table.TargetPreparation != TargetPreparationStrategy.Upsert)
        {
            return string.Empty;
        }

        if (table.PrimaryKeyColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Upsert for {table.TargetQualifiedName} requires a configured key.");
        }

        var keys = table.PrimaryKeyColumns.Select(source =>
            columns.FirstOrDefault(item =>
                item.SourceName.Equals(source, StringComparison.OrdinalIgnoreCase))?.TargetName ??
            throw new InvalidOperationException(
                $"Upsert key column {source} is not included for {table.TargetQualifiedName}."))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updates = columns.Where(item => !keys.Contains(item.TargetName))
            .Select(item => $"{Quote(item.TargetName)} = EXCLUDED.{Quote(item.TargetName)}")
            .ToArray();
        var conflict = $" ON CONFLICT ({string.Join(", ", keys.Select(Quote))}) ";
        return updates.Length == 0
            ? conflict + "DO NOTHING"
            : conflict + $"DO UPDATE SET {string.Join(", ", updates)}";
    }
}
