namespace MigrationStudio.Domain.Deployment;

/// <summary>
/// Authoritative interpretation of pre-deployment findings. The assessment
/// engine and every presentation surface must use this policy so that the
/// displayed blocker count cannot disagree with <see cref="PreDeploymentAssessment.CanDeploy"/>.
/// </summary>
public static class DeploymentBlockingPolicy
{
    public static bool IsBlocking(
        DeploymentFinding finding,
        PreDeploymentPolicy policy,
        bool administratorOverrideApplied)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (administratorOverrideApplied)
        {
            return finding.Severity == DeploymentFindingSeverity.Critical &&
                !finding.CanOverride;
        }

        return policy switch
        {
            PreDeploymentPolicy.BlockOnErrors =>
                finding.Severity is DeploymentFindingSeverity.Error
                    or DeploymentFindingSeverity.Critical,
            PreDeploymentPolicy.BlockOnCriticalOnly or PreDeploymentPolicy.AllowWarnings =>
                finding.Severity == DeploymentFindingSeverity.Critical,
            PreDeploymentPolicy.AdministratorOverride => true,
            _ => true
        };
    }

    public static bool IsBlocked(
        IEnumerable<DeploymentFinding> findings,
        PreDeploymentPolicy policy,
        bool administratorOverrideApplied) =>
        findings.Any(item => IsBlocking(item, policy, administratorOverrideApplied));

    public static int CountBlocking(
        IEnumerable<DeploymentFinding> findings,
        PreDeploymentPolicy policy,
        bool administratorOverrideApplied) =>
        findings.Count(item => IsBlocking(item, policy, administratorOverrideApplied));
}
