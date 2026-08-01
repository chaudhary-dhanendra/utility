using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.SqlServer;

public sealed class SqlServerDiscoveryDoctorService(
    IInventoryDiscoveryService discoveryService,
    IDiscoveryDiagnosticSession diagnosticSession) : IDiscoveryDoctorService
{
    private const string CapabilitySql = """
        SELECT
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')),
            CONVERT(nvarchar(256), SERVERPROPERTY('Edition')),
            CONVERT(int, SERVERPROPERTY('EngineEdition')),
            CONVERT(int, (SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID())),
            CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')),
            CONVERT(int, HAS_DBACCESS(N'msdb')),
            CONVERT(int, ISNULL(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0)),
            CONVERT(int, CASE WHEN OBJECT_ID(N'sys.security_policies') IS NULL THEN 0 ELSE 1 END),
            CONVERT(int, CASE WHEN OBJECT_ID(N'sys.change_tracking_tables') IS NULL THEN 0 ELSE 1 END),
            CONVERT(int, CASE WHEN OBJECT_ID(N'sys.database_scoped_credentials') IS NULL THEN 0 ELSE 1 END),
            CONVERT(int, CASE WHEN COL_LENGTH(N'sys.tables', N'is_node') IS NULL THEN 0 ELSE 1 END),
            CONVERT(int, CASE WHEN COL_LENGTH(N'sys.tables', N'ledger_type') IS NULL THEN 0 ELSE 1 END),
            CONVERT(int, CASE WHEN COL_LENGTH(N'sys.columns', N'is_hidden') IS NULL THEN 0 ELSE 1 END),
            CONVERT(int, CASE WHEN COL_LENGTH(N'sys.columns', N'encryption_type') IS NULL THEN 0 ELSE 1 END),
            CONVERT(int, CASE WHEN COL_LENGTH(N'sys.external_data_sources', N'connection_options') IS NULL THEN 0 ELSE 1 END);
        """;

    private const string FullTextDiagnosticSql = """
        SELECT fulltext_catalog_id, name, is_default, is_accent_sensitivity_on
        FROM sys.fulltext_catalogs;

        SELECT object_id, change_tracking_state_desc
        FROM sys.fulltext_indexes;
        """;

    public IReadOnlyList<CatalogQueryDescriptor> GetCatalog(int sqlServerMajorVersion)
    {
        if (sqlServerMajorVersion < 13)
        {
            return [];
        }
        var catalog = BuildCatalog(sqlServerMajorVersion);
        EnsureRegistered(catalog);
        return catalog.Select(item => item.Descriptor).ToArray();
    }

    public IReadOnlyList<CatalogQueryDescriptor> SelectCatalog(
        int sqlServerMajorVersion,
        DiscoveryDoctorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var registered = BuildCatalog(sqlServerMajorVersion);
        EnsureRegistered(registered);
        var selected = SelectQueries(registered, request);
        if (selected.Length == 0)
        {
            throw new InvalidOperationException(
                "No catalog diagnostic queries were selected. Discovery Doctor cannot run.");
        }
        return selected.Select(item => item.Descriptor).ToArray();
    }

    public async Task<DatabaseCompatibilityAudit> AuditAsync(
        SqlServerConnectionOptions connectionOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionOptions);
        connectionOptions.Validate();
        await using var connection = SqlServerConnectionFactory.Create(connectionOptions);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = CapabilitySql;
        command.CommandTimeout = connectionOptions.CommandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQL Server capability query returned no row.");
        }

        var productVersion = reader.GetString(0);
        var majorVersion = ParseMajorVersion(productVersion);
        var productLevel = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var edition = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var engineEdition = reader.GetInt32(3);
        var compatibilityLevel = reader.GetInt32(4);
        var capabilities = new[]
        {
            Capability("VIEW DEFINITION", reader.GetInt32(5) == 1,
                "Required for complete metadata visibility."),
            Capability("MSDB access", reader.GetInt32(6) == 1,
                "Required only for the optional SQL Agent stage."),
            Capability("Full Text installed", reader.GetInt32(7) == 1,
                "Controls optional Full Text metadata."),
            Capability("Row-level security catalog", reader.GetInt32(8) == 1,
                "Controls optional security-policy metadata."),
            Capability("Change Tracking catalog", reader.GetInt32(9) == 1,
                "Controls optional Change Tracking metadata."),
            Capability("Database scoped credentials catalog", reader.GetInt32(10) == 1,
                "Controls optional external credential metadata."),
            Capability("Graph table columns", reader.GetInt32(11) == 1,
                "Required only when graph metadata is selected."),
            Capability("Ledger table columns", reader.GetInt32(12) == 1,
                "Required only on SQL Server 2022+ ledger metadata."),
            Capability("Hidden column metadata", reader.GetInt32(13) == 1,
                "Used by temporal, graph, and ledger column discovery."),
            Capability("Always Encrypted metadata", reader.GetInt32(14) == 1,
                "Used by column encryption discovery."),
            Capability("External connection options", reader.GetInt32(15) == 1,
                "Used only on SQL Server 2022+.")
        };
        var findings = new List<string>();
        if (majorVersion < 13)
        {
            findings.Add($"SQL Server major version {majorVersion} is below the supported 2016 floor.");
        }
        if (compatibilityLevel < 130)
        {
            findings.Add(
                $"Database compatibility level {compatibilityLevel} is below SQL Server 2016 level 130.");
        }
        if (!capabilities[0].IsAvailable)
        {
            findings.Add("VIEW DEFINITION is not granted; catalog results can be incomplete.");
        }

        return new DatabaseCompatibilityAudit(
            productVersion,
            majorVersion,
            productLevel,
            edition,
            engineEdition,
            compatibilityLevel,
            capabilities,
            findings);
    }

    public async Task<DiscoveryDoctorReport> DiagnoseAsync(
        SqlServerConnectionOptions connection,
        DiscoveryDoctorRequest request,
        IProgress<DiscoveryDoctorProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid();
        DatabaseCompatibilityAudit audit;
        try
        {
            audit = await AuditAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errors = exception is SqlException sqlException
                ? SqlServerConnectionFactory.MapErrors(sqlException)
                : [];
            var descriptor = new CatalogQueryDescriptor(
                "SQLSERVER.COMPATIBILITY_AUDIT.V1",
                DiscoveryStage.TestingConnection,
                true,
                13,
                "Connection, SQL Server version, database compatibility, permissions, and catalog capabilities.",
                CapabilitySql,
                true);
            var summary = errors.Count == 0
                ? $"{exception.GetType().Name}: {exception.Message}"
                : $"SQL {errors[0].Number}, state {errors[0].State}, class {errors[0].Class}: {errors[0].Message}";
            audit = new DatabaseCompatibilityAudit(
                "Unknown", 0, string.Empty, string.Empty, 0, 0, [],
                ["Compatibility audit could not connect or execute."]);
            var failed = new CatalogQueryDiagnostic(
                descriptor,
                CatalogDiagnosticStatus.Failed,
                1,
                startedAt,
                DateTimeOffset.UtcNow,
                Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds),
                0,
                0,
                0,
                errors,
                exception.GetType().Name,
                CatalogFailurePhase.QueryExecution,
                [new CatalogPhaseDiagnostic(
                    CatalogFailurePhase.QueryExecution,
                    CatalogDiagnosticStatus.Failed,
                    summary)],
                summary,
                Remediation(errors, exception),
                IsTransient(errors));
            var failedReport = new DiscoveryDoctorReport(
                correlationId,
                startedAt,
                DateTimeOffset.UtcNow,
                connection.Server,
                connection.Database,
                audit,
                [failed],
                BuildCatalog(16).Count,
                1,
                1,
                DiscoveryStage.TestingConnection,
                descriptor.QueryId,
                summary,
                false);
            diagnosticSession.PublishDoctor(failedReport);
            return diagnosticSession.DoctorReport!;
        }
        var registered = audit.MajorVersion < 13 ? [] : BuildCatalog(audit.MajorVersion);
        EnsureRegistered(registered);
        var catalog = SelectQueries(registered, request);
        if (catalog.Length == 0)
        {
            throw new InvalidOperationException(
                "No catalog diagnostic queries were selected. Discovery Doctor cannot run.");
        }
        var results = new List<CatalogQueryDiagnostic>(catalog.Length);

        try
        {
            for (var index = 0; index < catalog.Length; index++)
            {
                var item = catalog[index];
                progress?.Report(new DiscoveryDoctorProgress(
                    item.Descriptor.QueryId,
                    item.Descriptor.Stage,
                    CatalogDiagnosticStatus.Running,
                    index,
                    catalog.Length,
                    $"Executing {item.Descriptor.QueryId} independently."));
                var result = await ExecuteIndependentAsync(
                    connection,
                    item,
                    audit,
                    progress,
                    index,
                    catalog.Length,
                    cancellationToken).ConfigureAwait(false);
                results.Add(result);
                progress?.Report(new DiscoveryDoctorProgress(
                    item.Descriptor.QueryId,
                    item.Descriptor.Stage,
                    result.Status,
                    index + 1,
                    catalog.Length,
                    result.Summary));
            }

            DiscoveryStage? failureStage = null;
            string? failureQuery = null;
            string? failureSummary = null;
            if (request.Mode is DiscoveryDoctorMode.QuickPreflight or DiscoveryDoctorMode.FullDiagnostic)
            {
                try
                {
                    await discoveryService.DiscoverAsync(
                        new InventoryDiscoveryRequest(
                            connection,
                            MigrationScopeMode.CompleteDatabase,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            new HashSet<InventoryObjectId>(),
                            new HashSet<InventoryObjectId>(),
                            DependencyPolicy.IncludeRequiredDependencies,
                            new DiscoveryOptions
                            {
                                IncludeServerLevelObjects = true,
                                IncludeSqlAgent = true
                            }),
                        null,
                        cancellationToken).ConfigureAwait(false);
                    ApplyProductionMappingResults(results, diagnosticSession.Current);
                }
                catch (SourceDatabaseException exception)
                {
                    failureStage = exception.Stage;
                    failureQuery = exception.QueryId;
                    failureSummary = exception.Message;
                    ApplyProductionMappingResults(results, diagnosticSession.Current);
                    ApplyProductionFailure(results, exception, diagnosticSession.Current);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failureStage = DiscoveryStage.Failed;
                    failureQuery = "DISCOVERY.PRODUCTION_PIPELINE";
                    failureSummary = $"{exception.GetType().Name}: {exception.Message}";
                }
            }

            var report = new DiscoveryDoctorReport(
                correlationId,
                startedAt,
                DateTimeOffset.UtcNow,
                connection.Server,
                connection.Database,
                audit,
                results,
                registered.Count,
                catalog.Length,
                results.Count(item => item.Status != CatalogDiagnosticStatus.Skipped),
                failureStage,
                failureQuery,
                failureSummary,
                false);
            diagnosticSession.PublishDoctor(report);
            return diagnosticSession.DoctorReport!;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var report = new DiscoveryDoctorReport(
                correlationId,
                startedAt,
                DateTimeOffset.UtcNow,
                connection.Server,
                connection.Database,
                audit,
                results,
                registered.Count,
                catalog.Length,
                results.Count(item => item.Status != CatalogDiagnosticStatus.Skipped),
                null,
                null,
                "Discovery Doctor cancelled after the active reader and connection were released.",
                true);
            diagnosticSession.PublishDoctor(report);
            throw;
        }
    }

    private static async Task<CatalogQueryDiagnostic> ExecuteIndependentAsync(
        SqlServerConnectionOptions connectionOptions,
        CatalogItem item,
        DatabaseCompatibilityAudit audit,
        IProgress<DiscoveryDoctorProgress>? progress,
        int completed,
        int total,
        CancellationToken cancellationToken)
    {
        if (!HasRequiredCapability(item.Descriptor, audit))
        {
            return CreateSkippedUnsupported(item.Descriptor);
        }
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var phases = new List<CatalogPhaseDiagnostic>
            {
                new(
                    CatalogFailurePhase.QuerySelection,
                    CatalogDiagnosticStatus.Succeeded,
                    "Query selected from the registered production catalog.")
            };
            var resultSets = 0;
            long rows = 0;
            try
            {
                await using var connection = SqlServerConnectionFactory.Create(connectionOptions);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = item.Sql;
                command.CommandTimeout = connectionOptions.CommandTimeoutSeconds;
                phases.Add(new CatalogPhaseDiagnostic(
                    CatalogFailurePhase.CommandCreation,
                    CatalogDiagnosticStatus.Succeeded,
                    "Command created with the configured metadata timeout."));
                await using var reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess,
                    cancellationToken).ConfigureAwait(false);
                phases.Add(new CatalogPhaseDiagnostic(
                    CatalogFailurePhase.QueryExecution,
                    CatalogDiagnosticStatus.Succeeded,
                    "Query compiled/executed and the reader opened."));
                do
                {
                    resultSets++;
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        rows++;
                    }
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                phases.Add(new CatalogPhaseDiagnostic(
                    CatalogFailurePhase.ReaderIteration,
                    CatalogDiagnosticStatus.Succeeded,
                    "All result sets were consumed.",
                    rows));
                phases.Add(new CatalogPhaseDiagnostic(
                    CatalogFailurePhase.Aggregation,
                    CatalogDiagnosticStatus.Succeeded,
                    "Result-set and metadata-row counts were aggregated.",
                    rows));

                stopwatch.Stop();
                return new CatalogQueryDiagnostic(
                    item.Descriptor,
                    CatalogDiagnosticStatus.Succeeded,
                    attempt,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    resultSets,
                    rows,
                    0,
                    [],
                    null,
                    null,
                    phases,
                    $"Succeeded in {stopwatch.ElapsedMilliseconds:N0} ms; {resultSets:N0} result sets, {rows:N0} metadata rows.",
                    string.Empty,
                    true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                var errors = exception is SqlException sqlException
                    ? SqlServerConnectionFactory.MapErrors(sqlException)
                    : [];
                var transient = IsTransient(errors);
                if (transient && attempt < maximumAttempts)
                {
                    progress?.Report(new DiscoveryDoctorProgress(
                        item.Descriptor.QueryId,
                        item.Descriptor.Stage,
                        CatalogDiagnosticStatus.Retrying,
                        completed,
                        total,
                        $"Transient failure; retrying attempt {attempt + 1} of {maximumAttempts}."));
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var first = errors.Count == 0
                    ? $"{exception.GetType().Name}: {exception.Message}"
                    : $"SQL {errors[0].Number}, state {errors[0].State}, class {errors[0].Class}: {errors[0].Message}";
                var failurePhase = phases.Any(phase =>
                    phase.Phase == CatalogFailurePhase.QueryExecution)
                    ? CatalogFailurePhase.ReaderIteration
                    : phases.Any(phase => phase.Phase == CatalogFailurePhase.CommandCreation)
                        ? CatalogFailurePhase.QueryExecution
                        : CatalogFailurePhase.CommandCreation;
                phases.Add(new CatalogPhaseDiagnostic(
                    failurePhase,
                    CatalogDiagnosticStatus.Failed,
                    first));
                return new CatalogQueryDiagnostic(
                    item.Descriptor,
                    CatalogDiagnosticStatus.Failed,
                    attempt,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    resultSets,
                    rows,
                    0,
                    errors,
                    exception.GetType().Name,
                    failurePhase,
                    phases,
                    first,
                    Remediation(errors, exception),
                    transient);
            }
        }
        throw new InvalidOperationException("Catalog query attempt loop terminated unexpectedly.");
    }

    private static IReadOnlyList<CatalogItem> BuildCatalog(int majorVersion) =>
    [
        Item("SQLSERVER.CONNECTION.OPEN", DiscoveryStage.TestingConnection, true, 13,
            "Open and authenticate a dedicated SQL Server diagnostic connection.",
            "SELECT CONVERT(int, 1) AS connection_test;", true),
        Item("SQLSERVER.SERVER_METADATA.V1", DiscoveryStage.LoadingServerMetadata, true, 13,
            "Server product version, level, edition, and engine edition.", SqlServerCatalogQueries.ServerMetadata, true),
        Item("SQLSERVER.DATABASE_METADATA.V2", DiscoveryStage.LoadingDatabaseMetadata, true, 13,
            "Database options, scoped configuration, files, and filegroups.", SqlServerCatalogQueries.DatabaseMetadata, true),
        Item("SQLSERVER.SCHEMAS.V1", DiscoveryStage.DiscoveringSchemas, true, 13,
            "Schemas, owners, and user-object counts.", SqlServerCatalogQueries.Schemas, true),
        Item($"SQLSERVER.OBJECTS.V{majorVersion}", DiscoveryStage.DiscoveringObjects, true, 13,
            "Object identity and SQL module metadata.", SqlServerCatalogQueries.Objects(majorVersion), true),
        Item($"SQLSERVER.TABLES.V{majorVersion}", DiscoveryStage.DiscoveringTables, true, 13,
            "Table feature, storage, row-count estimate, and external-table metadata.", SqlServerCatalogQueries.Tables(majorVersion), true),
        Item($"SQLSERVER.COLUMNS.V{majorVersion}", DiscoveryStage.DiscoveringColumns, true, 13,
            "Column, type, identity, computed, masking, encryption, and default metadata.", SqlServerCatalogQueries.Columns(majorVersion), true),
        Item("SQLSERVER.CONSTRAINTS.V1", DiscoveryStage.DiscoveringConstraints, true, 13,
            "Primary, unique, check, foreign-key, and default constraints.", SqlServerCatalogQueries.Constraints, true),
        Item("SQLSERVER.INDEXES.V1", DiscoveryStage.DiscoveringIndexes, true, 13,
            "Indexes, columns, partitions, compression, and placement.", SqlServerCatalogQueries.Indexes, true),
        Item("SQLSERVER.PROGRAMMABLE.V1", DiscoveryStage.DiscoveringProgrammableObjects, true, 13,
            "Modules, parameters, triggers, sequences, types, and synonyms.", SqlServerCatalogQueries.ProgrammableObjects),
        Item("SQLSERVER.DEPENDENCIES.V1", DiscoveryStage.DiscoveringDependencies, true, 13,
            "Catalog expression and structural dependencies.", SqlServerCatalogQueries.Dependencies, true),
        Item("SQLSERVER.EXTENDED_PROPERTIES.V1", DiscoveryStage.DiscoveringExtendedProperties, false, 13,
            "Database, schema, object, and column extended properties.", SqlServerCatalogQueries.ExtendedProperties),
        Item("SQLSERVER.SERVER_TRIGGERS.V1", DiscoveryStage.DiscoveringServerTriggers, false, 13,
            "Server DDL triggers and modules.", SqlServerCatalogQueries.ServerTriggers),
        Item("SQLSERVER.SECURITY.V1", DiscoveryStage.DiscoveringSecurity, false, 13,
            "Database principals, roles, memberships, grants, and denies.", SqlServerCatalogQueries.Security),
        Item($"SQLSERVER.ADVANCED.V{majorVersion}", DiscoveryStage.DiscoveringAdvancedFeatures, false, 13,
            "Temporal, Change Tracking, RLS, Full Text, Broker, CLR, encryption, DDL trigger, and replication metadata.", SqlServerCatalogQueries.Advanced(majorVersion)),
        Item("SQLSERVER.FULL_TEXT.V1", DiscoveryStage.DiscoveringAdvancedFeatures, false, 13,
            "Full Text catalogs and indexes.", FullTextDiagnosticSql, false, "Full Text installed"),
        Item($"SQLSERVER.EXTERNAL.V{majorVersion}", DiscoveryStage.DiscoveringExternalObjects, false, 13,
            "Partitioning, external objects, and cross-database dependencies.", SqlServerCatalogQueries.ExternalAndPartitioning(majorVersion)),
        Item("SQLSERVER.SQL_AGENT.V1", DiscoveryStage.DiscoveringSqlAgent, false, 13,
            "SQL Agent jobs, steps, and schedules.", SqlServerCatalogQueries.SqlAgent)
    ];

    private static CatalogItem Item(
        string queryId,
        DiscoveryStage stage,
        bool required,
        int minimumVersion,
        string description,
        string sql,
        bool includeInQuickPreflight = false,
        string? requiredCapability = null) =>
        new(new CatalogQueryDescriptor(
            queryId,
            stage,
            required,
            minimumVersion,
            description,
            sql,
            includeInQuickPreflight,
            requiredCapability), sql);

    private static void EnsureRegistered(IReadOnlyList<CatalogItem> registeredQueries)
    {
        if (registeredQueries.Count == 0)
        {
            throw new InvalidOperationException(
                "No catalog diagnostic queries are registered. Discovery Doctor cannot run.");
        }
        if (registeredQueries.Any(item =>
                string.IsNullOrWhiteSpace(item.Descriptor.QueryId) ||
                string.IsNullOrWhiteSpace(item.Sql)))
        {
            throw new InvalidOperationException(
                "A catalog diagnostic query has no stable query ID or resolvable SQL text.");
        }
    }

    private static CatalogItem[] SelectQueries(
        IReadOnlyList<CatalogItem> registeredQueries,
        DiscoveryDoctorRequest request) =>
        request.Mode switch
        {
            DiscoveryDoctorMode.QuickPreflight => registeredQueries
                .Where(item => item.Descriptor.IncludeInQuickPreflight)
                .ToArray(),
            DiscoveryDoctorMode.FullDiagnostic => registeredQueries.ToArray(),
            DiscoveryDoctorMode.SelectedQueries when request.QueryIds is { Count: > 0 } =>
                registeredQueries.Where(item =>
                    request.QueryIds.Contains(item.Descriptor.QueryId)).ToArray(),
            DiscoveryDoctorMode.SelectedQueries => throw new InvalidOperationException(
                "Selected-query diagnostics require at least one registered query ID."),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "Unknown doctor mode.")
        };

    internal static void ApplyProductionFailure(
        List<CatalogQueryDiagnostic> results,
        SourceDatabaseException exception,
        DiscoveryDiagnosticReport? productionReport)
    {
        var index = results.FindIndex(item => string.Equals(
            item.Descriptor.QueryId,
            exception.QueryId,
            StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }
        var current = results[index];
        var stage = productionReport?.Stages.LastOrDefault(item =>
            string.Equals(item.QueryId, exception.QueryId, StringComparison.OrdinalIgnoreCase));
        var phase = exception.Errors.Count > 0
            ? CatalogFailurePhase.QueryExecution
            : CatalogFailurePhase.MetadataMapping;
        var summary = exception.InnerException is null
            ? exception.Message
            : $"{exception.InnerException.GetType().Name}: {exception.InnerException.Message}";
        results[index] = current with
        {
            Status = CatalogDiagnosticStatus.Failed,
            RowsMapped = stage?.RowsAdded ?? 0,
            Errors = exception.Errors,
            ExceptionType = exception.InnerException?.GetType().Name ?? exception.GetType().Name,
            FailurePhase = phase,
            Phases = current.Phases.Concat(
            [
                new CatalogPhaseDiagnostic(
                    CatalogFailurePhase.MetadataMapping,
                    CatalogDiagnosticStatus.Failed,
                    summary,
                    stage?.RowsAdded ?? 0)
            ]).ToArray(),
            Summary = summary,
            Remediation = exception.Remediation ??
                "Correct the production metadata mapper for the reported query and rerun the selected query.",
            CanRetry = exception.IsRetryable
        };
    }

    private static void ApplyProductionMappingResults(
        List<CatalogQueryDiagnostic> results,
        DiscoveryDiagnosticReport? productionReport)
    {
        if (productionReport is null)
        {
            return;
        }
        foreach (var stage in productionReport.Stages.Where(item =>
                     item.State is DiscoveryStageState.Completed or
                         DiscoveryStageState.CompletedWithFindings))
        {
            var index = results.FindIndex(item => string.Equals(
                item.Descriptor.QueryId,
                stage.QueryId,
                StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }
            var current = results[index];
            results[index] = current with
            {
                RowsMapped = stage.RowsAdded,
                Phases = current.Phases.Concat(
                [
                    new CatalogPhaseDiagnostic(
                        CatalogFailurePhase.MetadataMapping,
                        CatalogDiagnosticStatus.Succeeded,
                        "The exact production mapper completed.",
                        stage.RowsAdded),
                    new CatalogPhaseDiagnostic(
                        CatalogFailurePhase.PostProcessing,
                        CatalogDiagnosticStatus.Succeeded,
                        "The production stage completed and updated the inventory accumulator.",
                        stage.RowsAdded)
                ]).ToArray()
            };
        }
    }

    internal static CatalogQueryDiagnostic CreateSkippedUnsupported(
        CatalogQueryDescriptor descriptor)
    {
        var capability = descriptor.RequiredCapability ??
            throw new InvalidOperationException("The skipped query has no required capability.");
        return new CatalogQueryDiagnostic(
            descriptor,
            CatalogDiagnosticStatus.Skipped,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            0,
            [],
            null,
            CatalogFailurePhase.QuerySelection,
            [new CatalogPhaseDiagnostic(
                CatalogFailurePhase.QuerySelection,
                CatalogDiagnosticStatus.Skipped,
                $"SkippedUnsupported: required capability '{capability}' is unavailable.")],
            $"SkippedUnsupported: required capability '{capability}' is unavailable.",
            "Install or enable the optional SQL Server feature before executing this query.",
            false);
    }

    private static bool HasRequiredCapability(
        CatalogQueryDescriptor descriptor,
        DatabaseCompatibilityAudit audit) =>
        descriptor.RequiredCapability is null ||
        audit.Capabilities.Any(item =>
            item.IsAvailable &&
            string.Equals(
                item.Name,
                descriptor.RequiredCapability,
                StringComparison.OrdinalIgnoreCase));

    private static DatabaseCapability Capability(string name, bool available, string impact) =>
        new(name, available, available ? "Available" : "Unavailable", impact);

    private static int ParseMajorVersion(string productVersion) =>
        int.TryParse(productVersion.Split('.')[0], out var major)
            ? major
            : throw new InvalidDataException($"Unrecognized SQL Server product version '{productVersion}'.");

    private static bool IsTransient(IReadOnlyList<SqlServerError> errors) =>
        errors.Any(error => error.Number is -2 or 20 or 64 or 233 or 10053 or 10054 or 10060
            or 10928 or 10929 or 40197 or 40501 or 40613 or 49918 or 49919 or 49920);

    private static string Remediation(IReadOnlyList<SqlServerError> errors, Exception exception)
    {
        if (errors.Any(error => error.Number == 229))
        {
            return "Grant the catalog-specific permission or VIEW DEFINITION, then retry this query.";
        }
        if (errors.Any(error => error.Number is 207 or 208))
        {
            return "Compare the detected SQL Server version/capabilities with this version-specific query.";
        }
        if (errors.Any(error => error.Number == -2))
        {
            return "Check catalog blocking and server load before increasing the metadata command timeout.";
        }
        return exception is InvalidCastException or IndexOutOfRangeException
            ? "The raw catalog shape is incompatible with the reader; report the query ID and correlation ID."
            : "Review the exact SQL Server error, metadata visibility, and server health.";
    }

    private sealed record CatalogItem(CatalogQueryDescriptor Descriptor, string Sql);
}
