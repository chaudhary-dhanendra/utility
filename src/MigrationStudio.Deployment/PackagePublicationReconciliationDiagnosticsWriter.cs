using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Deployment;

public sealed record PackagePublicationReconciliationReport(
    DateTimeOffset GeneratedAt,
    int OriginalFailedCount,
    int OriginalBlockedCount,
    int HardBlockedCount,
    int NonFatalBlockedCount,
    int RuntimeOnlyCount,
    int OptionalCount,
    int ManualReviewDependencyCount,
    int ExternalDependencyCount,
    int CascadingOrFalseBlockCount,
    int DeferredByPlanCount,
    int HardCycleCount,
    int UnresolvedInternalDependencyCount,
    bool PackagePublished,
    string PublicationReason,
    int PackagedCount,
    int ExecutableCount,
    string FinalConvertStatus,
    bool NextDeployEnabled,
    IReadOnlyList<BlockedDependencyArtifactDecision> ArtifactDecisions);

public static class PackagePublicationReconciliationDiagnosticsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync(
        BlockedDependencyReconciliation reconciliation,
        string outputDirectory,
        int packagedCount,
        int executableCount,
        string finalConvertStatus,
        bool nextDeployEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var report = new PackagePublicationReconciliationReport(
            DateTimeOffset.UtcNow,
            reconciliation.DirectValidationFailureCount,
            reconciliation.OriginalBlockedCount,
            reconciliation.HardBlockedCount,
            reconciliation.NonFatalBlockedCount,
            reconciliation.RuntimeOnlyCount,
            reconciliation.OptionalCount,
            reconciliation.ManualReviewDependencyCount,
            reconciliation.ExternalDependencyCount,
            reconciliation.CascadingOrFalseBlockCount,
            reconciliation.DeferredByPlanCount,
            reconciliation.HardCycleCount,
            reconciliation.UnresolvedInternalDependencyCount,
            reconciliation.CanPublish,
            PublicationReason(reconciliation),
            packagedCount,
            executableCount,
            finalConvertStatus,
            nextDeployEnabled,
            reconciliation.ArtifactDecisions);
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        await using (var stream = new FileStream(
                         Path.Combine(directory, "package-publication-reconciliation.json"),
                         FileMode.Create, FileAccess.Write, FileShare.None, 65536,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(
            Path.Combine(directory, "package-publication-reconciliation.md"),
            Markdown(report),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static string PublicationReason(BlockedDependencyReconciliation value) =>
        value.CanPublish
            ? "No direct validation failures, fatal hard blocks, not-run executable artifacts, hard cycles, or unresolved internal dependencies remain."
            : $"Publication blocked: failed={value.DirectValidationFailureCount}, hard-blocked={value.HardBlockedCount}, not-run={value.NotRunExecutableCount}, hard-cycles={value.HardCycleCount}, unresolved={value.UnresolvedInternalDependencyCount}.";

    private static string Markdown(PackagePublicationReconciliationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Package publication reconciliation");
        builder.AppendLine();
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("|---|---:|");
        Append(builder, "Original failed", report.OriginalFailedCount);
        Append(builder, "Original dependency-blocked", report.OriginalBlockedCount);
        Append(builder, "Fatal hard-blocked", report.HardBlockedCount);
        Append(builder, "Nonfatal/deferred blocked", report.NonFatalBlockedCount);
        Append(builder, "Runtime-only", report.RuntimeOnlyCount);
        Append(builder, "Optional", report.OptionalCount);
        Append(builder, "Manual-review dependency", report.ManualReviewDependencyCount);
        Append(builder, "External dependency", report.ExternalDependencyCount);
        Append(builder, "Cascading/false", report.CascadingOrFalseBlockCount);
        Append(builder, "Deferred by plan", report.DeferredByPlanCount);
        Append(builder, "Hard cycles", report.HardCycleCount);
        Append(builder, "Unresolved internal dependencies", report.UnresolvedInternalDependencyCount);
        Append(builder, "Packaged artifacts", report.PackagedCount);
        Append(builder, "Executable artifacts", report.ExecutableCount);
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Package published | {report.PackagePublished} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Final Convert status | {report.FinalConvertStatus} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Next/Deploy enabled | {report.NextDeployEnabled} |");
        builder.AppendLine();
        builder.AppendLine(report.PublicationReason);
        builder.AppendLine();
        builder.AppendLine("## Artifact decisions");
        builder.AppendLine();
        builder.AppendLine("| Artifact | Classification | Fatal | Blocking artifacts | Reason |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var item in report.ArtifactDecisions)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| `{Escape(item.TargetQualifiedName)}` | {item.ReconciledClassification} | {item.IsFatal} | `{Escape(string.Join(", ", item.BlockingArtifactIds))}` | {Escape(item.Reason)} |");
        }
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string name, int value) =>
        builder.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {value:N0} |");

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
