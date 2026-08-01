using System.IO;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Deployment;

public sealed class DeploymentRegressionTests
{
    [Fact]
    public void EquivalentPublicSchema_IsRetainedUnderFailPolicy()
    {
        var artifact = PackageArtifact(
            new InventoryObjectId(Guid.NewGuid()),
            DeploymentPhase.Schemas,
            "public",
            "public",
            "CREATE SCHEMA IF NOT EXISTS public;");
        var conflict = new ObjectConflict(
            artifact.SourceObjectId,
            PreDeploymentAssessmentService.DisplayTarget(artifact),
            "Schema",
            true,
            true,
            false,
            ExistingObjectConflictPolicy.Fail,
            "The public schema already exists.");

        var findings = PreDeploymentAssessmentService.CreateConflictFindings(
            [conflict],
            ExistingObjectConflictPolicy.Fail);
        var resolution = PostgreSqlDeploymentEngine.ResolveConflictForTesting(
            conflict,
            artifact,
            new DeploymentOptions
            {
                ConflictPolicy = ExistingObjectConflictPolicy.Fail
            });

        Assert.Empty(findings);
        Assert.Equal("public", conflict.TargetObject);
        Assert.DoesNotContain("public.public", conflict.TargetObject, StringComparison.Ordinal);
        Assert.True(resolution.Skip);
        Assert.False(resolution.Block);
        Assert.Null(resolution.Sql);
        Assert.Contains("retained", resolution.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEquivalentObject_StillBlocksUnderFailPolicy()
    {
        var id = new InventoryObjectId(Guid.NewGuid());
        var conflict = new ObjectConflict(
            id,
            "public.customer",
            "Table",
            true,
            false,
            false,
            ExistingObjectConflictPolicy.Fail,
            null);

        var finding = Assert.Single(
            PreDeploymentAssessmentService.CreateConflictFindings(
                [conflict],
                ExistingObjectConflictPolicy.Fail));

        Assert.Equal("TARGET.CONFLICT", finding.Code);
        Assert.Contains("public.customer", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentSplit_PlacesInsertPrerequisitesBeforeForeignKeys()
    {
        var phases = new[]
        {
            DeploymentPhase.Schemas,
            DeploymentPhase.Types,
            DeploymentPhase.Sequences,
            DeploymentPhase.Tables,
            DeploymentPhase.PrimaryKeys,
            DeploymentPhase.UniqueConstraints,
            DeploymentPhase.CheckConstraints,
            DeploymentPhase.ForeignKeys,
            DeploymentPhase.Indexes,
            DeploymentPhase.Functions,
            DeploymentPhase.Views,
            DeploymentPhase.Triggers
        };
        var artifacts = phases.Reverse().Select(phase =>
            PackageArtifact(
                new InventoryObjectId(Guid.NewGuid()),
                phase,
                "public",
                phase.ToString(),
                $"-- {phase}")).ToArray();
        var ordered = PostgreSqlDeploymentEngine.OrderArtifacts(artifacts);

        var (preData, postData) =
            PostgreSqlDeploymentEngine.SplitArtifactsAroundData(ordered);

        Assert.Equal(
            phases.Take(7),
            preData.Select(item => item.Phase));
        Assert.Equal(
            phases.Skip(7),
            postData.Select(item => item.Phase));
    }

    [Fact]
    public void SelfReferencingForeignKey_RemainsPostDataWithoutSyntheticDataArtifact()
    {
        var tableId = new InventoryObjectId(Guid.NewGuid());
        var table = PackageArtifact(
            tableId,
            DeploymentPhase.Tables,
            "public",
            "node",
            "CREATE TABLE public.node(id integer, parent_id integer);");
        var foreignKey = PackageArtifact(
            new InventoryObjectId(Guid.NewGuid()),
            DeploymentPhase.ForeignKeys,
            "public",
            "fk_node_parent",
            "ALTER TABLE public.node ADD CONSTRAINT fk_node_parent " +
            "FOREIGN KEY(parent_id) REFERENCES public.node(id);") with
        {
            Dependencies = [tableId]
        };

        var ordered = PostgreSqlDeploymentEngine.OrderArtifacts([foreignKey, table]);
        var (preData, postData) =
            PostgreSqlDeploymentEngine.SplitArtifactsAroundData(ordered);

        Assert.DoesNotContain(ordered, item => item.Phase == DeploymentPhase.Data);
        Assert.Equal(DeploymentPhase.Tables, Assert.Single(preData).Phase);
        Assert.Equal(DeploymentPhase.ForeignKeys, Assert.Single(postData).Phase);
    }

    [Fact]
    public void CheckConstraintDependency_PromotesOnlyRequiredFunctionBeforeData()
    {
        var tableId = new InventoryObjectId(Guid.NewGuid());
        var functionId = new InventoryObjectId(Guid.NewGuid());
        var checkId = new InventoryObjectId(Guid.NewGuid());
        var unrelatedFunctionId = new InventoryObjectId(Guid.NewGuid());
        var table = PackageArtifact(
            tableId,
            DeploymentPhase.Tables,
            "nrega_sk",
            "sau_details1617",
            "CREATE TABLE nrega_sk.sau_details1617(acc_no varchar(18));");
        var function = PackageArtifact(
            functionId,
            DeploymentPhase.Functions,
            "nrega_sk",
            "fnchksau_dupacc",
            "CREATE FUNCTION nrega_sk.fnchksau_dupacc(v varchar) " +
            "RETURNS boolean LANGUAGE sql AS $$ SELECT false; $$;") with
        {
            Dependencies = [tableId]
        };
        var check = PackageArtifact(
            checkId,
            DeploymentPhase.CheckConstraints,
            "nrega_sk",
            "chksau_dupacc",
            "ALTER TABLE nrega_sk.sau_details1617 ADD CONSTRAINT chksau_dupacc " +
            "CHECK (NOT nrega_sk.fnchksau_dupacc(acc_no));") with
        {
            Dependencies = [tableId, functionId]
        };
        var unrelatedFunction = PackageArtifact(
            unrelatedFunctionId,
            DeploymentPhase.Functions,
            "nrega_sk",
            "unrelated",
            "CREATE FUNCTION nrega_sk.unrelated() RETURNS integer " +
            "LANGUAGE sql AS $$ SELECT 1; $$;");

        var ordered = PostgreSqlDeploymentEngine.OrderArtifacts(
            [check, unrelatedFunction, function, table]);
        var (preData, postData) =
            PostgreSqlDeploymentEngine.SplitArtifactsAroundData(ordered);

        Assert.Equal(
            [tableId, functionId, checkId],
            preData.Select(item => item.SourceObjectId));
        Assert.Equal(
            unrelatedFunctionId,
            Assert.Single(postData).SourceObjectId);
    }

    [Theory]
    [InlineData(ConstraintDeploymentStrategy.AddNotValidThenValidate)]
    [InlineData(ConstraintDeploymentStrategy.ValidateInLaterPhase)]
    public void DeferredForeignKeyStrategy_AddsNotValidAndCreatesLaterValidation(
        ConstraintDeploymentStrategy strategy)
    {
        const string sql =
            "ALTER TABLE public.child ADD CONSTRAINT fk_child_parent " +
            "FOREIGN KEY(parent_id) REFERENCES public.parent(id);";
        var artifact = PackageArtifact(
            new InventoryObjectId(Guid.NewGuid()),
            DeploymentPhase.ForeignKeys,
            "public",
            "fk_child_parent",
            sql);

        var createSql = PostgreSqlDeploymentEngine.ApplyConstraintStrategyForTesting(
            sql,
            artifact,
            strategy);
        var validationSql =
            PostgreSqlDeploymentEngine.CreateConstraintValidationSqlForTesting(createSql);

        Assert.EndsWith(" NOT VALID;", createSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "ALTER TABLE public.child VALIDATE CONSTRAINT fk_child_parent;",
            validationSql);
    }

    [Fact]
    public void LiveValidationMerge_PreservesAuthoritativeArtifactCollectionBySourceId()
    {
        var original = new[]
        {
            Artifact(DeploymentPhase.Tables),
            Artifact(DeploymentPhase.Functions),
            Artifact(DeploymentPhase.Views)
        };
        var presented = new[]
        {
            original[0] with
            {
                PostgreSqlDefinition = "CREATE TABLE public.edited(id integer);",
                ContentHash = "edited"
            }
        };
        var validationById = original.ToDictionary(
            item => item.SourceObjectId,
            _ => PassedValidation());

        var merged = ConversionArtifactReconciler.ApplyValidationResults(
            original,
            presented,
            validationById);

        Assert.Equal(original.Length, merged.Count);
        Assert.Equal(
            original.Select(item => item.SourceObjectId),
            merged.Select(item => item.SourceObjectId));
        Assert.Equal(presented[0].PostgreSqlDefinition, merged[0].PostgreSqlDefinition);
        Assert.All(merged, item =>
            Assert.Equal(LiveSqlValidationOutcome.Passed, item.Validation.Outcome));
    }

    [Fact]
    public void LiveValidationMerge_PreservesMultipleArtifactsOwnedByOneSourceObject()
    {
        var table = Artifact(DeploymentPhase.Tables);
        var sequence = table with
        {
            TargetObjectId = new TargetObjectIdentifier("Sequence", "public", "table_id_seq"),
            PostgreSqlDefinition = "CREATE SEQUENCE public.table_id_seq;",
            RuleId = "IDENTITY.SEQUENCE",
            DeploymentPhase = DeploymentPhase.Sequences,
            ScriptFileName = "04_IdentitySequences.sql",
            ContentHash = "sequence-hash"
        };
        var results = new Dictionary<string, SqlValidationResult>(StringComparer.Ordinal)
        {
            [table.ContentHash] = PassedValidation(),
            [sequence.ContentHash] = PassedValidation()
        };

        var merged = ConversionArtifactReconciler.ApplyValidationResultsByContentHash(
            [table, sequence],
            [table, sequence],
            results);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, item =>
            Assert.Equal(LiveSqlValidationOutcome.Passed, item.Validation.Outcome));
        Assert.Equal(2, merged.Count(item => item.SourceObjectId == table.SourceObjectId));
    }

    [Fact]
    public void PackageReconciliation_BlocksWhenAnyConvertedArtifactDisappears()
    {
        var artifacts = new[]
        {
            Artifact(DeploymentPhase.Tables),
            Artifact(DeploymentPhase.Functions)
        };
        var run = Run(artifacts);
        var manifest = new MigrationPackageManifest
        {
            Artifacts =
            [
                PackageArtifact(
                    artifacts[0].SourceObjectId,
                    artifacts[0].DeploymentPhase,
                    "public",
                    "object_1",
                    artifacts[0].PostgreSqlDefinition)
            ]
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            MigrationPackageWriter.EnsureManifestReconciles(run, manifest));

        Assert.Contains("missing=1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageReconciliation_AcceptsExecutableManualUnsupportedAndTraceabilityArtifacts()
    {
        var artifacts = new[]
        {
            Artifact(DeploymentPhase.Tables),
            Artifact(DeploymentPhase.CheckConstraints) with { RequiresManualReview = true },
            Artifact(DeploymentPhase.Functions) with
            {
                Classification = ConversionClassification.Unsupported
            },
            Artifact(DeploymentPhase.Indexes) with
            {
                PostgreSqlDefinition = "-- Created by owning constraint."
            }
        };
        var run = Run(artifacts);
        var manifest = new MigrationPackageManifest
        {
            Artifacts = artifacts.Select((item, index) =>
                PackageArtifact(
                    item.SourceObjectId,
                    item.DeploymentPhase,
                    "public",
                    $"object_{index}",
                    item.PostgreSqlDefinition) with
                {
                    RequiresManualReview = item.RequiresManualReview,
                    Classification = item.Classification,
                    IsExecutable = index != 3
                }).ToArray()
        };

        MigrationPackageWriter.EnsureManifestReconciles(run, manifest);
    }

    [Fact]
    public void Assessment_BlocksManifestWhenSelectedMappedObjectsDisappear()
    {
        var retained = Artifact(DeploymentPhase.Tables);
        var missing = Artifact(DeploymentPhase.Functions);
        var manifest = new MigrationPackageManifest
        {
            Artifacts =
            [
                PackageArtifact(
                    retained.SourceObjectId,
                    retained.DeploymentPhase,
                    "public",
                    "retained",
                    retained.PostgreSqlDefinition)
            ],
            ObjectMappings =
            [
                Mapping(retained.SourceObjectId, "Table", "retained"),
                Mapping(missing.SourceObjectId, "Function", "missing")
            ]
        };
        var findings = new List<DeploymentFinding>();

        PreDeploymentAssessmentService.AssessManifest(
            manifest,
            new DeploymentOptions(),
            null,
            findings);

        var finding = Assert.Single(findings, item =>
            item.Code == "PACKAGE.ARTIFACT_RECONCILIATION");
        Assert.Equal(DeploymentFindingSeverity.Critical, finding.Severity);
        Assert.Contains("1 selected mapped objects", finding.Message, StringComparison.Ordinal);
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

    private static ConversionArtifact Artifact(DeploymentPhase phase)
    {
        var id = new InventoryObjectId(Guid.NewGuid());
        return new ConversionArtifact(
            id,
            new TargetObjectIdentifier(phase.ToString(), "public", id.ToString()),
            "source",
            $"-- {phase}",
            ConversionClassification.Automatic,
            "TEST",
            1,
            [],
            [],
            [],
            [],
            false,
            [],
            new SqlValidationResult(true, false, null, null, null),
            phase,
            $"{phase}.sql",
            id.ToString());
    }

    private static PackageArtifactManifest PackageArtifact(
        InventoryObjectId id,
        DeploymentPhase phase,
        string schema,
        string name,
        string sql) =>
        new(
            id,
            phase.ToString(),
            schema,
            name,
            phase,
            $"{phase}.sql",
            sql,
            "hash",
            ConversionClassification.Automatic,
            [],
            [],
            false,
            [],
            -1);

    private static SqlValidationResult PassedValidation() =>
        new(true, true, null, null, null)
        {
            Outcome = LiveSqlValidationOutcome.Passed,
            Confidence = LiveSqlValidationConfidence.DisposableDatabase
        };

    private static IdentifierMappingEntry Mapping(
        InventoryObjectId id,
        string objectType,
        string name) =>
        new(
            id,
            objectType,
            "dbo",
            name,
            $"[dbo].[{name}]",
            "public",
            name,
            $"public.{name}",
            name.Length,
            name.Length,
            false,
            false,
            null,
            "test");
}
