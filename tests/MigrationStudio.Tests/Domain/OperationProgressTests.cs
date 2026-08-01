using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Tests.Domain;

public sealed class OperationProgressTests
{
    [Fact]
    public void Constructor_AcceptsValidProgress()
    {
        var progress = new OperationProgress(25, "Reading metadata", 25, 100);

        Assert.Equal(25, progress.Percentage);
        Assert.Equal("Reading metadata", progress.Message);
        Assert.Equal(25, progress.CompletedUnits);
        Assert.Equal(100, progress.TotalUnits);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    public void Constructor_RejectsInvalidPercentage(double percentage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OperationProgress(percentage, "Working"));
    }

    [Fact]
    public void Constructor_RejectsCompletedUnitsGreaterThanTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OperationProgress(50, "Working", 11, 10));
    }
}
