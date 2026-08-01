using System.Text.Json.Serialization;

namespace MigrationStudio.ScaleTests;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScaleTestStatus
{
    Passed,
    Failed,
    Skipped,
    NotReproducible
}

public sealed record ScaleMeasurement
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required ScaleTestStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required double DurationMilliseconds { get; init; }
    public required long PeakManagedMemoryBytes { get; init; }
    public required long PeakWorkingSetBytes { get; init; }
    public required long AllocatedBytes { get; init; }
    public required int Gen0Collections { get; init; }
    public required int Gen1Collections { get; init; }
    public required int Gen2Collections { get; init; }
    public required string Detail { get; init; }
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record ScaleMachine(
    string MachineName,
    string Cpu,
    int LogicalProcessors,
    long PhysicalMemoryBytes,
    string Disk,
    string OperatingSystem,
    string Framework,
    string ProcessArchitecture);

public sealed record ScaleTestReport
{
    public int FormatVersion { get; init; } = 1;
    public required DateTimeOffset GeneratedAt { get; init; }
    public required ScaleMachine Machine { get; init; }
    public required string Fixture { get; init; }
    public required IReadOnlyList<ScaleMeasurement> Measurements { get; init; }
    public required IReadOnlyDictionary<string, string> Infrastructure { get; init; }
    public bool ReleaseGatePassed => Measurements
        .Where(item => item.Category == "Release gate")
        .All(item => item.Status == ScaleTestStatus.Passed);
}
