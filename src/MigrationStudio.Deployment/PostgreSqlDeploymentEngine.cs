using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using Npgsql;

namespace MigrationStudio.Deployment;

public sealed class PostgreSqlDeploymentEngine(
    IPreDeploymentAssessmentService assessmentService,
    IMigrationPackageReader packageReader,
    IPostgreSqlScriptParser scriptParser,
    IDatabaseProvisioningService provisioningService,
    IDeploymentJournalStore journalStore,
    IDataMigrationEngine dataMigrationEngine,
    IDeploymentSession session) : IPostgreSqlDeploymentEngine
{
    public async Task<PreDeploymentAssessment> AssessAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var assessment = await assessmentService.AssessAsync(request, cancellationToken)
            .ConfigureAwait(false);
        session.SetAssessment(assessment);
        return assessment;
    }

    public Task<DeploymentResult> DeployAsync(
        DeploymentRequest request,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(request with { ResumeDeploymentId = null }, null, progress, cancellationToken);

    public async Task<DeploymentResult> ResumeAsync(
        DeploymentRequest request,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.ResumeDeploymentId is null)
        {
            throw new InvalidOperationException("A deployment ID is required to resume.");
        }

        var journal = await journalStore.LoadAsync(
            request.ResumeDeploymentId.Value,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The deployment journal was not found.");
        return await ExecuteAsync(request, journal, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<DeploymentResult> RetryFailedAsync(
        DeploymentRequest request,
        IReadOnlySet<InventoryObjectId> selectedObjects,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var eligible = session.Result is null
            ? selectedObjects
            : SelectRetryEligibleObjects(session.Result.Objects, selectedObjects);
        if (eligible.Count == 0)
        {
            throw new InvalidOperationException(
                "No pending, retryable failed, or dependency-unblocked deployment artifacts were selected.");
        }

        return ExecuteAsync(
            request with
            {
                Options = request.Options with
                {
                    Scope = DeploymentScope.SelectedFailedObjects,
                    SelectedObjects = eligible
                }
            },
            null,
            progress,
            cancellationToken);
    }

    private static HashSet<InventoryObjectId> SelectRetryEligibleObjects(
        IReadOnlyList<DeploymentObjectJournal> journal,
        IReadOnlySet<InventoryObjectId> selected)
    {
        var eligible = journal
            .Where(item =>
                item.SourceObjectId is { } objectId &&
                selected.Contains(objectId) &&
                (item.Status == DeploymentObjectStatus.Pending ||
                 item.Status == DeploymentObjectStatus.Failed &&
                 PostgreSqlDeploymentErrorClassifier.IsTransient(item.Failure?.SqlState)))
            .Select(item => item.SourceObjectId!.Value)
            .ToHashSet();
        var stateById = journal
            .Where(item => item.SourceObjectId is not null)
            .GroupBy(item => item.SourceObjectId!.Value)
            .ToDictionary(group => group.Key, group => group.Last());
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var blocked in journal.Where(item =>
                         item.Status == DeploymentObjectStatus.BlockedByDependency &&
                         item.SourceObjectId is { } objectId &&
                         selected.Contains(objectId) &&
                         !eligible.Contains(objectId)))
            {
                var dependenciesReady = blocked.Dependencies.All(dependency =>
                    eligible.Contains(dependency) ||
                    stateById.TryGetValue(dependency, out var state) &&
                    state.Status is DeploymentObjectStatus.Succeeded
                        or DeploymentObjectStatus.SkippedEquivalent);
                if (dependenciesReady)
                {
                    eligible.Add(blocked.SourceObjectId!.Value);
                    changed = true;
                }
            }
        }

        return eligible;
    }

    private async Task<DeploymentResult> ExecuteAsync(
        DeploymentRequest originalRequest,
        DeploymentJournal? resumeJournal,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var assessmentRequest = resumeJournal is null
            ? originalRequest
            : originalRequest with
            {
                Options = originalRequest.Options with
                {
                    ConflictPolicy = ExistingObjectConflictPolicy.ManualDecision
                }
            };
        var assessment = await AssessAsync(assessmentRequest, cancellationToken).ConfigureAwait(false);
        if (assessment.Manifest is null || !assessment.CanDeploy)
        {
            var now = DateTimeOffset.UtcNow;
            var blocked = new DeploymentResult(
                Guid.NewGuid(),
                DeploymentRunStatus.Blocked,
                now,
                now,
                originalRequest.Connection.TargetDatabase,
                string.Empty,
                [],
                [],
                null,
                assessment.Findings);
            session.SetResult(blocked);
            return blocked;
        }

        var manifest = assessment.Manifest;
        var fingerprint = packageReader.ComputePackageFingerprint(manifest);
        var optionsHash = Hash(JsonSerializer.Serialize(originalRequest.Options));
        ValidateResume(resumeJournal, manifest, fingerprint, originalRequest, optionsHash);
        var request = originalRequest;
        DatabaseProvisioningResult? provisioning = null;
        if (request.Options.Mode == DeploymentMode.CreateDatabaseAndDeploy)
        {
            provisioning = await provisioningService.EnsureDatabaseAsync(
                request.Connection,
                request.Options.DatabaseCreation,
                cancellationToken).ConfigureAwait(false);
            request = request with
            {
                Connection = request.Connection with
                {
                    TargetDatabase = provisioning.EffectiveDatabase
                }
            };
        }

        var deploymentId = resumeJournal?.DeploymentId ?? Guid.NewGuid();
        var startedAt = resumeJournal?.StartedAt ?? DateTimeOffset.UtcNow;
        var entries = new List<DeploymentObjectJournal>(resumeJournal?.Objects ?? []);
        var failures = new List<DeploymentFailure>();
        var overrides = new List<string>();
        if (assessment.AdministratorOverrideApplied)
        {
            overrides.Add($"Pre-deployment override: {assessment.AdministratorOverrideReason}");
        }

        var destructive = new List<string>();
        if (provisioning?.WasDropped == true)
        {
            destructive.Add($"Database '{provisioning.RequestedDatabase}' was dropped and recreated.");
        }

        var journal = CreateJournal(
            deploymentId,
            manifest,
            request,
            fingerprint,
            optionsHash,
            startedAt,
            DeploymentRunStatus.Running,
            entries,
            null,
            assessment.Findings,
            overrides,
            destructive);
        var journalPath = await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
        if (request.Options.Mode is DeploymentMode.GenerateOnly or DeploymentMode.ValidateOnly)
        {
            var status = assessment.Findings.Any(item => item.Severity == DeploymentFindingSeverity.Warning)
                ? DeploymentRunStatus.SucceededWithWarnings
                : DeploymentRunStatus.Succeeded;
            return await CompleteAsync(
                request,
                journal,
                journalPath,
                status,
                entries,
                failures,
                null,
                assessment.Findings,
                cancellationToken).ConfigureAwait(false);
        }

        var selected = OrderArtifacts(manifest.Artifacts
            .Where(item => PreDeploymentAssessmentService.IsSelected(item, request.Options))
            .Where(item => item.IsExecutable)
            .Where(item => !item.RequiresManualReview &&
                item.Classification != Domain.Inventory.ConversionClassification.Unsupported)
            .ToArray());
        var validationCount =
            request.Options.ValidateConstraints &&
            request.Options.ConstraintStrategy is
                ConstraintDeploymentStrategy.AddNotValidThenValidate or
                ConstraintDeploymentStrategy.ValidateInLaterPhase
                ? selected.Count(item => item.Phase == DeploymentPhase.ForeignKeys)
                : 0;

        var selectedTableCount = selected.Count(item => item.Phase == DeploymentPhase.Tables);
        var analyzeCount = request.Options.AnalyzeTables ? selectedTableCount : 0;
        var vacuumAnalyzeCount = request.Options.VacuumAnalyze ? selectedTableCount : 0;
        var extensionVerificationCount = manifest.RequiredExtensions.Count > 0 ? 1 : 0;

        var total =
            selected.Count +
            validationCount +
            analyzeCount +
            vacuumAnalyzeCount +
            extensionVerificationCount +
            (request.Options.InstallRequiredExtensions
                ? manifest.RequiredExtensions.Count
                : 0) +
            (request.DataMigrationRequest is null
                ? 0
                : 1);

        Guid? dataMigrationRunId = resumeJournal?.DataMigrationRunId;
        try
        {
            await using var connection = new NpgsqlConnection(
                PostgreSqlDeploymentConnectionService.CreateBuilder(request.Connection, false).ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (request.Options.InstallRequiredExtensions)
            {
                foreach (var extension in manifest.RequiredExtensions.Order(StringComparer.Ordinal))
                {
                    var extensionEntry = await ExecuteSyntheticAsync(
                        connection,
                        deploymentId,
                        DeploymentPhase.Extensions,
                        $"extension:{extension}",
                        $"CREATE EXTENSION IF NOT EXISTS {Quote(extension)};",
                        "01_Extensions.sql",
                        [],
                        request,
                        entries,
                        failures,
                        progress,
                        total,
                        cancellationToken).ConfigureAwait(false);
                    await SaveRunningAsync(
                        journal,
                        entries,
                        dataMigrationRunId,
                        cancellationToken).ConfigureAwait(false);
                    if (extensionEntry.Status == DeploymentObjectStatus.Failed &&
                        ShouldStop(request.Options.ErrorPolicy))
                    {
                        return await FinishFailedAsync().ConfigureAwait(false);
                    }
                }
            }

            var (preDataArtifacts, postDataArtifacts) = SplitArtifactsAroundData(selected);
            var preDataFailed = await ExecuteArtifactGroupsAsync(
                connection,
                preDataArtifacts,
                assessment,
                request,
                journal,
                deploymentId,
                entries,
                failures,
                progress,
                total,
                dataMigrationRunId,
                cancellationToken).ConfigureAwait(false);
            if (preDataFailed)
            {
                return await FinishFailedAsync().ConfigureAwait(false);
            }

            if (ShouldExecuteDataMigration(request) &&
                !HasCompletedDataMigration(entries))
            {
                var dataResult = await ExecuteDataMigrationAsync(
                    request,
                    deploymentId,
                    entries,
                    failures,
                    progress,
                    total,
                    cancellationToken).ConfigureAwait(false);
                dataMigrationRunId = dataResult.RunId;
                await SaveRunningAsync(
                    journal,
                    entries,
                    dataMigrationRunId,
                    cancellationToken).ConfigureAwait(false);
                if (dataResult.State is not MigrationRunState.Completed &&
                    dataResult.State is not MigrationRunState.ValidationOnly)
                {
                    return await FinishFailedAsync().ConfigureAwait(false);
                }
            }

            var postDataFailed = await ExecuteArtifactGroupsAsync(
                connection,
                postDataArtifacts,
                assessment,
                request,
                journal,
                deploymentId,
                entries,
                failures,
                progress,
                total,
                dataMigrationRunId,
                cancellationToken).ConfigureAwait(false);
            if (postDataFailed)
            {
                return await FinishFailedAsync().ConfigureAwait(false);
            }

            await RunPostDeploymentAsync(
                connection,
                request,
                manifest,
                deploymentId,
                entries,
                failures,
                progress,
                total,
                cancellationToken).ConfigureAwait(false);
            var finalStatus = failures.Count == 0 &&
                entries.All(item => item.Status is DeploymentObjectStatus.Succeeded
                    or DeploymentObjectStatus.Skipped
                    or DeploymentObjectStatus.SkippedEquivalent
                    or DeploymentObjectStatus.Manual
                    or DeploymentObjectStatus.Unsupported)
                ? assessment.Findings.Any(item => item.Severity == DeploymentFindingSeverity.Warning)
                    ? DeploymentRunStatus.SucceededWithWarnings
                    : DeploymentRunStatus.Succeeded
                : DeploymentRunStatus.Failed;
            return await FinishAsync(finalStatus).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FinishAsync(DeploymentRunStatus.Cancelled, CancellationToken.None)
                .ConfigureAwait(false);
        }

        async Task<DeploymentResult> FinishFailedAsync() =>
            await FinishAsync(DeploymentRunStatus.Failed).ConfigureAwait(false);

        async Task<DeploymentResult> FinishAsync(
            DeploymentRunStatus status,
            CancellationToken? saveToken = null)
        {
            var completedJournal = journal with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                Status = status,
                Objects = entries.ToArray(),
                DataMigrationRunId = dataMigrationRunId,
                FinalFindings = assessment.Findings
            };
            journalPath = await journalStore.SaveAsync(
                completedJournal,
                saveToken ?? cancellationToken).ConfigureAwait(false);
            var result = new DeploymentResult(
                deploymentId,
                status,
                startedAt,
                DateTimeOffset.UtcNow,
                request.Connection.TargetDatabase,
                journalPath,
                entries.ToArray(),
                failures.ToArray(),
                dataMigrationRunId,
                assessment.Findings);
            session.SetResult(result);
            return result;
        }
    }

    private async Task<DeploymentObjectJournal> ExecuteArtifactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? phaseTransaction,
        Guid deploymentId,
        PackageArtifactManifest artifact,
        DeploymentRequest request,
        IReadOnlyList<DeploymentObjectJournal> existing,
        List<DeploymentFailure> failures,
        IProgress<DeploymentProgress>? progress,
        int total,
        CancellationToken cancellationToken)
    {
        var statements = scriptParser.Parse(artifact.Sql);
        var started = DateTimeOffset.UtcNow;
        var retries = new List<DeploymentRetryRecord>();
        Report(progress, deploymentId, artifact.Phase, artifact.SourceObjectId,
            Target(artifact), "Executing", existing, total, 0);
        NpgsqlTransaction? objectTransaction = null;
        var ownsTransaction = phaseTransaction is null &&
            request.Options.TransactionMode == DeploymentTransactionMode.TransactionPerObject &&
            statements.All(item => item.CanRunInTransaction);
        if (ownsTransaction)
        {
            objectTransaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var transaction = phaseTransaction ?? objectTransaction;
        try
        {
            foreach (var statement in statements)
            {
                await ExecuteStatementWithRetryAsync(
                    connection,
                    transaction,
                    statement,
                    artifact,
                    request,
                    retries,
                    cancellationToken).ConfigureAwait(false);
            }

            if (objectTransaction is not null)
            {
                await objectTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new DeploymentObjectJournal(
                artifact.SourceObjectId,
                Target(artifact),
                artifact.Phase,
                artifact.ScriptFile,
                artifact.SqlSha256,
                DeploymentObjectStatus.Succeeded,
                phaseTransaction is not null
                    ? CommitStatus.Pending
                    : objectTransaction is not null
                        ? CommitStatus.Committed
                        : CommitStatus.NonTransactional,
                started,
                DateTimeOffset.UtcNow,
                artifact.Dependencies,
                retries,
                null,
                IsIdempotent(artifact.Sql),
                null);
        }
        catch (PostgresException exception)
        {
            if (objectTransaction is not null)
            {
                await objectTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var failure = CreateFailure(
                request.PackageDirectory,
                artifact,
                exception,
                started,
                retries.Count);
            failures.Add(failure);
            Report(progress, deploymentId, artifact.Phase, artifact.SourceObjectId,
                Target(artifact), $"Failed with SQLSTATE {exception.SqlState}", existing, total, retries.Count);
            return new DeploymentObjectJournal(
                artifact.SourceObjectId,
                Target(artifact),
                artifact.Phase,
                artifact.ScriptFile,
                artifact.SqlSha256,
                DeploymentObjectStatus.Failed,
                objectTransaction is not null
                    ? CommitStatus.RolledBack
                    : phaseTransaction is not null
                        ? CommitStatus.Pending
                        : CommitStatus.NonTransactional,
                started,
                DateTimeOffset.UtcNow,
                artifact.Dependencies,
                retries,
                failure,
                IsIdempotent(artifact.Sql),
                "PostgreSQL rejected the object. Provider detail was redacted where it could contain data.");
        }
        finally
        {
            if (objectTransaction is not null)
            {
                await objectTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteStatementWithRetryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ParsedSqlStatement statement,
        PackageArtifactManifest artifact,
        DeploymentRequest request,
        List<DeploymentRetryRecord> retries,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var command = new NpgsqlCommand(statement.Sql, connection, transaction)
                {
                    CommandTimeout = request.Connection.CommandTimeoutSeconds
                };
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PostgresException exception) when (
                PostgreSqlDeploymentErrorClassifier.IsTransient(exception) &&
                attempt < request.Options.RetryCount &&
                request.Options.ErrorPolicy == DeploymentErrorPolicy.RetryTransientFailures)
            {
                var delay = TimeSpan.FromTicks(
                    request.Options.RetryBaseDelay.Ticks * (1L << Math.Min(attempt, 10)));
                retries.Add(new DeploymentRetryRecord(
                    attempt + 1,
                    DateTimeOffset.UtcNow,
                    delay,
                    $"Transient SQLSTATE {exception.SqlState}"));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<DeploymentObjectJournal> ExecuteSyntheticAsync(
        NpgsqlConnection connection,
        Guid deploymentId,
        DeploymentPhase phase,
        string target,
        string sql,
        string script,
        IReadOnlyList<InventoryObjectId> dependencies,
        DeploymentRequest request,
        List<DeploymentObjectJournal> entries,
        List<DeploymentFailure> failures,
        IProgress<DeploymentProgress>? progress,
        int total,
        CancellationToken cancellationToken)
    {
        var artifact = new PackageArtifactManifest(
            new InventoryObjectId(Guid.NewGuid()),
            "Synthetic",
            string.Empty,
            target,
            phase,
            script,
            sql,
            Hash(sql),
            Domain.Inventory.ConversionClassification.Automatic,
            dependencies,
            [],
            false,
            [],
            -1);
        var entry = await ExecuteArtifactAsync(
            connection,
            null,
            deploymentId,
            artifact,
            request,
            entries.ToArray(),
            failures,
            progress,
            total,
            cancellationToken).ConfigureAwait(false);
        entries.Add(entry);
        return entry;
    }

    private async Task<DataMigrationResult> ExecuteDataMigrationAsync(
        DeploymentRequest request,
        Guid deploymentId,
        List<DeploymentObjectJournal> entries,
        List<DeploymentFailure> failures,
        IProgress<DeploymentProgress>? progress,
        int total,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var dataRequest = request.DataMigrationRequest! with
        {
            TargetConnectionString =
                PostgreSqlDeploymentConnectionService.CreateBuilder(request.Connection, false).ConnectionString,
            Options = request.Options.DataMigrationOptions ??
                request.DataMigrationRequest!.Options
        };
        var result = await dataMigrationEngine.ExecuteAsync(
            dataRequest,
            new Progress<DataMigrationProgress>(item =>
                progress?.Report(new DeploymentProgress(
                    deploymentId,
                    DeploymentPhase.Data,
                    item.TableId,
                    item.TableId?.ToString() ?? "data migration",
                    item.Message,
                    entries.Count,
                    entries.Count(entry => entry.Status == DeploymentObjectStatus.Failed),
                    entries.Count(entry => entry.Status is DeploymentObjectStatus.Skipped
                        or DeploymentObjectStatus.SkippedEquivalent),
                    total,
                    item.RetryCount))),
            cancellationToken).ConfigureAwait(false);
        var succeeded = result.State is MigrationRunState.Completed or MigrationRunState.ValidationOnly;
        entries.Add(new DeploymentObjectJournal(
            null,
            $"data migration {result.RunId}",
            DeploymentPhase.Data,
            "10_Data",
            result.RunId.ToString("N"),
            succeeded ? DeploymentObjectStatus.Succeeded : DeploymentObjectStatus.Failed,
            succeeded ? CommitStatus.Committed : CommitStatus.NotStarted,
            started,
            DateTimeOffset.UtcNow,
            [],
            [],
            null,
            false,
            $"{result.Tables.Sum(item => item.RowsWritten):N0} rows written; state {result.State}."));
        return result;
    }

    private async Task<bool> ExecuteArtifactGroupsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<PackageArtifactManifest> artifacts,
        PreDeploymentAssessment assessment,
        DeploymentRequest request,
        DeploymentJournal journal,
        Guid deploymentId,
        List<DeploymentObjectJournal> entries,
        List<DeploymentFailure> failures,
        IProgress<DeploymentProgress>? progress,
        int total,
        Guid? dataMigrationRunId,
        CancellationToken cancellationToken)
    {
        foreach (var phaseGroup in GroupContiguousByPhase(artifacts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phaseArtifacts = phaseGroup.ToArray();
            var phaseCanUseTransaction = request.Options.TransactionMode is
                    DeploymentTransactionMode.TransactionPerPhase or
                    DeploymentTransactionMode.SingleTransactionWherePossible &&
                phaseArtifacts.All(item => scriptParser.Parse(item.Sql)
                    .All(statement => statement.CanRunInTransaction));
            await using var phaseTransaction = phaseCanUseTransaction
                ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var phaseStartIndex = entries.Count;
            var phaseFailed = false;
            foreach (var artifact in phaseArtifacts)
            {
                if (AlreadyCommitted(entries, artifact))
                {
                    continue;
                }

                if (HasFailedDependency(entries, artifact))
                {
                    entries.Add(CreateBlockedEntry(
                        artifact,
                        "A prerequisite failed.",
                        blockedByDependency: true));
                    Report(
                        progress,
                        deploymentId,
                        artifact.Phase,
                        artifact.SourceObjectId,
                        Target(artifact),
                        "Blocked by a failed prerequisite.",
                        entries,
                        total,
                        0);
                    continue;
                }

                var conflict = assessment.Conflicts.FirstOrDefault(item =>
                    item.SourceObjectId == artifact.SourceObjectId && item.Exists);
                var resolution = ResolveConflict(conflict, artifact, request.Options);
                if (resolution.Skip)
                {
                    entries.Add(CreateSkippedEntry(artifact, resolution.Message));
                    continue;
                }

                if (resolution.Block)
                {
                    entries.Add(CreateBlockedEntry(
                        artifact,
                        resolution.Message ?? "Conflict resolution blocked the object."));
                    phaseFailed = true;
                    if (ShouldStop(request.Options.ErrorPolicy))
                    {
                        break;
                    }

                    continue;
                }

                var entry = await ExecuteArtifactAsync(
                    connection,
                    phaseTransaction,
                    deploymentId,
                    artifact with
                    {
                        Sql = ApplyConstraintStrategy(
                            resolution.Sql ?? artifact.Sql,
                            artifact,
                            request.Options.ConstraintStrategy)
                    },
                    request,
                    entries,
                    failures,
                    progress,
                    total,
                    cancellationToken).ConfigureAwait(false);
                entries.Add(entry);
                phaseFailed |= entry.Status == DeploymentObjectStatus.Failed;
                await SaveRunningAsync(
                    journal,
                    entries,
                    dataMigrationRunId,
                    cancellationToken).ConfigureAwait(false);
                if (phaseFailed && ShouldStop(request.Options.ErrorPolicy))
                {
                    break;
                }

                if (phaseFailed && phaseTransaction is not null)
                {
                    break;
                }
            }

            if (phaseTransaction is not null)
            {
                if (phaseFailed)
                {
                    await phaseTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    for (var index = phaseStartIndex; index < entries.Count; index++)
                    {
                        if (entries[index].Status is DeploymentObjectStatus.Succeeded
                            or DeploymentObjectStatus.Failed)
                        {
                            entries[index] = entries[index] with
                            {
                                Status = entries[index].Status == DeploymentObjectStatus.Succeeded
                                    ? DeploymentObjectStatus.RolledBack
                                    : DeploymentObjectStatus.Failed,
                                CommitStatus = CommitStatus.RolledBack,
                                Message = "Rolled back with the deployment phase."
                            };
                        }
                    }
                }
                else
                {
                    await phaseTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    for (var index = phaseStartIndex; index < entries.Count; index++)
                    {
                        if (entries[index].Status == DeploymentObjectStatus.Succeeded)
                        {
                            entries[index] = entries[index] with
                            {
                                CommitStatus = CommitStatus.Committed
                            };
                        }
                    }
                }
            }

            if (phaseFailed &&
                (ShouldStop(request.Options.ErrorPolicy) ||
                 request.Options.ErrorPolicy == DeploymentErrorPolicy.ContinueCurrentPhase))
            {
                return true;
            }
        }

        return false;
    }


    private async Task RunPostDeploymentAsync(
        NpgsqlConnection connection,
        DeploymentRequest request,
        MigrationPackageManifest manifest,
        Guid deploymentId,
        List<DeploymentObjectJournal> entries,
        List<DeploymentFailure> failures,
        IProgress<DeploymentProgress>? progress,
        int total,
        CancellationToken cancellationToken)
    {
        var selectedForeignKeys = manifest.Artifacts
            .Where(item => item.Phase == DeploymentPhase.ForeignKeys)
            .Where(item => item.IsExecutable)
            .Where(item => !item.RequiresManualReview &&
                item.Classification != Domain.Inventory.ConversionClassification.Unsupported)
            .Where(item => PreDeploymentAssessmentService.IsSelected(item, request.Options))
            .ToArray();

        var selectedTables = manifest.Artifacts
            .Where(item => item.Phase == DeploymentPhase.Tables)
            .Where(item => item.IsExecutable)
            .Where(item => !item.RequiresManualReview &&
                item.Classification != Domain.Inventory.ConversionClassification.Unsupported)
            .Where(item => PreDeploymentAssessmentService.IsSelected(item, request.Options))
            .ToArray();

        /*
         * AddNotValidThenValidate:
         *     Create the foreign key as NOT VALID and validate it during
         *     this deployment.
         *
         * ValidateInLaterPhase:
         *     Create the foreign key as NOT VALID during an earlier run,
         *     then validate it when the validation phase is invoked.
         */
        var validateConstraintsNow =
            request.Options.ValidateConstraints &&
            request.Options.ConstraintStrategy is
                ConstraintDeploymentStrategy.AddNotValidThenValidate or
                ConstraintDeploymentStrategy.ValidateInLaterPhase;

        if (validateConstraintsNow)
        {
            foreach (var constraint in selectedForeignKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var validationSql = CreateSafeConstraintValidationSql(constraint);
                if (validationSql is null)
                {
                    continue;
                }

                await ExecuteSyntheticAsync(
                        connection,
                        deploymentId,
                        DeploymentPhase.Validation,
                        $"validate:{Target(constraint)}",
                        validationSql,
                        "21_Validation.sql",
                        [constraint.SourceObjectId],
                        request,
                        entries,
                        failures,
                        progress,
                        total,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (request.Options.AnalyzeTables)
        {
            foreach (var table in selectedTables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await ExecuteSyntheticAsync(
                        connection,
                        deploymentId,
                        DeploymentPhase.PostDeployment,
                        $"analyze:{Target(table)}",
                        $"ANALYZE {Qualified(table)};",
                        "20_PostDeployment.sql",
                        [table.SourceObjectId],
                        request,
                        entries,
                        failures,
                        progress,
                        total,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (request.Options.VacuumAnalyze)
        {
            foreach (var table in selectedTables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await ExecuteSyntheticAsync(
                        connection,
                        deploymentId,
                        DeploymentPhase.PostDeployment,
                        $"vacuum:{Target(table)}",
                        $"VACUUM (ANALYZE) {Qualified(table)};",
                        "20_PostDeployment.sql",
                        [table.SourceObjectId],
                        request,
                        entries,
                        failures,
                        progress,
                        total,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (manifest.RequiredExtensions.Count > 0)
        {
            var requiredExtensionsSql =
                $"DO $$ BEGIN " +
                $"IF EXISTS (" +
                $"SELECT required.name " +
                $"FROM (VALUES {string.Join(", ", manifest.RequiredExtensions.Select(item => $"({Literal(item)})"))}) required(name) " +
                $"WHERE NOT EXISTS (" +
                $"SELECT 1 FROM pg_extension e " +
                $"WHERE e.extname = required.name" +
                $")) " +
                $"THEN RAISE EXCEPTION 'Required extension verification failed'; " +
                $"END IF; " +
                $"END $$;";

            await ExecuteSyntheticAsync(
                    connection,
                    deploymentId,
                    DeploymentPhase.Validation,
                    "required extension verification",
                    requiredExtensionsSql,
                    "21_Validation.sql",
                    [],
                    request,
                    entries,
                    failures,
                    progress,
                    total,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string? CreateSafeConstraintValidationSql(
        PackageArtifactManifest artifact)
    {
        var validationSql = CreateConstraintValidationSql(artifact.Sql);
        if (validationSql is null)
        {
            return null;
        }

        return $"""
            DO
            $$
            BEGIN
                {validationSql.TrimEnd(';')};
            EXCEPTION
                WHEN undefined_object THEN
                    NULL;
            END;
            $$;
            """;
    }

    private async Task SaveRunningAsync(
        DeploymentJournal template,
        IReadOnlyList<DeploymentObjectJournal> entries,
        Guid? dataMigrationRunId,
        CancellationToken cancellationToken)
    {
        await journalStore.SaveAsync(
            template with
            {
                Objects = entries.ToArray(),
                DataMigrationRunId = dataMigrationRunId
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeploymentResult> CompleteAsync(
        DeploymentRequest request,
        DeploymentJournal journal,
        string journalPath,
        DeploymentRunStatus status,
        IReadOnlyList<DeploymentObjectJournal> entries,
        IReadOnlyList<DeploymentFailure> failures,
        Guid? dataMigrationRunId,
        IReadOnlyList<DeploymentFinding> findings,
        CancellationToken cancellationToken)
    {
        var completed = journal with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            Status = status,
            Objects = entries,
            DataMigrationRunId = dataMigrationRunId,
            FinalFindings = findings
        };
        journalPath = await journalStore.SaveAsync(completed, cancellationToken)
            .ConfigureAwait(false);
        var result = new DeploymentResult(
            journal.DeploymentId,
            status,
            journal.StartedAt,
            DateTimeOffset.UtcNow,
            request.Connection.TargetDatabase,
            journalPath,
            entries,
            failures,
            dataMigrationRunId,
            findings);
        session.SetResult(result);
        return result;
    }

    private static DeploymentJournal CreateJournal(
        Guid deploymentId,
        MigrationPackageManifest manifest,
        DeploymentRequest request,
        string fingerprint,
        string optionsHash,
        DateTimeOffset startedAt,
        DeploymentRunStatus status,
        IReadOnlyList<DeploymentObjectJournal> entries,
        Guid? dataMigrationRunId,
        IReadOnlyList<DeploymentFinding> findings,
        IReadOnlyList<string> overrides,
        IReadOnlyList<string> destructive) =>
        new(
            DeploymentJournal.CurrentFormatVersion,
            deploymentId,
            manifest.PackageId,
            manifest.MigrationRunId,
            startedAt,
            null,
            status,
            typeof(PostgreSqlDeploymentEngine).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Environment.MachineName,
            Environment.UserName,
            Path.GetFullPath(request.PackageDirectory),
            fingerprint,
            $"{request.Connection.Host}:{request.Connection.Port}",
            request.Connection.TargetDatabase,
            optionsHash,
            overrides,
            destructive,
            entries,
            dataMigrationRunId,
            findings);

    internal static void ValidateResume(
        DeploymentJournal? journal,
        MigrationPackageManifest manifest,
        string fingerprint,
        DeploymentRequest request,
        string optionsHash)
    {
        if (journal is null)
        {
            return;
        }

        if (journal.PackageId != manifest.PackageId ||
            !journal.PackageFingerprint.Equals(fingerprint, StringComparison.Ordinal) ||
            !journal.OptionsHash.Equals(optionsHash, StringComparison.Ordinal) ||
            !journal.TargetServer.Equals(
                $"{request.Connection.Host}:{request.Connection.Port}",
                StringComparison.OrdinalIgnoreCase) ||
            !journal.TargetDatabase.Equals(request.Connection.TargetDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resume refused because the package, deployment options, or target identity changed.");
        }
    }

    internal static List<PackageArtifactManifest> OrderArtifacts(
        IReadOnlyList<PackageArtifactManifest> artifacts)
        => ArtifactDependencyPlanner.Order(
                artifacts,
                item => item.SourceObjectId,
                item => item.Dependencies,
                item => DeploymentPhaseOrdering.GetRank(item.Phase, item.TargetObjectType),
                Target,
                failOnCycle: false)
            .ToList();

    internal static (
        IReadOnlyList<PackageArtifactManifest> PreData,
        IReadOnlyList<PackageArtifactManifest> PostData)
        SplitArtifactsAroundData(IReadOnlyList<PackageArtifactManifest> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var preDataDependencyIds =
            ArtifactDependencyPlanner.GetTransitiveDependencyClosure(
                artifacts,
                item => item.SourceObjectId,
                item => item.Dependencies,
                item => IsPreDataPhase(item.Phase));
        bool IsPreDataArtifact(PackageArtifactManifest item) =>
            IsPreDataPhase(item.Phase) ||
            item.Phase == DeploymentPhase.Functions &&
            preDataDependencyIds.Contains(item.SourceObjectId);
        return (
            artifacts.Where(IsPreDataArtifact).ToArray(),
            artifacts.Where(item => !IsPreDataArtifact(item)).ToArray());
    }

    private static bool IsPreDataPhase(DeploymentPhase phase) =>
        phase is DeploymentPhase.PreDeployment
            or DeploymentPhase.Extensions
            or DeploymentPhase.Schemas
            or DeploymentPhase.Types
            or DeploymentPhase.Sequences
            or DeploymentPhase.Tables
            or DeploymentPhase.PreDataFunctions
            or DeploymentPhase.DefaultsAndGeneratedColumns
            or DeploymentPhase.PrimaryKeys
            or DeploymentPhase.UniqueConstraints
            or DeploymentPhase.CheckConstraints;

    private static bool ShouldExecuteDataMigration(DeploymentRequest request) =>
        request.DataMigrationRequest is not null &&
        request.Options.Scope is DeploymentScope.CompletePackage or DeploymentScope.DataOnly;

    private static bool HasCompletedDataMigration(
        IReadOnlyList<DeploymentObjectJournal> entries) =>
        entries.Any(item =>
            item.Phase == DeploymentPhase.Data &&
            item.Status == DeploymentObjectStatus.Succeeded &&
            item.CommitStatus == CommitStatus.Committed);

    private static List<IGrouping<DeploymentPhase, PackageArtifactManifest>>
        GroupContiguousByPhase(IReadOnlyList<PackageArtifactManifest> artifacts)
    {
        var groups = new List<IGrouping<DeploymentPhase, PackageArtifactManifest>>();
        var index = 0;
        while (index < artifacts.Count)
        {
            var phase = artifacts[index].Phase;
            var items = new List<PackageArtifactManifest>();
            while (index < artifacts.Count && artifacts[index].Phase == phase)
            {
                items.Add(artifacts[index]);
                index++;
            }
            groups.Add(new ContiguousPhaseGroup(phase, items));
        }
        return groups;
    }

    private sealed class ContiguousPhaseGroup(
        DeploymentPhase key,
        IReadOnlyList<PackageArtifactManifest> items)
        : IGrouping<DeploymentPhase, PackageArtifactManifest>
    {
        public DeploymentPhase Key { get; } = key;

        public IEnumerator<PackageArtifactManifest> GetEnumerator() => items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private static ConflictResolution ResolveConflict(
        ObjectConflict? conflict,
        PackageArtifactManifest artifact,
        DeploymentOptions options)
    {
        if (conflict is null || !conflict.Exists)
        {
            return new(false, false, null, null);
        }

        if (conflict.IsEquivalent)
        {
            return new(true, false, null, "Equivalent target object retained.");
        }

        return options.ConflictPolicy switch
        {
            ExistingObjectConflictPolicy.ReplaceWhenSafe when artifact.Phase is
                DeploymentPhase.Views or DeploymentPhase.Functions or
                DeploymentPhase.PreDataFunctions or DeploymentPhase.Procedures =>
                new(false, false, ToCreateOrReplace(artifact.Sql), "Existing programmable object replaced."),
            ExistingObjectConflictPolicy.DropAndRecreate when
                artifact.Phase != DeploymentPhase.Tables || !conflict.ContainsData ||
                options.DatabaseCreation.DestructiveActionConfirmed =>
                new(false, false, DropSql(artifact) + Environment.NewLine + artifact.Sql,
                    "Existing object dropped and recreated."),
            ExistingObjectConflictPolicy.Fail =>
                new(false, true, null, "Existing-object policy is Fail."),
            ExistingObjectConflictPolicy.RenameTarget =>
                new(false, true, null, "RenameTarget requires an explicit regenerated identifier mapping."),
            ExistingObjectConflictPolicy.ManualDecision =>
                new(false, true, null, "Manual conflict decision is required."),
            _ => new(false, true, null, "The existing object is not safely reusable.")
        };
    }

    internal static (bool Skip, bool Block, string? Sql, string? Message)
        ResolveConflictForTesting(
            ObjectConflict? conflict,
            PackageArtifactManifest artifact,
            DeploymentOptions options)
    {
        var result = ResolveConflict(conflict, artifact, options);
        return (result.Skip, result.Block, result.Sql, result.Message);
    }

    private static string ToCreateOrReplace(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("CREATE OR REPLACE", StringComparison.OrdinalIgnoreCase)
            ? sql
            : "CREATE OR REPLACE" + trimmed["CREATE".Length..];
    }

    private static string DropSql(PackageArtifactManifest artifact) =>
        artifact.Phase switch
        {
            DeploymentPhase.Tables => $"DROP TABLE {Qualified(artifact)} CASCADE;",
            DeploymentPhase.Views => $"DROP VIEW {Qualified(artifact)} CASCADE;",
            DeploymentPhase.Sequences => $"DROP SEQUENCE {Qualified(artifact)} CASCADE;",
            DeploymentPhase.Types => $"DROP TYPE {Qualified(artifact)} CASCADE;",
            _ => throw new InvalidOperationException(
                $"Drop-and-recreate is not safely defined for {Target(artifact)}.")
        };

    private static string ApplyConstraintStrategy(
        string sql,
        PackageArtifactManifest artifact,
        ConstraintDeploymentStrategy strategy)
    {
        if (artifact.Phase != DeploymentPhase.ForeignKeys ||
            strategy is not ConstraintDeploymentStrategy.AddNotValidThenValidate and
                not ConstraintDeploymentStrategy.ValidateInLaterPhase ||
            sql.Contains(" NOT VALID", StringComparison.OrdinalIgnoreCase))
        {
            return sql;
        }

        var trimmed = sql.TrimEnd();
        return trimmed.EndsWith(';')
            ? trimmed[..^1] + " NOT VALID;"
            : trimmed + " NOT VALID";
    }

    internal static string ApplyConstraintStrategyForTesting(
        string sql,
        PackageArtifactManifest artifact,
        ConstraintDeploymentStrategy strategy) =>
        ApplyConstraintStrategy(sql, artifact, strategy);

    private static string? CreateConstraintValidationSql(string sql)
    {
        var match = Regex.Match(
            sql,
            @"(?is)ALTER\s+TABLE\s+(?<table>(?:""[^""]+""|[a-zA-Z0-9_]+)(?:\s*\.\s*(?:""[^""]+""|[a-zA-Z0-9_]+))?)\s+ADD\s+CONSTRAINT\s+(?<constraint>""[^""]+""|[a-zA-Z0-9_]+)");
        return match.Success
            ? $"ALTER TABLE {match.Groups["table"].Value} VALIDATE CONSTRAINT {match.Groups["constraint"].Value};"
            : null;
    }

    internal static string? CreateConstraintValidationSqlForTesting(string sql) =>
        CreateConstraintValidationSql(sql);

    private static bool AlreadyCommitted(
        IEnumerable<DeploymentObjectJournal> entries,
        PackageArtifactManifest artifact) =>
        entries.Any(item =>
            item.SourceObjectId == artifact.SourceObjectId &&
            item.ExecutedSqlHash.Equals(artifact.SqlSha256, StringComparison.OrdinalIgnoreCase) &&
            item.Status == DeploymentObjectStatus.Succeeded &&
            item.CommitStatus is CommitStatus.Committed or CommitStatus.NonTransactional);

    private static bool HasFailedDependency(
        IEnumerable<DeploymentObjectJournal> entries,
        PackageArtifactManifest artifact) =>
        artifact.Dependencies.Any(dependency => entries.Any(item =>
            item.SourceObjectId == dependency &&
            item.Status is DeploymentObjectStatus.Failed or DeploymentObjectStatus.Blocked
                or DeploymentObjectStatus.BlockedByDependency
                or DeploymentObjectStatus.RolledBack));

    private static DeploymentObjectJournal CreateBlockedEntry(
        PackageArtifactManifest artifact,
        string message = "A prerequisite failed.",
        bool blockedByDependency = false) =>
        new(
            artifact.SourceObjectId,
            Target(artifact),
            artifact.Phase,
            artifact.ScriptFile,
            artifact.SqlSha256,
            blockedByDependency
                ? DeploymentObjectStatus.BlockedByDependency
                : DeploymentObjectStatus.Blocked,
            CommitStatus.NotStarted,
            null,
            DateTimeOffset.UtcNow,
            artifact.Dependencies,
            [],
            null,
            false,
            message);

    private static DeploymentObjectJournal CreateSkippedEntry(
        PackageArtifactManifest artifact,
        string? message) =>
        new(
            artifact.SourceObjectId,
            Target(artifact),
            artifact.Phase,
            artifact.ScriptFile,
            artifact.SqlSha256,
            DeploymentObjectStatus.SkippedEquivalent,
            CommitStatus.NotStarted,
            null,
            DateTimeOffset.UtcNow,
            artifact.Dependencies,
            [],
            null,
            true,
            message);

    private static DeploymentFailure CreateFailure(
        string package,
        PackageArtifactManifest artifact,
        PostgresException exception,
        DateTimeOffset started,
        int retries) =>
        new(
            package,
            artifact.Phase,
            artifact.SourceObjectId,
            Target(artifact),
            artifact.SqlSha256,
            artifact.ScriptFile,
            null,
            exception.SqlState,
            exception.Severity,
            string.IsNullOrWhiteSpace(exception.Detail) ? null : "PostgreSQL detail redacted.",
            exception.Hint,
            exception.Position,
            exception.InternalPosition,
            exception.SchemaName,
            exception.TableName,
            exception.ColumnName,
            exception.ConstraintName,
            exception.DataTypeName,
            started,
            DateTimeOffset.UtcNow,
            retries);

    private static bool ShouldStop(DeploymentErrorPolicy policy) =>
        policy is DeploymentErrorPolicy.StopImmediately or DeploymentErrorPolicy.ManualDecision;

    private static bool IsIdempotent(string sql) =>
        sql.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("CREATE OR REPLACE", StringComparison.OrdinalIgnoreCase) ||
        sql.TrimStart().StartsWith("GRANT ", StringComparison.OrdinalIgnoreCase) ||
        sql.TrimStart().StartsWith("COMMENT ON ", StringComparison.OrdinalIgnoreCase);

    private static void Report(
        IProgress<DeploymentProgress>? progress,
        Guid deploymentId,
        DeploymentPhase phase,
        InventoryObjectId? objectId,
        string target,
        string message,
        IEnumerable<DeploymentObjectJournal> entries,
        int total,
        int retry)
    {
        var snapshot = entries.ToArray();
        progress?.Report(new DeploymentProgress(
            deploymentId,
            phase,
            objectId,
            target,
            message,
            snapshot.Count(item => item.Status == DeploymentObjectStatus.Succeeded),
            snapshot.Count(item => item.Status == DeploymentObjectStatus.Failed),
            snapshot.Count(item => item.Status is DeploymentObjectStatus.Skipped
                or DeploymentObjectStatus.SkippedEquivalent),
            total,
            retry));
    }

    private static string Qualified(PackageArtifactManifest artifact) =>
        $"{Quote(Unquote(artifact.TargetSchema))}.{Quote(Unquote(artifact.TargetName))}";

    private static string Target(PackageArtifactManifest artifact) =>
        artifact.Phase == DeploymentPhase.Schemas
            ? artifact.TargetName
            : $"{artifact.TargetSchema}.{artifact.TargetName}";

    private static string Quote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);

    private static string Unquote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Unquote(identifier);

    private static string Literal(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ConflictResolution(
        bool Skip,
        bool Block,
        string? Sql,
        string? Message);
}
