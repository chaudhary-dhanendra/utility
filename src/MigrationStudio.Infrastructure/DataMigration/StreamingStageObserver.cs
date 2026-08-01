using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Security;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using Npgsql;

namespace MigrationStudio.Infrastructure.DataMigration;

internal sealed partial class StreamingStageObserver(
    Guid runId,
    IProgress<DataMigrationProgress>? progress,
    ISensitiveDataRedactor redactor,
    ILogger logger) : IStreamingStageObserver
{
    private readonly ConcurrentDictionary<Guid, StreamingStageDiagnostic> _entries = [];
    private readonly ConcurrentDictionary<Guid, InventoryObjectId> _tableIds = [];
    private readonly ConcurrentDictionary<InventoryObjectId, StreamingExecutionStage> _lastSuccessful = [];

    public Guid Start(
        StreamingExecutionStage stage,
        TableLoadPlan? table = null,
        long currentBatch = 0,
        long rowsRead = 0,
        long rowsWritten = 0,
        string? currentReader = null,
        string? currentWriter = null,
        string? sqlServerQuery = null,
        string? postgreSqlQuery = null,
        string? copySql = null,
        string? insertSql = null)
    {
        var id = Guid.NewGuid();
        var entry = new StreamingStageDiagnostic(
            id,
            stage,
            StreamingStageOutcome.Running,
            DateTimeOffset.UtcNow,
            null,
            0,
            SourceSchema(table),
            SourceTable(table),
            table?.TargetQualifiedName,
            currentBatch,
            rowsRead,
            rowsWritten,
            currentReader,
            currentWriter,
            SanitizeSql(sqlServerQuery),
            SanitizeSql(postgreSqlQuery),
            SanitizeSql(copySql),
            SanitizeSql(insertSql),
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        _entries[id] = entry;
        if (table is not null)
        {
            _tableIds[id] = table.SourceTableId;
        }
        Publish(entry);
        LogStageStarted(
            logger,
            (int)stage,
            stage,
            entry.SourceTable ?? "(run)",
            currentBatch,
            rowsRead,
            rowsWritten);
        return id;
    }

    public void Succeed(Guid executionId)
    {
        if (!_entries.TryGetValue(executionId, out var entry))
        {
            return;
        }

        var completed = Complete(entry, StreamingStageOutcome.Succeeded);
        _entries[executionId] = completed;
        if (_tableIds.TryGetValue(executionId, out var tableId))
        {
            _lastSuccessful[tableId] = completed.Stage;
        }

        Publish(completed);
        LogStageSucceeded(
            logger,
            (int)completed.Stage,
            completed.Stage,
            completed.SourceTable ?? "(run)",
            completed.ElapsedMilliseconds);
    }

    public void Fail(Guid executionId, Exception exception)
    {
        if (!_entries.TryGetValue(executionId, out var entry))
        {
            return;
        }

        var sqlState = FindSqlState(exception);
        var reason = redactor.Redact(exception.Message);
        var failed = Complete(entry, StreamingStageOutcome.Failed) with
        {
            SqlState = sqlState,
            FailureComponent = Component(entry.Stage),
            FailureReason = reason,
            Remediation = Remediation(entry.Stage, sqlState),
            ExceptionType = exception.GetType().FullName,
            InnerException = exception.InnerException is null
                ? null
                : redactor.Redact($"{exception.InnerException.GetType().FullName}: {exception.InnerException.Message}"),
            StackTrace = redactor.Redact(exception.StackTrace)
        };
        _entries[executionId] = failed;
        Publish(failed);
        LogStageFailed(
            logger,
            (int)failed.Stage,
            failed.Stage,
            failed.SourceTable ?? "(run)",
            failed.FailureComponent,
            failed.SqlState,
            failed.FailureReason);
    }

    public IReadOnlyList<StreamingStageDiagnostic> Snapshot() =>
        _entries.Values
            .OrderBy(item => item.StartedAt)
            .ThenBy(item => item.ExecutionId)
            .ToArray();

    private void Publish(StreamingStageDiagnostic entry)
    {
        var tableId = _tableIds.GetValueOrDefault(entry.ExecutionId);
        var hasTable = _tableIds.ContainsKey(entry.ExecutionId);
        _lastSuccessful.TryGetValue(tableId, out var lastSuccessful);
        var failed = entry.Outcome == StreamingStageOutcome.Failed;
        progress?.Report(new DataMigrationProgress(
            runId,
            hasTable ? tableId : null,
            $"Stage {(int)entry.Stage}: {entry.Stage}",
            failed
                ? $"{entry.Stage} failed: {entry.FailureReason}"
                : $"{entry.Stage}: {entry.Outcome}",
            entry.RowsRead,
            entry.RowsWritten,
            0,
            0,
            entry.CurrentBatch,
            0,
            entry.CurrentReader is null ? 0 : 1,
            entry.CurrentWriter is null ? 0 : 1,
            entry.SourceTable is null ? 0 : 1,
            0,
            0,
            TimeSpan.FromMilliseconds(entry.ElapsedMilliseconds),
            failed ? TableMigrationState.Failed : TableMigrationState.Running)
        {
            StreamingStage = entry.Stage,
            CurrentTable = entry.SourceTable,
            CurrentReader = entry.CurrentReader,
            CurrentWriter = entry.CurrentWriter,
            LastSuccessfulStage = lastSuccessful == default ? null : lastSuccessful,
            FailureStage = failed ? entry.Stage : null,
            FailureComponent = entry.FailureComponent,
            FailureReason = entry.FailureReason,
            Remediation = entry.Remediation
        });
    }

    private static StreamingStageDiagnostic Complete(
        StreamingStageDiagnostic entry,
        StreamingStageOutcome outcome)
    {
        var completedAt = DateTimeOffset.UtcNow;
        return entry with
        {
            Outcome = outcome,
            CompletedAt = completedAt,
            ElapsedMilliseconds = Math.Max(0, (long)(completedAt - entry.StartedAt).TotalMilliseconds)
        };
    }

    private string? SanitizeSql(string? sql) =>
        string.IsNullOrWhiteSpace(sql) ? null : redactor.Redact(sql);

    private static string? FindSqlState(Exception exception) =>
        exception switch
        {
            PostgresException postgres => postgres.SqlState,
            NpgsqlException { InnerException: PostgresException postgres } => postgres.SqlState,
            _ => exception.InnerException is null ? null : FindSqlState(exception.InnerException)
        };

    private static string Component(StreamingExecutionStage stage) =>
        stage switch
        {
            <= StreamingExecutionStage.ReadSourceSchema => "SQL Server reader",
            StreamingExecutionStage.ResolvePostgreSqlTable => "PostgreSQL target resolver",
            StreamingExecutionStage.GenerateWritePlan => "PostgreSQL write-plan generator",
            >= StreamingExecutionStage.OpenPostgreSqlConnection and <= StreamingExecutionStage.Commit =>
                "PostgreSQL writer",
            _ => "Streaming coordinator"
        };

    private static string Remediation(StreamingExecutionStage stage, string? sqlState)
    {
        if (sqlState == PostgresErrorCodes.UndefinedTable)
        {
            return "Deploy or create the mapped PostgreSQL target table, verify its mapped schema/name, then retry the table.";
        }

        return stage switch
        {
            StreamingExecutionStage.OpenSqlServerConnection =>
                "Verify SQL Server connectivity and permissions, then retry.",
            StreamingExecutionStage.OpenSqlReader or StreamingExecutionStage.ReadSourceSchema =>
                "Verify the generated source query and SELECT permissions.",
            StreamingExecutionStage.ResolvePostgreSqlTable =>
                "Verify that the mapped target schema and table exist and that the login can access them.",
            StreamingExecutionStage.InitializeCopy =>
                "Verify the target relation and mapped columns, then retry the table.",
            _ => "Review the sanitized stage details and correct the failing component before retrying."
        };
    }

    private static string? SourceSchema(TableLoadPlan? table)
    {
        if (table is null)
        {
            return null;
        }

        var separator = table.SourceQualifiedName.IndexOf('.');
        return separator < 0 ? null : table.SourceQualifiedName[..separator].Trim('[', ']');
    }

    private static string? SourceTable(TableLoadPlan? table) => table?.SourceQualifiedName;

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Information,
        Message = "Streaming stage {StageNumber} {Stage} started for {Table}; Batch={Batch}; RowsRead={RowsRead}; RowsWritten={RowsWritten}")]
    private static partial void LogStageStarted(
        ILogger logger,
        int stageNumber,
        StreamingExecutionStage stage,
        string table,
        long batch,
        long rowsRead,
        long rowsWritten);

    [LoggerMessage(
        EventId = 5102,
        Level = LogLevel.Information,
        Message = "Streaming stage {StageNumber} {Stage} succeeded for {Table}; ElapsedMs={ElapsedMs}")]
    private static partial void LogStageSucceeded(
        ILogger logger,
        int stageNumber,
        StreamingExecutionStage stage,
        string table,
        long elapsedMs);

    [LoggerMessage(
        EventId = 5103,
        Level = LogLevel.Error,
        Message = "Streaming stage {StageNumber} {Stage} failed for {Table}; Component={Component}; SQLSTATE={SqlState}; Reason={Reason}")]
    private static partial void LogStageFailed(
        ILogger logger,
        int stageNumber,
        StreamingExecutionStage stage,
        string table,
        string? component,
        string? sqlState,
        string? reason);

}
