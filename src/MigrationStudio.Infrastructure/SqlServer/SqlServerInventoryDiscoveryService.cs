using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.SqlServer;

public sealed partial class SqlServerInventoryDiscoveryService(
    ILogger<SqlServerInventoryDiscoveryService> logger,
    IDiscoveryDiagnosticSession? diagnosticSession = null,
    ISourceObjectScopePolicy? sourceScopePolicy = null) : IInventoryDiscoveryService
{
    private const int StageCount = 15;
    internal const CommandBehavior CatalogReaderBehavior = CommandBehavior.Default;

    public async Task<InventorySnapshot> DiscoverAsync(
        InventoryDiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Connection.Validate();
        ValidateOptions(request.Options);

        var correlationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var diagnostics = new List<DiscoveryStageDiagnostic>();
        var accumulator = new InventoryAccumulator(request.Connection.Database);
        var unwrappedStage = DiscoveryStage.TestingConnection;
        var unwrappedQueryId = "SQLSERVER.CONNECTION.OPEN";
        PublishReport(
            request,
            correlationId,
            startedAt,
            null,
            accumulator,
            DiscoveryStage.Initializing,
            DiscoveryStageState.Running,
            diagnostics,
            "Discovery initialized.",
            partialInventoryDiscarded: false);

        try
        {
            Report(
                progress,
                DiscoveryStage.TestingConnection,
                DiscoveryStageState.Running,
                "SQLSERVER.CONNECTION.OPEN",
                true,
                1,
                0,
                accumulator,
                "Opening SQL Server connection.");
            await using var connection = SqlServerConnectionFactory.Create(request.Connection);
            var connectionStartedAt = DateTimeOffset.UtcNow;
            var connectionStopwatch = Stopwatch.StartNew();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            connectionStopwatch.Stop();
            diagnostics.Add(new DiscoveryStageDiagnostic(
                DiscoveryStage.TestingConnection,
                DiscoveryStageState.Completed,
                "SQLSERVER.CONNECTION.OPEN",
                true,
                1,
                connectionStartedAt,
                DateTimeOffset.UtcNow,
                connectionStopwatch.ElapsedMilliseconds,
                0,
                [],
                "SQL Server connection opened successfully.",
                false));
            Report(
                progress,
                DiscoveryStage.TestingConnection,
                DiscoveryStageState.Completed,
                "SQLSERVER.CONNECTION.OPEN",
                true,
                1,
                0,
                accumulator,
                "SQL Server connection opened successfully.");
            DiscoveryLog.Starting(
                logger,
                request.ScopeMode,
                correlationId);

            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.LoadingServerMetadata,
                "SQLSERVER.SERVER_METADATA.V1", SqlServerCatalogQueries.ServerMetadata, 1,
                accumulator, progress,
                reader => ReadServerMetadataAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            if (accumulator.SqlServerMajorVersion < 13)
            {
                throw CreateFailure(
                    DiscoveryStage.LoadingServerMetadata,
                    "SQLSERVER.SERVER_VERSION.UNSUPPORTED",
                    correlationId,
                    $"SQL Server major version {accumulator.SqlServerMajorVersion} is unsupported. SQL Server 2016 or later is required.",
                    [],
                    new NotSupportedException("Unsupported SQL Server catalog version."),
                    false,
                    "Upgrade the source or use a supported SQL Server 2016 or later instance.");
            }

            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.LoadingDatabaseMetadata,
                "SQLSERVER.DATABASE_METADATA.V2", SqlServerCatalogQueries.DatabaseMetadata, 2,
                accumulator, progress,
                reader => ReadDatabaseMetadataAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.DiscoveringSchemas,
                "SQLSERVER.SCHEMAS.V1", SqlServerCatalogQueries.Schemas, 3,
                accumulator, progress,
                reader => ReadSchemasAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.DiscoveringObjects,
                $"SQLSERVER.OBJECTS.V{accumulator.SqlServerMajorVersion}",
                SqlServerCatalogQueries.Objects(accumulator.SqlServerMajorVersion), 4,
                accumulator, progress,
                reader => ReadObjectsAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.DiscoveringTables,
                $"SQLSERVER.TABLES.V{accumulator.SqlServerMajorVersion}",
                SqlServerCatalogQueries.Tables(accumulator.SqlServerMajorVersion), 5,
                accumulator, progress,
                reader => ReadTablesAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.DiscoveringColumns,
                $"SQLSERVER.COLUMNS.V{accumulator.SqlServerMajorVersion}",
                SqlServerCatalogQueries.Columns(accumulator.SqlServerMajorVersion), 6,
                accumulator, progress,
                reader => ReadColumnsAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.DiscoveringConstraints,
                "SQLSERVER.CONSTRAINTS.V1", SqlServerCatalogQueries.Constraints, 7,
                accumulator, progress,
                reader => ReadConstraintsAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.DiscoveringIndexes,
                "SQLSERVER.INDEXES.V1", SqlServerCatalogQueries.Indexes, 8,
                accumulator, progress,
                reader => ReadIndexesAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.DiscoveringProgrammableObjects,
                "SQLSERVER.PROGRAMMABLE.V1", SqlServerCatalogQueries.ProgrammableObjects, 9,
                accumulator, progress,
                reader => ReadProgrammableObjectsAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);
            await ExecuteRequiredStageAsync(
                connection, request, DiscoveryStage.DiscoveringDependencies,
                "SQLSERVER.DEPENDENCIES.V1", SqlServerCatalogQueries.Dependencies, 10,
                accumulator, progress,
                reader => ReadDependenciesAsync(reader, accumulator, cancellationToken),
                diagnostics, correlationId, cancellationToken);

            await ExecuteOptionalStageAsync(
                connection, request, DiscoveryStage.DiscoveringExtendedProperties,
                "SQLSERVER.EXTENDED_PROPERTIES.V1", SqlServerCatalogQueries.ExtendedProperties, 11,
                reader => ReadExtendedPropertiesAsync(reader, accumulator, cancellationToken),
                accumulator, progress, diagnostics, correlationId, cancellationToken);
            if (request.Options.IncludeServerLevelObjects)
            {
                await ExecuteOptionalStageAsync(
                    connection, request, DiscoveryStage.DiscoveringServerTriggers,
                    "SQLSERVER.SERVER_TRIGGERS.V1", SqlServerCatalogQueries.ServerTriggers, 11,
                    reader => ReadServerTriggersAsync(reader, accumulator, cancellationToken),
                    accumulator, progress, diagnostics, correlationId, cancellationToken);
            }
            await ExecuteOptionalStageAsync(
                connection, request, DiscoveryStage.DiscoveringSecurity,
                "SQLSERVER.SECURITY.V1", SqlServerCatalogQueries.Security, 12,
                reader => ReadSecurityAsync(reader, accumulator, cancellationToken),
                accumulator, progress, diagnostics, correlationId, cancellationToken);
            await ExecuteOptionalStageAsync(
                connection, request, DiscoveryStage.DiscoveringAdvancedFeatures,
                $"SQLSERVER.ADVANCED.V{accumulator.SqlServerMajorVersion}",
                SqlServerCatalogQueries.Advanced(accumulator.SqlServerMajorVersion), 13,
                reader => ReadAdvancedAsync(reader, accumulator, cancellationToken),
                accumulator, progress, diagnostics, correlationId, cancellationToken);
            await ExecuteOptionalStageAsync(
                connection, request, DiscoveryStage.DiscoveringExternalObjects,
                $"SQLSERVER.EXTERNAL.V{accumulator.SqlServerMajorVersion}",
                SqlServerCatalogQueries.ExternalAndPartitioning(accumulator.SqlServerMajorVersion), 14,
                reader => ReadExternalAndPartitioningAsync(reader, accumulator, cancellationToken),
                accumulator, progress, diagnostics, correlationId, cancellationToken);
            if (request.Options.IncludeSqlAgent)
            {
                await ExecuteOptionalStageAsync(
                    connection, request, DiscoveryStage.DiscoveringSqlAgent,
                    "SQLSERVER.SQL_AGENT.V1", SqlServerCatalogQueries.SqlAgent, 14,
                    reader => ReadSqlAgentAsync(reader, accumulator, cancellationToken),
                    accumulator, progress, diagnostics, correlationId, cancellationToken);
            }

            unwrappedStage = DiscoveryStage.BuildingDependencyGraph;
            unwrappedQueryId = "INVENTORY.FINALIZE.V1";
            Report(
                progress,
                DiscoveryStage.BuildingDependencyGraph,
                DiscoveryStageState.Running,
                "INVENTORY.FINALIZE.V1",
                true,
                1,
                14,
                accumulator,
                "Building dependency graph, detecting cycles, and finalizing inventory.");
            cancellationToken.ThrowIfCancellationRequested();
            var applicationVersion =
                typeof(IInventoryDiscoveryService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            var completeSnapshot = accumulator.Build(applicationVersion);
            cancellationToken.ThrowIfCancellationRequested();
            var selectedSnapshot = InventoryScopeSelector.Apply(
                completeSnapshot,
                request,
                sourceScopePolicy);
            LogDiagnosticColumnDiscovery(selectedSnapshot);
            Report(
                progress,
                DiscoveryStage.Completed,
                DiscoveryStageState.Completed,
                "DISCOVERY.COMPLETE",
                true,
                1,
                15,
                accumulator,
                $"Discovery complete: {selectedSnapshot.Objects.Count:N0} objects and {selectedSnapshot.Findings.Count:N0} findings.");
            PublishReport(
                request,
                correlationId,
                startedAt,
                DateTimeOffset.UtcNow,
                accumulator,
                DiscoveryStage.Completed,
                DiscoveryStageState.Completed,
                diagnostics,
                "Discovery completed successfully.",
                partialInventoryDiscarded: false);
            DiscoveryLog.Completed(
                logger,
                selectedSnapshot.Objects.Count,
                selectedSnapshot.Dependencies.Count,
                selectedSnapshot.Findings.Count,
                correlationId);
            return selectedSnapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report(
                progress,
                DiscoveryStage.Cancelled,
                DiscoveryStageState.Cancelled,
                "DISCOVERY.CANCELLED",
                true,
                1,
                Math.Min(StageCount - 1, diagnostics.Count(item =>
                    item.State is DiscoveryStageState.Completed or
                        DiscoveryStageState.CompletedWithFindings)),
                accumulator,
                "Discovery cancelled after commands, readers, and the connection were released.");
            PublishReport(
                request,
                correlationId,
                startedAt,
                DateTimeOffset.UtcNow,
                accumulator,
                DiscoveryStage.Cancelled,
                DiscoveryStageState.Cancelled,
                diagnostics,
                "Discovery was cancelled. Partial inventory was discarded.",
                partialInventoryDiscarded: true);
            DiscoveryLog.Cancelled(logger, correlationId);
            throw;
        }
        catch (SourceDatabaseException exception)
        {
            Report(
                progress,
                exception.Stage,
                DiscoveryStageState.Failed,
                exception.QueryId,
                true,
                1,
                Math.Min(StageCount - 1, diagnostics.Count(item =>
                    item.State is DiscoveryStageState.Completed or
                        DiscoveryStageState.CompletedWithFindings)),
                accumulator,
                BuildFailureMessage(exception));
            PublishReport(
                request,
                correlationId,
                startedAt,
                DateTimeOffset.UtcNow,
                accumulator,
                exception.Stage,
                DiscoveryStageState.Failed,
                diagnostics,
                BuildFailureMessage(exception),
                partialInventoryDiscarded: true);
            DiscoveryLog.Failed(
                logger,
                exception.Stage,
                exception.QueryId,
                correlationId,
                exception);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var errors = exception is SqlException sqlException
                ? SqlServerConnectionFactory.MapErrors(sqlException)
                : [];
            var wrapped = CreateFailure(
                unwrappedStage,
                unwrappedQueryId,
                correlationId,
                $"{unwrappedStage} failed in {unwrappedQueryId}. {FirstError(errors, exception)}",
                errors,
                exception,
                false,
                Remediation(errors, exception));
            diagnostics.Add(new DiscoveryStageDiagnostic(
                unwrappedStage,
                DiscoveryStageState.Failed,
                unwrappedQueryId,
                true,
                1,
                startedAt,
                DateTimeOffset.UtcNow,
                Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds),
                0,
                errors,
                SafeExceptionSummary(exception, errors),
                false));
            Report(
                progress,
                wrapped.Stage,
                DiscoveryStageState.Failed,
                wrapped.QueryId,
                true,
                1,
                Math.Min(StageCount - 1, diagnostics.Count(item =>
                    item.State is DiscoveryStageState.Completed or
                        DiscoveryStageState.CompletedWithFindings)),
                accumulator,
                BuildFailureMessage(wrapped));
            PublishReport(
                request,
                correlationId,
                startedAt,
                DateTimeOffset.UtcNow,
                accumulator,
                wrapped.Stage,
                DiscoveryStageState.Failed,
                diagnostics,
                BuildFailureMessage(wrapped),
                partialInventoryDiscarded: true);
            DiscoveryLog.Failed(
                logger,
                wrapped.Stage,
                wrapped.QueryId,
                correlationId,
                wrapped);
            throw wrapped;
        }
    }

    private void LogDiagnosticColumnDiscovery(InventorySnapshot snapshot)
    {
        var table = snapshot.Objects.FirstOrDefault(item =>
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals("verify_observe1819", StringComparison.OrdinalIgnoreCase) &&
            snapshot.Tables.Any(facet => facet.ObjectId == item.Id));
        var column = table is null
            ? null
            : snapshot.Columns.FirstOrDefault(item =>
                item.ParentObjectId == table.Id &&
                item.Name.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase));
        var key = table is not null && column is not null
            ? new ColumnIdentifierKey(table.Id, column.ColumnId).ToString()
            : string.Empty;
        if (logger.IsEnabled(LogLevel.Information))
        {
            var details =
                $"DiscoveryInventoryCreated; ObjectId={column?.ObjectId}; " +
                $"ParentTableObjectId={table?.Id}; ColumnId={column?.ColumnId}; " +
                $"Schema={table?.SourceSchema ?? "nrega_SK"}; Table={table?.SourceName ?? "verify_observe1819"}; " +
                $"Column={column?.Name ?? "discre_obsrv"}; CanonicalKey={key}; TargetIdentifier=; " +
                $"MappingSetId={Guid.Empty}; MappingVersion={IdentifierMappingSchema.CurrentVersion}; " +
                $"Exists={column is not null}; Included={table?.IsIncluded == true}; LoadedFromCache=False";
            LogIdentifierLifecycle(logger, details);
        }
    }

    private async Task ExecuteRequiredStageAsync(
        SqlConnection connection,
        InventoryDiscoveryRequest request,
        DiscoveryStage stage,
        string queryId,
        string commandText,
        int stageNumber,
        InventoryAccumulator accumulator,
        IProgress<DiscoveryProgress>? progress,
        Func<SqlDataReader, Task> readAsync,
        List<DiscoveryStageDiagnostic> diagnostics,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        await ExecuteStageAsync(
            connection, request, stage, queryId, commandText, stageNumber, true,
            accumulator, progress, readAsync, diagnostics, correlationId, cancellationToken)
            .ConfigureAwait(false);

    private async Task ExecuteOptionalStageAsync(
        SqlConnection connection,
        InventoryDiscoveryRequest request,
        DiscoveryStage stage,
        string queryId,
        string commandText,
        int stageNumber,
        Func<SqlDataReader, Task> readAsync,
        InventoryAccumulator accumulator,
        IProgress<DiscoveryProgress>? progress,
        List<DiscoveryStageDiagnostic> diagnostics,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteStageAsync(
                connection, request, stage, queryId, commandText, stageNumber, false,
                accumulator, progress, readAsync, diagnostics, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SourceDatabaseException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var evidence = string.Join(
                " | ",
                exception.Errors.Select(error =>
                    $"{error.Number}/{error.State}: {error.Message}"));
            accumulator.Findings.Add(new InventoryFinding(
                $"DISCOVERY.{stage.ToString().ToUpperInvariant()}",
                exception.Errors.Any(error => error.Number == 229)
                    ? FindingSeverity.Warning
                    : FindingSeverity.Error,
                $"Optional discovery stage {stage} could not be completed. Query {queryId}.",
                Evidence: evidence,
                Remediation: exception.Remediation ??
                    "Grant metadata visibility or review SQL Server version and feature compatibility."));
            Report(
                progress,
                stage,
                DiscoveryStageState.CompletedWithFindings,
                queryId,
                false,
                1,
                stageNumber,
                accumulator,
                $"Optional stage failed and was recorded as a finding: {FirstError(exception)}");
            DiscoveryLog.OptionalStageFailed(
                logger, stage, queryId, correlationId, exception);
        }
    }

    private async Task ExecuteStageAsync(
        SqlConnection primaryConnection,
        InventoryDiscoveryRequest request,
        DiscoveryStage stage,
        string queryId,
        string commandText,
        int stageNumber,
        bool isRequired,
        InventoryAccumulator accumulator,
        IProgress<DiscoveryProgress>? progress,
        Func<SqlDataReader, Task> readAsync,
        List<DiscoveryStageDiagnostic> diagnostics,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var maximumAttempts = checked(request.Options.MaximumTransientRetries + 1);
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = accumulator.TotalFacetCount;
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            Report(
                progress,
                stage,
                attempt == 1 ? DiscoveryStageState.Running : DiscoveryStageState.Retrying,
                queryId,
                isRequired,
                attempt,
                stageNumber - 1,
                accumulator,
                attempt == 1
                    ? $"Starting {stage}."
                    : $"Retrying {stage} with a fresh SQL Server connection (attempt {attempt} of {maximumAttempts}).");

            try
            {
                if (attempt > 1)
                {
                    await primaryConnection.CloseAsync().ConfigureAwait(false);
                    await primaryConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                }
                await ExecuteCommandAsync(
                    primaryConnection,
                    request.Connection.CommandTimeoutSeconds,
                    commandText,
                    readAsync,
                    cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                var added = accumulator.TotalFacetCount - before;
                diagnostics.Add(new DiscoveryStageDiagnostic(
                    stage,
                    DiscoveryStageState.Completed,
                    queryId,
                    isRequired,
                    attempt,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    added,
                    [],
                    $"{stage} completed.",
                    false));
                DiscoveryLog.StageCompleted(
                    logger,
                    stage,
                    queryId,
                    added,
                    stopwatch.ElapsedMilliseconds,
                    attempt,
                    correlationId);
                Report(
                    progress,
                    stage,
                    DiscoveryStageState.Completed,
                    queryId,
                    isRequired,
                    attempt,
                    stageNumber,
                    accumulator,
                    $"{stage} completed; {added:N0} inventory rows added.");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                diagnostics.Add(new DiscoveryStageDiagnostic(
                    stage,
                    DiscoveryStageState.Cancelled,
                    queryId,
                    isRequired,
                    attempt,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    accumulator.TotalFacetCount - before,
                    [],
                    "Cancellation observed; stage resources are being released.",
                    false));
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                stopwatch.Stop();
                var errors = exception is SqlException sqlException
                    ? SqlServerConnectionFactory.MapErrors(sqlException)
                    : [];
                var rowsAdded = accumulator.TotalFacetCount - before;
                var transient = exception is SqlException &&
                    IsTransient(errors) &&
                    rowsAdded == 0;
                var retry = transient && attempt < maximumAttempts;
                diagnostics.Add(new DiscoveryStageDiagnostic(
                    stage,
                    retry ? DiscoveryStageState.Retrying : DiscoveryStageState.Failed,
                    queryId,
                    isRequired,
                    attempt,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    rowsAdded,
                    errors,
                    retry
                        ? "Transient read failure; retry scheduled with a fresh connection."
                        : SafeExceptionSummary(exception, errors),
                    retry));
                if (retry)
                {
                    DiscoveryLog.StageRetrying(
                        logger,
                        stage,
                        queryId,
                        attempt,
                        FirstError(errors, exception),
                        correlationId);
                    Report(
                        progress,
                        stage,
                        DiscoveryStageState.Retrying,
                        queryId,
                        isRequired,
                        attempt,
                        stageNumber - 1,
                        accumulator,
                        $"Transient SQL Server failure. Retrying safely; {FirstError(errors, exception)}");
                    var delay = TimeSpan.FromMilliseconds(
                        request.Options.InitialRetryDelayMilliseconds *
                        Math.Pow(2, attempt - 1));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw CreateFailure(
                    stage,
                    queryId,
                    correlationId,
                    $"{stage} failed in query {queryId}. {FirstError(errors, exception)}",
                    errors,
                    exception,
                    transient,
                    Remediation(errors, exception));
            }
        }
    }

    private static async Task ExecuteCommandAsync(
        SqlConnection connection,
        int commandTimeout,
        string commandText,
        Func<SqlDataReader, Task> readAsync,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeout;
        await using var reader = await command.ExecuteReaderAsync(
            // Mappers intentionally address catalog columns by name and do not guarantee ordinal
            // order. SequentialAccess would make a later named read permanently prevent access
            // to an earlier ordinal in the same row.
            CatalogReaderBehavior,
            cancellationToken).ConfigureAwait(false);
        await readAsync(reader).ConfigureAwait(false);
    }

    private static SourceDatabaseException CreateFailure(
        DiscoveryStage stage,
        string queryId,
        Guid correlationId,
        string message,
        IReadOnlyList<SqlServerError> errors,
        Exception exception,
        bool isRetryable,
        string remediation) =>
        new(
            message,
            errors,
            exception,
            stage,
            queryId,
            correlationId,
            isRetryable,
            remediation);

    private void PublishReport(
        InventoryDiscoveryRequest request,
        Guid correlationId,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt,
        InventoryAccumulator accumulator,
        DiscoveryStage finalStage,
        DiscoveryStageState finalState,
        IReadOnlyList<DiscoveryStageDiagnostic> diagnostics,
        string summary,
        bool partialInventoryDiscarded) =>
        diagnosticSession?.Publish(new DiscoveryDiagnosticReport(
            correlationId,
            startedAt,
            finishedAt,
            request.Connection.Server,
            request.Connection.Database,
            accumulator.SqlServerMajorVersion > 0
                ? accumulator.SqlServerMajorVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                : null,
            finalStage,
            finalState,
            diagnostics.ToArray(),
            summary,
            partialInventoryDiscarded));

    private static void Report(
        IProgress<DiscoveryProgress>? progress,
        DiscoveryStage stage,
        DiscoveryStageState state,
        string queryId,
        bool isRequired,
        int attempt,
        int completedStageNumber,
        InventoryAccumulator accumulator,
        string message) =>
        progress?.Report(new DiscoveryProgress(
            stage,
            state,
            queryId,
            isRequired,
            attempt,
            completedStageNumber,
            StageCount,
            accumulator.ObjectsBySqlId.Count,
            message,
            DateTimeOffset.UtcNow));

    private static bool IsTransient(IEnumerable<SqlServerError> errors) =>
        errors.Any(error => error.Number is -2 or 20 or 64 or 233 or 10053 or 10054 or 10060
            or 10928 or 10929 or 40197 or 40501 or 40613 or 49918 or 49919 or 49920);

    private static string Remediation(
        IReadOnlyList<SqlServerError> errors,
        Exception exception)
    {
        if (errors.Any(error => error.Number == 229))
        {
            return "Grant VIEW DEFINITION and the feature-specific catalog permission, then retry.";
        }
        if (errors.Any(error => error.Number is 207 or 208))
        {
            return "Verify the SQL Server product version and catalog compatibility for this query.";
        }
        if (errors.Any(error => error.Number == -2))
        {
            return "Increase the catalog command timeout only after checking blocking and server load.";
        }
        return exception is InvalidCastException or IndexOutOfRangeException or InvalidDataException
            ? "Export the sanitized diagnostic and report the query ID as a discovery mapping defect."
            : "Verify connectivity, metadata visibility, server health, and the reported SQL Server error.";
    }

    private static string SafeExceptionSummary(
        Exception exception,
        IReadOnlyList<SqlServerError> errors) =>
        errors.Count > 0 ? FirstError(errors, exception) :
        $"{exception.GetType().Name}: {exception.Message}";

    private static string FirstError(SourceDatabaseException exception) =>
        exception.Errors.Count == 0
            ? exception.InnerException?.Message ?? exception.Message
            : FirstError(exception.Errors, exception);

    private static string FirstError(
        IReadOnlyList<SqlServerError> errors,
        Exception exception) =>
        errors.Count == 0
            ? $"{exception.GetType().Name}: {exception.Message}"
            : $"SQL {errors[0].Number}, state {errors[0].State}, class {errors[0].Class}: {errors[0].Message}";

    private static string BuildFailureMessage(SourceDatabaseException exception) =>
        $"{exception.Stage} failed ({exception.QueryId}). {FirstError(exception)} " +
        $"Correlation {exception.CorrelationId:N}.";

    private static void ValidateOptions(DiscoveryOptions options)
    {
        if (options.MaximumConcurrentCommands is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum concurrent catalog commands must be between 1 and 8.");
        }
        if (options.MaximumTransientRetries is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Transient retries must be between zero and five.");
        }
        if (options.InitialRetryDelayMilliseconds is < 50 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Initial retry delay must be between 50 and 30,000 milliseconds.");
        }
    }

    [LoggerMessage(
        3010,
        LogLevel.Information,
        "Identifier lifecycle {Details}")]
    private static partial void LogIdentifierLifecycle(ILogger logger, string details);
}

internal static partial class DiscoveryLog
{
    [LoggerMessage(
        3000,
        LogLevel.Information,
        "Starting SQL Server discovery with scope {ScopeMode}; correlation {CorrelationId}.")]
    public static partial void Starting(
        ILogger logger,
        MigrationScopeMode scopeMode,
        Guid correlationId);

    [LoggerMessage(
        3001,
        LogLevel.Information,
        "Discovery completed: {ObjectCount} objects, {DependencyCount} dependencies, {FindingCount} findings; correlation {CorrelationId}.")]
    public static partial void Completed(
        ILogger logger,
        int objectCount,
        int dependencyCount,
        int findingCount,
        Guid correlationId);

    [LoggerMessage(
        3002,
        LogLevel.Warning,
        "Optional discovery stage {Stage} query {QueryId} failed; correlation {CorrelationId}.")]
    public static partial void OptionalStageFailed(
        ILogger logger,
        DiscoveryStage stage,
        string queryId,
        Guid correlationId,
        Exception exception);

    [LoggerMessage(
        3003,
        LogLevel.Error,
        "SQL Server discovery failed at {Stage} query {QueryId}; correlation {CorrelationId}.")]
    public static partial void Failed(
        ILogger logger,
        DiscoveryStage stage,
        string queryId,
        Guid correlationId,
        Exception exception);

    [LoggerMessage(
        3004,
        LogLevel.Information,
        "Discovery stage {Stage} query {QueryId} completed in {DurationMilliseconds} ms and added {RowCount} inventory rows on attempt {Attempt}; correlation {CorrelationId}.")]
    public static partial void StageCompleted(
        ILogger logger,
        DiscoveryStage stage,
        string queryId,
        int rowCount,
        long durationMilliseconds,
        int attempt,
        Guid correlationId);

    [LoggerMessage(
        3005,
        LogLevel.Warning,
        "Retrying discovery stage {Stage} query {QueryId} after attempt {Attempt}: {Reason}; correlation {CorrelationId}.")]
    public static partial void StageRetrying(
        ILogger logger,
        DiscoveryStage stage,
        string queryId,
        int attempt,
        string reason,
        Guid correlationId);

    [LoggerMessage(
        3006,
        LogLevel.Information,
        "SQL Server discovery was cancelled after resource release; correlation {CorrelationId}.")]
    public static partial void Cancelled(
        ILogger logger,
        Guid correlationId);
}
