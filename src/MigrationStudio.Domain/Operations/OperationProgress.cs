namespace MigrationStudio.Domain.Operations;

public sealed record OperationProgress
{
    public OperationProgress(
        double percentage,
        string message,
        long? completedUnits = null,
        long? totalUnits = null,
        Conversion.ConversionProgressSnapshot? conversion = null)
    {
        if (double.IsNaN(percentage) || percentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage), "Percentage must be between 0 and 100.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A progress message is required.", nameof(message));
        }

        if (completedUnits < 0 || totalUnits < 0 || completedUnits > totalUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(completedUnits), "Completed and total units must describe a valid range.");
        }

        Percentage = percentage;
        Message = message.Trim();
        CompletedUnits = completedUnits;
        TotalUnits = totalUnits;
        Conversion = conversion;
    }

    public double Percentage { get; }

    public string Message { get; }

    public long? CompletedUnits { get; }

    public long? TotalUnits { get; }

    public Conversion.ConversionProgressSnapshot? Conversion { get; }
}
