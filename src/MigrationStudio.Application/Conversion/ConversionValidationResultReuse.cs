using MigrationStudio.Domain.Conversion;

namespace MigrationStudio.Application.Conversion;

public static class ConversionValidationResultReuse
{
    public static ConversionRun ReuseUnchangedSuccessfulResults(
        ConversionRun converted,
        ConversionRun? previous)
    {
        ArgumentNullException.ThrowIfNull(converted);
        if (previous is null)
        {
            return converted;
        }

        var reusable = previous.Artifacts
            .Where(item =>
                item.Validation.Outcome == LiveSqlValidationOutcome.Passed &&
                item.Validation.WasLiveValidated &&
                item.Validation.IsStructurallyValid)
            .GroupBy(item => item.ContentHash, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().Validation,
                StringComparer.Ordinal);
        if (reusable.Count == 0)
        {
            return converted;
        }

        return converted with
        {
            Artifacts = converted.Artifacts.Select(item =>
                    reusable.TryGetValue(item.ContentHash, out var validation)
                        ? item with { Validation = validation }
                        : item)
                .ToArray()
        };
    }
}
