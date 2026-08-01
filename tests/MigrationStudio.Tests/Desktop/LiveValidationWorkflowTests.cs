using MigrationStudio.Application.Conversion;
using MigrationStudio.Desktop.ViewModels;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Desktop;

public sealed class LiveValidationWorkflowTests
{
    [Fact]
    public async Task ValidationStartsWithSixtySevenExecutableNotRunArtifacts()
    {
        var artifacts = Enumerable.Range(1, 67)
            .Select(index => Artifact(index))
            .ToArray();
        var validator = new RecordingValidator(item => Passed(item));

        var result = await LiveValidationWorkflow.ExecuteAsync(
            Run(artifacts),
            artifacts,
            validator,
            new PostgreSqlValidationOptions("Host=not-opened"),
            CancellationToken.None);

        Assert.True(validator.WasCalled);
        Assert.Equal(67, validator.ArtifactCount);
        Assert.Equal(67, result.PassedCount);
        Assert.Equal(67, result.TotalBefore);
        Assert.Equal(67, result.TotalAfter);
        Assert.Empty(
            ConversionArtifactReconciler
                .GetArtifactsWithoutCurrentSuccessfulLiveValidation(
                    result.Run.Artifacts));
    }

    [Fact]
    public void PackageExportRemainsGatedForNotRunArtifacts()
    {
        var run = Run([Artifact(1)]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkspaceViewModel.EnsureAllDeployableArtifactsValidated(run));

        Assert.Contains(
            "require successful live PostgreSQL validation",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulValidationPreservesCollectionAndEnablesPackageGate()
    {
        var artifacts = Enumerable.Range(1, 12)
            .Select(index => Artifact(index))
            .ToArray();

        var result = await LiveValidationWorkflow.ExecuteAsync(
            Run(artifacts),
            artifacts,
            new RecordingValidator(item => Passed(item)),
            new PostgreSqlValidationOptions("Host=not-opened"),
            CancellationToken.None);

        Assert.Equal(artifacts.Length, result.Run.Artifacts.Count);
        Assert.Equal(
            artifacts.Select(item => item.SourceObjectId),
            result.Run.Artifacts.Select(item => item.SourceObjectId));
        WorkspaceViewModel.EnsureAllDeployableArtifactsValidated(result.Run);
        Assert.All(
            result.Run.Artifacts,
            item =>
            {
                Assert.Equal(item.ContentHash, item.Validation.ValidatedSqlHash);
                Assert.NotNull(item.Validation.ValidatedAt);
            });
    }

    [Fact]
    public async Task CertificationSizedRunPreservesAllTwoHundredTwentyOneArtifacts()
    {
        var artifacts = Enumerable.Range(1, 221)
            .Select(index => index <= 67
                ? Artifact(index) with
                {
                    PostgreSqlDefinition = $"-- Traceability-only artifact {index}."
                }
                : Artifact(index))
            .ToArray();

        var result = await LiveValidationWorkflow.ExecuteAsync(
            Run(artifacts),
            artifacts,
            new RecordingValidator(item => Passed(item)),
            new PostgreSqlValidationOptions("Host=not-opened"),
            CancellationToken.None);

        Assert.Equal(221, result.TotalBefore);
        Assert.Equal(221, result.TotalAfter);
        Assert.Equal(154, result.ExecutableCount);
        Assert.Equal(
            artifacts.Select(ConversionArtifactReconciler.Identity),
            result.Run.Artifacts.Select(ConversionArtifactReconciler.Identity));
        WorkspaceViewModel.EnsureAllDeployableArtifactsValidated(result.Run);
    }

    [Fact]
    public async Task FailedValidationCompletesAndKeepsPackageGateClosed()
    {
        var artifacts = new[] { Artifact(1), Artifact(2) };
        var result = await LiveValidationWorkflow.ExecuteAsync(
            Run(artifacts),
            artifacts,
            new RecordingValidator(item =>
                item.SourceObjectId == artifacts[0].SourceObjectId
                    ? Failed(item)
                    : Passed(item)),
            new PostgreSqlValidationOptions("Host=not-opened"),
            CancellationToken.None);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(
            LiveSqlValidationOutcome.Failed,
            result.Run.Artifacts[0].Validation.Outcome);
        Assert.Throws<InvalidOperationException>(() =>
            WorkspaceViewModel.EnsureAllDeployableArtifactsValidated(result.Run));
    }

    [Fact]
    public async Task DependencyBlockedValidationCompletesAndKeepsPackageGateClosed()
    {
        var prerequisite = Artifact(1);
        var dependent = Artifact(2) with
        {
            Dependencies = [prerequisite.SourceObjectId]
        };
        var result = await LiveValidationWorkflow.ExecuteAsync(
            Run([prerequisite, dependent]),
            [prerequisite, dependent],
            new RecordingValidator(item =>
                item.SourceObjectId == prerequisite.SourceObjectId
                    ? Failed(item)
                    : Blocked(item, prerequisite.SourceObjectId)),
            new PostgreSqlValidationOptions("Host=not-opened"),
            CancellationToken.None);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.BlockedCount);
        Assert.Equal(
            LiveSqlValidationOutcome.BlockedByDependency,
            result.Run.Artifacts[1].Validation.Outcome);
        Assert.Contains(
            prerequisite.SourceObjectId,
            result.Run.Artifacts[1].Validation.BlockingDependencies);
        Assert.Throws<InvalidOperationException>(() =>
            WorkspaceViewModel.EnsureAllDeployableArtifactsValidated(result.Run));
    }

    [Fact]
    public void ChangedSqlHashMakesPreviousValidationStale()
    {
        var original = Artifact(1);
        var passed = original with
        {
            Validation = Passed(original)
        };
        WorkspaceViewModel.EnsureAllDeployableArtifactsValidated(Run([passed]));

        var changed = passed with
        {
            PostgreSqlDefinition = "CREATE TABLE public.t1(id bigint);",
            ContentHash = "changed-hash"
        };

        Assert.Throws<InvalidOperationException>(() =>
            WorkspaceViewModel.EnsureAllDeployableArtifactsValidated(Run([changed])));
    }

    [Fact]
    public void TraceabilityOnlyArtifactsAreNotMisreportedAsExecutable()
    {
        var traceability = Enumerable.Range(1, 67)
            .Select(index => Artifact(index) with
            {
                PostgreSqlDefinition = $"-- Traceability-only artifact {index}."
            })
            .ToArray();

        Assert.Empty(
            ConversionArtifactReconciler
                .GetArtifactsWithoutCurrentSuccessfulLiveValidation(traceability));
        WorkspaceViewModel.EnsureAllDeployableArtifactsValidated(
            Run(traceability));
    }

    private static ConversionArtifact Artifact(int index)
    {
        var id = InventoryObjectId.Create(
            "orchestration",
            InventoryObjectType.Table,
            "public",
            $"t{index}",
            index);
        return new ConversionArtifact(
            id,
            new TargetObjectIdentifier("Table", "public", $"t{index}"),
            $"CREATE TABLE dbo.t{index}(id int);",
            $"CREATE TABLE public.t{index}(id integer);",
            ConversionClassification.Automatic,
            "TEST.ORCHESTRATION",
            1m,
            [],
            [],
            [],
            [],
            false,
            [],
            new SqlValidationResult(true, false, null, null, null),
            DeploymentPhase.Tables,
            "05_Tables.sql",
            $"hash-{index}");
    }

    private static ConversionRun Run(IReadOnlyList<ConversionArtifact> artifacts) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "source",
            new PostgreSqlVersion(17),
            new ConversionOptions(),
            [],
            [],
            artifacts,
            [],
            [],
            "test");

