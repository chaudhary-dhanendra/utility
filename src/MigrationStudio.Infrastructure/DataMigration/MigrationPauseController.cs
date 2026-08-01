using MigrationStudio.Application.DataMigration;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class MigrationPauseController : IMigrationPauseController
{
    private volatile TaskCompletionSource _resume =
        CompletedSource();

    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (IsPaused)
        {
            return;
        }

        IsPaused = true;
        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Unpause()
    {
        IsPaused = false;
        _resume.TrySetResult();
    }

    public Task WaitIfPausedAsync(CancellationToken cancellationToken) =>
        IsPaused ? _resume.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
