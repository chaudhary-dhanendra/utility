using MigrationStudio.Application.Conversion;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Desktop.ViewModels;

internal sealed record LiveValidationWorkflowResult(
    ConversionRun Run,
    int TotalBefore,
    int TotalAfter,
    int ExecutableCount,
    int ReusedCount,
    int RequiringValidationCount,
    int PassedCount,
    int FailedCount,
    int BlockedCount,
    int NotRunCount,
    int ManualReviewCount,
    DeploymentPlan DeploymentPlan,
    BlockedDependencyReconciliation Reconciliation);

internal static class LiveValidationWorkflow
{
    public static async Task<LiveValidationWorkflowResult> ExecuteAsync(
        ConversionRun current,
        IReadOnlyList<ConversionArtifact> presentedArtifacts,
        IGeneratedSqlValidator validator,
        PostgreSqlValidationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(presentedArtifacts);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(options);

        var artifacts = ConversionArtifactReconciler.OverlayPresentedEdits(
            current.Artifacts,
            presentedArtifacts);
        var reusable = artifacts
            .Where(ConversionArtifactReconciler.HasCurrentSuccessfulLiveValidation)
            .GroupBy(item => item.ContentHash, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().Validation,
                StringComparer.Ordinal);
        var executableCount = artifacts.Count(
            ConversionArtifactReconciler.IsDeployableExecutable);
        var reusedCount = artifacts.Count(item =>
            ConversionArtifactReconciler.IsDeployableExecutable(item) &&
            reusable.ContainsKey(item.ContentHash));
        var requiringValidationCount = executableCount - reusedCount;

        var results = await validator.ValidateLiveAsync(
            artifacts,
            options with { ReusableSuccessfulResults = reusable },
            cancellationToken).ConfigureAwait(false);
        var resultsByIdentity = artifacts
            .Where(item => results.ContainsKey(item.ContentHash))
            .ToDictionary(
                ConversionArtifactReconciler.Identity,
                item => results[item.ContentHash]);
        var updated = ConversionArtifactReconciler.ApplyValidationResultsByIdentity(
            current.Artifacts,
            artifacts,
            resultsByIdentity);
        var run = current with { Artifacts = updated };
        var planning = DeploymentPublicationReconciler.Reconcile(run);
        run = run with { PublicationReconciliation = planning.Reconciliation };

        return new LiveValidationWorkflowResult(
            run,
            current.Artifacts.Count,
            updated.Count,
            executableCount,
            reusedCount,
            requiringValidationCount,
            updated.Count(item => item.Validation.Outcome == LiveSqlValidationOutcome.Passed),
            updated.Count(item => item.Validation.Outcome == LiveSqlValidationOutcome.Failed),
            updated.Count(item =>
                item.Validation.Outcome == LiveSqlValidationOutcome.BlockedByDependency),
            updated.Count(item => item.Validation.Outcome == LiveSqlValidationOutcome.NotRun),
            updated.Count(item =>
                item.Validation.Outcome is LiveSqlValidationOutcome.Manual or
                    LiveSqlValidationOutcome.Unsupported),
            planning.Plan,
            planning.Reconciliation);
    }
}
