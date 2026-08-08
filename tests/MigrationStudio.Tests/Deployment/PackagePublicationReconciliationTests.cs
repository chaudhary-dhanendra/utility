using System.IO;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Application.Conversion;

namespace MigrationStudio.Tests.Deployment;

public sealed class PackagePublicationReconciliationTests
{
    [Fact]
    public async Task ZeroFailuresAndZeroBlocksPublishCompletedPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"migrationstudio-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var table = Artifact("table", "Table", DeploymentPhase.Tables,
                "CREATE TABLE public.table(id integer);") with
            {
                Validation = Passed("hash-table")
            };
            var run = Run([table]);
            var planning = DeploymentPublicationReconciler.Reconcile(run);
            run = run with { PublicationReconciliation = planning.Reconciliation };

            var package = await new MigrationPackageWriter(new EmptyReportWriter())
                .WriteAsync(run, root, CancellationToken.None);
            var report = await File.ReadAllTextAsync(Path.Combine(
                package, "Reports", "package-publication-reconciliation.json"));

            Assert.True(planning.Reconciliation.CanPublish);
            Assert.False(planning.Reconciliation.HasWarnings);
            Assert.Contains("\"finalConvertStatus\": \"Completed\"", report);
            Assert.Contains("\"nextDeployEnabled\": true", report);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NonfatalAndDeferredBlocksPublishOneDeterministicPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"migrationstudio-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var table = Artifact("table", "Table", DeploymentPhase.Tables, "CREATE TABLE public.table(id integer);")
                with { Validation = Passed("hash-table") };
            var manualFunction = Artifact(
                "manual_function", "Function", DeploymentPhase.Functions,
                "CREATE FUNCTION public.manual_function() RETURNS boolean LANGUAGE sql AS $$ SELECT true; $$;") with
            {
                Classification = ConversionClassification.ManualConversion,
                RequiresManualReview = true,
                Validation = Manual("hash-manual_function")
            };
            var deferredCheck = Artifact(
                "deferred_check", "CheckConstraint", DeploymentPhase.CheckConstraints,
                "ALTER TABLE public.table ADD CONSTRAINT deferred_check CHECK (public.manual_function());") with
            {
                Dependencies = [manualFunction.SourceObjectId],
                Validation = Blocked("hash-deferred_check", manualFunction.SourceObjectId)
            };
            var runtimeProcedure = Artifact(
                "runtime_procedure", "Procedure", DeploymentPhase.Procedures,
                "CREATE PROCEDURE public.runtime_procedure() LANGUAGE plpgsql AS $$ BEGIN PERFORM public.manual_function(); END; $$;") with
            {
                Dependencies = [manualFunction.SourceObjectId],
                Validation = Blocked("hash-runtime_procedure", manualFunction.SourceObjectId)
            };
            var run = Run([runtimeProcedure, deferredCheck, manualFunction, table]);
            var planning = DeploymentPublicationReconciler.Reconcile(run);
            run = run with { PublicationReconciliation = planning.Reconciliation };

            var package = await new MigrationPackageWriter(new EmptyReportWriter())
                .WriteAsync(run, root, CancellationToken.None);
            var manifest = await new MigrationPackageReader()
                .ReadAndVerifyAsync(package, false, CancellationToken.None);
            var executionPlan = await File.ReadAllTextAsync(Path.Combine(package, "00_ExecutionPlan.sql"));
            var reconciliationJson = Path.Combine(
                package, "Reports", "package-publication-reconciliation.json");
            var reconciliationMarkdown = Path.Combine(
                package, "Reports", "package-publication-reconciliation.md");

            Assert.True(planning.Reconciliation.CanPublish);
            Assert.Equal(2, planning.Reconciliation.OriginalBlockedCount);
            Assert.Equal(0, planning.Reconciliation.HardBlockedCount);
            Assert.Equal(2, planning.Reconciliation.NonFatalBlockedCount);
            Assert.Equal(1, planning.Reconciliation.DeferredByPlanCount);
            Assert.Equal(planning.Plan.PlanId, manifest.DeploymentPlanId);
            Assert.NotNull(manifest.BlockedDependencyReconciliation);
            Assert.Equal(
                planning.Reconciliation.OriginalBlockedCount,
                manifest.BlockedDependencyReconciliation.OriginalBlockedCount);
            Assert.Equal(
                planning.Reconciliation.ArtifactDecisions.Select(item => item.ArtifactId),
                manifest.BlockedDependencyReconciliation.ArtifactDecisions.Select(item => item.ArtifactId));
            Assert.Equal(4, manifest.Artifacts.Count);
            Assert.Equal(2, manifest.Artifacts.Count(item => item.IsExecutable));
            Assert.False(Assert.Single(manifest.Artifacts,
                item => item.TargetName == "deferred_check").IsExecutable);
            Assert.True(Assert.Single(manifest.Artifacts,
                item => item.TargetName == "runtime_procedure").IsExecutable);
            Assert.Contains("CREATE TABLE", executionPlan, StringComparison.Ordinal);
            Assert.Contains("CREATE PROCEDURE", executionPlan, StringComparison.Ordinal);
            Assert.DoesNotContain("ADD CONSTRAINT deferred_check", executionPlan, StringComparison.Ordinal);
            Assert.True(File.Exists(reconciliationJson));
            Assert.True(File.Exists(reconciliationMarkdown));
            Assert.Contains("\"originalBlockedCount\": 2", await File.ReadAllTextAsync(reconciliationJson));
            Assert.Contains("| Executable artifacts | 2 |", await File.ReadAllTextAsync(reconciliationMarkdown));

            var secondRoot = Path.Combine(root, "second");
            var secondPackage = await new MigrationPackageWriter(new EmptyReportWriter())
                .WriteAsync(run, secondRoot, CancellationToken.None);
            var secondManifest = await new MigrationPackageReader()
                .ReadAndVerifyAsync(secondPackage, false, CancellationToken.None);
            Assert.Equal(
                manifest.Artifacts.Select(item => item.TargetName),
                secondManifest.Artifacts.Select(item => item.TargetName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectValidationFailureRefusesReconciledPackagePublication()
    {
        var artifact = Artifact("failed", "Table", DeploymentPhase.Tables,
            "CREATE TABLE public.failed(id integer);") with
        {
            Validation = Failed("hash-failed")
        };
        var run = Run([artifact]);
        var planning = DeploymentPublicationReconciler.Reconcile(run);
        run = run with { PublicationReconciliation = planning.Reconciliation };
        var root = Path.Combine(Path.GetTempPath(), $"migrationstudio-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new MigrationPackageWriter(new EmptyReportWriter())
                    .WriteAsync(run, root, CancellationToken.None));
            Assert.Contains("failed=1", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConversionArtifact Artifact(
        string name,
        string type,
        DeploymentPhase phase,
        string sql)
    {
        var id = InventoryObjectId.Create(
            "publication", InventoryObjectType.Table, "public", name, name.GetHashCode(StringComparison.Ordinal));
        return new ConversionArtifact(
            id,
            new TargetObjectIdentifier(type, "public", name),
            sql,
            sql,
            ConversionClassification.Automatic,
            "TEST.PUBLICATION",
            1m,
            [], [], [], [], false, [],
            new SqlValidationResult(true, false, null, null, null),
            phase,
            $"{(int)phase:00}.sql",
            $"hash-{name}");
    }

    private static ConversionRun Run(IReadOnlyList<ConversionArtifact> artifacts) =>
        new(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "source", new PostgreSqlVersion(17),
            new ConversionOptions(), [], [], artifacts, [], [], "test");

    private static SqlValidationResult Passed(string hash) =>
        new(true, true, null, null, null)
        {
            Outcome = LiveSqlValidationOutcome.Passed,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase,
            ValidatedSqlHash = hash,
            ValidatedAt = DateTimeOffset.UtcNow
        };

    private static SqlValidationResult Blocked(string hash, InventoryObjectId dependency) =>
        new(true, false, null, "Blocked by dependency.", null)
        {
            Outcome = LiveSqlValidationOutcome.BlockedByDependency,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase,
            BlockingDependencies = [dependency],
            ValidatedSqlHash = hash,
            ValidatedAt = DateTimeOffset.UtcNow
        };

    private static SqlValidationResult Manual(string hash) =>
        new(true, false, null, "Manual review.", null)
        {
            Outcome = LiveSqlValidationOutcome.Manual,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase,
            ValidatedSqlHash = hash,
            ValidatedAt = DateTimeOffset.UtcNow
        };

    private static SqlValidationResult Failed(string hash) =>
        new(false, true, "42601", "syntax error", 1)
        {
            Outcome = LiveSqlValidationOutcome.Failed,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase,
            ValidatedSqlHash = hash,
            ValidatedAt = DateTimeOffset.UtcNow
        };

    private sealed class EmptyReportWriter : IConversionReportWriter
    {
        public Task WriteAsync(
            ConversionRun run,
            string reportsDirectory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(reportsDirectory);
            return Task.CompletedTask;
        }
    }
}
