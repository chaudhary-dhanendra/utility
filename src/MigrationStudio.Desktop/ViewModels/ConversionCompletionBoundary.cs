namespace MigrationStudio.Desktop.ViewModels;

internal static class ConversionCompletionBoundary
{
    public static IReadOnlyList<Exception> Execute(
        Action preserveResult,
        Action presentResult)
    {
        ArgumentNullException.ThrowIfNull(preserveResult);
        ArgumentNullException.ThrowIfNull(presentResult);

        var failures = new List<Exception>(2);
        ExecuteIsolated(preserveResult, failures);
        ExecuteIsolated(presentResult, failures);
        return failures;
    }

    private static void ExecuteIsolated(
        Action action,
        List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
