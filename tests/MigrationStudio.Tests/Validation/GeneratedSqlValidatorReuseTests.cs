using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Validation;

namespace MigrationStudio.Tests.Validation;

public sealed class GeneratedSqlValidatorReuseTests
{
    [Fact]
    public async Task UnchangedSuccessfulArtifactIsReusedWithoutOpeningPostgreSql()
    {
        var artifact = Artifact("unchanged-hash");
        var previous = new SqlValidationResult(true, true, null, null, null)
        {
            Outcome = LiveSqlValidationOutcome.Passed,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase,
            ValidationRunId = Guid.NewGuid()
        };
        LiveSqlValidationProgress? progress = null;

        var results = await new GeneratedSqlValidator().ValidateLiveAsync(
            [artifact],
            new PostgreSqlValidationOptions(
                "Host=invalid.example;Database=invalid;Username=invalid;Password=not-logged")
            {
                ReusableSuccessfulResults =
                    new Dictionary<string, SqlValidationResult>(StringComparer.Ordinal)
                    {
                        [artifact.ContentHash] = previous
                    },
                Progress = new InlineProgress<LiveSqlValidationProgress>(item => progress = item)
            },
            CancellationToken.None);

        Assert.Same(previous, results[artifact.ContentHash]);
        Assert.NotNull(progress);
        Assert.Equal(100, progress!.Percentage);
    }

    [Fact]
    public async Task NonSuccessfulPriorResultIsNeverReused()
    {
        var artifact = Artifact("changed-hash");
        var previous = new SqlValidationResult(false, true, "42601", "syntax", 1)
        {
            Outcome = LiveSqlValidationOutcome.Failed
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new GeneratedSqlValidator().ValidateLiveAsync(
                [artifact],
                new PostgreSqlValidationOptions(
                    "Host=127.0.0.1;Port=1;Database=invalid;Username=invalid;Password=not-logged;Timeout=1")
                {
                    PreferDisposableDatabase = false,
                    ReusableSuccessfulResults =
                        new Dictionary<string, SqlValidationResult>(StringComparer.Ordinal)
                        {
                            [artifact.ContentHash] = previous
                        }
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task ManualArtifactDoesNotForcePostgreSqlConnectionWhenExecutableArtifactsAreReusable()
    {
        var executable = Artifact("executable-hash");
        var manual = Artifact("manual-hash") with
        {
            Classification = ConversionClassification.ManualConversion,
            RequiresManualReview = true
        };
        var previous = new SqlValidationResult(true, true, null, null, null)
        {
            Outcome = LiveSqlValidationOutcome.Passed,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase
        };

        var results = await new GeneratedSqlValidator().ValidateLiveAsync(
            [executable, manual],
            new PostgreSqlValidationOptions(
                "Host=invalid.example;Database=invalid;Username=invalid;Password=not-logged")
            {
                ReusableSuccessfulResults =
                    new Dictionary<string, SqlValidationResult>(StringComparer.Ordinal)
                    {
                        [executable.ContentHash] = previous
                    }
            },
            CancellationToken.None);

        Assert.Same(previous, results[executable.ContentHash]);
        Assert.Equal(
            LiveSqlValidationOutcome.Manual,
            results[manual.ContentHash].Outcome);
        Assert.False(results[manual.ContentHash].WasLiveValidated);
    }

    private static ConversionArtifact Artifact(string hash)
    {
        var id = InventoryObjectId.Create(
            "fixture",
            InventoryObjectType.Table,
            "public",
            "probe",
            1);
        return new ConversionArtifact(
            id,
            new TargetObjectIdentifier("Table", "public", "probe"),
            "fixture",
            "CREATE TABLE public.probe(id integer);",
            ConversionClassification.Automatic,
            "TEST",
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
            hash);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
