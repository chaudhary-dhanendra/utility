using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Tests.Deployment;

public sealed class DeploymentBlockingPolicyTests
{
    [Theory]
    [InlineData(PreDeploymentPolicy.BlockOnErrors, DeploymentFindingSeverity.Error, true)]
    [InlineData(PreDeploymentPolicy.BlockOnErrors, DeploymentFindingSeverity.Warning, false)]
    [InlineData(PreDeploymentPolicy.BlockOnCriticalOnly, DeploymentFindingSeverity.Error, false)]
    [InlineData(PreDeploymentPolicy.BlockOnCriticalOnly, DeploymentFindingSeverity.Critical, true)]
    [InlineData(PreDeploymentPolicy.AllowWarnings, DeploymentFindingSeverity.Warning, false)]
    public void AssessmentAndPresentationUseSameBlockingDecision(
        PreDeploymentPolicy policy,
        DeploymentFindingSeverity severity,
        bool expected)
    {
        var finding = new DeploymentFinding("TEST", severity, "fixture");

        Assert.Equal(
            expected,
            DeploymentBlockingPolicy.IsBlocking(finding, policy, false));
        Assert.Equal(
            expected ? 1 : 0,
            DeploymentBlockingPolicy.CountBlocking([finding], policy, false));
        Assert.Equal(
            expected,
            DeploymentBlockingPolicy.IsBlocked([finding], policy, false));
    }

    [Fact]
    public void AdministratorOverrideCannotSuppressNonOverridableCriticalFinding()
    {
        var overridable = new DeploymentFinding(
            "OVERRIDABLE",
            DeploymentFindingSeverity.Critical,
            "fixture",
            CanOverride: true);
        var mandatory = new DeploymentFinding(
            "MANDATORY",
            DeploymentFindingSeverity.Critical,
            "fixture");

        Assert.False(DeploymentBlockingPolicy.IsBlocking(
            overridable,
            PreDeploymentPolicy.AdministratorOverride,
            true));
        Assert.True(DeploymentBlockingPolicy.IsBlocking(
            mandatory,
            PreDeploymentPolicy.AdministratorOverride,
            true));
    }
}
