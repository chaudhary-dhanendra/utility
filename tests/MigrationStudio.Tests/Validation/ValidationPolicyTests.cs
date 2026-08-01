using MigrationStudio.Domain.Validation;
using MigrationStudio.Validation;

namespace MigrationStudio.Tests.Validation;

public sealed class ValidationPolicyTests
{
    [Theory]
    [InlineData("bit", "boolean", "boolean")]
    [InlineData("nvarchar(max)", "text", "text")]
    [InlineData("uniqueidentifier", "uuid", "uuid")]
    [InlineData("varbinary", "bytea", "bytea")]
    public void ConfiguredSemanticMappingsAreExpectedTransformations(
        string source,
        string target,
        string configured)
    {
        var classification = SemanticTypeComparer.Compare(source, target, configured, out var explanation);

        Assert.Equal(ComparisonClassification.EquivalentWithExpectedTransformation, classification);
        Assert.Contains("Expected semantic mapping", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void PrecisionAndTimezoneRisksAreDetected()
    {
        Assert.True(SemanticTypeComparer.HasPrecisionLossRisk("decimal", 38, 10, "numeric(20,4)"));
        Assert.True(SemanticTypeComparer.HasTimezoneSemanticChange(
            "datetimeoffset", "timestamp without time zone"));
    }

    [Fact]
    public void SequenceBehindMaximumKeyIsCritical()
    {
        var result = SequenceAlignmentEvaluator.Evaluate(
            "dbo.orders.id", "public.orders_id_seq", 40, 50, 1, 1, long.MaxValue, false);
        var severity = ValidationSeverityPolicy.Resolve(
            "SEQUENCE.SEQUENCE_DUPLICATE", result.Classification, new ValidationConfiguration());

        Assert.True(result.WouldGenerateDuplicate);
        Assert.Equal(41, result.ExpectedNextValue);
        Assert.Equal(ValidationSeverity.Critical, severity);
    }

    [Fact]
    public void SeverityOverridesAreAppliedByRule()
    {
        var configuration = new ValidationConfiguration
        {
            SeverityOverrides = new Dictionary<string, ValidationSeverity>
            {
                ["CONSTRAINT.UNVALIDATED"] = ValidationSeverity.Error
            }
        };

        Assert.Equal(
            ValidationSeverity.Error,
            ValidationSeverityPolicy.Resolve(
                "CONSTRAINT.UNVALIDATED", ComparisonClassification.Warning, configuration));
    }

    [Fact]
    public void ReadinessIsWeightedExplainedAndNeverHidesCriticalBlocker()
    {
        var findings = new[]
        {
            Finding(ValidationCategory.StructuralCompleteness, ValidationSeverity.Information,
                ComparisonClassification.Equivalent),
            Finding(ValidationCategory.DataReconciliation, ValidationSeverity.Information,
                ComparisonClassification.Equivalent),
            Finding(ValidationCategory.Constraints, ValidationSeverity.Critical,
                ComparisonClassification.Mismatch)
        };

        var assessment = ReadinessCalculator.Calculate(findings, new ValidationConfiguration());

        Assert.Equal(ReadinessStatus.NotReady, assessment.OverallStatus);
        Assert.NotNull(assessment.WeightedScore);
        Assert.Single(assessment.CriticalBlockers);
        Assert.Contains("critical blocker", assessment.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.All(assessment.Categories, category => Assert.False(string.IsNullOrWhiteSpace(category.Explanation)));
    }

    private static ValidationFinding Finding(
        ValidationCategory category,
        ValidationSeverity severity,
        ComparisonClassification classification) =>
        new("TEST", category, severity, classification, "Table", "dbo.t", "public.t", "test");
}
