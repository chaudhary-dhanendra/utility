using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Validation;

public static class SemanticTypeComparer
{
    public static ComparisonClassification Compare(
        string sourceType,
        string targetType,
        string configuredTargetType,
        out string explanation)
    {
        var actual = Normalize(targetType);
        var expected = Normalize(configuredTargetType);
        if (actual == expected)
        {
            var source = Normalize(sourceType);
            explanation = source == actual
                ? "Source and target datatypes are textually equivalent."
                : $"Expected semantic mapping {sourceType} -> {configuredTargetType} was applied.";
            return source == actual
                ? ComparisonClassification.Equivalent
                : ComparisonClassification.EquivalentWithExpectedTransformation;
        }

        explanation = $"Target datatype '{targetType}' does not match configured mapping '{configuredTargetType}'.";
        return ComparisonClassification.Mismatch;
    }

    public static bool HasPrecisionLossRisk(string sourceType, int sourcePrecision, int sourceScale, string targetType)
    {
        var target = Normalize(targetType);
        return (sourceType.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
                sourceType.Equals("numeric", StringComparison.OrdinalIgnoreCase)) &&
               target.StartsWith("numeric(", StringComparison.Ordinal) &&
               ParseNumeric(target) is { } dimensions &&
               (dimensions.Precision < sourcePrecision || dimensions.Scale < sourceScale);
    }

    public static bool HasTimezoneSemanticChange(string sourceType, string targetType) =>
        sourceType.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase) !=
        Normalize(targetType).StartsWith("timestamp with time zone", StringComparison.Ordinal);

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Replace("character varying", "varchar", StringComparison.Ordinal)
            .Replace("timestamp without time zone", "timestamp", StringComparison.Ordinal);

    private static (int Precision, int Scale)? ParseNumeric(string value)
    {
        var start = value.IndexOf('(');
        var end = value.IndexOf(')');
        if (start < 0 || end <= start)
        {
            return null;
        }
        var parts = value[(start + 1)..end].Split(',');
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var precision) &&
               int.TryParse(parts[1], out var scale)
            ? (precision, scale)
            : null;
    }
}

public static class SequenceAlignmentEvaluator
{
    public static SequenceValidationResult Evaluate(
        string source,
        string target,
        decimal current,
        decimal? maximumColumnValue,
        decimal increment,
        decimal minimum,
        decimal maximum,
        bool cycling)
    {
        if (increment == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(increment), "Sequence increment cannot be zero.");
        }
        var next = current + increment;
        var duplicate = maximumColumnValue is not null &&
                        (increment > 0 ? next <= maximumColumnValue : next >= maximumColumnValue);
        return new SequenceValidationResult(
            source, target, current, maximumColumnValue, increment, minimum, maximum, cycling,
            next, duplicate,
            duplicate ? ComparisonClassification.Mismatch : ComparisonClassification.Equivalent);
    }
}

public static class ValidationSeverityPolicy
{
    public static ValidationSeverity Resolve(
        string ruleId,
        ComparisonClassification classification,
        ValidationConfiguration configuration)
    {
        if (configuration.SeverityOverrides.TryGetValue(ruleId, out var configured))
        {
            return configured;
        }
        if (ruleId.Contains("MISSING_TABLE", StringComparison.OrdinalIgnoreCase) ||
            ruleId.Contains("ROW_COUNT", StringComparison.OrdinalIgnoreCase) ||
            ruleId.Contains("SEQUENCE_DUPLICATE", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationSeverity.Critical;
        }
        if (ruleId.Contains("MISSING_FOREIGN_KEY", StringComparison.OrdinalIgnoreCase) ||
            classification is ComparisonClassification.Mismatch or ComparisonClassification.Missing)
        {
            return ValidationSeverity.Error;
        }
        return classification switch
        {
            ComparisonClassification.Warning or ComparisonClassification.ManualReview or
                ComparisonClassification.NotComparable => ValidationSeverity.Warning,
            _ => ValidationSeverity.Information
        };
    }
}

public static class ReadinessCalculator
{
    public static ReadinessAssessment Calculate(
        IReadOnlyList<ValidationFinding> findings,
        ValidationConfiguration configuration)
    {
        var categories = Enum.GetValues<ValidationCategory>().Select(category =>
        {
            var categoryFindings = findings.Where(item => item.Category == category).ToArray();
            var selected = IsSelected(category, configuration.Level);
            var passed = categoryFindings.Count(item => item.Severity == ValidationSeverity.Information);
            var warnings = categoryFindings.Count(item => item.Severity == ValidationSeverity.Warning);
            var blockers = categoryFindings.Count(item =>
                item.Severity is ValidationSeverity.Error or ValidationSeverity.Critical);
            var applicable = categoryFindings.Length;
            decimal? score = !selected ? null : applicable == 0 ? 100 : (decimal?)
                Math.Round(100m * (passed + warnings * 0.5m) / applicable, 2);
            var status = !selected ? ReadinessStatus.Incomplete :
                blockers > 0 ? ReadinessStatus.NotReady :
                warnings > 0 ? ReadinessStatus.ReadyWithWarnings : ReadinessStatus.Ready;
            var weight = configuration.CategoryWeights.TryGetValue(category, out var value) ? value : 0;
            return new ValidationCategoryScore(
                category, weight, applicable, passed, warnings, blockers, score, status,
                !selected
                    ? $"This category is outside the selected {configuration.Level} validation level."
                    : applicable == 0
                    ? "No applicable source objects were present; the category completed without findings."
                    : $"{passed} passed, {warnings} warnings, and {blockers} blockers across {applicable} checks.");
        }).ToArray();

        var scored = categories.Where(item => item.Score is not null && item.Weight > 0).ToArray();
        var totalWeight = scored.Sum(item => item.Weight);
        decimal? weighted = totalWeight == 0 ? null :
            Math.Round(scored.Sum(item => item.Score!.Value * item.Weight) / totalWeight, 2);
        var critical = findings.Where(item => item.Severity == ValidationSeverity.Critical).ToArray();
        var hasErrors = findings.Any(item => item.Severity == ValidationSeverity.Error);
        var incomplete = categories.Any(item =>
            IsSelected(item.Category, configuration.Level) &&
            item.Status == ReadinessStatus.Incomplete);
        var overall = critical.Length > 0 || hasErrors ? ReadinessStatus.NotReady :
            incomplete ? ReadinessStatus.Incomplete :
            findings.Any(item => item.Severity == ValidationSeverity.Warning)
                ? ReadinessStatus.ReadyWithWarnings
                : ReadinessStatus.Ready;
        return new ReadinessAssessment(
            overall,
            weighted,
            categories,
            critical,
            critical.Length > 0
                ? $"{critical.Length} critical blocker(s) prevent readiness regardless of the weighted score."
                : incomplete
                    ? "One or more categories were not evaluated; full validation cannot be claimed."
                    : "All evaluated categories contribute transparently using the configured weights.");
    }

    private static bool IsSelected(ValidationCategory category, ValidationLevel level) => level switch
    {
        ValidationLevel.InventoryOnly => category == ValidationCategory.StructuralCompleteness,
        ValidationLevel.Structural => category is ValidationCategory.StructuralCompleteness or
            ValidationCategory.Constraints or ValidationCategory.UnsupportedFeatures,
        ValidationLevel.DataCounts or ValidationLevel.DataSampling or ValidationLevel.DataComprehensive =>
            category is ValidationCategory.DataReconciliation or ValidationCategory.Constraints,
        ValidationLevel.ProgrammableObject => category == ValidationCategory.ProgrammableObjects,
        ValidationLevel.Full => true,
        _ => false
    };
}
