using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Deployment;

public sealed class MigrationPackageWriter(IConversionReportWriter reportWriter) : IDeploymentPackageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<string> WriteAsync(
        ConversionRun run,
        string parentDirectory,
        CancellationToken cancellationToken) =>
        await WriteAsync(run, parentDirectory, null, cancellationToken).ConfigureAwait(false);

    public async Task<string> WriteAsync(
        ConversionRun run,
        string parentDirectory,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ConversionArtifactReconciler.EnsureSameSourceObjects(
            run.Artifacts,
            run.Artifacts,
            "package input validation");
        var publicationPlanning = run.PublicationReconciliation is null
            ? null
            : DeploymentPublicationReconciler.Reconcile(run);
        if (publicationPlanning is not null && !publicationPlanning.Reconciliation.CanPublish)
        {
            throw new InvalidDataException(
                "Package publication was refused by dependency reconciliation: " +
                $"failed={publicationPlanning.Reconciliation.DirectValidationFailureCount:N0}, " +
                $"hard-blocked={publicationPlanning.Reconciliation.HardBlockedCount:N0}, " +
                $"not-run={publicationPlanning.Reconciliation.NotRunExecutableCount:N0}, " +
                $"hard-cycles={publicationPlanning.Reconciliation.HardCycleCount:N0}, " +
                $"unresolved={publicationPlanning.Reconciliation.UnresolvedInternalDependencyCount:N0}.");
        }
        if (publicationPlanning is not null &&
            run.PublicationReconciliation!.DeploymentPlanId != publicationPlanning.Plan.PlanId)
        {
            throw new InvalidDataException(
                "Package publication was refused because the reconciled deployment plan changed.");
        }
        var structurallyInvalid = run.Artifacts.FirstOrDefault(item =>
            !item.RequiresManualReview &&
            item.Classification is ConversionClassification.Automatic or
                ConversionClassification.AutomaticWithWarning &&
            ContainsExecutableSql(item.PostgreSqlDefinition) &&
            !item.Validation.IsStructurallyValid);
        if (structurallyInvalid is not null)
        {
            throw new InvalidDataException(
                "Package publication refused because executable generated SQL failed offline validation: " +
                $"{structurallyInvalid.TargetObjectId.QualifiedName}. " +
                $"{structurallyInvalid.Validation.Message ?? "No validation detail was supplied."}");
        }
        var orderedArtifacts = publicationPlanning is null
            ? ArtifactDependencyPlanner.Order(
                run.Artifacts,
                item => item.SourceObjectId,
                item => item.Dependencies,
                item => DeploymentPhaseOrdering.GetRank(
                    item.DeploymentPhase,
                    item.TargetObjectId.ObjectType),
                item => $"{item.TargetObjectId.QualifiedName}|{item.ContentHash}",
                failOnCycle: true)
            : DeploymentPublicationReconciler.OrderForPackage(run, publicationPlanning);
        var deferredIdentities = publicationPlanning?.Reconciliation.ArtifactDecisions
            .Where(item => item.ReconciledClassification ==
                           ReconciledBlockedClassification.DeferredByDeploymentPlan)
            .Select(item => $"{item.SourceObjectId}|{item.TargetQualifiedName}")
            .ToHashSet(StringComparer.Ordinal) ?? [];
        bool IsDeferredArtifact(ConversionArtifact item) => deferredIdentities.Contains(
            $"{item.SourceObjectId}|{item.TargetObjectId.QualifiedName}");

        var packageName = $"Migration_{run.GeneratedAt:yyyyMMdd_HHmmss}_{run.RunId:N}";
        var finalPackageDirectory = Path.Combine(Path.GetFullPath(parentDirectory), packageName);
        if (Directory.Exists(finalPackageDirectory))
        {
            throw new IOException($"Migration package already exists: {finalPackageDirectory}");
        }
        var packageDirectory = Path.Combine(
            Path.GetFullPath(parentDirectory),
            $".{packageName}.partial-{Guid.NewGuid():N}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageWorkTotal = Math.Max(
                1,
                ScriptNames().Count + run.Artifacts.Count(item => item.RequiresManualReview) + 6);
            var packageWorkCompleted = 0;
            void Report(string message, string currentObject = "")
            {
                progress?.Report(new ConversionProgress(
                    ConversionStage.BuildingDeploymentPackage,
                    packageWorkCompleted,
                    packageWorkTotal,
                    message)
                {
                    CurrentObjectType = "Package",
                    CurrentObject = currentObject,
                    MappingSetId = run.MappingSet.MappingSetId,
                    LastProgressAt = DateTimeOffset.UtcNow
                });
            }
            Report("Creating atomic package workspace.");
            Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(Path.Combine(packageDirectory, "10_Data"));
        Directory.CreateDirectory(Path.Combine(packageDirectory, "ManualReview"));
        Directory.CreateDirectory(Path.Combine(packageDirectory, "Reports"));
        Directory.CreateDirectory(Path.Combine(packageDirectory, "Logs"));

        foreach (var script in ScriptNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifacts = orderedArtifacts.Where(item =>
                    string.Equals(item.ScriptFileName, script, StringComparison.Ordinal))
                .ToArray();
            var path = Path.Combine(packageDirectory, script.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = BuildScript(run, script, artifacts);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            packageWorkCompleted++;
            Report($"Generated {script}.", script);
        }

        const string executionPlanName = "00_ExecutionPlan.sql";
        var executionPlanArtifacts = orderedArtifacts
            .Where(item => !item.RequiresManualReview && ContainsExecutableSql(item.PostgreSqlDefinition))
            .Where(item => !IsDeferredArtifact(item))
            .ToArray();
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory, executionPlanName),
            BuildScript(run, executionPlanName, executionPlanArtifacts),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        packageWorkCompleted++;
        Report("Generated canonical dependency-aware execution plan.", executionPlanName);

        var extensionSql = BuildExtensions(run);
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory, "01_Extensions.sql"),
            Header(run, "01_Extensions.sql") + extensionSql,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        packageWorkCompleted++;
        Report("Generated extension script.", "01_Extensions.sql");

        foreach (var artifact in run.Artifacts.Where(item => item.RequiresManualReview)
                     .OrderBy(item => item.TargetObjectId.QualifiedName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeName = string.Concat(artifact.TargetObjectId.Name
                .Where(character => char.IsLetterOrDigit(character) || character is '_' or '-'))
                .Trim('_');
            if (safeName.Length == 0)
            {
                safeName = artifact.SourceObjectId.ToString();
            }
            var path = Path.Combine(
                packageDirectory,
                "ManualReview",
                $"{safeName}_{artifact.SourceObjectId}.sql");
            await File.WriteAllTextAsync(
                path,
                BuildManualReviewFile(run, artifact),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            packageWorkCompleted++;
            if ((packageWorkCompleted & 127) == 0)
            {
                Report(
                    $"Generated {packageWorkCompleted:N0}/{packageWorkTotal:N0} package items.",
                    artifact.TargetObjectId.QualifiedName);
            }
        }

        Report("Generating conversion reports.");
        var reportTask = reportWriter.WriteAsync(
            run,
            Path.Combine(packageDirectory, "Reports"),
            cancellationToken);
        while (!reportTask.IsCompleted)
        {
            var completed = await Task.WhenAny(
                reportTask,
                Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false);
            if (completed == reportTask)
            {
                break;
            }
            Report("Generating conversion reports; report writer is responsive.", "Reports");
        }
        await reportTask.ConfigureAwait(false);
        if (publicationPlanning is not null)
        {
            var completedWithWarnings = run.RequiresManualReview ||
                                        publicationPlanning.Reconciliation.HasWarnings;
            await PackagePublicationReconciliationDiagnosticsWriter.WriteAsync(
                    publicationPlanning.Reconciliation,
                    Path.Combine(packageDirectory, "Reports"),
                    orderedArtifacts.Count,
                    executionPlanArtifacts.Length,
                    completedWithWarnings ? "CompletedWithWarnings" : "Completed",
                    nextDeployEnabled: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        packageWorkCompleted++;
        Report("Conversion reports generated.");

        var filePaths = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetRelativePath(packageDirectory, path)
                .StartsWith($"Logs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var files = new PackageFileManifest[filePaths.Length];
        for (var index = 0; index < filePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = filePaths[index];
            files[index] = new PackageFileManifest(
                Path.GetRelativePath(packageDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
                HashFile(path),
                new FileInfo(path).Length,
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}ManualReview{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase));
            if ((index & 255) == 0)
            {
                Report($"Hashed {index + 1:N0}/{filePaths.Length:N0} package files.", path);
            }
        }
        packageWorkCompleted++;
        Report("Package file manifest generated.");
        var sourceMetadataHash = HashText(string.Join(
            "\n",
            run.Artifacts.OrderBy(item => item.SourceObjectId.Value)
                .Select(item => $"{item.SourceObjectId}:{item.ContentHash}")));
        var configurationHash = HashText(JsonSerializer.Serialize(run.Options));
        var componentByObject = run.Artifacts
            .SelectMany(item => item.Dependencies.Select(dependency => (item.SourceObjectId, dependency)))
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, _ => -1);
        var mappingsBySourceObject = run.IdentifierMappings
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var manifest = new MigrationPackageManifest
        {
            PackageId = Guid.NewGuid(),
            MigrationRunId = run.RunId,
            GeneratedAt = run.GeneratedAt,
            SourceDatabase = run.SourceDatabase,
            TargetPostgreSqlVersion = run.TargetVersion.Major,
            ApplicationVersion = run.EngineVersion,
            SourceMetadataHash = sourceMetadataHash,
            ConversionConfigurationHash = configurationHash,
            Files = files,
            RequiredExtensions = run.RequiredExtensions,
            ObjectMappings = run.IdentifierMappings,
            DataReferences = Directory.Exists(Path.Combine(packageDirectory, "10_Data"))
                ? ["10_Data/"]
                : [],
            ManualReviewItems = run.Artifacts.Where(item => item.RequiresManualReview)
                .Select(item => item.TargetObjectId.QualifiedName)
                .ToArray(),
            UnsupportedFeatures = run.Artifacts.SelectMany(item => item.UnsupportedConstructs)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            DeploymentPlanId = publicationPlanning?.Plan.PlanId,
            BlockedDependencyReconciliation = publicationPlanning?.Reconciliation,
            Artifacts = orderedArtifacts.Select(item => new PackageArtifactManifest(
                item.SourceObjectId,
                item.TargetObjectId.ObjectType,
                item.TargetObjectId.Schema,
                item.TargetObjectId.Name,
                item.DeploymentPhase,
                item.ScriptFileName,
                item.PostgreSqlDefinition,
                item.ContentHash,
                item.Classification,
                item.Dependencies,
                item.RequiredExtensions,
                item.RequiresManualReview,
                item.UnsupportedConstructs,
                componentByObject.GetValueOrDefault(item.SourceObjectId, -1))
                {
                    TargetParentObject = ResolveTargetParent(
                        item.SourceObjectId,
                        item.TargetObjectId.ObjectType,
                        mappingsBySourceObject),
                    RoutineIdentityArguments = ExtractRoutineIdentityArguments(
                        item.TargetObjectId.ObjectType,
                        item.PostgreSqlDefinition),
                    IsExecutable = ConversionArtifactReconciler.IsDeployableExecutable(item) &&
                                   !IsDeferredArtifact(item),
                    LiveValidation = item.Validation
                })
                .ToArray()
        };
        EnsureManifestReconciles(run, manifest);
        if (publicationPlanning is null)
        {
            ArtifactDependencyPlanner.EnsureDependenciesPrecedeDependents(
                manifest.Artifacts,
                item => item.SourceObjectId,
                item => item.Dependencies);
        }
        await using (var manifestStream = new FileStream(
                         Path.Combine(packageDirectory, "manifest.json"),
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         65536,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var serializationTask = JsonSerializer.SerializeAsync(
                manifestStream,
                manifest,
                JsonOptions,
                cancellationToken);
            while (!serializationTask.IsCompleted)
            {
                var completed = await Task.WhenAny(
                    serializationTask,
                    Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false);
                if (completed == serializationTask)
                {
                    break;
                }
                Report("Serializing deployment manifest; writer is responsive.", "manifest.json");
            }
            await serializationTask.ConfigureAwait(false);
        }
        packageWorkCompleted = packageWorkTotal;
        Report("Deployment package generated and ready for atomic publication.");

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(packageDirectory, finalPackageDirectory);
            return finalPackageDirectory;
        }
        catch
        {
            if (Directory.Exists(packageDirectory))
            {
                try
                {
                    Directory.Delete(packageDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // Preserve the original failure; the partial folder remains clearly
                    // marked and is never treated as a published migration package.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve the original failure and never publish the partial folder.
                }
            }
            throw;
        }
    }

    internal static void EnsureManifestReconciles(
        ConversionRun run,
        MigrationPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(manifest);

        var runIds = run.Artifacts.Select(item => item.SourceObjectId).ToArray();
        var manifestIds = manifest.Artifacts.Select(item => item.SourceObjectId).ToArray();
        var runCounts = runIds
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        var manifestCounts = manifestIds
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        var missing = runCounts.Sum(item =>
            Math.Max(0, item.Value - manifestCounts.GetValueOrDefault(item.Key)));
        var unexpected = manifestCounts.Sum(item =>
            Math.Max(0, item.Value - runCounts.GetValueOrDefault(item.Key)));
        if (run.Artifacts.Count != manifest.Artifacts.Count ||
            missing > 0 ||
            unexpected > 0)
        {
            throw new InvalidDataException(
                "Package artifact reconciliation failed: " +
                $"conversion={run.Artifacts.Count:N0}, manifest={manifest.Artifacts.Count:N0}, " +
                $"missing={missing:N0}, unexpected={unexpected:N0}.");
        }

        // Every run artifact has a manifest entry. Non-executable entries are
        // retained explicitly as traceability-only artifacts rather than
        // disappearing from the package.
    }

    private static string BuildScript(
        ConversionRun run,
        string script,
        IReadOnlyList<ConversionArtifact> artifacts)
    {
        var builder = new StringBuilder(Header(run, script));
        foreach (var artifact in artifacts)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"-- Source object: {artifact.SourceObjectId}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"-- Target object: {artifact.TargetObjectId.QualifiedName}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"-- Classification: {artifact.Classification}; Rule: {artifact.RuleId}; Hash: {artifact.ContentHash}");
            builder.AppendLine(artifact.PostgreSqlDefinition.TrimEnd());
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string BuildExtensions(ConversionRun run)
    {
        if (run.RequiredExtensions.Count == 0)
        {
            return "-- No PostgreSQL extensions are required." + Environment.NewLine;
        }
        return string.Join(
                   Environment.NewLine,
                   run.RequiredExtensions.Order(StringComparer.Ordinal)
                       .Select(extension => $"CREATE EXTENSION IF NOT EXISTS \"{extension.Replace("\"", "\"\"", StringComparison.Ordinal)}\";")) +
               Environment.NewLine;
    }

    private static string BuildManualReviewFile(ConversionRun run, ConversionArtifact artifact)
    {
        var builder = new StringBuilder(Header(run, "ManualReview/" + artifact.TargetObjectId.Name + ".sql"))
            .AppendLine(CultureInfo.InvariantCulture, $"-- Source object: {artifact.SourceObjectId}")
            .AppendLine(CultureInfo.InvariantCulture, $"-- Target object: {artifact.TargetObjectId.QualifiedName}")
            .AppendLine(CultureInfo.InvariantCulture, $"-- Classification: {artifact.Classification}")
            .AppendLine(CultureInfo.InvariantCulture, $"-- Unsupported constructs: {string.Join(", ", artifact.UnsupportedConstructs)}");
        foreach (var finding in artifact.Findings)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"-- [{finding.Severity}] {finding.Code}: {finding.Message}");
        }
        builder.AppendLine()
            .AppendLine(artifact.PostgreSqlDefinition.TrimEnd())
            .AppendLine()
            .AppendLine("/* Preserved source definition:")
            .AppendLine(artifact.SourceDefinition.Replace("*/", "* /", StringComparison.Ordinal))
            .AppendLine("*/");
        return builder.ToString();
    }

    private static string Header(ConversionRun run, string script) =>
        $"-- SQL Server to PostgreSQL Migration Studio{Environment.NewLine}" +
        $"-- Script: {script}{Environment.NewLine}" +
        $"-- Generated: {run.GeneratedAt:O}{Environment.NewLine}" +
        $"-- Source database: {run.SourceDatabase}{Environment.NewLine}" +
        $"-- Target PostgreSQL: {run.TargetVersion.Major}{Environment.NewLine}" +
        $"-- Application/engine version: {run.EngineVersion}{Environment.NewLine}" +
        $"-- Run: {run.RunId:N}{Environment.NewLine}{Environment.NewLine}";

    private static string ResolveTargetParent(
        InventoryObjectId sourceObjectId,
        string targetObjectType,
        Dictionary<InventoryObjectId, IdentifierMappingEntry[]> mappingsBySourceObject)
    {
        if (!mappingsBySourceObject.TryGetValue(sourceObjectId, out var mappings))
        {
            return string.Empty;
        }

        return mappings.FirstOrDefault(mapping =>
                   string.Equals(
                       mapping.ObjectType,
                       targetObjectType,
                       StringComparison.OrdinalIgnoreCase))
                   ?.TargetParentObject
               ?? mappings.FirstOrDefault(mapping =>
                   !string.IsNullOrWhiteSpace(mapping.TargetParentObject))
                   ?.TargetParentObject
               ?? string.Empty;
    }

    private static string ExtractRoutineIdentityArguments(string targetObjectType, string sql)
    {
        if (!targetObjectType.Contains("function", StringComparison.OrdinalIgnoreCase) &&
            !targetObjectType.Contains("procedure", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var open = sql.IndexOf('(');
        if (open < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var index = open; index < sql.Length; index++)
        {
            var current = sql[index];
            if (current == '\'' && !inDoubleQuote &&
                (index == 0 || sql[index - 1] != '\\'))
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }
            if (current == '"' && !inSingleQuote &&
                (index + 1 >= sql.Length || sql[index + 1] != '"'))
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }
            if (inSingleQuote || inDoubleQuote)
            {
                continue;
            }
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                return string.Join(
                    ' ',
                    sql[(open + 1)..index]
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return string.Empty;
    }

    private static bool ContainsExecutableSql(string sql) =>
        sql.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line => line.Length > 0 &&
                !line.StartsWith("--", StringComparison.Ordinal) &&
                !line.StartsWith("/*", StringComparison.Ordinal) &&
                !line.StartsWith("*/", StringComparison.Ordinal));

    private static IReadOnlyList<string> ScriptNames() =>
    [
        "00_PreDeployment.sql",
        "02_Schemas.sql",
        "03_Types.sql",
        "04_IdentitySequences.sql",
        "05_Tables.sql",
        "06_PreDataFunctions.sql",
        "06_DefaultsAndGeneratedColumns.sql",
        "07_PrimaryKeys.sql",
        "08_UniqueConstraints.sql",
        "09_CheckConstraints.sql",
        "10_Sequences.sql",
        "11_SequenceReset.sql",
        "12_ForeignKeys.sql",
        "13_Indexes.sql",
        "14_Functions.sql",
        "15_Procedures.sql",
        "16_Views.sql",
        "17_Triggers.sql",
        "18_Security.sql",
        "19_Comments.sql",
        "20_PostDeployment.sql"
    ];

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
