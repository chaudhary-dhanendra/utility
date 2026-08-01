using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Infrastructure.DataMigration;

namespace MigrationStudio.Tests.DataMigration;

public sealed class MigrationSafetyTests
{
    [Fact]
    public void DestructivePreparation_RequiresExplicitConfirmation()
    {
        var options = new DataMigrationOptions
        {
            TargetPreparation = TargetPreparationStrategy.Truncate
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
        var confirmed = options with { IsDestructiveTargetPreparationConfirmed = true };
        Assert.Same(confirmed, confirmed.Validate());
    }

    [Theory]
    [InlineData(100, 90, 1, 1, 100)]
    [InlineData(null, null, 1000, 5, 1000)]
    [InlineData(-100, -90, -1, -1, -100)]
    [InlineData(null, -50, -10, -5, -50)]
    public void IdentityRestart_RespectsIncrementDirection(
        int? source,
        int? target,
        int seed,
        int increment,
        int expected)
    {
        Assert.Equal(
            expected,
            SequenceRestartCalculator.Select(source, target, seed, increment));
    }

    [Fact]
    public void BatchBisection_NeverDropsOrDuplicatesRows()
    {
        var rows = Enumerable.Range(1, 11).ToArray();
        var (left, right) = BatchBisection.Split(rows);

        Assert.Equal(rows, left.Concat(right));
        Assert.Equal(5, left.Count);
        Assert.Equal(6, right.Count);
    }

    [Fact]
    public async Task PauseController_BlocksAndReleasesWithoutPolling()
    {
        var controller = new MigrationPauseController();
        controller.Pause();
        var wait = controller.WaitIfPausedAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        controller.Unpause();
        await wait;
        Assert.True(wait.IsCompletedSuccessfully);
    }
}
