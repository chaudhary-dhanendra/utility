using MigrationStudio.Application.Deployment;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Deployment;

public sealed class PostgreSqlTargetIdentityTests
{
    [Theory]
    [InlineData("pg_catalog")]
    [InlineData("information_schema")]
    [InlineData("pg_toast")]
    [InlineData("pg_temp_12")]
    [InlineData("pg_toast_temp_12")]
    public void SystemSchemaPolicy_ExcludesPostgreSqlInternalSchemas(string schema)
    {
        Assert.True(PostgreSqlSystemSchemaPolicy.IsSystemSchema(schema));
    }

    [Theory]
    [InlineData("dbo")]
    [InlineData("nrega_sk")]
    [InlineData("public")]
    public void SystemSchemaPolicy_PreservesUserSchemas(string schema)
    {
        Assert.False(PostgreSqlSystemSchemaPolicy.IsSystemSchema(schema));
    }

    [Fact]
    public void EmptyTarget_HasNoConflictsWithPackageArtifacts()
    {
        var artifact = Artifact(DeploymentPhase.Tables, "dbo", "states_uso1", "Table");
        var key = PreDeploymentAssessmentService.CreateTargetObjectKey(artifact)!;

        Assert.False(PreDeploymentAssessmentService.TargetContains([], key));
    }

    [Fact]
    public void TargetIdentity_IsSchemaAndObjectTypeSensitive()
    {
        var table = PreDeploymentAssessmentService.CreateTargetObjectKey(
            Artifact(DeploymentPhase.Tables, "dbo", "same_name", "Table"))!;
        var otherSchema = PreDeploymentAssessmentService.CreateTargetObjectKey(
            Artifact(DeploymentPhase.Tables, "nrega", "same_name", "Table"))!;
        var view = PreDeploymentAssessmentService.CreateTargetObjectKey(
            Artifact(DeploymentPhase.Views, "dbo", "same_name", "View"))!;

        var target = new HashSet<PreDeploymentAssessmentService.PostgreSqlTargetObjectKey>
        {
            table
        };
        Assert.True(PreDeploymentAssessmentService.TargetContains(target, table));
        Assert.False(PreDeploymentAssessmentService.TargetContains(target, otherSchema));
        Assert.False(PreDeploymentAssessmentService.TargetContains(target, view));
    }

    [Fact]
    public void RoutineIdentity_IncludesKindAndSignature()
    {
        var oneArgument = Artifact(
            DeploymentPhase.Functions,
            "dbo",
            "calculate",
            "Function") with
        {
            RoutineIdentityArguments = "integer"
        };
        var twoArguments = oneArgument with
        {
            RoutineIdentityArguments = "integer, integer"
        };
        var procedure = Artifact(
            DeploymentPhase.Procedures,
            "dbo",
            "calculate",
            "Procedure") with
        {
            RoutineIdentityArguments = "integer"
        };
        var target = new HashSet<PreDeploymentAssessmentService.PostgreSqlTargetObjectKey>
        {
            PreDeploymentAssessmentService.CreateTargetObjectKey(oneArgument)!
        };

        Assert.False(PreDeploymentAssessmentService.TargetContains(
            target,
            PreDeploymentAssessmentService.CreateTargetObjectKey(twoArguments)!));
        Assert.False(PreDeploymentAssessmentService.TargetContains(
            target,
            PreDeploymentAssessmentService.CreateTargetObjectKey(procedure)!));
    }

    [Fact]
    public void TriggerIdentity_IncludesParentTable()
    {
        var first = Artifact(DeploymentPhase.Triggers, "dbo", "audit_trigger", "Trigger") with
        {
            TargetParentObject = "\"dbo\".\"orders\""
        };
        var second = first with { TargetParentObject = "\"dbo\".\"customers\"" };

        Assert.NotEqual(
            PreDeploymentAssessmentService.CreateTargetObjectKey(first),
            PreDeploymentAssessmentService.CreateTargetObjectKey(second));
    }

    [Fact]
    public void PackageDuplicate_IsSeparateAndTyped()
    {
        var first = Artifact(DeploymentPhase.Tables, "dbo", "orders", "Table");
        var second = first with { SourceObjectId = new InventoryObjectId(Guid.NewGuid()) };
        var view = Artifact(DeploymentPhase.Views, "dbo", "orders", "View");
        var manifest = new MigrationPackageManifest { Artifacts = [first, second, view] };

        var duplicates = PreDeploymentAssessmentService.FindPackageDuplicates(
            manifest,
            new DeploymentOptions { Scope = DeploymentScope.CompletePackage });

        var duplicate = Assert.Single(duplicates);
        Assert.Equal("Table", duplicate.ObjectKind);
        Assert.Equal(2, duplicate.SourceObjectIds.Count);
    }

    private static PackageArtifactManifest Artifact(
        DeploymentPhase phase,
        string schema,
        string name,
        string type) =>
        new(
            new InventoryObjectId(Guid.NewGuid()),
            type,
            schema,
            name,
            phase,
            $"{(int)phase:00}.sql",
            "SELECT 1;",
            "hash",
            ConversionClassification.Automatic,
            [],
            [],
            false,
            [],
            -1);
}
