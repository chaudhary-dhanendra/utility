using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MigrationStudio.ScaleTests;

#pragma warning disable CA1305 // All formatted values below use explicit invariant formatting where data is locale-sensitive.

public static class ScaleArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync(
        ScaleTestReport report,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "scale-test-report.json"),
            json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "scale-test-summary.md"),
            Markdown(report), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "scale-test-report.html"),
            Html(report), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static string Markdown(ScaleTestReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Scale test summary")
            .AppendLine()
            .AppendLine($"Generated: {report.GeneratedAt:O}")
            .AppendLine($"Fixture: {report.Fixture}")
            .AppendLine($"Machine: {report.Machine.Cpu}; {report.Machine.LogicalProcessors} logical processors; " +
                        $"{Bytes(report.Machine.PhysicalMemoryBytes)} RAM; {report.Machine.OperatingSystem}")
            .AppendLine()
            .AppendLine("| Test | Status | Duration | Peak managed | Peak working set | Detail |")
            .AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (var item in report.Measurements)
        {
            builder.Append("| ").Append(item.Name.Replace("|", "\\|", StringComparison.Ordinal))
                .Append(" | ").Append(item.Status)
                .Append(" | ").Append(item.DurationMilliseconds.ToString("N0", CultureInfo.InvariantCulture)).Append(" ms")
                .Append(" | ").Append(Bytes(item.PeakManagedMemoryBytes))
                .Append(" | ").Append(Bytes(item.PeakWorkingSetBytes))
                .Append(" | ").Append(item.Detail.Replace("|", "\\|", StringComparison.Ordinal)
                    .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal))
                .AppendLine(" |");
        }

        builder.AppendLine()
            .AppendLine("## Infrastructure")
            .AppendLine();
        foreach (var (name, value) in report.Infrastructure)
        {
            builder.Append("- ").Append(name).Append(": ").AppendLine(value);
        }
        builder.AppendLine()
            .AppendLine($"Release gate passed: **{report.ReleaseGatePassed}**")
            .AppendLine()
            .AppendLine("Skipped tests are not counted as passes. A skipped release-gate item keeps workload validation incomplete.");
        return builder.ToString();
    }

    private static string Html(ScaleTestReport report)
    {
        var builder = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Migration Studio scale-test report</title><style>
            body{font:14px/1.45 Segoe UI,Arial,sans-serif;margin:32px;color:#172033;background:#f4f7fb}
            .card{background:#fff;border:1px solid #ccd5e2;border-radius:8px;padding:16px;margin:12px 0;overflow:auto}
            table{border-collapse:collapse;width:100%}th,td{padding:8px;border-bottom:1px solid #d8e0ea;text-align:left}
            .Passed{color:#067647}.Failed{color:#b42318}.Skipped,.NotReproducible{color:#b54708}
            </style></head><body><h1>Scale-test report</h1>
            """);
        builder.Append("<div class=\"card\"><strong>Fixture:</strong> ").Append(H(report.Fixture))
            .Append("<br><strong>Generated:</strong> ").Append(H(report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)))
            .Append("<br><strong>CPU:</strong> ").Append(H(report.Machine.Cpu))
            .Append("<br><strong>RAM:</strong> ").Append(H(Bytes(report.Machine.PhysicalMemoryBytes)))
            .Append("<br><strong>OS:</strong> ").Append(H(report.Machine.OperatingSystem))
            .Append("</div><div class=\"card\"><table><thead><tr><th>Test</th><th>Status</th><th>Duration</th><th>Peak managed</th><th>Peak working set</th><th>GC 0/1/2</th><th>Detail</th></tr></thead><tbody>");
        foreach (var item in report.Measurements)
        {
            builder.Append("<tr><td>").Append(H(item.Name)).Append("</td><td class=\"")
                .Append(item.Status).Append("\">").Append(item.Status)
                .Append("</td><td>").Append(item.DurationMilliseconds.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" ms</td><td>").Append(Bytes(item.PeakManagedMemoryBytes))
                .Append("</td><td>").Append(Bytes(item.PeakWorkingSetBytes))
                .Append("</td><td>").Append(CultureInfo.InvariantCulture, $"{item.Gen0Collections}/{item.Gen1Collections}/{item.Gen2Collections}")
                .Append("</td><td>").Append(H(item.Detail)).Append("</td></tr>");
        }
        builder.Append("</tbody></table></div><div class=\"card\"><h2>Infrastructure</h2><ul>");
        foreach (var (name, value) in report.Infrastructure)
        {
            builder.Append("<li><strong>").Append(H(name)).Append(":</strong> ").Append(H(value)).Append("</li>");
        }
        builder.Append("</ul></div><p>Release gate passed: <strong>").Append(report.ReleaseGatePassed)
            .Append("</strong>. Skipped tests are not passes.</p></body></html>");
        return builder.ToString();
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);

    private static string Bytes(long value) =>
        value <= 0 ? "Not detected" : $"{value / 1024d / 1024d:N1} MiB";
}
