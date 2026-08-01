namespace MigrationStudio.Infrastructure.DataMigration;

internal static class BatchBisection
{
    public static (IReadOnlyList<T> Left, IReadOnlyList<T> Right) Split<T>(
        IReadOnlyList<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count < 2)
        {
            throw new ArgumentException("At least two rows are required for bisection.", nameof(rows));
        }

        var midpoint = rows.Count / 2;
        return (rows.Take(midpoint).ToArray(), rows.Skip(midpoint).ToArray());
    }
}
