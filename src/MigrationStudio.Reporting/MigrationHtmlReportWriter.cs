using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Reporting;

namespace MigrationStudio.Reporting;

public static class MigrationHtmlReportWriter
{
    public static Task WriteAsync(
        MigrationReportDocument report,
        string path,
        CancellationToken cancellationToken)
    {
        var html = Build(report);
        return File.WriteAllTextAsync(
            path, html, new UTF8Encoding(false), cancellationToken);
    }

    public static string Build(MigrationReportDocument report)
    {
        var dark = report.Template.UseDarkDashboardTheme;
        var background = dark ? "#101827" : "#f4f7fb";
        var panel = dark ? "#172235" : "#ffffff";
        var text = dark ? "#e7edf5" : "#172033";
        var muted = dark ? "#a9b5c8" : "#5f6b7a";
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            """);
        html.Append("<title>").Append(H(report.Template.ReportTitle)).Append("</title><style>");
        html.Append(CultureInfo.InvariantCulture, $$$"""
            :root{--bg:{{{background}}};--panel:{{{panel}}};--text:{{{text}}};--muted:{{{muted}}};--brand:#2f6fed;--border:#ccd5e2;--critical:#b42318;--warning:#b54708;--ok:#067647}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI",Arial,sans-serif}
            aside{position:fixed;inset:0 auto 0 0;width:250px;background:#12213c;color:#fff;padding:22px;overflow:auto}
            aside h2{font-size:16px;margin:0 0 18px}aside a{display:block;color:#d7e3fa;text-decoration:none;padding:7px 0}
            main{margin-left:250px;padding:28px;max-width:1600px}.hero{display:flex;justify-content:space-between;gap:20px;align-items:flex-start}
            .brand{display:flex;gap:16px;align-items:flex-start}.logo{max-width:180px;max-height:80px;object-fit:contain}
            .mark{font-weight:700;color:#ffcf70}.muted{color:var(--muted)}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin:20px 0}
            .card,.section{background:var(--panel);border:1px solid var(--border);border-radius:10px;padding:16px;box-shadow:0 2px 8px #0001}
            .card strong{display:block;font-size:24px;margin-top:8px}.section{margin:16px 0}.section h2{margin-top:0}
            .chart-row{display:grid;grid-template-columns:180px 1fr 55px;gap:8px;align-items:center;margin:7px 0}.track{background:#dce5f3;border-radius:5px;height:14px}.bar{height:100%;background:var(--brand);border-radius:5px}
            .toolbar{display:flex;gap:8px;margin:8px 0;align-items:center;flex-wrap:wrap}.toolbar input{width:min(480px,100%);padding:8px;border:1px solid var(--border);border-radius:5px}.toolbar button{padding:7px 12px;border:1px solid var(--border);border-radius:5px;background:var(--panel);color:var(--text)}
            .table-wrap{overflow:auto;max-height:560px}table{border-collapse:collapse;width:100%;font-size:13px}th,td{border-bottom:1px solid var(--border);padding:8px;text-align:left;vertical-align:top}th{position:sticky;top:0;background:var(--panel);z-index:1}
            details{border:1px solid var(--border);border-radius:7px;margin:8px 0;padding:9px}summary{cursor:pointer;font-weight:600}
            pre{white-space:pre-wrap;word-break:break-word;background:#0d1524;color:#e7edf5;padding:12px;border-radius:6px;max-height:420px;overflow:auto}
            .critical{color:var(--critical);font-weight:700}.warning{color:var(--warning)}.ok{color:var(--ok)}
            @media(max-width:850px){aside{position:static;width:auto}main{margin:0}.hero{display:block}}
            @media print{aside,.toolbar{display:none}main{margin:0;padding:0}.section,.card{box-shadow:none;break-inside:avoid}}
            </style></head><body>
            """);
        AddNavigation(html);
        html.Append("<main><div class=\"hero\"><div class=\"brand\">");
        AppendLogo(html, report.Template.LogoPath);
        html.Append("<div><h1>").Append(H(report.Template.ReportTitle))
            .Append("</h1><p class=\"muted\">").Append(H(report.Template.OrganizationName))
            .Append(" - ").Append(H(report.Template.ProjectName)).Append("</p></div></div><div class=\"mark\">")
            .Append(H(report.Template.ClassificationMarking)).Append("</div></div>");
        AddCards(html, report);
        AddExecutiveSummary(html, report);
        AddCharts(html, report);
        AddBlockers(html, report);
        AddObjectReconciliation(html, report);
        AddFilterableTable(html, "inventory", "Object inventory",
            ["Type", "Source", "Included", "Classification"],
            report.Inventory.Objects.Select(item => new[]
            {
                item.ObjectType.ToString(), item.QualifiedSourceName, item.IsIncluded ? "Yes" : "No",
                item.ConversionClassification.ToString()
            }));
        AddFilterableTable(html, "findings", "Findings",
            ["Severity", "Code", "Object", "Finding"],
            (report.Conversion?.Findings ?? report.Inventory.Findings).Select(item => new[]
            {
                item.Severity.ToString(), item.Code, item.ObjectId?.ToString() ?? string.Empty, item.Message
            }));
        AddDeployment(html, report);
        AddValidation(html, report);
        AddDataReconciliation(html, report);
        AddManualReview(html, report);
        AddObjectDetails(html, report);
        html.Append("<footer class=\"muted\"><p>").Append(H(report.Template.Footer))
            .Append(" - ").Append(H(report.Summary.GeneratedAt.ToString(
                report.Template.DateTimeFormat, CultureInfo.InvariantCulture)))
            .Append("</p></footer></main>");
        html.Append("""
            <script>
            const pagedTables={};
            function renderPagedTable(id){
              const state=pagedTables[id],body=document.querySelector('#'+id+' tbody');
              if(!state||!body)return;
              const filtered=state.query.length===0?state.rows:state.rows.filter(r=>r.some(v=>String(v).toLowerCase().includes(state.query)));
              const pages=Math.max(1,Math.ceil(filtered.length/state.size));
              state.page=Math.min(state.page,pages-1);
              body.replaceChildren(...filtered.slice(state.page*state.size,(state.page+1)*state.size).map(row=>{
                const tr=document.createElement('tr');
                row.forEach(value=>{const td=document.createElement('td');td.textContent=value??'';tr.appendChild(td);});
                return tr;
              }));
              document.getElementById(id+'-status').textContent=`${filtered.length.toLocaleString()} rows · page ${state.page+1} of ${pages}`;
            }
            function initializePagedTable(id){
              const node=document.getElementById(id+'-data');
              pagedTables[id]={rows:JSON.parse(node.textContent),query:'',page:0,size:100};
              renderPagedTable(id);
            }
            function filterTable(input,id){const state=pagedTables[id];state.query=input.value.toLowerCase();state.page=0;renderPagedTable(id);}
            function movePage(id,delta){const state=pagedTables[id];state.page=Math.max(0,state.page+delta);renderPagedTable(id);}
            </script></body></html>
            """);
        return html.ToString();
    }

    private static void AppendLogo(StringBuilder html, string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath))
        {
            return;
        }
        var extension = Path.GetExtension(logoPath).ToLowerInvariant();
        var contentType = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => throw new InvalidOperationException("The validated logo format is not supported.")
        };
        var data = Convert.ToBase64String(File.ReadAllBytes(logoPath));
        html.Append("<img class=\"logo\" alt=\"Organization logo\" src=\"data:")
            .Append(contentType).Append(";base64,").Append(data).Append("\">");
    }

    private static void AddNavigation(StringBuilder html)
    {
        html.Append("""
            <aside><h2>Migration report</h2>
            <a href="#executive">Executive summary</a><a href="#charts">Charts</a>
            <a href="#blockers">Critical blockers</a><a href="#inventory">Object inventory</a>
            <a href="#findings">Findings</a><a href="#deployment">Deployment timeline</a>
            <a href="#validation">Validation scorecards</a><a href="#reconciliation">Data reconciliation</a>
            <a href="#manual">Manual review</a><a href="#objects">SQL details</a></aside>
            """);
    }

    private static void AddCards(StringBuilder html, MigrationReportDocument report)
    {
        var summary = report.Summary;
        html.Append("<div class=\"cards\">");
        Card(html, "Overall readiness", summary.OverallReadiness);
        Card(html, "Included objects", summary.IncludedObjects.ToString("N0", CultureInfo.InvariantCulture));
        Card(html, "Rows written", summary.RowsWritten.ToString("N0", CultureInfo.InvariantCulture));
        Card(html, "Critical blockers", summary.CriticalBlockers.ToString("N0", CultureInfo.InvariantCulture));
        Card(html, "Manual review", summary.ManualReviews.ToString("N0", CultureInfo.InvariantCulture));
        Card(html, "Unsupported", summary.Unsupported.ToString("N0", CultureInfo.InvariantCulture));
        html.Append("</div>");
    }

    private static void Card(StringBuilder html, string label, string value) =>
        html.Append("<div class=\"card\"><span class=\"muted\">").Append(H(label))
            .Append("</span><strong>").Append(H(value)).Append("</strong></div>");

    private static void AddExecutiveSummary(StringBuilder html, MigrationReportDocument report)
    {
        var item = report.Summary;
        html.Append("<section class=\"section\" id=\"executive\"><h2>Executive summary</h2><table><tbody>");
        SummaryRow(html, "Source", $"{item.Source.Server} / {item.Source.Database} · SQL Server {item.Source.Version} {item.Source.Edition}");
        SummaryRow(html, "Target", $"{item.Target.Server} / {item.Target.Database} · PostgreSQL {item.Target.Version}");
        SummaryRow(html, "Scope", $"{item.Scope}; schemas: {string.Join(", ", item.IncludedSchemas)}");
        SummaryRow(html, "Schema conversion", item.SchemaConversionResult);
        SummaryRow(html, "Data migration", item.DataMigrationResult);
        SummaryRow(html, "Deployment", item.DeploymentResult);
        SummaryRow(html, "Validation", item.ValidationResult);
        SummaryRow(html, "Duration", item.TotalDuration.ToString());
        SummaryRow(html, "Throughput", $"{item.RowsPerSecond:N1} rows/sec");
        html.Append("</tbody></table></section>");
    }

    private static void SummaryRow(StringBuilder html, string label, string value) =>
        html.Append("<tr><th>").Append(H(label)).Append("</th><td>").Append(H(value)).Append("</td></tr>");

    private static void AddCharts(StringBuilder html, MigrationReportDocument report)
    {
        html.Append("<section class=\"section\" id=\"charts\"><h2>Migration charts</h2>");
        Chart(html, "Objects by type", report.Inventory.Objects.GroupBy(item => item.ObjectType.ToString())
            .Select(group => (group.Key, (double)group.Count())));
        Chart(html, "Conversion classification", report.Inventory.Objects
            .GroupBy(item => item.ConversionClassification.ToString())
            .Select(group => (group.Key, (double)group.Count())));
        Chart(html, "Findings by severity", (report.Conversion?.Findings ?? report.Inventory.Findings)
            .GroupBy(item => item.Severity.ToString()).Select(group => (group.Key, (double)group.Count())));
        Chart(html, "Deployment outcomes", report.Deployment?.Objects
            .GroupBy(item => item.Status.ToString())
            .Select(group => (group.Key, (double)group.Count())) ?? []);
        Chart(html, "Validation outcomes", report.Validation?.Findings
            .GroupBy(item => item.Classification.ToString())
            .Select(group => (group.Key, (double)group.Count())) ?? []);
        if (report.DataMigration is not null)
        {
            Chart(html, "Data rows migrated by schema", report.DataMigration.Tables
                .GroupBy(item => item.Table.Split('.', 2)[0])
                .Select(group => (group.Key, (double)group.Sum(item => item.RowsWritten))));
            Chart(html, "Slowest tables (seconds)", report.DataMigration.Tables
                .OrderByDescending(item => item.TotalDuration).Take(10)
                .Select(item => (item.Table, item.TotalDuration.TotalSeconds)));
            Chart(html, "Throughput (rows per second)", report.DataMigration.Tables
                .OrderByDescending(item => item.RowsPerSecond).Take(10)
                .Select(item => (item.Table, item.RowsPerSecond)));
        }
        Chart(html, "Migration duration by phase",
        [
            ("Data migration", report.DataMigration is null
                ? 0
                : (report.DataMigration.CompletedAt - report.DataMigration.StartedAt).TotalSeconds),
            ("Deployment", report.Summary.DeploymentDuration.TotalSeconds),
            ("Validation", report.Summary.ValidationDuration.TotalSeconds)
        ]);
        Chart(html, "Manual-review status", report.ManualReviews
            .GroupBy(item => item.Status.ToString())
            .Select(group => (group.Key, (double)group.Count())));
        Chart(html, "Unsupported features by category", report.Conversion?.Artifacts
            .Where(item => item.Classification == Domain.Inventory.ConversionClassification.Unsupported ||
                           item.UnsupportedConstructs.Count > 0)
            .SelectMany(item => item.UnsupportedConstructs.Count == 0
                ? ["Unclassified"]
                : item.UnsupportedConstructs)
            .GroupBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, (double)group.Count())) ?? []);
        html.Append("</section>");
    }

    private static void Chart(StringBuilder html, string title, IEnumerable<(string Label, double Value)> values)
    {
        var items = values.OrderByDescending(item => item.Value).ToArray();
        var maximum = items.Length == 0 ? 1 : Math.Max(1, items.Max(item => item.Value));
        html.Append("<h3>").Append(H(title)).Append("</h3>");
        foreach (var item in items)
        {
            var width = Math.Clamp(item.Value * 100 / maximum, 0, 100);
            html.Append("<div class=\"chart-row\"><span>").Append(H(item.Label))
                .Append("</span><div class=\"track\"><div class=\"bar\" style=\"width:")
                .Append(width.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("%\"></div></div><strong>")
                .Append(item.Value.ToString("N0", CultureInfo.InvariantCulture)).Append("</strong></div>");
        }
    }

    private static void AddBlockers(StringBuilder html, MigrationReportDocument report)
    {
        html.Append("<section class=\"section\" id=\"blockers\"><h2>Critical blockers</h2>");
        var blockers = report.Validation?.Readiness.CriticalBlockers ?? [];
        if (blockers.Count == 0)
        {
            html.Append("<p class=\"ok\">No critical validation blockers recorded.</p>");
        }
        else
        {
            html.Append("<ul>");
            foreach (var blocker in blockers)
            {
                html.Append("<li class=\"critical\">").Append(H(blocker.SourceObject))
                    .Append(": ").Append(H(blocker.Summary)).Append("</li>");
            }
            html.Append("</ul>");
        }
        html.Append("</section>");
    }

    private static void AddDeployment(StringBuilder html, MigrationReportDocument report)
    {
        html.Append("<section class=\"section\" id=\"deployment\"><h2>Deployment timeline</h2>");
        if (report.Deployment is null)
        {
            html.Append("<p class=\"muted\">Deployment was not part of this report.</p></section>");
            return;
        }
        AddTable(html,
            ["Phase", "Object", "Status", "Commit", "Started", "Ended"],
            report.Deployment.Objects.Select(item => new[]
            {
                item.Phase.ToString(), item.TargetObject, item.Status.ToString(), item.CommitStatus.ToString(),
                item.StartedAt?.ToString("O") ?? string.Empty, item.EndedAt?.ToString("O") ?? string.Empty
            }));
        html.Append("</section>");
    }

    private static void AddValidation(StringBuilder html, MigrationReportDocument report)
    {
        html.Append("<section class=\"section\" id=\"validation\"><h2>Validation scorecards</h2>");
        if (report.Validation is null)
        {
            html.Append("<p class=\"muted\">Validation was not part of this report.</p></section>");
            return;
        }
        AddTable(html,
            ["Category", "Status", "Score", "Weight", "Passed", "Warnings", "Blockers", "Explanation"],
            report.Validation.Readiness.Categories.Select(item => new[]
            {
                item.Category.ToString(), item.Status.ToString(),
                item.Score?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A",
                item.Weight.ToString(CultureInfo.InvariantCulture),
                item.PassedChecks.ToString(CultureInfo.InvariantCulture),
                item.WarningChecks.ToString(CultureInfo.InvariantCulture),
                item.BlockerChecks.ToString(CultureInfo.InvariantCulture), item.Explanation
            }));
        html.Append("</section>");
    }

    private static void AddDataReconciliation(StringBuilder html, MigrationReportDocument report)
    {
        html.Append("<section class=\"section\" id=\"reconciliation\"><h2>Data reconciliation</h2>");
        html.Append("<p><strong>Rows read:</strong> ")
            .Append(report.Summary.RowsRead.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" &nbsp; <strong>Rows written:</strong> ")
            .Append(report.Summary.RowsWritten.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" &nbsp; <strong>Rows rejected:</strong> ")
            .Append(report.Summary.FailedRows.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" &nbsp; <strong>Balanced:</strong> ")
            .Append(report.Summary.RowsReconcile ? "Yes" : "No")
            .Append("</p>");
        if (report.Validation is null || report.Validation.DataComparisons.Count == 0)
        {
            html.Append("<p class=\"muted\">No post-migration data reconciliation results are available.</p></section>");
            return;
        }
        AddTable(html,
            ["Source", "Target", "Source rows", "Target rows", "Result", "Detail"],
            report.Validation.DataComparisons.Select(item => new[]
            {
                item.SourceTable, item.TargetTable, item.SourceRowCount.ToString(CultureInfo.InvariantCulture),
                item.TargetRowCount.ToString(CultureInfo.InvariantCulture),
                item.Classification.ToString(), item.Detail
            }));
        html.Append("</section>");
    }

    private static void AddObjectReconciliation(
        StringBuilder html,
        MigrationReportDocument report)
    {
        var summary = report.ReconciliationSummary;
        html.Append("<section class=\"section\" id=\"object-reconciliation\"><h2>Object reconciliation</h2>");
        html.Append("<p><strong>Selected source objects:</strong> ")
            .Append(summary.SelectedSourceObjects.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" &nbsp; <strong>Reconciled:</strong> ")
            .Append(summary.ReconciledTotal.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" &nbsp; <strong>Unreconciled:</strong> ")
            .Append(summary.Unreconciled.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" &nbsp; <strong>Balanced:</strong> ")
            .Append(summary.IsBalanced ? "Yes" : "No")
            .Append("</p>");
        AddTable(html,
            ["Final status", "Count"],
            Enum.GetValues<SourceObjectFinalStatus>().Select(status => new[]
            {
                status.ToString(),
                report.ObjectReconciliation.Count(item => item.Status == status)
                    .ToString("N0", CultureInfo.InvariantCulture)
            }));
        html.Append("</section>");
    }

    private static void AddManualReview(StringBuilder html, MigrationReportDocument report)
    {
        html.Append("<section class=\"section\" id=\"manual\"><h2>Manual-review checklist</h2>");
        AddTable(html,
            ["Status", "Owner", "Title", "Source", "Critical", "Resolution"],
            report.ManualReviews.Select(item => new[]
            {
                item.Status.ToString(), item.Owner ?? string.Empty, item.Title, item.Source,
                item.IsCriticalBlocker ? "Yes" : "No", item.Resolution ?? string.Empty
            }));
        html.Append("</section>");
    }

    private static void AddObjectDetails(StringBuilder html, MigrationReportDocument report)
    {
        html.Append("<section class=\"section\" id=\"objects\"><h2>Source and target SQL</h2>");
        if (report.Conversion is null)
        {
            html.Append("<p class=\"muted\">Conversion SQL is not available.</p></section>");
            return;
        }
        foreach (var artifact in report.Conversion.Artifacts)
        {
            html.Append("<details><summary>").Append(H(artifact.TargetObjectId.QualifiedName))
                .Append(" · ").Append(H(artifact.Classification.ToString())).Append("</summary>")
                .Append("<h4>SQL Server</h4><pre>").Append(H(artifact.SourceDefinition))
                .Append("</pre><h4>PostgreSQL</h4><pre>").Append(H(artifact.PostgreSqlDefinition))
                .Append("</pre></details>");
        }
        html.Append("</section>");
    }

    private static void AddFilterableTable(
        StringBuilder html,
        string id,
        string title,
        IReadOnlyList<string> headers,
        IEnumerable<string[]> rows)
    {
        var materializedRows = rows.ToArray();
        var tableId = $"{id}-table";
        html.Append("<section class=\"section\" id=\"").Append(H(id)).Append("\"><h2>")
            .Append(H(title)).Append("</h2><div class=\"toolbar\"><input type=\"search\" placeholder=\"Search ")
            .Append(H(title)).Append("\" oninput=\"filterTable(this,'").Append(H(id))
            .Append("-table')\"><button type=\"button\" onclick=\"movePage('").Append(H(tableId))
            .Append("',-1)\">Previous</button><button type=\"button\" onclick=\"movePage('")
            .Append(H(tableId)).Append("',1)\">Next</button><span id=\"").Append(H(tableId))
            .Append("-status\" class=\"muted\"></span></div><div class=\"table-wrap\"><table id=\"")
            .Append(H(tableId)).Append("\">");
        AddTableContent(html, headers, []);
        html.Append("</table></div><script type=\"application/json\" id=\"").Append(H(tableId))
            .Append("-data\">").Append(JsonSerializer.Serialize(materializedRows))
            .Append("</script><script>initializePagedTable('").Append(H(tableId))
            .Append("');</script></section>");
    }

    private static void AddTable(
        StringBuilder html,
        IReadOnlyList<string> headers,
        IEnumerable<string[]> rows)
    {
        html.Append("<div class=\"table-wrap\"><table>");
        AddTableContent(html, headers, rows);
        html.Append("</table></div>");
    }

    private static void AddTableContent(
        StringBuilder html,
        IReadOnlyList<string> headers,
        IEnumerable<string[]> rows)
    {
        html.Append("<thead><tr>");
        foreach (var header in headers)
        {
            html.Append("<th>").Append(H(header)).Append("</th>");
        }
        html.Append("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var value in row)
            {
                html.Append("<td>").Append(H(value)).Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</tbody>");
    }

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
