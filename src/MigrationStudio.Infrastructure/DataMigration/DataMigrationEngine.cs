using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Application.Security;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.SqlServer;
using Npgsql;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class DataMigrationEngine(
    IDataMigrationPlanner planner,
    IEnumerable<IDataTransferStrategy> transferStrategies,
    IEnumerable<IDataValueTransformer> transformers,
    IMigrationCheckpointStore checkpointStore,
    IDataMigrationValidator validator,
    ISequenceResetService sequenceResetService,
    IDataMigrationSession session,
    IMigrationPauseController pauseController,
    ITransientErrorClassifier transientErrors,
    ICanonicalValueFormatter canonicalFormatter,
    ISensitiveDataRedactor? sensitiveDataRedactor = null,
    ILogger<DataMigrationEngine>? logger = null) : IDataMigrationEngine
{

    private readonly ILogger<DataMigrationEngine> _logger =
    logger ?? NullLogger<DataMigrationEngine>.Instance;

    private static readonly Action<ILogger, string, long, long?, string, string?, string, Exception?>
    LogMigrationFailure =
        LoggerMessage.Define<
            string,
            long,
            long?,
            string,
            string?,
            string>(
            LogLevel.Error,
            new EventId(1001, nameof(LogMigrationFailure)),
            "Data migration failed for table {Table}; Batch={Batch}; Row={Row}; Category={Category}; SqlState={SqlState}; ProviderMessage={ProviderMessage}");


    public Task<DataMigrationResult> ExecuteAsync(
        DataMigrationRequest request,
        IProgress<DataMigrationProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(request with { ResumeRunId = null }, false, progress, cancellationToken);

    public Task<DataMigrationResult> ResumeAsync(
        DataMigrationRequest request,
        IProgress<DataMigrationProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(request, true, progress, cancellationToken);

    public Task RestartTableAsync(
        Guid runId,
        InventoryObjectId tableId,
        CancellationToken cancellationToken) =>
        checkpointStore.DeleteTableAsync(runId, tableId, cancellationToken);

    public Task RestartRunAsync(Guid runId, CancellationToken cancellationToken) =>
        checkpointStore.DeleteRunAsync(runId, cancellationToken);

    private async Task<DataMigrationResult> ExecuteCoreAsync(
        DataMigrationRequest request,
        bool resume,
        IProgress<DataMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = planner.CreatePlan(request);
        var stageObserver = new StreamingStageObserver(
    plan.RunId,
    progress,
    sensitiveDataRedactor ?? PassThroughRedactor.Instance,
    _logger);
        var loadPlanStage = stageObserver.Start(StreamingExecutionStage.LoadMigrationPlan);
        stageObserver.Succeed(loadPlanStage);
        session.SetPlan(plan);
        if (plan.Options.ExecutionMode == DataMigrationExecutionMode.Execute &&
            plan.Options.MigrationMode != DataMigrationMode.SchemaOnly)
        {
            var readiness = await AssessTargetReadinessAsync(
                plan,
                request.TargetConnectionString,
                cancellationToken).ConfigureAwait(false);
            if (!readiness.IsReady)
            {
                throw new DataMigrationTargetReadinessException(readiness);
            }
        }
        var checkpoint = resume
            ? await RequireValidCheckpointAsync(plan, cancellationToken).ConfigureAwait(false)
            : CreateCheckpoint(plan);
        var checkpoints = new ConcurrentDictionary<InventoryObjectId, TableCheckpoint>(
            checkpoint.Tables.ToDictionary(item => item.TableId));
        var metrics = new ConcurrentBag<TableMigrationMetrics>();
        var failures = new ConcurrentBag<MigrationFailure>();
        var validations = new ConcurrentBag<TableValidationResult>();
        var warnings = new ConcurrentBag<string>(plan.Warnings);
        var startedAt = DateTimeOffset.UtcNow;
        var checkpointStage = stageObserver.Start(StreamingExecutionStage.CreateCheckpoint);
        string checkpointPath;
        try
        {
            checkpointPath = await SaveCheckpointAsync(plan, checkpoints, cancellationToken)
                .ConfigureAwait(false);
            stageObserver.Succeed(checkpointStage);
        }
        catch (Exception exception)
        {
            stageObserver.Fail(checkpointStage, exception);
            throw;
        }
        var tableLimit = EffectiveTableParallelism(plan.Options, plan.Tables.Count);
        using var readerSlots = new SemaphoreSlim(plan.Options.MaximumConcurrentReaders);
        using var writerSlots = new SemaphoreSlim(plan.Options.MaximumConcurrentWriters);
        var activeReaders = 0;
        var activeWriters = 0;
        var activeTables = 0;
        var peakReaders = 0;
        var peakWriters = 0;
        Exception? fatal = null;

        if (plan.Options.MigrationMode != DataMigrationMode.SchemaOnly)
        {
            try
            {
                await Parallel.ForEachAsync(
                    plan.Tables,
                    new ParallelOptions
                    {
                        //dhanendra
                        /*                        MaxDegreeOfParallelism = tableLimit,
                        */
                        MaxDegreeOfParallelism = tableLimit,

                        CancellationToken = cancellationToken
                    },
                    async (table, token) =>
                    {
                        if (fatal is not null)
                        {
                            return;
                        }

                        Interlocked.Increment(ref activeTables);
                        try
                        {
                            var prior = checkpoints.GetValueOrDefault(table.SourceTableId);
                            if (prior?.State == TableMigrationState.Completed &&
                                plan.Options.ExecutionMode != DataMigrationExecutionMode.ValidationOnly)
                            {
                                return;
                            }

                            if (table.RequiresManualAction)
                            {
                                metrics.Add(SkippedMetric(table, table.ManualReason));
                                return;
                            }

                            if (plan.Options.ExecutionMode == DataMigrationExecutionMode.ValidationOnly)
                            {
                                validations.Add(await validator.ValidateAsync(request, table, token)
                                    .ConfigureAwait(false));
                                metrics.Add(ValidationOnlyMetric(table));
                                return;
                            }

                            if (plan.Options.ExecutionMode == DataMigrationExecutionMode.Preview)
                            {
                                metrics.Add(PreviewMetric(table));
                                return;
                            }

                            var result = await MigrateTableAsync(
                                request,
                                plan,
                                table,
                                prior,
                                checkpoints,
                                failures,
                                progress,
                                readerSlots,
                                writerSlots,
                                () =>
                                {
                                    var value = Interlocked.Increment(ref activeReaders);
                                    UpdatePeak(ref peakReaders, value);
                                },
                                () => Interlocked.Decrement(ref activeReaders),
                                () =>
                                {
                                    var value = Interlocked.Increment(ref activeWriters);
                                    UpdatePeak(ref peakWriters, value);
                                },
                                () => Interlocked.Decrement(ref activeWriters),
                                () => Volatile.Read(ref activeReaders),
                                () => Volatile.Read(ref activeWriters),
                                () => Volatile.Read(ref activeTables),
                                stageObserver,
                                token).ConfigureAwait(false);
                            metrics.Add(result);
                            checkpointPath = await SaveCheckpointAsync(plan, checkpoints, token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            var failure = SafeFailure(table, 0, null, exception, 0, FailureDisposition.TableStopped);
                            failures.Add(failure);
                            metrics.Add(FailedMetric(table, failure.SanitizedMessage));
                            if (checkpoints.TryGetValue(table.SourceTableId, out var failedCheckpoint))
                            {
                                checkpoints[table.SourceTableId] = failedCheckpoint with
                                {
                                    CompletedAt = DateTimeOffset.UtcNow,
                                    State = TableMigrationState.Failed
                                };
                                checkpointPath = await SaveCheckpointAsync(
                                        plan,
                                        checkpoints,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            if (plan.Options.FailurePolicy == MigrationFailurePolicy.FailFast)
                            {
                                Interlocked.CompareExchange(ref fatal, exception, null);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeTables);
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await SaveCheckpointAsync(plan, checkpoints, CancellationToken.None).ConfigureAwait(false);
                var cancelled = CreateResult(
                    plan,
                    startedAt,
                    MigrationRunState.Cancelled,
                    metrics,
                    failures,
                    validations,
                    [],
                    checkpointPath,
                    tableLimit,
                    peakReaders,
                    peakWriters,
                    warnings);
                cancelled = cancelled with { StreamingStages = stageObserver.Snapshot() };
                session.SetResult(cancelled);
                return cancelled;
            }
        }

        if (fatal is not null)
        {
        }

        var completedMetrics = metrics.ToArray();
        var sequenceResets = fatal is null &&
            plan.Options.ExecutionMode == DataMigrationExecutionMode.Execute &&
            plan.Options.MigrationMode != DataMigrationMode.SchemaOnly
            ? await sequenceResetService.ResetAsync(request, completedMetrics, cancellationToken)
                .ConfigureAwait(false)
            : [];

        if (fatal is null && plan.Options.ExecutionMode != DataMigrationExecutionMode.Preview)
        {
            foreach (var table in plan.Tables.Where(table =>
                         completedMetrics.Any(metric =>
                             metric.TableId == table.SourceTableId &&
                             metric.State is TableMigrationState.Completed
                                 or TableMigrationState.CompletedWithFailures
                                 or TableMigrationState.ValidationOnly)))
            {
                validations.Add(await validator.ValidateAsync(request, table, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        var state = DetermineState(plan, fatal, failures, validations);
        var result = CreateResult(
            plan,
            startedAt,
            state,
            metrics,
            failures,
            validations,
            sequenceResets,
            checkpointPath,
            tableLimit,
            peakReaders,
            peakWriters,
            warnings);
        result = result with { StreamingStages = stageObserver.Snapshot() };
        session.SetResult(result);
        return result;
    }

    internal static async Task<DataMigrationTargetReadiness> AssessTargetReadinessAsync(
        DataMigrationPlan plan,
        string targetConnectionString,
        CancellationToken cancellationToken)
    {
        var expectedSchemas = plan.Tables
            .Select(item => item.TargetSchema)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var expectedTables = plan.Tables
            .Select(item => $"{item.TargetSchema}\u001f{item.TargetTable}")
            .ToHashSet(StringComparer.Ordinal);
        var expectedColumns = plan.Tables
            .SelectMany(table => table.Columns.Select(column =>
                $"{table.TargetSchema}\u001f{table.TargetTable}\u001f{column.TargetName}"))
            .ToHashSet(StringComparer.Ordinal);

        await using var connection = new NpgsqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var predicate = PostgreSqlSystemSchemaPolicy.CatalogPredicate("n.nspname");
        var sql = $"""
            SELECT n.nspname
            FROM pg_catalog.pg_namespace n
            WHERE n.nspname = ANY (@schemas)
              AND {predicate};

            SELECT n.nspname, c.relname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = ANY (@schemas)
              AND c.relkind IN ('r','p')
              AND {predicate};

            SELECT n.nspname, c.relname, a.attname
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = ANY (@schemas)
              AND c.relkind IN ('r','p')
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND {predicate};
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schemas", expectedSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var existingSchemas = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            existingSchemas.Add(reader.GetString(0));
        }
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var existingTables = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            existingTables.Add($"{reader.GetString(0)}\u001f{reader.GetString(1)}");
        }
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var existingColumns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            existingColumns.Add(
                $"{reader.GetString(0)}\u001f{reader.GetString(1)}\u001f{reader.GetString(2)}");
        }

        var missingSchemas = expectedSchemas.Except(existingSchemas, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingTables = expectedTables.Except(existingTables, StringComparer.Ordinal)
            .Select(ReadableKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingColumns = expectedColumns.Except(existingColumns, StringComparer.Ordinal)
            .Select(ReadableKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new DataMigrationTargetReadiness(
            expectedSchemas.Length,
            expectedSchemas.Length - missingSchemas.Length,
            missingSchemas.Length,
            expectedTables.Count,
            expectedTables.Count - missingTables.Length,
            missingTables.Length,
            expectedColumns.Count,
            expectedColumns.Count - missingColumns.Length,
            missingColumns.Length,
            missingSchemas,
            missingTables,
            missingColumns);
    }

    private static string ReadableKey(string key) =>
        key.Replace('\u001f', '.');

    private async Task<TableMigrationMetrics> MigrateTableAsync(
        DataMigrationRequest request,
        DataMigrationPlan plan,
        TableLoadPlan table,
        TableCheckpoint? prior,
        ConcurrentDictionary<InventoryObjectId, TableCheckpoint> checkpoints,
        ConcurrentBag<MigrationFailure> failures,
        IProgress<DataMigrationProgress>? progress,
        SemaphoreSlim readerSlots,
        SemaphoreSlim writerSlots,
        Action readerEntered,
        Action readerExited,
        Action writerEntered,
        Action writerExited,
        Func<int> activeReaders,
        Func<int> activeWriters,
        Func<int> activeTables,
        StreamingStageObserver stageObserver,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var readDuration = TimeSpan.Zero;
        var writeDuration = TimeSpan.Zero;
        var rowsRead = prior?.RowsRead ?? 0;
        var rowsWritten = prior?.RowsWritten ?? 0;
        var rowsRejected = prior?.RowsRejected ?? 0;
        var bytes = 0L;
        var retries = 0;
        var batchNumber = prior?.LastCompletedBatch ?? 0;
        var started = prior?.StartedAt ?? DateTimeOffset.UtcNow;
        var checkpoint = new TableCheckpoint(
            table.SourceTableId,
            table.SourceQualifiedName,
            table.TargetQualifiedName,
            table.TransferStrategy,
            batchNumber,
            prior?.LastStableKeyCanonical,
            rowsRead,
            rowsWritten,
            rowsRejected,
            null,
            started,
            null,
            TableMigrationState.Running,
            table.IsResumable);
        checkpoints[table.SourceTableId] = checkpoint;
        if (prior is null || prior.RowsWritten == 0)
        {
            var targetSql = CreateTargetPreparationSql(request, table);
            var resolveStage = stageObserver.Start(
                StreamingExecutionStage.ResolvePostgreSqlTable,
                table,
                batchNumber,
                rowsRead,
                rowsWritten,
                currentWriter: "Npgsql target preparation command",
                postgreSqlQuery: targetSql);
            try
            {
                await PrepareTargetAsync(
                        request,
                        table,
                        plan.Options,
                        targetSql,
                        cancellationToken)
                    .ConfigureAwait(false);
                stageObserver.Succeed(resolveStage);
            }
            catch (Exception exception)
            {
                stageObserver.Fail(resolveStage, exception);
                throw;
            }
        }

        await readerSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        readerEntered();
        try
        {
            await using var source = SqlServerConnectionFactory.Create(request.SourceConnection);
            var sourceConnectionStage = stageObserver.Start(
                StreamingExecutionStage.OpenSqlServerConnection,
                table,
                batchNumber,
                rowsRead,
                rowsWritten,
                currentReader: nameof(SqlConnection));
            try
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                stageObserver.Succeed(sourceConnectionStage);
            }
            catch (Exception exception)
            {
                stageObserver.Fail(sourceConnectionStage, exception);
                throw;
            }

            var sourceTransactionStage = stageObserver.Start(
                StreamingExecutionStage.BeginSqlServerTransaction,
                table,
                batchNumber,
                rowsRead,
                rowsWritten,
                currentReader: nameof(SqlTransaction));
            SqlTransaction transaction;
            try
            {
                transaction = await BeginSourceTransactionAsync(
                    source,
                    plan.Options.ConsistencyMode,
                    cancellationToken).ConfigureAwait(false);
                stageObserver.Succeed(sourceTransactionStage);
            }
            catch (Exception exception)
            {
                stageObserver.Fail(sourceTransactionStage, exception);
                throw;
            }
            await using var transactionScope = transaction;
            await using var command = BuildSourceCommand(source, transaction, table, prior, plan.Options);
            var readStarted = stopwatch.Elapsed;
            var openReaderStage = stageObserver.Start(
                StreamingExecutionStage.OpenSqlReader,
                table,
                batchNumber,
                rowsRead,
                rowsWritten,
                currentReader: nameof(SqlDataReader),
                sqlServerQuery: command.CommandText);
            SqlDataReader reader;
            try
            {
                reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                    cancellationToken).ConfigureAwait(false);
                stageObserver.Succeed(openReaderStage);
            }
            catch (Exception exception)
            {
                stageObserver.Fail(openReaderStage, exception);
                throw;
            }
            await using var readerScope = reader;
            var included = table.Columns.Where(item => item.IsIncluded).ToArray();
            var schemaStage = stageObserver.Start(
                StreamingExecutionStage.ReadSourceSchema,
                table,
                batchNumber,
                rowsRead,
                rowsWritten,
                currentReader: nameof(SqlDataReader),
                sqlServerQuery: command.CommandText);
            int[] ordinals;
            try
            {
                ordinals = included.Select(item => reader.GetOrdinal(item.SourceName)).ToArray();
                stageObserver.Succeed(schemaStage);
            }
            catch (Exception exception)
            {
                stageObserver.Fail(schemaStage, exception);
                throw;
            }
            var buffer = new List<DataRowBuffer>(Math.Min(plan.Options.BatchRowCount, 10_000));
            var bufferedBytes = 0L;
            string? lastKey = prior?.LastStableKeyCanonical;

            var firstReadStage = stageObserver.Start(
                StreamingExecutionStage.ReadFirstRow,
                table,
                batchNumber,
                rowsRead,
                rowsWritten,
                currentReader: nameof(SqlDataReader),
                sqlServerQuery: command.CommandText);
            bool hasRow;
            try
            {
                hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                stageObserver.Succeed(firstReadStage);
            }
            catch (Exception exception)
            {
                stageObserver.Fail(firstReadStage, exception);
                throw;
            }

            var isFirstRow = true;
            while (hasRow)
            {
                await pauseController.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                var rowValues = new object?[included.Length];
                var rowBytes = 0L;
                var conversionStage = isFirstRow
                    ? stageObserver.Start(
                        StreamingExecutionStage.ConvertFirstRow,
                        table,
                        batchNumber,
                        rowsRead,
                        rowsWritten,
                        currentReader: nameof(SqlDataReader))
                    : Guid.Empty;
                try
                {
                    for (var index = 0; index < included.Length; index++)
                    {
                        var raw = await reader.IsDBNullAsync(ordinals[index], cancellationToken).ConfigureAwait(false)
                            ? null
                            : reader.GetValue(ordinals[index]);
                        var converted = DataTransportConverter.ConvertValue(raw, included[index]);
                        foreach (var transformer in transformers.Where(item => item.CanTransform(included[index])))
                        {
                            converted = await transformer.TransformAsync(
                                converted,
                                new RowTransformationContext(
                                    plan.RunId,
                                    table,
                                    included[index],
                                    rowsRead + 1,
                                    new Dictionary<string, object?>()),
                                cancellationToken).ConfigureAwait(false);
                        }

                        rowValues[index] = converted;
                        rowBytes += DataTransportConverter.EstimateBytes(converted);
                    }
                    if (isFirstRow)
                    {
                        stageObserver.Succeed(conversionStage);
                    }
                }
                catch (Exception exception)
                {
                    if (isFirstRow)
                    {
                        stageObserver.Fail(conversionStage, exception);
                    }
                    throw;
                }

                if (rowBytes > plan.Options.MaximumRowSize)
                {
                    throw new InvalidDataException(
                        $"A row in {table.SourceQualifiedName} exceeds the configured maximum row size.");
                }

                rowsRead++;
                bufferedBytes += rowBytes;
                if (table.StableResumeKey is not null)
                {
                    var keyIndex = Array.FindIndex(
                        included,
                        item => item.SourceName.Equals(
                            table.StableResumeKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (keyIndex >= 0)
                    {
                        lastKey = canonicalFormatter.Format(rowValues[keyIndex], included[keyIndex].TransportKind);
                    }
                }

                buffer.Add(new DataRowBuffer(rowsRead, rowValues, rowBytes, lastKey));
                if (buffer.Count >= EffectiveBatchRows(plan.Options, table) ||
                    bufferedBytes >= plan.Options.BatchByteSize)
                {
                    readDuration += stopwatch.Elapsed - readStarted;
                    var write = await WriteWithRecoveryAsync(
                        request,
                        plan,
                        table,
                        buffer,
                        batchNumber + 1,
                        failures,
                        writerSlots,
                        writerEntered,
                        writerExited,
                        stageObserver,
                        rowsRead,
                        rowsWritten,
                        cancellationToken).ConfigureAwait(false);
                    batchNumber++;
                    rowsWritten += write.Written;
                    rowsRejected += write.Rejected;
                    retries += write.Retries;
                    bytes += write.Bytes;
                    writeDuration += write.Duration;
                    checkpoint = checkpoint with
                    {
                        LastCompletedBatch = batchNumber,
                        LastStableKeyCanonical = lastKey,
                        RowsRead = rowsRead,
                        RowsWritten = rowsWritten,
                        RowsRejected = rowsRejected
                    };
                    checkpoints[table.SourceTableId] = checkpoint;
                    if (rowsWritten % plan.Options.CheckpointInterval < buffer.Count)
                    {
                        await SaveCheckpointAsync(plan, checkpoints, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    Report(progress, plan, table, rowsRead, rowsWritten, rowsRejected, bytes,
                        batchNumber, retries, stopwatch.Elapsed, activeReaders(), activeWriters(),
                        activeTables(), TableMigrationState.Running);
                    buffer.Clear();
                    bufferedBytes = 0;
                    readStarted = stopwatch.Elapsed;
                }

                isFirstRow = false;
                hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            readDuration += stopwatch.Elapsed - readStarted;
            if (buffer.Count > 0)
            {
                var write = await WriteWithRecoveryAsync(
                    request,
                    plan,
                    table,
                    buffer,
                    batchNumber + 1,
                    failures,
                    writerSlots,
                    writerEntered,
                    writerExited,
                    stageObserver,
                    rowsRead,
                    rowsWritten,
                    cancellationToken).ConfigureAwait(false);
                batchNumber++;
                rowsWritten += write.Written;
                rowsRejected += write.Rejected;
                retries += write.Retries;
                bytes += write.Bytes;
                writeDuration += write.Duration;
                lastKey = buffer[^1].StableKeyCanonical;
            }
            await reader.CloseAsync().ConfigureAwait(false);
            var sourceCommitStage = stageObserver.Start(
                StreamingExecutionStage.Commit,
                table,
                batchNumber,
                rowsRead,
                rowsWritten,
                currentReader: nameof(SqlTransaction));
            try
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                stageObserver.Succeed(sourceCommitStage);
            }
            catch (Exception exception)
            {
                stageObserver.Fail(sourceCommitStage, exception);
                throw;
            }
            var state = rowsRejected == 0
                ? TableMigrationState.Completed
                : TableMigrationState.CompletedWithFailures;
            checkpoints[table.SourceTableId] = checkpoint with
            {
                LastCompletedBatch = batchNumber,
                LastStableKeyCanonical = lastKey,
                RowsRead = rowsRead,
                RowsWritten = rowsWritten,
                RowsRejected = rowsRejected,
                CompletedAt = DateTimeOffset.UtcNow,
                State = state
            };
            Report(progress, plan, table, rowsRead, rowsWritten, rowsRejected, bytes,
                batchNumber, retries, stopwatch.Elapsed, activeReaders(), activeWriters(),
                activeTables(), state);
            return CreateMetric(
                table, state, rowsRead, rowsWritten, rowsRejected, bytes, readDuration,
                writeDuration, stopwatch.Elapsed, retries, tableLimit: 1, null);
        }
        finally
        {
            readerExited();
            readerSlots.Release();
        }
    }

    private async Task<WriteRecoveryResult> WriteWithRecoveryAsync(
        DataMigrationRequest request,
        DataMigrationPlan plan,
        TableLoadPlan table,
        List<DataRowBuffer> rows,
        long batch,
        ConcurrentBag<MigrationFailure> failures,
        SemaphoreSlim writerSlots,
        Action writerEntered,
        Action writerExited,
        IStreamingStageObserver stageObserver,
        long rowsRead,
        long rowsWritten,
        CancellationToken cancellationToken)
    {
        var strategy = ResolveStrategy(table, rows.Count == 1);
        var retries = 0;
        while (true)
        {
            try
            {
                await writerSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                writerEntered();
                try
                {
                    var result = await strategy.WriteBatchAsync(
                        new DataTransferContext(
                            plan.RunId,
                            table,
                            request.TargetConnectionString,
                            EffectiveCommandTimeout(plan.Options, table),
                            transformers.ToArray(),
                            stageObserver,
                            batch,
                            rowsRead,
                            rowsWritten),
                        rows,
                        cancellationToken).ConfigureAwait(false);
                    return new WriteRecoveryResult(
                        result.RowsWritten,
                        0,
                        result.BytesWritten,
                        retries,
                        result.Duration);
                }
                finally
                {
                    writerExited();
                    writerSlots.Release();
                }
            }
            catch (Exception exception) when (
                transientErrors.IsTransient(exception) &&
                retries < plan.Options.RetryCount)
            {
                retries++;
                await Task.Delay(
                    TimeSpan.FromTicks(plan.Options.RetryBackoff.Ticks * retries),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (
                rows.Count > 1 &&
                plan.Options.FailurePolicy != MigrationFailurePolicy.FailFast)
            {
                var midpoint = rows.Count / 2;
                var left = await WriteWithRecoveryAsync(
                    request, plan, table, rows.GetRange(0, midpoint), batch, failures,
                    writerSlots, writerEntered, writerExited, stageObserver, rowsRead, rowsWritten,
                    cancellationToken).ConfigureAwait(false);
                var right = await WriteWithRecoveryAsync(
                    request, plan, table, rows.GetRange(midpoint, rows.Count - midpoint), batch, failures,
                    writerSlots, writerEntered, writerExited, stageObserver, rowsRead, rowsWritten,
                    cancellationToken).ConfigureAwait(false);
                return left.Combine(right);
            }
            catch (Exception exception) when (
                rows.Count == 1 &&
                plan.Options.FailurePolicy == MigrationFailurePolicy.SkipFailedRows)
            {
                if (failures.Count >= plan.Options.MaximumFailedRows)
                {
                    throw new InvalidOperationException(
                        $"The migration exceeded the configured failed-row limit for {table.SourceQualifiedName}.",
                        exception);
                }

                failures.Add(SafeFailure(
                    table,
                    batch,
                    rows[0].Ordinal,
                    exception,
                    retries,
                    FailureDisposition.RowSkipped));
                return new WriteRecoveryResult(0, 1, 0, retries, TimeSpan.Zero);
            }
        }
    }

    private IDataTransferStrategy ResolveStrategy(TableLoadPlan table, bool singleRowFallback)
    {
        var selected = singleRowFallback ||
            table.TransferStrategy == DataTransferStrategy.CustomTransformer
            ? DataTransferStrategy.ParameterizedBatchInsert
            : table.TransferStrategy;
        return transferStrategies.FirstOrDefault(item => item.Strategy == selected && item.CanExecute(table))
            ?? transferStrategies.First(item =>
                item.Strategy == DataTransferStrategy.ParameterizedBatchInsert);
    }

    private static async Task PrepareTargetAsync(
        DataMigrationRequest request,
        TableLoadPlan table,
        DataMigrationOptions options,
        string? sql,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(request.TargetConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (sql is null)
        {
            return;
        }

        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = EffectiveCommandTimeout(options, table)
        };
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (table.TargetPreparation == TargetPreparationStrategy.FailIfNotEmpty && result is true)
        {
            throw new InvalidOperationException(
                $"Target table {table.TargetQualifiedName} contains rows. Select an explicit preparation strategy.");
        }
    }

    private static string? CreateTargetPreparationSql(
        DataMigrationRequest request,
        TableLoadPlan table)
    {
        var qualified = $"{QuotePg(table.TargetSchema)}.{QuotePg(table.TargetTable)}";
        return table.TargetPreparation switch
        {
            TargetPreparationStrategy.FailIfNotEmpty =>
                $"SELECT EXISTS (SELECT 1 FROM {qualified} LIMIT 1)",
            TargetPreparationStrategy.Truncate => $"TRUNCATE TABLE {qualified}",
            TargetPreparationStrategy.Delete => $"DELETE FROM {qualified}",
            TargetPreparationStrategy.Append => null,
            TargetPreparationStrategy.Upsert => null,
            TargetPreparationStrategy.Recreate => CreateRecreateSql(request, table, qualified),
            _ => throw new ArgumentOutOfRangeException(
                nameof(table),
                table.TargetPreparation,
                "Unknown target preparation strategy.")
        };
    }

    private static string CreateRecreateSql(
        DataMigrationRequest request,
        TableLoadPlan table,
        string qualified)
    {
        var artifact = request.Conversion.Artifacts.FirstOrDefault(item =>
            item.SourceObjectId == table.SourceTableId &&
            item.DeploymentPhase == Domain.Conversion.DeploymentPhase.Tables &&
            !item.RequiresManualReview);
        if (artifact is null)
        {
            throw new InvalidOperationException(
                $"No automatically converted table artifact is available to recreate {table.TargetQualifiedName}.");
        }

        return $"DROP TABLE IF EXISTS {qualified} CASCADE;{Environment.NewLine}{artifact.PostgreSqlDefinition}";
    }

    private static SqlCommand BuildSourceCommand(
        SqlConnection connection,
        SqlTransaction transaction,
        TableLoadPlan table,
        TableCheckpoint? prior,
        DataMigrationOptions options)
    {
        var columns = table.Columns.Where(item => item.IsIncluded).ToArray();
        var where = new List<string>();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = EffectiveCommandTimeout(options, table);
        if (table.SourcePredicate is not null)
        {
            where.Add($"({table.SourcePredicate})");
        }

        if (prior?.LastStableKeyCanonical is not null && table.StableResumeKey is not null)
        {
            where.Add($"{QuoteSqlServer(table.StableResumeKey)} > @resumeKey");
            var mapping = columns.First(item =>
                item.SourceName.Equals(table.StableResumeKey, StringComparison.OrdinalIgnoreCase));
            command.Parameters.AddWithValue(
                "@resumeKey",
                ParseCanonicalKey(prior.LastStableKeyCanonical, mapping.TransportKind));
        }

        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(item => QuoteSqlServer(item.SourceName)))} " +
            $"FROM {QuoteSqlServer(table.SourceSchema)}.{QuoteSqlServer(table.SourceTable)}" +
            (where.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", where)}") +
            (table.StableResumeKey is null ? string.Empty : $" ORDER BY {QuoteSqlServer(table.StableResumeKey)}");
        return command;
    }

    private static async Task<SqlTransaction> BeginSourceTransactionAsync(
        SqlConnection connection,
        DataConsistencyMode mode,
        CancellationToken cancellationToken)
    {
        var isolation = mode switch
        {
            DataConsistencyMode.SnapshotWhereAvailable or
            DataConsistencyMode.DatabaseSnapshotConfiguredExternally or
            DataConsistencyMode.SourceQuiesced => IsolationLevel.Snapshot,
            _ => IsolationLevel.ReadCommitted

        };
        try
        {
            return (SqlTransaction)await connection.BeginTransactionAsync(isolation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqlException) when (mode == DataConsistencyMode.SnapshotWhereAvailable)
        {
            return (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<MigrationCheckpoint> RequireValidCheckpointAsync(
        DataMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        var checkpoint = await checkpointStore.LoadAsync(plan.RunId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new InvalidOperationException($"No checkpoint exists for migration run {plan.RunId}.");
        if (!checkpoint.SourceDatabaseIdentity.Equals(plan.SourceDatabaseIdentity, StringComparison.Ordinal) ||
            !checkpoint.TargetDatabaseIdentity.Equals(plan.TargetDatabaseIdentity, StringComparison.Ordinal) ||
            !checkpoint.SourceMetadataHash.Equals(plan.SourceMetadataHash, StringComparison.Ordinal) ||
            !checkpoint.ConfigurationHash.Equals(plan.ConfigurationHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resume refused because the source, target, mappings or migration configuration changed.");
        }

        var unsafeTable = checkpoint.Tables.FirstOrDefault(item =>
            item.State != TableMigrationState.Completed &&
            item.RowsWritten > 0 &&
            !item.IsResumable);
        if (unsafeTable is not null)
        {
            throw new InvalidOperationException(
                $"{unsafeTable.SourceQualifiedName} is not safely resumable. Restart that table explicitly.");
        }

        return checkpoint;
    }

    private static MigrationCheckpoint CreateCheckpoint(DataMigrationPlan plan) =>
        new(
            MigrationCheckpoint.CurrentFormatVersion,
            plan.RunId,
            plan.SourceDatabaseIdentity,
            plan.SourceMetadataHash,
            plan.TargetDatabaseIdentity,
            plan.ConfigurationHash,
            plan.ApplicationVersion,
            DateTimeOffset.UtcNow,
            []);

    private async Task<string> SaveCheckpointAsync(
        DataMigrationPlan plan,
        ConcurrentDictionary<InventoryObjectId, TableCheckpoint> checkpoints,
        CancellationToken cancellationToken) =>
        await checkpointStore.SaveAsync(
            new MigrationCheckpoint(
                MigrationCheckpoint.CurrentFormatVersion,
                plan.RunId,
                plan.SourceDatabaseIdentity,
                plan.SourceMetadataHash,
                plan.TargetDatabaseIdentity,
                plan.ConfigurationHash,
                plan.ApplicationVersion,
                DateTimeOffset.UtcNow,
                checkpoints.Values.OrderBy(item => item.SourceQualifiedName, StringComparer.Ordinal).ToArray()),
            cancellationToken).ConfigureAwait(false);

    private static DataMigrationResult CreateResult(
        DataMigrationPlan plan,
        DateTimeOffset started,
        MigrationRunState state,
        IEnumerable<TableMigrationMetrics> metrics,
        IEnumerable<MigrationFailure> failures,
        IEnumerable<TableValidationResult> validations,
        IReadOnlyList<SequenceResetResult> sequenceResets,
        string checkpointPath,
        int parallelism,
        int readers,
        int writers,
        IEnumerable<string> warnings) =>
        new(
            plan.RunId,
            state,
            started,
            DateTimeOffset.UtcNow,
            metrics.OrderBy(item => item.Table, StringComparer.Ordinal).ToArray(),
            failures.ToArray(),
            validations.OrderBy(item => item.Table, StringComparer.Ordinal).ToArray(),
            sequenceResets,
            checkpointPath,
            parallelism,
            readers,
            writers,
            warnings.ToArray());

    private static MigrationRunState DetermineState(
        DataMigrationPlan plan,
        Exception? fatal,
        IEnumerable<MigrationFailure> failures,
        IEnumerable<TableValidationResult> validations)
    {
        if (fatal is not null)
        {
            return MigrationRunState.Failed;
        }

        if (plan.Options.ExecutionMode == DataMigrationExecutionMode.ValidationOnly)
        {
            return MigrationRunState.ValidationOnly;
        }

        return failures.Any() || validations.Any(item => item.Outcome == ValidationOutcome.Failed)
            ? MigrationRunState.CompletedWithFailures
            : MigrationRunState.Completed;
    }


    private MigrationFailure SafeFailure(
        TableLoadPlan table,
        long batch,
        long? row,
        Exception exception,
        int retry,
        FailureDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var providerException = GetRelevantDatabaseException(exception);
        var sqlState = providerException switch
        {
            PostgresException postgresException => postgresException.SqlState,
            SqlException sqlException => sqlException.Number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };

        var category = transientErrors.Classify(providerException);
        var originalMessage = providerException switch
        {
            PostgresException postgresException => BuildPostgreSqlError(postgresException),
            SqlException sqlException => BuildSqlServerError(sqlException),
            NpgsqlException npgsqlException =>
                $"NpgsqlException: {npgsqlException.Message}",
            _ => BuildExceptionChainMessage(exception)
        };

        var storedMessage = sensitiveDataRedactor is null
            ? originalMessage
            : sensitiveDataRedactor.Redact(originalMessage);

        LogMigrationFailure(
     _logger,
     table.SourceQualifiedName,
     batch,
     row,
     category.ToString(),
     sqlState,
     storedMessage,
     exception);

        return new MigrationFailure(
            table.SourceQualifiedName,
            batch,
            row,
            null,
            null,
            null,
            null,
            sqlState,
            storedMessage,
            retry,
            category,
            disposition);
    }

    private static Exception GetRelevantDatabaseException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Exception? current = exception;
        Exception deepest = exception;
        NpgsqlException? npgsqlException = null;

        while (current is not null)
        {
            deepest = current;

            if (current is PostgresException or SqlException)
            {
                return current;
            }

            if (current is NpgsqlException candidate)
            {
                npgsqlException = candidate;
            }

            current = current.InnerException;
        }

        return npgsqlException ?? deepest;
    }

    private static string BuildPostgreSqlError(PostgresException exception)
    {
        var parts = new List<string>
        {
            $"PostgreSQL error {exception.SqlState}: {exception.MessageText}"
        };

        if (!string.IsNullOrWhiteSpace(exception.Detail))
        {
            parts.Add($"Detail: {exception.Detail}");
        }

        if (!string.IsNullOrWhiteSpace(exception.Hint))
        {
            parts.Add($"Hint: {exception.Hint}");
        }

        if (!string.IsNullOrWhiteSpace(exception.Where))
        {
            parts.Add($"Where: {exception.Where}");
        }

        if (!string.IsNullOrWhiteSpace(exception.SchemaName))
        {
            parts.Add($"Schema: {exception.SchemaName}");
        }

        if (!string.IsNullOrWhiteSpace(exception.TableName))
        {
            parts.Add($"Table: {exception.TableName}");
        }

        if (!string.IsNullOrWhiteSpace(exception.ColumnName))
        {
            parts.Add($"Column: {exception.ColumnName}");
        }

        if (!string.IsNullOrWhiteSpace(exception.ConstraintName))
        {
            parts.Add($"Constraint: {exception.ConstraintName}");
        }

        if (!string.IsNullOrWhiteSpace(exception.DataTypeName))
        {
            parts.Add($"Data type: {exception.DataTypeName}");
        }

        if (exception.Position > 0)
        {
            parts.Add($"Position: {exception.Position.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(" | ", parts);
    }

    private static string BuildSqlServerError(SqlException exception)
    {
        var errors = exception.Errors
            .Cast<SqlError>()
            .Select(error =>
                $"SQL Server error {error.Number}, state {error.State}, " +
                $"severity {error.Class}, procedure {error.Procedure ?? "(none)"}, " +
                $"line {error.LineNumber}: {error.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return errors.Length == 0
            ? $"SqlException: {exception.Message}"
            : string.Join(" | ", errors);
    }

    private static string BuildExceptionChainMessage(Exception exception)
    {
        var messages = new List<string>();
        Exception? current = exception;

        while (current is not null)
        {
            var message = $"{current.GetType().Name}: {current.Message}";
            if (!messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }

            current = current.InnerException;
        }

        return string.Join(" | Inner: ", messages);
    }

    private static void Report(
        IProgress<DataMigrationProgress>? progress,
        DataMigrationPlan plan,
        TableLoadPlan table,
        long read,
        long written,
        long rejected,
        long bytes,
        long batch,
        int retries,
        TimeSpan elapsed,
        int readers,
        int writers,
        int tables,
        TableMigrationState state)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        progress?.Report(new DataMigrationProgress(
            plan.RunId,
            table.SourceTableId,
            "Data transfer",
            $"{table.SourceQualifiedName}: {written:N0} rows transferred",
            read,
            written,
            rejected,
            table.EstimatedRows,
            batch,
            retries,
            readers,
            writers,
            tables,
            written / seconds,
            bytes / seconds,
            elapsed,
            state));
    }

    private static int EffectiveTableParallelism(DataMigrationOptions options, int tableCount) =>
        options.ParallelismMode switch
        {
            ParallelismMode.Sequential => 1,
            ParallelismMode.Fixed => Math.Min(options.MaximumConcurrentTables, Math.Max(tableCount, 1)),
            ParallelismMode.Adaptive => Math.Min(
                Math.Min(options.MaximumConcurrentTables, Environment.ProcessorCount),
                Math.Max(tableCount, 1)),
            _ => 1
        };

    private static int EffectiveBatchRows(DataMigrationOptions options, TableLoadPlan table) =>
        options.TableOverrides.FirstOrDefault(item => item.TableId == table.SourceTableId)?.BatchRowCount ??
        options.BatchRowCount;

    private static int EffectiveCommandTimeout(DataMigrationOptions options, TableLoadPlan table) =>
        options.TableOverrides.FirstOrDefault(item => item.TableId == table.SourceTableId)?.CommandTimeoutSeconds ??
        options.CommandTimeoutSeconds;

    private static object ParseCanonicalKey(string value, DataTransportKind kind)
    {
        var payload = value.Length > 0 ? value[1..] : value;
        return kind switch
        {
            DataTransportKind.Signed16 => short.Parse(payload, CultureInfo.InvariantCulture),
            DataTransportKind.Signed32 => int.Parse(payload, CultureInfo.InvariantCulture),
            DataTransportKind.Signed64 => long.Parse(payload, CultureInfo.InvariantCulture),
            DataTransportKind.Uuid => Guid.Parse(payload),
            _ => throw new InvalidOperationException("The configured resume key type is not supported.")
        };
    }

    private static void UpdatePeak(ref int peak, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref peak);
            if (value <= current || Interlocked.CompareExchange(ref peak, value, current) == current)
            {
                return;
            }
        }
    }

    private static TableMigrationMetrics CreateMetric(
        TableLoadPlan table,
        TableMigrationState state,
        long read,
        long written,
        long rejected,
        long bytes,
        TimeSpan readDuration,
        TimeSpan writeDuration,
        TimeSpan total,
        int retries,
        int tableLimit,
        string? message)
    {
        var seconds = Math.Max(total.TotalSeconds, 0.001);
        return new TableMigrationMetrics(
            table.SourceTableId,
            table.SourceQualifiedName,
            state,
            read,
            written,
            rejected,
            bytes,
            readDuration,
            writeDuration,
            TimeSpan.Zero,
            total,
            retries,
            (int)Math.Min(rejected, int.MaxValue),
            tableLimit,
            GC.GetTotalMemory(false),
            written / seconds,
            bytes / seconds,
            message);
    }

    private static TableMigrationMetrics SkippedMetric(TableLoadPlan table, string? message) =>
        CreateMetric(table, TableMigrationState.Skipped, 0, 0, 0, 0, TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, message);

    private static TableMigrationMetrics PreviewMetric(TableLoadPlan table) =>
        CreateMetric(table, TableMigrationState.Skipped, 0, 0, 0, 0, TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, "Preview only; no data was transferred.");

    private static TableMigrationMetrics ValidationOnlyMetric(TableLoadPlan table) =>
        CreateMetric(table, TableMigrationState.ValidationOnly, 0, 0, 0, 0, TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, "Validation only.");

    private static TableMigrationMetrics FailedMetric(TableLoadPlan table, string message) =>
        CreateMetric(table, TableMigrationState.Failed, 0, 0, 0, 0, TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, message);

    private static string QuotePg(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);

    private static string QuoteSqlServer(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private sealed record WriteRecoveryResult(
        long Written,
        long Rejected,
        long Bytes,
        int Retries,
        TimeSpan Duration)
    {
        public WriteRecoveryResult Combine(WriteRecoveryResult other) =>
            new(
                Written + other.Written,
                Rejected + other.Rejected,
                Bytes + other.Bytes,
                Retries + other.Retries,
                Duration + other.Duration);
    }

    private sealed class PassThroughRedactor : ISensitiveDataRedactor
    {
        public static PassThroughRedactor Instance { get; } = new();

        public string Redact(string? value) => value ?? string.Empty;

        public string RedactConnectionString(string? connectionString) => string.Empty;
    }
}
