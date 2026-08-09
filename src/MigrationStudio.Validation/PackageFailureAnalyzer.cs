using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MigrationStudio.Validation;

public sealed record PackageAnalysisOptions(
    string InputPath,
    string OutputDirectory,
    int? ComparisonFailed = null,
    int? ComparisonBlocked = null,
    string? SanitizedLogPath = null);

public sealed record PackageAnalysisCounts(
    int Total,
    int Passed,
    int Failed,
    int DependencyBlocked,
    int NotRun,
    int ManualReview,
    int RootFailures,
    int CascadingDependencyFailures);

public sealed record PackageRootCauseGroup(
    string RootCauseId,
    string SqlState,
    string NormalizedMessage,
    IReadOnlyList<string> AffectedRootObjects,
    int BlockedDependentCount,
    IReadOnlyList<string> SourceObjectTypes,
    string LikelyConverterSubsystem,
    string RepresentativeSanitizedGeneratedSqlFragment,
    string RecommendedNextImplementationPrompt);

public sealed record PackageArtifactDiagnostic(
    string SourceObjectId,
    string TargetObject,
    string Outcome,
    bool RequiresManualReview,
    bool IsRootFailure,
    bool IsCascadingFailure,
    string SqlState,
    string NormalizedMessage,
    string RuleId,
    string ObjectType,
    string DeploymentPhase,
    IReadOnlyList<string> SourceConstructs,
    IReadOnlyList<string> ResidualGeneratedSqlPatterns,
    IReadOnlyList<string> AttributedRootCauseIds,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> BlockingDependencies);

public sealed record PackageFailureBaseline(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string InputPath,
    string? SanitizedLogPath,
    PackageAnalysisCounts Counts,
    IReadOnlyDictionary<string, int> SqlStateGroups,
    IReadOnlyDictionary<string, int> RuleIds,
    IReadOnlyDictionary<string, int> ObjectTypes,
    IReadOnlyDictionary<string, int> DeploymentPhases,
    IReadOnlyDictionary<string, int> SourceConstructs,
    IReadOnlyDictionary<string, int> RepeatedGeneratedSqlPatterns,
    IReadOnlyList<PackageRootCauseGroup> RootCauseGroups,
    IReadOnlyList<PackageArtifactDiagnostic> Artifacts);

