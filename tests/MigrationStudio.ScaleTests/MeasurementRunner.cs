using System.Diagnostics;

namespace MigrationStudio.ScaleTests;

public static class MeasurementRunner
{
    public static async Task<ScaleMeasurement> RunAsync(
        string name,
        string category,
        Func<CancellationToken, Task<(string Detail, IReadOnlyDictionary<string, string> Values)>> action,
        CancellationToken cancellationToken)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var beforeAllocated = GC.GetTotalAllocatedBytes(true);
        var beforeCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var peakManaged = GC.GetTotalMemory(false);
        using var process = Process.GetCurrentProcess();
        var peakWorkingSet = process.WorkingSet64;
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var samplerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampler = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (await timer.WaitForNextTickAsync(samplerCancellation.Token).ConfigureAwait(false))
            {
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            }
        }, CancellationToken.None);

        ScaleTestStatus status;
        string detail;
        IReadOnlyDictionary<string, string> values;
        try
        {
            (detail, values) = await action(cancellationToken).ConfigureAwait(false);
            status = ScaleTestStatus.Passed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            status = ScaleTestStatus.Failed;
            detail = $"{exception.GetType().Name}: {exception.Message}";
            values = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        finally
        {
            stopwatch.Stop();
            await samplerCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await sampler.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when measurement completes.
            }
        }

        peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
        process.Refresh();
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        return new ScaleMeasurement
        {
            Name = name,
            Category = category,
            Status = status,
            StartedAt = started,
            DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            PeakManagedMemoryBytes = peakManaged,
            PeakWorkingSetBytes = peakWorkingSet,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - beforeAllocated,
            Gen0Collections = GC.CollectionCount(0) - beforeCollections[0],
            Gen1Collections = GC.CollectionCount(1) - beforeCollections[1],
            Gen2Collections = GC.CollectionCount(2) - beforeCollections[2],
            Detail = detail,
            Values = values
        };
    }

    public static ScaleMeasurement Skipped(string name, string detail) => new()
    {
        Name = name,
        Category = "Release gate",
        Status = ScaleTestStatus.Skipped,
        StartedAt = DateTimeOffset.UtcNow,
        DurationMilliseconds = 0,
        PeakManagedMemoryBytes = GC.GetTotalMemory(false),
        PeakWorkingSetBytes = Environment.WorkingSet,
        AllocatedBytes = 0,
        Gen0Collections = 0,
        Gen1Collections = 0,
        Gen2Collections = 0,
        Detail = detail
    };
}
