using MigrationStudio.Validation;
using System.Text.Json;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

var arguments = args.Select((value, index) => (value, index))
    .Where(item => item.value.StartsWith("--", StringComparison.Ordinal))
    .ToDictionary(
        item => item.value,
        item => item.index + 1 < args.Length && !args[item.index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[item.index + 1]
            : "true",
        StringComparer.OrdinalIgnoreCase);
if (!arguments.TryGetValue("--input", out var input) ||
    !arguments.TryGetValue("--output", out var output))
{
    Console.Error.WriteLine("Usage: PackageAnalyzer --input <conversion-run.json|manifest.json> --output <diagnostics-directory> [--manifest <manifest.json>] [--log <sanitized.jsonl>] [--expected-failed N] [--expected-blocked N]");
    return 2;
}

var options = new PackageAnalysisOptions(
    input,
    output,
    Parse(arguments, "--expected-failed"),
    Parse(arguments, "--expected-blocked"),
    arguments.GetValueOrDefault("--log"));
var report = PackageFailureAnalyzer.Analyze(options);
Console.WriteLine($"Analyzed {report.Counts.Total:N0} artifacts: Passed={report.Counts.Passed:N0}, Failed={report.Counts.Failed:N0}, Blocked={report.Counts.DependencyBlocked:N0}, NotRun={report.Counts.NotRun:N0}, Manual={report.Counts.ManualReview:N0}.");
if (arguments.TryGetValue("--manifest", out var manifestPath))
{
    await using var stream = File.OpenRead(Path.GetFullPath(manifestPath));
    var manifest = await JsonSerializer.DeserializeAsync<MigrationPackageManifest>(
        stream,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (manifest is null)
    {
        throw new InvalidDataException($"Deployment manifest could not be read: {manifestPath}");
    }
    var (graph, initialPlan) = DeploymentGraphPlanner.Build(manifest);
    var persistedBlocked = report.Artifacts
        .Where(item => item.Outcome == "BlockedByDependency" && Guid.TryParse(item.SourceObjectId, out _))
        .Select(item => new PersistedBlockedArtifact(
            new InventoryObjectId(Guid.Parse(item.SourceObjectId)),
            item.TargetObject,
            item.BlockingDependencies.Where(value => Guid.TryParse(value, out _))
                .Select(value => new InventoryObjectId(Guid.Parse(value))).ToArray()))
        .ToArray();
    var blockedAnalysis = BlockedDependencyAnalyzer.Analyze(graph, initialPlan, persistedBlocked);
    var plan = initialPlan with { PersistedBlockedArtifacts = blockedAnalysis };
    var blocked = persistedBlocked.Select(item => item.SourceObjectId).ToHashSet();
    await DeploymentGraphDiagnosticsWriter.WriteAsync(graph, plan, output, blocked);
    Console.WriteLine(
        $"Deployment plan: Nodes={graph.Nodes.Count:N0}, Edges={graph.Edges.Count:N0}, " +
        $"Cycles={plan.Statistics.CycleCount:N0}, Ordered={plan.Statistics.OrderedArtifactCount:N0}, " +
        $"Deferred={plan.Statistics.DeferredArtifactCount:N0}, " +
        $"PersistedBlocked={plan.PersistedBlockedArtifactCount:N0}, EffectiveBlocked={plan.EffectiveBlockedArtifactCount:N0}.");
}
Console.WriteLine($"Reports: {Path.GetFullPath(output)}");
return 0;

static int? Parse(IReadOnlyDictionary<string, string> values, string key) =>
    values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : null;