public static class PackageFailureAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static PackageFailureBaseline Analyze(PackageAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        var inputPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Conversion run or package manifest was not found.", inputPath);
        }

        var artifacts = new List<ArtifactState>();
        foreach (var document in StreamRootArrayObjects(inputPath, "Artifacts"))
        {
            using (document)
            {
                artifacts.Add(ParseArtifact(document.RootElement));
            }
        }
        if (artifacts.Count == 0)
        {
            throw new InvalidDataException("The input contains no root Artifacts array entries.");
        }

        var byId = artifacts.ToDictionary(item => item.SourceObjectId, StringComparer.OrdinalIgnoreCase);
        var failed = artifacts.Where(item => item.Outcome == "Failed").ToArray();
        var groups = failed.GroupBy(
                item => $"{item.SqlState}|{item.NormalizedMessage}",
                StringComparer.Ordinal)
            .ToDictionary(
                group => RootCauseId(group.Key),
                group => new RootGroupBuilder(group.Key, group.ToArray()),
                StringComparer.Ordinal);
        var rootGroupByArtifact = groups.Values.SelectMany(group => group.Roots.Select(root =>
                new KeyValuePair<string, string>(root.SourceObjectId, group.RootCauseId)))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var blocked in artifacts.Where(item => item.Outcome == "BlockedByDependency"))
        {
            var rootIds = ResolveRootFailures(blocked, byId);
            foreach (var rootId in rootIds)
            {
                if (rootGroupByArtifact.TryGetValue(rootId, out var groupId))
                {
                    blocked.AttributedRootCauseIds.Add(groupId);
                    groups[groupId].BlockedDependents.Add(blocked.SourceObjectId);
                }
            }
        }

        var diagnostics = artifacts.Select(item => new PackageArtifactDiagnostic(
            item.SourceObjectId,
            item.TargetObject,
            item.Outcome,
            item.RequiresManualReview,
            item.Outcome == "Failed",
            item.Outcome == "BlockedByDependency",
            item.SqlState,
            item.NormalizedMessage,
            item.RuleId,
            item.ObjectType,
            item.DeploymentPhase,
            item.SourceConstructs,
            item.ResidualPatterns,
            item.AttributedRootCauseIds.Order(StringComparer.Ordinal).ToArray(),
            item.Dependencies,
            item.BlockingDependencies)).ToArray();
        var rootCauseGroups = groups.Values
            .OrderByDescending(item => item.Roots.Length)
            .ThenBy(item => item.RootCauseId, StringComparer.Ordinal)
            .Select(item => item.ToReport()).ToArray();
        var counts = new PackageAnalysisCounts(
            artifacts.Count,
            artifacts.Count(item => item.Outcome == "Passed"),
            failed.Length,
            artifacts.Count(item => item.Outcome == "BlockedByDependency"),
            artifacts.Count(item => item.Outcome == "NotRun" && !item.RequiresManualReview),
            artifacts.Count(item => item.RequiresManualReview || item.Outcome == "Manual"),
            failed.Length,
            artifacts.Count(item => item.Outcome == "BlockedByDependency"));
        var baseline = new PackageFailureBaseline(
            "1.0",
            DateTimeOffset.UtcNow,
            inputPath,
            options.SanitizedLogPath is null ? null : Path.GetFullPath(options.SanitizedLogPath),
            counts,
            CountBy(failed, item => Empty(item.SqlState, "(none)")),
            CountBy(artifacts, item => Empty(item.RuleId, "(none)")),
            CountBy(artifacts, item => Empty(item.ObjectType, "(unknown)")),
            CountBy(artifacts, item => Empty(item.DeploymentPhase, "(unknown)")),
            CountMany(artifacts, item => item.SourceConstructs),
            CountMany(artifacts, item => item.ResidualPatterns),
            rootCauseGroups,
            diagnostics);
        WriteReports(options, baseline);
        return baseline;
    }

    private static ArtifactState ParseArtifact(JsonElement artifact)
    {
        var sourceId = Identifier(artifact, "SourceObjectId");
        var isManifestArtifact = !artifact.TryGetProperty("TargetObjectId", out var target);
        var objectType = isManifestArtifact
            ? Text(artifact, "TargetObjectType")
            : Text(target, "ObjectType");
        var targetSchema = isManifestArtifact
            ? Text(artifact, "TargetSchema")
            : Text(target, "Schema");
        var targetName = isManifestArtifact
            ? Text(artifact, "TargetName")
            : Text(target, "Name");
        var targetObject = string.IsNullOrWhiteSpace(targetSchema)
            ? targetName
            : $"{targetSchema}.{targetName}";
        var validation = artifact.GetProperty(isManifestArtifact ? "LiveValidation" : "Validation");
        var outcome = Outcome(validation.GetProperty("Outcome"));
        var sqlState = Text(validation, "SqlState");
        var message = Text(validation, "Message");
        var generatedSql = Text(artifact, isManifestArtifact ? "Sql" : "PostgreSqlDefinition");
        var sourceSql = isManifestArtifact ? string.Empty : Text(artifact, "SourceDefinition");
        var residuals = ResidualSqlServerSyntaxScanner.Scan(generatedSql);
        var sourceConstructs = ResidualSqlServerSyntaxScanner.Scan(sourceSql)
            .Select(item => item.Construct).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new ArtifactState
        {
            SourceObjectId = sourceId,
            TargetObject = targetObject,
            ObjectType = objectType,
            Outcome = outcome,
            SqlState = sqlState,
            NormalizedMessage = NormalizeMessage(message),
            RawMessage = message,
            RuleId = isManifestArtifact ? "(manifest)" : Text(artifact, "RuleId"),
            DeploymentPhase = EnumText(
                artifact.GetProperty(isManifestArtifact ? "Phase" : "DeploymentPhase"),
                DeploymentPhaseNames),
            RequiresManualReview = Boolean(artifact, "RequiresManualReview"),
            Dependencies = Identifiers(artifact, "Dependencies"),
            BlockingDependencies = Identifiers(validation, "BlockingDependencies"),
            SourceConstructs = sourceConstructs,
            ResidualPatterns = residuals.Select(item => $"{item.RuleId}:{item.Construct}")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            RepresentativeFragment = RepresentativeFragment(
                generatedSql,
                residuals.Count > 0 ? residuals[0].Offset : null),
            AttributedRootCauseIds = new HashSet<string>(StringComparer.Ordinal)
        };
    }

    private static HashSet<string> ResolveRootFailures(
        ArtifactState start,
        IReadOnlyDictionary<string, ArtifactState> byId)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(start.BlockingDependencies.Concat(start.Dependencies));
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!visited.Add(id) || !byId.TryGetValue(id, out var dependency))
            {
                continue;
            }
            if (dependency.Outcome == "Failed")
            {
                roots.Add(id);
                continue;
            }
            if (dependency.Outcome == "BlockedByDependency")
            {
                foreach (var parent in dependency.BlockingDependencies.Concat(dependency.Dependencies))
                {
                    pending.Push(parent);
                }
            }
        }
        return roots;
    }

    private static void WriteReports(PackageAnalysisOptions options, PackageFailureBaseline baseline)
    {
        var output = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(output);
        File.WriteAllText(
            Path.Combine(output, "failure-baseline.json"),
            JsonSerializer.Serialize(baseline, JsonOptions),
            new UTF8Encoding(false));
        WriteCsv(Path.Combine(output, "failure-baseline.csv"), baseline.Artifacts);
        File.WriteAllText(
            Path.Combine(output, "failure-baseline.md"),
            BaselineMarkdown(baseline),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(output, "regression-delta.md"),
            DeltaMarkdown(baseline, options.ComparisonFailed, options.ComparisonBlocked),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(output, "conversion-architecture.md"),
            ArchitectureMarkdown(baseline),
            new UTF8Encoding(false));
    }

    private static void WriteCsv(string path, IReadOnlyList<PackageArtifactDiagnostic> artifacts)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("SourceObjectId,TargetObject,Outcome,ManualReview,RootFailure,CascadingFailure,SQLSTATE,NormalizedMessage,RuleId,ObjectType,DeploymentPhase,SourceConstructs,ResidualGeneratedSqlPatterns,RootCauseIds");
        foreach (var item in artifacts)
        {
            writer.WriteLine(string.Join(',', new[]
            {
                item.SourceObjectId, item.TargetObject, item.Outcome,
                item.RequiresManualReview.ToString(CultureInfo.InvariantCulture),
                item.IsRootFailure.ToString(CultureInfo.InvariantCulture),
                item.IsCascadingFailure.ToString(CultureInfo.InvariantCulture),
                item.SqlState, item.NormalizedMessage, item.RuleId, item.ObjectType, item.DeploymentPhase,
                string.Join(';', item.SourceConstructs), string.Join(';', item.ResidualGeneratedSqlPatterns),
                string.Join(';', item.AttributedRootCauseIds)
            }.Select(Csv)));
        }
    }

    private static string BaselineMarkdown(PackageFailureBaseline report)
    {
        var builder = new StringBuilder()
            .AppendLine("# Conversion failure baseline")
            .AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Generated: `{report.GeneratedAt:O}`  ");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Input: `{report.InputPath}`");
        builder.AppendLine()
            .AppendLine("## Outcome counts")
            .AppendLine()
            .AppendLine("| Outcome | Count |")
            .AppendLine("|---|---:|");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Total artifacts | {report.Counts.Total:N0} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Passed | {report.Counts.Passed:N0} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Direct/root validation failures | {report.Counts.Failed:N0} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Dependency-blocked/cascading failures | {report.Counts.DependencyBlocked:N0} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Not run | {report.Counts.NotRun:N0} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Manual review | {report.Counts.ManualReview:N0} |");
        builder.AppendLine()
            .AppendLine("## Root-cause groups")
            .AppendLine()
            .AppendLine("| Root cause | SQLSTATE | Roots | Blocked dependents | Object types | Normalized message | Subsystem |")
            .AppendLine("|---|---|---:|---:|---|---|---|");
        foreach (var group in report.RootCauseGroups)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {Md(group.RootCauseId)} | {Md(group.SqlState)} | {group.AffectedRootObjects.Count:N0} | {group.BlockedDependentCount:N0} | {Md(string.Join(", ", group.SourceObjectTypes))} | {Md(group.NormalizedMessage)} | {Md(group.LikelyConverterSubsystem)} |");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Representative `{group.RootCauseId}` SQL: `{Md(group.RepresentativeSanitizedGeneratedSqlFragment)}`");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Recommended prompt: {group.RecommendedNextImplementationPrompt}");
            builder.AppendLine();
        }
        AppendCounts(builder, "SQLSTATE groups", report.SqlStateGroups);
        AppendCounts(builder, "Rule IDs", report.RuleIds);
        AppendCounts(builder, "Object types", report.ObjectTypes);
        AppendCounts(builder, "Deployment phases", report.DeploymentPhases);
        AppendCounts(builder, "Source constructs", report.SourceConstructs);
        AppendCounts(builder, "Residual generated-SQL patterns", report.RepeatedGeneratedSqlPatterns);
        return builder.ToString();
    }

    private static string DeltaMarkdown(PackageFailureBaseline report, int? oldFailed, int? oldBlocked) =>
        $"""
        # Regression delta

        Comparison source: sanitized validation checkpoint supplied to the analyzer.

        | Metric | Comparison | Current | Delta |
        |---|---:|---:|---:|
        | Direct failures | {Format(oldFailed)} | {report.Counts.Failed:N0} | {Delta(report.Counts.Failed, oldFailed)} |
        | Dependency blocked | {Format(oldBlocked)} | {report.Counts.DependencyBlocked:N0} | {Delta(report.Counts.DependencyBlocked, oldBlocked)} |

        The comparison is diagnostic only. A zero delta means this report reproduces the persisted checkpoint; it does not mean the failures are fixed.
        """;

    private static string ArchitectureMarkdown(PackageFailureBaseline report) =>
        $"""
        # Conversion and validation architecture

        ## Existing production flow

        1. SQL Server discovery builds the inventory and dependency graph.
        2. `ConversionEngine` creates the central identifier map before invoking existing object converters.
        3. Converters emit `ConversionArtifact` records containing source/target SQL, rule ID, dependencies, deployment phase, findings and classification.
        4. `GeneratedSqlValidator` performs offline structural validation.
        5. Live PostgreSQL validation executes deployable artifacts in dependency order and records `Passed`, `Failed`, `BlockedByDependency`, `Manual`, `Unsupported` or `NotRun` outcomes.
        6. `MigrationPackageWriter` writes dependency-ordered scripts, reports and a hashed manifest.

        ## Prompt 1 analysis flow

        `PackageAnalyzer` streams the persisted root `Artifacts` array from `{report.InputPath}`. It does not rerun conversion or PostgreSQL validation. Each artifact is classified, scanned for residual SQL Server syntax, and reduced to a bounded diagnostic record. Direct failures are grouped by SQLSTATE plus normalized provider message. Blocked artifacts are traced through `BlockingDependencies` and hard artifact dependencies to the failed roots, keeping the {report.Counts.Failed:N0} direct failures separate from the {report.Counts.DependencyBlocked:N0} cascading failures.

        ## Reproduction

        ```powershell
        dotnet run --project tools/PackageAnalyzer/PackageAnalyzer.csproj -- --input "{report.InputPath}" --output diagnostics --expected-failed {report.Counts.Failed} --expected-blocked {report.Counts.DependencyBlocked}
        ```

        Sensitive SQL literals and comments are masked in representative fragments. Full generated SQL is not copied into diagnostics.
        """;

    private static IEnumerable<JsonDocument> StreamRootArrayObjects(string path, string propertyName)
    {
        using var file = File.OpenRead(path);
        using var stream = new BufferedStream(file, 1 << 20);
        var marker = Encoding.UTF8.GetBytes($"\"{propertyName}\"");
        var matched = 0;
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                throw new InvalidDataException($"Root property '{propertyName}' was not found.");
            }
            matched = value == marker[matched] ? matched + 1 : value == marker[0] ? 1 : 0;
            if (matched == marker.Length)
            {
                break;
            }
        }
        while (stream.ReadByte() is var value && value >= 0 && value != '[') { }

        while (true)
        {
            var value = stream.ReadByte();
            while (value >= 0 && (char.IsWhiteSpace((char)value) || value == ','))
            {
                value = stream.ReadByte();
            }
            if (value is < 0 or ']')
            {
                yield break;
            }
            if (value != '{')
            {
                throw new InvalidDataException($"Expected an object in '{propertyName}', found byte {value}.");
            }
            using var buffer = new MemoryStream();
            buffer.WriteByte((byte)value);
            var depth = 1;
            var inString = false;
            var escaped = false;
            while (depth > 0)
            {
                value = stream.ReadByte();
                if (value < 0)
                {
                    throw new EndOfStreamException($"Unexpected end of file inside '{propertyName}' object.");
                }
                buffer.WriteByte((byte)value);
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == '"') inString = false;
                }
                else if (value == '"') inString = true;
                else if (value == '{') depth++;
                else if (value == '}') depth--;
            }
            yield return JsonDocument.Parse(buffer.ToArray());
        }
    }

    private static string Identifier(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) ? Identifier(value) : string.Empty;

    private static string Identifier(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("Value", out var id)
                ? id.GetString() ?? string.Empty
                : string.Empty;

    private static string[] Identifiers(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(Identifier).Where(item => item.Length > 0).ToArray()
            : [];

    private static string Text(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString()
            : string.Empty;

    private static bool Boolean(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Outcome(JsonElement value) => EnumText(value,
        ["NotRun", "Passed", "Failed", "BlockedByDependency", "Manual", "Unsupported", "Cancelled"]);

    private static string EnumText(JsonElement value, string[] names) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.TryGetInt32(out var number) && number >= 0 && number < names.Length
                ? names[number]
                : value.ToString();

    private static readonly string[] DeploymentPhaseNames =
    [
        "PreDeployment", "Schemas", "Types", "Tables", "PreDataFunctions", "DefaultsAndGeneratedColumns",
        "PrimaryKeys", "UniqueConstraints", "CheckConstraints", "Sequences", "ForeignKeys", "Indexes",
        "Functions", "Procedures", "Views", "Triggers", "Security", "Comments", "PostDeployment",
        "ManualReview"
    ];

    private static string NormalizeMessage(string message)
    {
        var normalized = message.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[0-9a-f]{8}-[0-9a-f-]{27,}", "<guid>", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"""(?<token>(?:""""|[^""])*)""",
            match => PreserveSyntaxToken(match.Groups["token"].Value),
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"'(?:''|[^'])*'", "<literal>", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\b\d+\b", "<n>", RegexOptions.CultureInvariant);
        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string PreserveSyntaxToken(string token)
    {
        var normalized = token.Trim().ToUpperInvariant();
        return normalized is "ON" or "THEN" or ":=" or "END" or "RAISE" or "NOTICE" or
            "IF" or "ELSE" or "LOOP" or "EXEC" or "EXECUTE" or ")" or ";"
            ? normalized.ToLowerInvariant()
            : "<identifier>";
    }

    private static string RepresentativeFragment(string sql, int? offset)
    {
        var masked = ResidualSqlServerSyntaxScanner.MaskCommentsAndStringLiterals(sql);
        var start = Math.Max(0, (offset ?? 0) - 100);
        var length = Math.Min(500, masked.Length - start);
        if (length <= 0) return string.Empty;
        var value = Regex.Replace(masked.Substring(start, length), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return value.Length <= 400 ? value : value[..400];
    }

    private static string RootCauseId(string key) =>
        "RC-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];

    private static string Subsystem(ArtifactState item)
    {
        var text = $"{item.SqlState} {item.NormalizedMessage} {string.Join(' ', item.ResidualPatterns)}";
        if (text.Contains("timestamp", StringComparison.OrdinalIgnoreCase) || text.Contains("bigint", StringComparison.OrdinalIgnoreCase) || text.Contains("invalid input syntax", StringComparison.OrdinalIgnoreCase)) return "type/default expression conversion";
        if (text.Contains("round(double precision", StringComparison.OrdinalIgnoreCase) || text.Contains("function", StringComparison.OrdinalIgnoreCase) && item.SqlState == "42883") return "expression and function mapping";
        if (text.Contains("then", StringComparison.OrdinalIgnoreCase) || text.Contains("end", StringComparison.OrdinalIgnoreCase)) return "procedural control-flow emission";
        if (text.Contains("raise notice", StringComparison.OrdinalIgnoreCase)) return "diagnostic statement emission";
        if (text.Contains(":=", StringComparison.OrdinalIgnoreCase) || text.Contains("@variable", StringComparison.OrdinalIgnoreCase)) return "local-variable and assignment emission";
        if (text.Contains("exec", StringComparison.OrdinalIgnoreCase)) return "dynamic SQL emission";
        if (text.Contains("near on", StringComparison.OrdinalIgnoreCase) || text.Contains("nocount", StringComparison.OrdinalIgnoreCase)) return "procedure/session statement emission";
        if (item.ResidualPatterns.Any(pattern => pattern.Contains("DATE", StringComparison.OrdinalIgnoreCase) || pattern.Contains("GETDATE", StringComparison.OrdinalIgnoreCase))) return "temporal expression translation";
        return item.ObjectType switch
        {
            "StoredProcedure" or "Function" or "Trigger" => "programmable-object conversion",
            "Table" or "Column" => "table/default conversion",
            _ => "generated SQL emission"
        };
    }

    private static string Prompt(ArtifactState item) =>
        $"Prompt {item.SqlState}/{Subsystem(item)}: add a focused fixture for normalized error '{item.NormalizedMessage}', correct only the responsible emitter, and prove the root artifact deploys before revalidating its blocked dependents.";

    private static Dictionary<string, int> CountBy(
        IEnumerable<ArtifactState> artifacts,
        Func<ArtifactState, string> selector) =>
        artifacts.GroupBy(selector, StringComparer.Ordinal).OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> CountMany(
        IEnumerable<ArtifactState> artifacts,
        Func<ArtifactState, IReadOnlyList<string>> selector) =>
        artifacts.SelectMany(item => selector(item).Distinct(StringComparer.Ordinal))
            .GroupBy(item => item, StringComparer.Ordinal).OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static void AppendCounts(StringBuilder builder, string title, IReadOnlyDictionary<string, int> values)
    {
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"## {title}");
        builder.AppendLine().AppendLine("| Value | Artifacts |").AppendLine("|---|---:|");
        foreach (var item in values)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {Md(item.Key)} | {item.Value:N0} |");
        }
    }

    private static string Empty(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);
    private static string Format(int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "n/a";
    private static string Delta(int current, int? old) => old is null ? "n/a" : (current - old.Value).ToString("+0;-0;0", CultureInfo.InvariantCulture);

    private sealed class ArtifactState
    {
        public required string SourceObjectId { get; init; }
        public required string TargetObject { get; init; }
        public required string ObjectType { get; init; }
        public required string Outcome { get; init; }
        public required string SqlState { get; init; }
        public required string NormalizedMessage { get; init; }
        public required string RawMessage { get; init; }
        public required string RuleId { get; init; }
        public required string DeploymentPhase { get; init; }
        public bool RequiresManualReview { get; init; }
        public required string[] Dependencies { get; init; }
        public required string[] BlockingDependencies { get; init; }
        public required string[] SourceConstructs { get; init; }
        public required string[] ResidualPatterns { get; init; }
        public required string RepresentativeFragment { get; init; }
        public required HashSet<string> AttributedRootCauseIds { get; init; }
    }

    private sealed class RootGroupBuilder
    {
        public RootGroupBuilder(string key, ArtifactState[] roots)
        {
            Roots = roots;
            RootCauseId = PackageFailureAnalyzer.RootCauseId(key);
        }
        public string RootCauseId { get; }
        public ArtifactState[] Roots { get; }
        public HashSet<string> BlockedDependents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public PackageRootCauseGroup ToReport()
        {
            var representative = Roots[0];
            return new PackageRootCauseGroup(
                RootCauseId,
                representative.SqlState,
                representative.NormalizedMessage,
                Roots.Select(item => item.TargetObject).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                BlockedDependents.Count,
                Roots.Select(item => item.ObjectType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Subsystem(representative),
                representative.RepresentativeFragment,
                Prompt(representative));
        }
    }
}
