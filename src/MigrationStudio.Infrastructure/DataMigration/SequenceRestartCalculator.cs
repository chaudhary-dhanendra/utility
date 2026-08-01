namespace MigrationStudio.Infrastructure.DataMigration;

internal static class SequenceRestartCalculator
{
    public static decimal Select(
        decimal? sourceBoundary,
        decimal? targetBoundary,
        decimal seed,
        decimal increment) =>
        sourceBoundary is null && targetBoundary is null
            ? seed
            : increment >= 0
                ? Math.Max(sourceBoundary ?? seed, targetBoundary ?? seed)
                : Math.Min(sourceBoundary ?? seed, targetBoundary ?? seed);
}