    private static SqlValidationResult Passed(ConversionArtifact artifact) =>
        new(true, true, null, null, null)
        {
            Outcome = LiveSqlValidationOutcome.Passed,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase,
            ValidatedSqlHash = artifact.ContentHash,
            ValidatedAt = DateTimeOffset.UtcNow
        };

    private static SqlValidationResult Failed(ConversionArtifact artifact) =>
        new(false, true, "42601", "syntax error", 1)
        {
            Outcome = LiveSqlValidationOutcome.Failed,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase,
            ValidatedSqlHash = artifact.ContentHash,
            ValidatedAt = DateTimeOffset.UtcNow
        };

    private static SqlValidationResult Blocked(
        ConversionArtifact artifact,
        InventoryObjectId prerequisite) =>
        new(true, false, null, "Blocked by dependency.", null)
        {
            Outcome = LiveSqlValidationOutcome.BlockedByDependency,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase,
            BlockingDependencies = [prerequisite],
            ValidatedSqlHash = artifact.ContentHash,
            ValidatedAt = DateTimeOffset.UtcNow
        };

    private sealed class RecordingValidator(
        Func<ConversionArtifact, SqlValidationResult> resultFactory)
        : IGeneratedSqlValidator
    {
        public bool WasCalled { get; private set; }

        public int ArtifactCount { get; private set; }

        public Task<SqlValidationResult> ValidateOfflineAsync(
            string sql,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SqlValidationResult(true, false, null, null, null));

        public Task<IReadOnlyDictionary<string, SqlValidationResult>> ValidateLiveAsync(
            IReadOnlyList<ConversionArtifact> artifacts,
            PostgreSqlValidationOptions options,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            ArtifactCount = artifacts.Count;
            return Task.FromResult<IReadOnlyDictionary<string, SqlValidationResult>>(
                artifacts.ToDictionary(
                    item => item.ContentHash,
                    resultFactory,
                    StringComparer.Ordinal));
        }
    }
}
