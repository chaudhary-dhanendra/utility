using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Conversion;

public sealed class ConversionValidationResultReuseTests
{
    [Fact]
    public void ReusesOnlyUnchangedSuccessfulLiveValidation()
    {
        var passed = Validation(LiveSqlValidationOutcome.Passed, true, true);
        var failed = Validation(LiveSqlValidationOutcome.Failed, true, false);
        var previous = Run(
            Artifact("unchanged", passed),
            Artifact("failed", failed),
            Artifact("removed", passed));
        var converted = Run(
            Artifact("unchanged", Validation(LiveSqlValidationOutcome.NotRun, false, true)),
            Artifact("changed", Validation(LiveSqlValidationOutcome.NotRun, false, true)),
            Artifact("failed", Validation(LiveSqlValidationOutcome.NotRun, false, true)));

        var result = ConversionValidationResultReuse.ReuseUnchangedSuccessfulResults(
            converted,
            previous);

        Assert.Same(passed, result.Artifacts.Single(item =>
            item.ContentHash == "unchanged").Validation);
        Assert.Equal(
            LiveSqlValidationOutcome.NotRun,
            result.Artifacts.Single(item => item.ContentHash == "changed").Validation.Outcome);
        Assert.Equal(
            LiveSqlValidationOutcome.NotRun,
            result.Artifacts.Single(item => item.ContentHash == "failed").Validation.Outcome);
        Assert.DoesNotContain(result.Artifacts, item => item.ContentHash == "removed");
    }

    private static SqlValidationResult Validation(
        LiveSqlValidationOutcome outcome,
        bool live,
        bool structural) =>
        new(structural, live, null, null, null)
        {
            Outcome = outcome,
            Confidence = live
                ? LiveSqlValidationConfidence.DisposableDatabase
                : LiveSqlValidationConfidence.None
        };

    private static ConversionArtifact Artifact(
        string hash,
        SqlValidationResult validation)
    {
        var id = new InventoryObjectId(Guid.NewGuid());
        return new ConversionArtifact(
            id,
            new TargetObjectIdentifier("Table", "public", hash),
            "source",
            $"CREATE TABLE public.{hash}(id integer);",
            ConversionClassification.Automatic,
            "TEST",
            1m,
            [],
            [],
            [],
            [],
            false,
            [],
            validation,
            DeploymentPhase.Tables,
            "05_Tables.sql",
            hash);
    }

    private static ConversionRun Run(params ConversionArtifact[] artifacts) =>
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
}
