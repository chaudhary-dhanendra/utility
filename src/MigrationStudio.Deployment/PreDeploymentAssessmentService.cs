using System.Text.Json;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;
using Npgsql;

namespace MigrationStudio.Deployment;

public sealed class PreDeploymentAssessmentService(
    IMigrationPackageReader packageReader,
    IPostgreSqlDeploymentConnectionService connectionService) : IPreDeploymentAssessmentService
{
    public async Task<PreDeploymentAssessment> AssessAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        request.Options.Validate();
        request.Connection.Validate();
        var findings = new List<DeploymentFinding>();
        MigrationPackageManifest? manifest;
        var integrity = true;
        try
        {
            manifest = await packageReader.ReadAndVerifyAsync(
                request.PackageDirectory,
                false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            integrity = false;
            findings.Add(new DeploymentFinding(
                "PACKAGE.INTEGRITY",
                DeploymentFindingSeverity.Critical,
                exception.Message));
            manifest = await TryDiagnosticReadAsync(request, cancellationToken).ConfigureAwait(false);
        }

        PostgreSqlCapabilityAssessment? capabilities = null;
        if (request.Options.Mode != DeploymentMode.GenerateOnly)
        {
            try
            {
                capabilities = await connectionService.AssessAsync(
                    request.Connection,
                    request.Options.Mode == DeploymentMode.CreateDatabaseAndDeploy,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                findings.Add(new DeploymentFinding(
                    "CONNECTION.FAILED",
                    DeploymentFindingSeverity.Critical,
                    "The PostgreSQL connection or capability assessment failed. Credentials and provider details were redacted."));
            }
        }

        var conflicts = new List<ObjectConflict>();
        var packageDuplicates = new List<PackageObjectDuplicate>();
        if (manifest is not null)
        {
            AssessManifest(manifest, request.Options, capabilities, findings);
            packageDuplicates.AddRange(FindPackageDuplicates(manifest, request.Options));
            if (capabilities is not null &&
                request.Options.Mode != DeploymentMode.CreateDatabaseAndDeploy)
            {
                conflicts.AddRange(await FindConflictsAsync(
                    request,
                    manifest,
                    cancellationToken).ConfigureAwait(false));
                findings.AddRange(CreateConflictFindings(
                    conflicts,
                    request.Options.ConflictPolicy));
            }
        }

        if (request.Options.Mode == DeploymentMode.CreateDatabaseAndDeploy &&
            request.Options.DatabaseCreation.ExistsPolicy == DatabaseExistsPolicy.DropAndRecreate &&
            capabilities is not null)
        {
            var activeConnections = await CountActiveTargetConnectionsAsync(
                request.Connection,
                cancellationToken).ConfigureAwait(false);
            findings.Add(new DeploymentFinding(
                "TARGET.ACTIVE_CONNECTIONS",
                activeConnections == 0
                    ? DeploymentFindingSeverity.Information
                    : DeploymentFindingSeverity.Warning,
                activeConnections == 0
                    ? "No active sessions are connected to the target database."
                    : $"{activeConnections} active target sessions will be terminated by the confirmed drop-and-recreate operation.",
                CanOverride: true));
        }

        findings.Add(new DeploymentFinding(
            "TARGET.DISK_SPACE",
            DeploymentFindingSeverity.Information,
            "PostgreSQL does not expose portable filesystem free-space data; verify target tablespace capacity externally."));
        var overrideApplied = request.Options.PreDeploymentPolicy == PreDeploymentPolicy.AdministratorOverride &&
            request.Options.AdministratorOverrideConfirmed;
        var canDeploy = integrity && manifest is not null && capabilities is not null &&
            !DeploymentBlockingPolicy.IsBlocked(
                findings,
                request.Options.PreDeploymentPolicy,
                overrideApplied);
        if (request.Options.Mode == DeploymentMode.GenerateOnly)
        {
            canDeploy = integrity && manifest is not null;
        }

        return new PreDeploymentAssessment(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Path.GetFullPath(request.PackageDirectory),
            manifest,
            capabilities,
            findings,
            conflicts,
            integrity,
            canDeploy,
            overrideApplied,
            overrideApplied ? request.Options.AdministratorOverrideReason : null)
        {
            PackageDuplicates = packageDuplicates
        };
    }

    private async Task<MigrationPackageManifest?> TryDiagnosticReadAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await packageReader.ReadAndVerifyAsync(
                request.PackageDirectory,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            return null;
        }
    }

    internal static void AssessManifest(
        MigrationPackageManifest manifest,
        DeploymentOptions options,
        PostgreSqlCapabilityAssessment? capabilities,
        List<DeploymentFinding> findings)
    {
        var artifactSourceIds = manifest.Artifacts
            .Select(item => item.SourceObjectId)
            .ToHashSet();
        var missingMappedObjects = manifest.ObjectMappings
            .Where(item => item.IncludedInScope && IsArtifactLevelMapping(item))
            .Select(item => item.SourceObjectId)
            .Distinct()
            .Where(item => !artifactSourceIds.Contains(item))
            .ToArray();


        if (missingMappedObjects.Length > 0)
        {
            foreach (var id in missingMappedObjects)
            {
                Console.WriteLine($"Missing artifact: {id}");
            }
            findings.Add(new DeploymentFinding(
                "PACKAGE.ARTIFACT_RECONCILIATION",
                DeploymentFindingSeverity.Critical,
                $"{missingMappedObjects.Length:N0} selected mapped objects are absent from the package manifest."));
        }

        if (capabilities?.ServerMajorVersion is { } version &&
            version < manifest.TargetPostgreSqlVersion)
        {
            findings.Add(new DeploymentFinding(
                "SERVER.VERSION",
                DeploymentFindingSeverity.Critical,
                $"Package targets PostgreSQL {manifest.TargetPostgreSqlVersion}, but the server is {version}."));
        }

        if (capabilities is not null)
        {
            foreach (var extension in manifest.RequiredExtensions)
            {
                if (capabilities.InstalledExtensions.ContainsKey(extension))
                {
                    continue;
                }

                var available = capabilities.AvailableExtensions.Contains(extension);
                findings.Add(new DeploymentFinding(
                    available ? "EXTENSION.INSTALL" : "EXTENSION.UNAVAILABLE",
                    available && options.InstallRequiredExtensions
                        ? DeploymentFindingSeverity.Warning
                        : DeploymentFindingSeverity.Critical,
                    available
                        ? $"Required extension '{extension}' is available but not installed."
                        : $"Required extension '{extension}' is unavailable on the target server.",
                    DeploymentPhase.Extensions));
            }

            if (!capabilities.CanCreateSchema)
            {
                findings.Add(new DeploymentFinding(
                    "PRIVILEGE.CREATE",
                    DeploymentFindingSeverity.Critical,
                    "The target role lacks CREATE privilege on the target database."));
            }

            if (options.Mode == DeploymentMode.CreateDatabaseAndDeploy &&
                !capabilities.CanCreateDatabase)
            {
                findings.Add(new DeploymentFinding(
                    "PRIVILEGE.CREATEDB",
                    DeploymentFindingSeverity.Critical,
                    "The target role lacks permission to create a database."));
            }
        }

        foreach (var mapping in manifest.ObjectMappings)
        {
            if (mapping.TargetUtf8ByteLength > 63)
            {
                findings.Add(new DeploymentFinding(
                    "IDENTIFIER.TOO_LONG",
                    DeploymentFindingSeverity.Critical,
                    $"Mapped identifier exceeds PostgreSQL's 63-byte limit: {mapping.TargetQualifiedName}.",
                    null,
                    mapping.SourceObjectId));
            }
            if (mapping.IsReservedWord && !mapping.WasQuoted)
            {
                findings.Add(new DeploymentFinding(
                    "IDENTIFIER.RESERVED_UNQUOTED",
                    DeploymentFindingSeverity.Critical,
                    $"Restricted PostgreSQL keyword is not quoted: {mapping.TargetQualifiedName}.",
                    null,
                    mapping.SourceObjectId));
            }
            if (mapping.IsBlocking || mapping.HadCollision && !mapping.CollisionResolved)
            {
                findings.Add(new DeploymentFinding(
                    "IDENTIFIER.BLOCKING",
                    DeploymentFindingSeverity.Critical,
                    $"Blocking identifier mapping: {mapping.SourceQualifiedName} -> {mapping.TargetQualifiedName}.",
                    null,
                    mapping.SourceObjectId));
            }
        }

        foreach (var duplicate in manifest.ObjectMappings
                     .GroupBy(MappingUniquenessKey, StringComparer.Ordinal)
                     .Where(group => group.Select(item => item.SourceObjectId).Distinct().Count() > 1))
        {
            findings.Add(new DeploymentFinding(
                "IDENTIFIER.COLLISION",
                DeploymentFindingSeverity.Critical,
                $"Multiple source objects map to the same PostgreSQL identifier namespace: '{duplicate.Key}'."));
        }

        foreach (var duplicate in FindPackageDuplicates(manifest, options))
        {
            findings.Add(new DeploymentFinding(
                "PACKAGE.DUPLICATE",
                DeploymentFindingSeverity.Critical,
                $"{duplicate.SourceObjectIds.Count} package artifacts have the same PostgreSQL identity: " +
                $"{duplicate.ObjectKind} {duplicate.TargetSchema}.{duplicate.TargetName}."));
        }

        var selectedArtifacts = manifest.Artifacts
            .Where(item => IsSelected(item, options) && item.IsExecutable)
            .ToArray();
        var deployableSelectedArtifacts = selectedArtifacts
            .Where(item => !item.RequiresManualReview &&
                item.Classification != Domain.Inventory.ConversionClassification.Unsupported)
            .ToArray();
        var reconciledDecisions = GetReconciledDecisions(manifest);

        if (options.RequireLivePostgreSqlValidation)
        {
            var missingLiveValidation = deployableSelectedArtifacts
                .Where(item =>
                    !HasSuccessfulCurrentLiveValidation(item) &&
                    !HasAcceptedNonFatalValidationBlock(item, reconciledDecisions))
                .ToArray();
            if (missingLiveValidation.Length > 0)
            {
                findings.Add(new DeploymentFinding(
                    "VALIDATION.LIVE_REQUIRED",
                    DeploymentFindingSeverity.Critical,
                    $"{missingLiveValidation.Length:N0} selected executable artifacts have not passed " +
                    "live PostgreSQL validation and are not covered by an accepted nonfatal " +
                    "dependency-reconciliation decision. Run live validation and export a fresh package " +
                    "before production deployment."));
            }

            var acceptedNonFatalBlocks = deployableSelectedArtifacts
                .Where(item =>
                    !HasSuccessfulCurrentLiveValidation(item) &&
                    HasAcceptedNonFatalValidationBlock(item, reconciledDecisions))
                .ToArray();
            if (acceptedNonFatalBlocks.Length > 0)
            {
                findings.Add(new DeploymentFinding(
                    "VALIDATION.NONFATAL_DEPENDENCY_RECONCILED",
                    DeploymentFindingSeverity.Warning,
                    $"{acceptedNonFatalBlocks.Length:N0} executable artifacts were structurally valid but " +
                    "were not executed during isolated validation because their dependencies require " +
                    "manual review or were otherwise classified as nonfatal. They will be deployed using " +
                    "the verified dependency-aware deployment plan."));
            }
        }

        var requiredByDeployableArtifact = deployableSelectedArtifacts
            .SelectMany(item => item.Dependencies)
            .ToHashSet();
        foreach (var cycle in FindDependencyCycles(deployableSelectedArtifacts))
        {
            findings.Add(new DeploymentFinding(
                "ORDER.CYCLE",
                DeploymentFindingSeverity.Critical,
                $"Deployment dependency cycle contains {cycle.Count:N0} executable artifacts: " +
                string.Join(", ", cycle.Take(8)) +
                (cycle.Count > 8 ? ", ..." : string.Empty)));
        }
        foreach (var artifact in manifest.Artifacts)
        {
            if (artifact.RequiresManualReview && IsSelected(artifact, options))
            {
                var isRequiredDependency = requiredByDeployableArtifact.Contains(artifact.SourceObjectId);
                var acceptedNonFatalDependency = isRequiredDependency &&
                    IsAcceptedManualReviewDependency(
                        artifact.SourceObjectId,
                        deployableSelectedArtifacts,
                        reconciledDecisions);

                findings.Add(new DeploymentFinding(
                    isRequiredDependency
                        ? acceptedNonFatalDependency
                            ? "MANUAL.RECONCILED_DEPENDENCY"
                            : "MANUAL.REQUIRED_DEPENDENCY"
                        : "MANUAL.REVIEW",
                    isRequiredDependency && !acceptedNonFatalDependency
                        ? DeploymentFindingSeverity.Critical
                        : DeploymentFindingSeverity.Warning,
                    isRequiredDependency
                        ? acceptedNonFatalDependency
                            ? $"{artifact.TargetSchema}.{artifact.TargetName} requires manual review and is " +
                              "referenced by executable artifacts, but package reconciliation classified " +
                              "the dependency as nonfatal."
                            : $"{artifact.TargetSchema}.{artifact.TargetName} requires manual review and is a " +
                              "dependency of an executable artifact."
                        : $"{artifact.TargetSchema}.{artifact.TargetName} requires manual review and will be " +
                          "reported but not executed.",
                    artifact.Phase,
                    artifact.SourceObjectId,
                    !isRequiredDependency || acceptedNonFatalDependency));
            }

            if (artifact.Classification == Domain.Inventory.ConversionClassification.Unsupported &&
                IsSelected(artifact, options))
            {
                var isRequiredDependency = requiredByDeployableArtifact.Contains(artifact.SourceObjectId);
                findings.Add(new DeploymentFinding(
                    isRequiredDependency
                        ? "OBJECT.UNSUPPORTED_REQUIRED_DEPENDENCY"
                        : "OBJECT.UNSUPPORTED_REPORT_ONLY",
                    isRequiredDependency
                        ? DeploymentFindingSeverity.Critical
                        : DeploymentFindingSeverity.Warning,
                    isRequiredDependency
                        ? $"{artifact.TargetSchema}.{artifact.TargetName} is unsupported and required by an executable artifact."
                        : $"{artifact.TargetSchema}.{artifact.TargetName} is unsupported and will be reported but not executed.",
                    artifact.Phase,
                    artifact.SourceObjectId));
            }

            if (IsSelected(artifact, options) &&
                artifact.IsExecutable &&
                !artifact.RequiresManualReview &&
                artifact.Classification != Domain.Inventory.ConversionClassification.Unsupported &&
                !HasExecutableSql(artifact.Sql))
            {
                findings.Add(new DeploymentFinding(
                    "OBJECT.SQL_MISSING",
                    DeploymentFindingSeverity.Critical,
                    $"{artifact.TargetSchema}.{artifact.TargetName} has no executable SQL.",
                    artifact.Phase,
                    artifact.SourceObjectId));
            }
        }
    }

    private static Dictionary<InventoryObjectId, BlockedDependencyArtifactDecision>
       GetReconciledDecisions(MigrationPackageManifest manifest)
    {
        return manifest.BlockedDependencyReconciliation?
            .ArtifactDecisions
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.Last())
            ?? new Dictionary<InventoryObjectId, BlockedDependencyArtifactDecision>();
    }
    private static bool HasSuccessfulCurrentLiveValidation(
        PackageArtifactManifest artifact)
    {
        return artifact.LiveValidation.Outcome == LiveSqlValidationOutcome.Passed &&
               artifact.LiveValidation.WasLiveValidated &&
               artifact.LiveValidation.IsStructurallyValid &&
               string.Equals(
                   artifact.LiveValidation.ValidatedSqlHash,
                   artifact.SqlSha256,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAcceptedNonFatalValidationBlock(
        PackageArtifactManifest artifact,
        Dictionary<InventoryObjectId, BlockedDependencyArtifactDecision> decisions)
    {
        return artifact.LiveValidation.Outcome == LiveSqlValidationOutcome.BlockedByDependency &&
               artifact.LiveValidation.IsStructurallyValid &&
               string.Equals(
                   artifact.LiveValidation.ValidatedSqlHash,
                   artifact.SqlSha256,
                   StringComparison.OrdinalIgnoreCase) &&
               decisions.TryGetValue(artifact.SourceObjectId, out var decision) &&
               !decision.IsFatal &&
               decision.ReconciledClassification is
                   ReconciledBlockedClassification.RuntimeOnly or
                   ReconciledBlockedClassification.Optional or
                   ReconciledBlockedClassification.ManualReviewDependency or
                   ReconciledBlockedClassification.ExternalDependency or
                   ReconciledBlockedClassification.FalseOrCascadingBlock or
                   ReconciledBlockedClassification.DeferredByDeploymentPlan;
    }

    private static bool IsAcceptedManualReviewDependency(
        InventoryObjectId manualObjectId,
        IReadOnlyList<PackageArtifactManifest> deployableArtifacts,
        Dictionary<InventoryObjectId, BlockedDependencyArtifactDecision> decisions)
    {
        var dependents = deployableArtifacts
            .Where(item => item.Dependencies.Contains(manualObjectId))
            .ToArray();

        return dependents.Length > 0 &&
               dependents.All(item =>
                   decisions.TryGetValue(item.SourceObjectId, out var decision) &&
                   !decision.IsFatal &&
                   decision.ReconciledClassification ==
                       ReconciledBlockedClassification.ManualReviewDependency);
    }

    private static bool IsArtifactLevelMapping(IdentifierMappingEntry mapping) =>
        mapping.ObjectType is not "column" and
            not "field" and
            not "parameter" and
            not "trigger_function" and
            not "sequence";

    private static async Task<IReadOnlyList<ObjectConflict>> FindConflictsAsync(
        DeploymentRequest request,
        MigrationPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var builder = PostgreSqlDeploymentConnectionService.CreateBuilder(request.Connection, false);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var targetObjects = await LoadTargetObjectKeysAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var result = new List<ObjectConflict>();
        foreach (var artifact in manifest.Artifacts.Where(item =>
                     IsSelected(item, request.Options) && item.IsExecutable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = CreateTargetObjectKey(artifact);
            if (key is null || PostgreSqlSystemSchemaPolicy.IsSystemSchema(key.Schema) ||
                !TargetContains(targetObjects, key))
            {
                continue;
            }

            var containsData = artifact.Phase == DeploymentPhase.Tables &&
                await TableContainsDataAsync(connection, artifact, cancellationToken).ConfigureAwait(false);
            var equivalent = artifact.Phase == DeploymentPhase.Schemas;
            result.Add(new ObjectConflict(
                artifact.SourceObjectId,
                DisplayTarget(artifact),
                artifact.TargetObjectType,
                true,
                equivalent,
                containsData,
                request.Options.ConflictPolicy,
                "An object with the same PostgreSQL catalog identity already exists."));
        }

        return result;
    }

    internal static string DisplayTarget(PackageArtifactManifest artifact) =>
        artifact.Phase == DeploymentPhase.Schemas
            ? Unquote(artifact.TargetName)
            : $"{artifact.TargetSchema}.{artifact.TargetName}";

    internal static IReadOnlyList<DeploymentFinding> CreateConflictFindings(
        IReadOnlyList<ObjectConflict> conflicts,
        ExistingObjectConflictPolicy conflictPolicy) =>
        conflicts
            .Where(item => item.Exists && !item.IsEquivalent &&
                           conflictPolicy == ExistingObjectConflictPolicy.Fail)
            .Select(conflict => new DeploymentFinding(
                "TARGET.CONFLICT",
                DeploymentFindingSeverity.Error,
                $"Target object already exists: {conflict.TargetObject}",
                null,
                conflict.SourceObjectId,
                true))
            .ToArray();

    private static async Task<HashSet<PostgreSqlTargetObjectKey>> LoadTargetObjectKeysAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var schemaPredicate = PostgreSqlSystemSchemaPolicy.CatalogPredicate("n.nspname");
        var sql = $"""
            SELECT 'Schema', n.nspname, n.nspname, '', ''
            FROM pg_catalog.pg_namespace n
            WHERE {schemaPredicate}
            UNION ALL
            SELECT CASE c.relkind
                       WHEN 'r' THEN 'Table'
                       WHEN 'p' THEN 'Table'
                       WHEN 'v' THEN 'View'
                       WHEN 'm' THEN 'View'
                       WHEN 'S' THEN 'Sequence'
                       WHEN 'i' THEN 'Index'
                       WHEN 'I' THEN 'Index'
                       ELSE 'Other'
                   END,
                   n.nspname, c.relname, '', ''
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r','p','v','m','S','i','I')
              AND {schemaPredicate}
            UNION ALL
            SELECT CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END,
                   n.nspname, p.proname, '',
                   pg_catalog.pg_get_function_identity_arguments(p.oid)
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE {schemaPredicate}
            UNION ALL
            SELECT 'Constraint', n.nspname, con.conname, c.relname, ''
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE {schemaPredicate}
            UNION ALL
            SELECT 'Trigger', n.nspname, t.tgname, c.relname, ''
            FROM pg_catalog.pg_trigger t
            JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal
              AND {schemaPredicate}
            UNION ALL
            SELECT 'Type', n.nspname, t.typname, '', ''
            FROM pg_catalog.pg_type t
            JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typrelid = 0
              AND t.typtype IN ('d','e','r')
              AND {schemaPredicate}
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new HashSet<PostgreSqlTargetObjectKey>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PostgreSqlTargetObjectKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return result;
    }

    private static async Task<bool> TableContainsDataAsync(
        NpgsqlConnection connection,
        PackageArtifactManifest artifact,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT EXISTS (SELECT 1 FROM {Quote(Unquote(artifact.TargetSchema))}." +
            $"{Quote(Unquote(artifact.TargetName))} LIMIT 1)";
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyList<PackageObjectDuplicate> FindPackageDuplicates(
        MigrationPackageManifest manifest,
        DeploymentOptions options) =>
        manifest.Artifacts
            .Where(item => IsSelected(item, options) && item.IsExecutable)
            .Select(item => (Artifact: item, Key: CreateTargetObjectKey(item)))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!)
            .Where(group => group.Select(item => item.Artifact.SourceObjectId).Distinct().Count() > 1)
            .Select(group => new PackageObjectDuplicate(
                group.Key.Kind,
                group.Key.Schema,
                group.Key.Name,
                group.Key.ParentName,
                group.Key.RoutineIdentityArguments,
                group.Select(item => item.Artifact.SourceObjectId).Distinct().ToArray()))
            .OrderBy(item => item.ObjectKind, StringComparer.Ordinal)
            .ThenBy(item => item.TargetSchema, StringComparer.Ordinal)
            .ThenBy(item => item.TargetName, StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<IReadOnlyList<InventoryObjectId>>
        FindDependencyCycles(IReadOnlyList<PackageArtifactManifest> artifacts)
    {
        var byId = artifacts
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.First());
        var graph = byId.ToDictionary(
            item => item.Key,
            item => item.Value.Dependencies
                .Where(dependency => dependency != item.Key && byId.ContainsKey(dependency))
                .Distinct()
                .ToArray());
        var reverseGraph = byId.Keys.ToDictionary(
            item => item,
            _ => new List<InventoryObjectId>());
        foreach (var (source, dependencies) in graph)
        {
            foreach (var dependency in dependencies)
            {
                reverseGraph[dependency].Add(source);
            }
        }

        var visited = new HashSet<InventoryObjectId>();
        var finishOrder = new List<InventoryObjectId>(byId.Count);
        foreach (var start in byId.Keys)
        {
            if (visited.Contains(start))
            {
                continue;
            }
            var stack = new Stack<(InventoryObjectId ObjectId, bool Finished)>();
            stack.Push((start, false));
            while (stack.TryPop(out var frame))
            {
                if (frame.Finished)
                {
                    finishOrder.Add(frame.ObjectId);
                    continue;
                }
                if (!visited.Add(frame.ObjectId))
                {
                    continue;
                }
                stack.Push((frame.ObjectId, true));
                foreach (var dependency in graph[frame.ObjectId])
                {
                    if (!visited.Contains(dependency))
                    {
                        stack.Push((dependency, false));
                    }
                }
            }
        }

        var assigned = new HashSet<InventoryObjectId>();
        var cycles = new List<IReadOnlyList<InventoryObjectId>>();
        for (var index = finishOrder.Count - 1; index >= 0; index--)
        {
            var start = finishOrder[index];
            if (!assigned.Add(start))
            {
                continue;
            }
            var component = new List<InventoryObjectId>();
            var stack = new Stack<InventoryObjectId>();
            stack.Push(start);
            while (stack.TryPop(out var current))
            {
                component.Add(current);
                foreach (var dependent in reverseGraph[current])
                {
                    if (assigned.Add(dependent))
                    {
                        stack.Push(dependent);
                    }
                }
            }
            if (component.Count > 1 || graph[component[0]].Contains(component[0]))
            {
                cycles.Add(component);
            }
        }
        return cycles;
    }

    internal static bool TargetContains(
        HashSet<PostgreSqlTargetObjectKey> targetObjects,
        PostgreSqlTargetObjectKey packageKey)
    {
        if (targetObjects.Contains(packageKey))
        {
            return true;
        }

        // Format-version 2 packages did not persist PostgreSQL identity
        // arguments. In that case matching any overload is conservative and
        // prevents an unsafe CREATE against an existing routine namespace.
        return packageKey.Kind is "Function" or "Procedure" &&
            targetObjects.Any(target =>
                target.Kind == packageKey.Kind &&
                target.Schema == packageKey.Schema &&
                target.Name == packageKey.Name &&
                (string.IsNullOrWhiteSpace(packageKey.RoutineIdentityArguments) ||
                 string.IsNullOrWhiteSpace(target.RoutineIdentityArguments)));
    }

    internal static PostgreSqlTargetObjectKey? CreateTargetObjectKey(
        PackageArtifactManifest artifact)
    {
        var kind = artifact.Phase switch
        {
            DeploymentPhase.Schemas => "Schema",
            DeploymentPhase.Types => "Type",
            DeploymentPhase.Sequences => "Sequence",
            DeploymentPhase.Tables => "Table",
            DeploymentPhase.PrimaryKeys or DeploymentPhase.UniqueConstraints or
                DeploymentPhase.CheckConstraints or DeploymentPhase.ForeignKeys => "Constraint",
            DeploymentPhase.Indexes => "Index",
            DeploymentPhase.Views => "View",
            DeploymentPhase.Functions or DeploymentPhase.PreDataFunctions => "Function",
            DeploymentPhase.Procedures => "Procedure",
            DeploymentPhase.Triggers => "Trigger",
            _ => null
        };
        if (kind is null)
        {
            return null;
        }

        var schema = kind == "Schema"
            ? Unquote(artifact.TargetName)
            : Unquote(artifact.TargetSchema);
        var name = Unquote(artifact.TargetName);
        return new PostgreSqlTargetObjectKey(
            kind,
            schema,
            name,
            ExtractLastQualifiedIdentifier(artifact.TargetParentObject),
            NormalizeSignature(artifact.RoutineIdentityArguments));
    }

    private static string ExtractLastQualifiedIdentifier(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return string.Empty;
        }

        var inQuotes = false;
        var lastSeparator = -1;
        for (var index = 0; index < qualifiedName.Length; index++)
        {
            if (qualifiedName[index] == '"')
            {
                if (inQuotes && index + 1 < qualifiedName.Length &&
                    qualifiedName[index + 1] == '"')
                {
                    index++;
                    continue;
                }
                inQuotes = !inQuotes;
            }
            else if (qualifiedName[index] == '.' && !inQuotes)
            {
                lastSeparator = index;
            }
        }
        return Unquote(qualifiedName[(lastSeparator + 1)..].Trim());
    }

    private static string NormalizeSignature(string signature) =>
        string.Join(' ', signature.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool HasExecutableSql(string sql) =>
        sql.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line => line.Length > 0 &&
                !line.StartsWith("--", StringComparison.Ordinal) &&
                !line.StartsWith("/*", StringComparison.Ordinal) &&
                !line.StartsWith("*/", StringComparison.Ordinal));

    internal sealed record PostgreSqlTargetObjectKey(
        string Kind,
        string Schema,
        string Name,
        string ParentName,
        string RoutineIdentityArguments);

    private static async Task<long> CountActiveTargetConnectionsAsync(
        PostgreSqlConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var builder = PostgreSqlDeploymentConnectionService.CreateBuilder(options, true);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid()",
            connection);
        command.Parameters.AddWithValue("database", options.TargetDatabase);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /*    internal static bool IsSelected(PackageArtifactManifest artifact, DeploymentOptions options) =>
            options.Scope switch
            {
                DeploymentScope.CompletePackage => true,
                DeploymentScope.SchemaOnly => artifact.Phase is >= DeploymentPhase.Extensions
                        and <= DeploymentPhase.CheckConstraints
                    or DeploymentPhase.PreDataFunctions,
                DeploymentScope.DataOnly => artifact.Phase is DeploymentPhase.Data
                    or DeploymentPhase.SequenceReset,
                DeploymentScope.ProgrammableObjectsOnly => artifact.Phase is >= DeploymentPhase.Views
                        and <= DeploymentPhase.Triggers
                    or DeploymentPhase.PreDataFunctions,
                DeploymentScope.SecurityOnly => artifact.Phase == DeploymentPhase.Security,
                DeploymentScope.SelectedFailedObjects => options.SelectedObjects.Contains(artifact.SourceObjectId),
                DeploymentScope.SelectedPhases => options.SelectedPhases.Contains(artifact.Phase),
                _ => false
            };*/


    internal static bool IsSelected(
    PackageArtifactManifest artifact,
    DeploymentOptions options) =>
    options.Scope switch
    {
        DeploymentScope.CompletePackage =>
            true,

        /*
         * Schema deployment contains all objects that should exist
         * before the separate data migration operation begins.
         *
         * Foreign keys are deliberately excluded because they are
         * deployed and validated later by the Validate operation.
         */
        DeploymentScope.SchemaOnly =>
            artifact.Phase is
                DeploymentPhase.PreDeployment or
                DeploymentPhase.Extensions or
                DeploymentPhase.Schemas or
                DeploymentPhase.Types or
                DeploymentPhase.Sequences or
                DeploymentPhase.Tables or
                DeploymentPhase.PreDataFunctions or
                DeploymentPhase.DefaultsAndGeneratedColumns or
                DeploymentPhase.PrimaryKeys or
                DeploymentPhase.UniqueConstraints or
                DeploymentPhase.CheckConstraints or
                DeploymentPhase.Indexes or
                DeploymentPhase.Functions or
                DeploymentPhase.Procedures or
                DeploymentPhase.Views or
                DeploymentPhase.Triggers or
                DeploymentPhase.Security or
                DeploymentPhase.Comments,

        /*
         * DataOnly is retained for cases where the deployment engine
         * is explicitly asked to process package data artifacts.
         *
         * Migration Studio's normal UI workflow still invokes the
         * independent Data Migration Engine directly.
         */
        DeploymentScope.DataOnly =>
            artifact.Phase is
                DeploymentPhase.Data or
                DeploymentPhase.SequenceReset,

        DeploymentScope.ProgrammableObjectsOnly =>
            artifact.Phase is
                DeploymentPhase.PreDataFunctions or
                DeploymentPhase.Functions or
                DeploymentPhase.Procedures or
                DeploymentPhase.Views or
                DeploymentPhase.Triggers,

        DeploymentScope.SecurityOnly =>
            artifact.Phase == DeploymentPhase.Security,

        DeploymentScope.SelectedFailedObjects =>
            options.SelectedObjects.Contains(
                artifact.SourceObjectId),

        DeploymentScope.SelectedPhases =>
            options.SelectedPhases.Contains(
                artifact.Phase),

        _ => false
    };

    private static string Quote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);

    private static string Unquote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Unquote(identifier);

    private static string MappingUniquenessKey(IdentifierMappingEntry mapping)
    {
        var objectType = mapping.ObjectType.ToLowerInvariant();
        var scope = objectType switch
        {
            "column" or "constraint" or "parameter" or "field" or "trigger" =>
                mapping.ParentObject,
            "table" or "view" or "sequence" or "index" or "userdefinedtype" or
                "trigger_function" or "helper" or "temporary" => mapping.TargetSchema,
            "function" or "storedprocedure" or "procedure" => $"{mapping.TargetSchema}|routine",
            _ => $"{mapping.TargetSchema}|{objectType}"
        };
        return $"{scope}|{objectType}|{mapping.TargetName}";
    }
}
