using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Deployment;

public sealed class IdentifierDeploymentGateTests
{
    [Fact]
    public void Assessment_AllowsSafeReservedAndShortenedMappings()
    {
        var findings = Assess(
            Mapping("user", "\"user\"") with
            {
                IsReservedWord = true,
                RequiresQuoting = true,
                WasQuoted = true,
                MappingStatus = IdentifierMappingStatus.ReservedWordSafelyQuoted
            },
            Mapping(new string('x', 70), "readable_12345678") with
            {
                WasShortened = true,
                MappingStatus = IdentifierMappingStatus.AutomaticallyShortened,
                Severity = IdentifierMappingSeverity.Warning
            });

        Assert.DoesNotContain(findings, item =>
            item.Code.StartsWith("IDENTIFIER.", StringComparison.Ordinal) &&
            item.Severity == DeploymentFindingSeverity.Critical);
    }

    [Fact]
    public void Assessment_BlocksUnquotedReservedOverlengthAndUnresolvedCollisions()
    {
        var findings = Assess(
            Mapping("user", "user") with
            {
                IsReservedWord = true,
                RequiresQuoting = true,
                WasQuoted = false
            },
            Mapping("long", "long") with { TargetUtf8ByteLength = 64 },
            Mapping("collision", "collision") with
            {
                HadCollision = true,
                CollisionResolved = false,
                MappingStatus = IdentifierMappingStatus.BlockingConflict,
                Severity = IdentifierMappingSeverity.Error,
                ManualReviewRequired = true
            });

        Assert.Contains(findings, item => item.Code == "IDENTIFIER.RESERVED_UNQUOTED");
        Assert.Contains(findings, item => item.Code == "IDENTIFIER.TOO_LONG");
        Assert.Contains(findings, item => item.Code == "IDENTIFIER.BLOCKING");
    }

    private static List<DeploymentFinding> Assess(params IdentifierMappingEntry[] mappings)
    {
        var findings = new List<DeploymentFinding>();
        PreDeploymentAssessmentService.AssessManifest(
            new MigrationPackageManifest
            {
                TargetPostgreSqlVersion = 18,
                ObjectMappings = mappings
            },
            new DeploymentOptions { Mode = DeploymentMode.GenerateOnly },
            null,
            findings);
        return findings;
    }

    private static IdentifierMappingEntry Mapping(string source, string target) =>
        new(
            InventoryObjectId.Create("fixture", InventoryObjectType.Column, "dbo", source, null),
            "column",
            "dbo",
            source,
            $"[dbo].[table].[{source}]",
            "public",
            target,
            $"public.{target}",
            System.Text.Encoding.UTF8.GetByteCount(source),
            System.Text.Encoding.UTF8.GetByteCount(target.Trim('"')),
            false,
            false,
            null,
            "test")
        {
            ParentObject = "[dbo].[table]",
            SourceDatabase = "fixture"
        };
}
