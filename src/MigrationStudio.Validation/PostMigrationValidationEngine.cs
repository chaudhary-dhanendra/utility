using System.Data.Common;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Validation;
using Npgsql;

namespace MigrationStudio.Validation;

public sealed class PostMigrationValidationEngine(
    IPostgreSqlValidationMetadataReader targetReader,
    ICanonicalValueSerializer canonicalSerializer,
    ICanonicalChecksumService checksumService) : IValidationEngine
{
    public async Task<ValidationRun> ValidateAsync(
        ValidationRequest request,
        IProgress<ValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = DateTimeOffset.UtcNow;
        progress?.Report(new ValidationProgress("Metadata", 0, 4, "Reading PostgreSQL catalog"));
        var target = await targetReader.ReadAsync(
            request.Connections.TargetConnectionString,
            request.Configuration.Scope,
            cancellationToken).ConfigureAwait(false);

        var findings = new List<ValidationFinding>();
        var comparisons = CompareStructure(request, target, findings);
        progress?.Report(new ValidationProgress("Structure", 1, 4, "Structural comparison complete"));

        var data = ShouldValidateData(request.Configuration.Level)
            ? await ValidateDataAsync(request, findings, progress, cancellationToken).ConfigureAwait(false)
            : [];
        progress?.Report(new ValidationProgress("Data", 2, 4, "Data reconciliation complete"));

        var sequences = request.Configuration.Level is ValidationLevel.Structural or ValidationLevel.Full
            ? await ValidateSequencesAsync(
                request, target, findings, cancellationToken).ConfigureAwait(false)
            : [];
        await ValidateForeignKeyOrphansAsync(request, target, findings, cancellationToken)
            .ConfigureAwait(false);
        var executedQueries = await ValidateCustomQueriesAsync(
            request, findings, cancellationToken).ConfigureAwait(false);
        var semanticallyTestedObjects = request.Configuration.Level is
            ValidationLevel.ProgrammableObject or ValidationLevel.Full
            ? await ValidateRoutineTestsAsync(request, findings, cancellationToken).ConfigureAwait(false)
            : new HashSet<InventoryObjectId>();
        if (request.Configuration.Level is ValidationLevel.ProgrammableObject or ValidationLevel.Full)
        {
            AddProgrammableObjectFindings(request, target, findings, semanticallyTestedObjects);
        }
        if (request.Configuration.Level == ValidationLevel.Full)
        {
            AddSecurityFindings(request, target, findings);
        }
        progress?.Report(new ValidationProgress("Objects", 3, 4, "Constraint and object checks complete"));

        var readiness = ReadinessCalculator.Calculate(findings, request.Configuration);
        var run = new ValidationRun
        {
            RunId = Guid.NewGuid(),
            MigrationRunId = request.MigrationRunId,
            DeploymentRunId = request.DeploymentRunId,
            SourceSnapshotHash = ComputeSnapshotHash(request.SourceSnapshot),
            TargetDatabaseIdentity = target.Identity,
            Configuration = request.Configuration,
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow,
            ObjectComparisons = comparisons,
            DataComparisons = data,
            SequenceResults = sequences,
            Findings = findings,
            QueriesExecuted = executedQueries,
            Readiness = readiness
        };
        progress?.Report(new ValidationProgress("Complete", 4, 4, readiness.OverallStatus.ToString()));
        return run;
    }

    private static List<ObjectComparison> CompareStructure(
        ValidationRequest request,
        TargetDatabaseSnapshot target,
        List<ValidationFinding> findings)
    {
        var comparisons = new List<ObjectComparison>();
        var mappedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedObjects = request.SourceSnapshot.Objects.Where(item =>
            item.IsIncluded && request.Configuration.Scope.Includes(
                item.SourceSchema, item.ObjectType, item.QualifiedSourceName));
        foreach (var source in includedObjects)
        {
            if (!TryMapping(request.Conversion, source.Id, source.ObjectType.ToString(), source.SourceName, out var mapping))
            {
                Add(findings, request, "STRUCTURE.MAPPING_MISSING", ValidationCategory.StructuralCompleteness,
                    ComparisonClassification.NotComparable, source.ObjectType.ToString(), source.QualifiedSourceName,
                    null, "Identifier mapping is missing; source and target names were not compared directly.");
                continue;
            }

            var targetSchema = Unquote(mapping.TargetSchema);
            var targetName = Unquote(mapping.TargetName);
            var expectedType = TargetTypeName(source.ObjectType);
            var match = target.Objects.FirstOrDefault(item =>
                item.Schema.Equals(targetSchema, StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase) &&
                item.ObjectType.Equals(expectedType, StringComparison.OrdinalIgnoreCase));
            var classification = match is null ? ComparisonClassification.Missing : ComparisonClassification.Equivalent;
            var detail = match is null ? "Mapped target object was not found." : "Mapped target object exists.";
            var sourceComment = source.ExtendedProperties.FirstOrDefault(item =>
                item.Name.Equals("MS_Description", StringComparison.OrdinalIgnoreCase))?.Value;
            if (match is not null && !string.Equals(
                    sourceComment?.Trim(), match.Comment?.Trim(), StringComparison.Ordinal))
            {
                classification = string.IsNullOrWhiteSpace(sourceComment) && string.IsNullOrWhiteSpace(match.Comment)
                    ? classification
                    : ComparisonClassification.Warning;
                detail += " Object comment differs or is missing.";
            }
            var ruleId =
                source.ObjectType == InventoryObjectType.Table &&
                classification == ComparisonClassification.Missing
                    ? "STRUCTURE.MISSING_TABLE"
                    : source.ObjectType == InventoryObjectType.Table
                        ? "STRUCTURE.TABLE"
                        : "STRUCTURE.OBJECT";

            var severity = Add(findings, request,
                ruleId,
                ValidationCategory.StructuralCompleteness, classification, source.ObjectType.ToString(),
                source.QualifiedSourceName, mapping.TargetQualifiedName, detail,
                source.SourceDefinition, match?.Definition);
            comparisons.Add(new ObjectComparison(
                source.ObjectType.ToString(), source.QualifiedSourceName, mapping.TargetQualifiedName,
                classification, severity, detail));
            if (match is not null)
            {
                mappedTargets.Add($"{match.ObjectType}:{match.Schema}.{match.Name}");
            }
        }

        if (request.Configuration.Level != ValidationLevel.InventoryOnly)
        {
            CompareColumns(request, target, findings, comparisons);
            CompareConstraints(request, target, findings, comparisons);
            CompareIndexes(request, target, findings, comparisons);
        }

        foreach (var extra in target.Objects.Where(item =>
                     !mappedTargets.Contains($"{item.ObjectType}:{item.Schema}.{item.Name}") &&
                     item.ObjectType is not "Index"))
        {
            var detail = "Target object has no source identifier mapping in the selected scope.";
            var severity = Add(findings, request, "STRUCTURE.EXTRA_OBJECT",
                ValidationCategory.StructuralCompleteness, ComparisonClassification.Extra,
                extra.ObjectType, string.Empty, $"{extra.Schema}.{extra.Name}", detail);
            comparisons.Add(new ObjectComparison(
                extra.ObjectType, string.Empty, $"{extra.Schema}.{extra.Name}",
                ComparisonClassification.Extra, severity, detail));
        }
        return comparisons;
    }

    private static void CompareColumns(
        ValidationRequest request,
        TargetDatabaseSnapshot target,
        List<ValidationFinding> findings,
        List<ObjectComparison> comparisons)
    {
        var objects = request.SourceSnapshot.Objects.ToDictionary(item => item.Id);
        foreach (var source in request.SourceSnapshot.Columns)
        {
            if (!objects.TryGetValue(source.ParentObjectId, out var table) || !table.IsIncluded ||
                !request.Configuration.Scope.Includes(
                    table.SourceSchema, InventoryObjectType.Table, table.QualifiedSourceName) ||
                !TryMapping(request.Conversion, table.Id, table.ObjectType.ToString(), table.SourceName, out var tableMap))
            {
                continue;
            }
            var columnMap = FindColumnMapping(request.Conversion, source);
            if (columnMap is null)
            {
                Add(findings, request, "STRUCTURE.COLUMN_MAPPING_MISSING",
                    ValidationCategory.StructuralCompleteness, ComparisonClassification.NotComparable,
                    "Column", $"{table.QualifiedSourceName}.{source.Name}", null,
                    "Column mapping is missing; direct name comparison is prohibited.");
                continue;
            }
            var targetSchema = Unquote(tableMap.TargetSchema);
            var targetTable = Unquote(tableMap.TargetName);
            var targetColumnName = Unquote(columnMap.TargetName);

            var targetColumn = target.Columns.FirstOrDefault(item =>
                item.Schema.Equals(targetSchema, StringComparison.OrdinalIgnoreCase) &&
                item.Table.Equals(targetTable, StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(targetColumnName, StringComparison.OrdinalIgnoreCase));

            var sourceName = $"{table.QualifiedSourceName}.{source.Name}";
            var targetName = $"{targetSchema}.{targetTable}.{targetColumnName}";
            if (targetColumn is null)
            {
                var severity = Add(findings, request, "STRUCTURE.MISSING_COLUMN",
                    ValidationCategory.StructuralCompleteness, ComparisonClassification.Missing,
                    "Column", sourceName, targetName, "Mapped target column was not found.");
                comparisons.Add(new ObjectComparison(
                    "Column", sourceName, targetName, ComparisonClassification.Missing, severity,
                    "Mapped target column was not found."));
                continue;
            }

            var expectedType = ExpectedPostgreSqlType(source);
            var classification = SemanticTypeComparer.Compare(
                source.SystemTypeName, targetColumn.DataType, expectedType, out var typeExplanation);
            if (source.IsNullable != targetColumn.IsNullable ||
                source.IsComputed != targetColumn.IsGenerated)
            {
                classification = ComparisonClassification.Mismatch;
                typeExplanation += " Nullability, identity, or generated-column metadata differs.";
            }
            var expectsNativeIdentity = source.IsIdentity &&
                                        request.Conversion.Options.IdentityStrategy is
                                            IdentityConversionStrategy.GeneratedAlwaysAsIdentity or
                                            IdentityConversionStrategy.GeneratedByDefaultAsIdentity;
            var expectsSequenceDefault = source.IsIdentity &&
                                         request.Conversion.Options.IdentityStrategy ==
                                         IdentityConversionStrategy.SequenceAndDefault;
            if ((expectsNativeIdentity && !targetColumn.IsIdentity) ||
                (expectsSequenceDefault && targetColumn.DefaultExpression is null))
            {
                classification = ComparisonClassification.Mismatch;
                typeExplanation += " Target identity strategy does not match the configured conversion strategy.";
            }
            var sourceHasDefault = !string.IsNullOrWhiteSpace(source.DefaultDefinition);
            var targetHasDefault = !string.IsNullOrWhiteSpace(targetColumn.DefaultExpression);
            if (!source.IsIdentity && sourceHasDefault != targetHasDefault)
            {
                classification = ComparisonClassification.Warning;
                typeExplanation += " Default-expression presence differs.";
            }
            var sourceComment = source.ExtendedProperties.FirstOrDefault(item =>
                item.Name.Equals("MS_Description", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.Equals(sourceComment?.Trim(), targetColumn.Comment?.Trim(), StringComparison.Ordinal) &&
                (!string.IsNullOrWhiteSpace(sourceComment) || !string.IsNullOrWhiteSpace(targetColumn.Comment)))
            {
                classification = ComparisonClassification.Warning;
                typeExplanation += " Column comment differs or is missing.";
            }
            if (SemanticTypeComparer.HasPrecisionLossRisk(
                    source.SystemTypeName, source.Precision, source.Scale, targetColumn.DataType))
            {
                classification = ComparisonClassification.Warning;
                typeExplanation += " The target declaration may lose decimal precision or scale.";
            }
            if (SemanticTypeComparer.HasTimezoneSemanticChange(source.SystemTypeName, targetColumn.DataType))
            {
                classification = ComparisonClassification.Warning;
                typeExplanation += " Timestamp timezone semantics changed.";
            }
            var severityResult = Add(findings, request, "STRUCTURE.COLUMN_SEMANTICS",
                ValidationCategory.StructuralCompleteness, classification, "Column",
                sourceName, targetName, typeExplanation);
            comparisons.Add(new ObjectComparison(
                "Column", sourceName, targetName, classification, severityResult, typeExplanation));
        }
    }

    private static void CompareConstraints(
        ValidationRequest request,
        TargetDatabaseSnapshot target,
        List<ValidationFinding> findings,
        List<ObjectComparison> comparisons)
    {
        var objects = request.SourceSnapshot.Objects.ToDictionary(item => item.Id);
        foreach (var source in request.SourceSnapshot.Constraints.Where(item =>
                     item.Kind != ConstraintKind.Default))
        {
            if (!objects.TryGetValue(source.TableObjectId, out var table) || !table.IsIncluded ||
                !TryMapping(request.Conversion, table.Id, table.ObjectType.ToString(), table.SourceName, out var tableMap))
            {
                continue;
            }
            var nameMap = request.Conversion.IdentifierMappings.LastOrDefault(item =>
                item.SourceObjectId == table.Id &&
                item.ObjectType.Contains("Constraint", StringComparison.OrdinalIgnoreCase) &&
                item.SourceName.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            if (nameMap is null)
            {
                continue;
            }
            var match = target.Constraints.FirstOrDefault(item =>
                item.Schema.Equals(Unquote(tableMap.TargetSchema), StringComparison.OrdinalIgnoreCase) &&
                item.Table.Equals(Unquote(tableMap.TargetName), StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(Unquote(nameMap.TargetName), StringComparison.OrdinalIgnoreCase));
            var classification = match is null
                ? ComparisonClassification.Missing
                : match.IsValidated ? ComparisonClassification.Equivalent : ComparisonClassification.Warning;
            var rule = source.Kind == ConstraintKind.ForeignKey && match is null
                ? "CONSTRAINT.MISSING_FOREIGN_KEY" : "CONSTRAINT.STATE";
            var detail = match is null ? "Mapped constraint is missing." :
                match.IsValidated ? "Constraint exists and is validated." : "Constraint exists but is not validated.";
            if (match is not null)
            {
                var expectedColumns = MapChildNames(
                    request.Conversion, table.Id, source.Columns.Select(item => item.Name));
                if (!expectedColumns.SequenceEqual(match.Columns, StringComparer.OrdinalIgnoreCase) ||
                    !ConstraintTypeMatches(source.Kind, match.ConstraintType))
                {
                    classification = ComparisonClassification.Mismatch;
                    detail += " Constraint kind or mapped column order differs.";
                }
                if (source.Kind == ConstraintKind.ForeignKey &&
                    source.ReferencedTableObjectId is { } referencedId &&
                    objects.TryGetValue(referencedId, out var referenced) &&
                    TryMapping(request.Conversion, referenced.Id, referenced.ObjectType.ToString(),
                        referenced.SourceName, out var referencedMap))
                {
                    var expectedReferencedColumns = MapChildNames(
                        request.Conversion, referenced.Id,
                        source.ReferencedColumns.Select(item => item.Name));
                    if (!string.Equals(Unquote(referencedMap.TargetSchema), match.ReferencedSchema,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Unquote(referencedMap.TargetName), match.ReferencedTable,
                            StringComparison.OrdinalIgnoreCase) ||
                        !expectedReferencedColumns.SequenceEqual(
                            match.ReferencedColumns, StringComparer.OrdinalIgnoreCase))
                    {
                        classification = ComparisonClassification.Mismatch;
                        detail += " Referenced table or mapped foreign-key columns differ.";
                    }
                }
            }
            var severity = Add(findings, request, rule, ValidationCategory.Constraints, classification,
                source.Kind.ToString(), $"{table.QualifiedSourceName}.{source.Name}",
                nameMap.TargetQualifiedName, detail);
            comparisons.Add(new ObjectComparison(
                source.Kind.ToString(), $"{table.QualifiedSourceName}.{source.Name}",
                nameMap.TargetQualifiedName, classification, severity, detail));
        }
    }

    private static void CompareIndexes(
        ValidationRequest request,
        TargetDatabaseSnapshot target,
        List<ValidationFinding> findings,
        List<ObjectComparison> comparisons)
    {
        var objects = request.SourceSnapshot.Objects.ToDictionary(item => item.Id);
        foreach (var source in request.SourceSnapshot.Indexes.Where(item =>
                     !item.IsPrimaryKey && !item.IsUniqueConstraint))
        {
            if (!objects.TryGetValue(source.TableObjectId, out var table) || !table.IsIncluded ||
                !TryMapping(request.Conversion, table.Id, table.ObjectType.ToString(), table.SourceName, out var tableMap))
            {
                continue;
            }
            var nameMap = request.Conversion.IdentifierMappings.LastOrDefault(item =>
                item.SourceObjectId == table.Id &&
                item.ObjectType.Equals("Index", StringComparison.OrdinalIgnoreCase) &&
                item.SourceName.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            if (nameMap is null)
            {
                continue;
            }
            var match = target.Indexes.FirstOrDefault(item =>
                item.Schema.Equals(Unquote(tableMap.TargetSchema), StringComparison.OrdinalIgnoreCase) &&
                item.Table.Equals(Unquote(tableMap.TargetName), StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(Unquote(nameMap.TargetName), StringComparison.OrdinalIgnoreCase));
            var classification = match is null ? ComparisonClassification.Missing :
                match.IsValid ? ComparisonClassification.Equivalent : ComparisonClassification.Mismatch;
            var detail = match is null ? "Mapped index is missing." :
                match.IsValid ? "Index exists and is valid." : "Index exists but PostgreSQL marks it invalid.";
            if (match is not null)
            {
                var expectedKeys = MapChildNames(
                    request.Conversion, table.Id,
                    source.Columns.Where(item => !item.IsIncluded)
                        .OrderBy(item => item.KeyOrdinal).Select(item => item.Name));
                var expectedIncluded = MapChildNames(
                    request.Conversion, table.Id,
                    source.Columns.Where(item => item.IsIncluded).Select(item => item.Name));
                var predicateExpected = !string.IsNullOrWhiteSpace(source.FilterDefinition);
                if (source.IsUnique != match.IsUnique ||
                    !expectedKeys.SequenceEqual(match.KeyColumns, StringComparer.OrdinalIgnoreCase) ||
                    !expectedIncluded.SequenceEqual(match.IncludedColumns, StringComparer.OrdinalIgnoreCase) ||
                    predicateExpected != !string.IsNullOrWhiteSpace(match.Predicate))
                {
                    classification = ComparisonClassification.Mismatch;
                    detail += " Uniqueness, mapped keys, included columns, or partial-index predicate differs.";
                }
            }
            var severity = Add(findings, request, "CONSTRAINT.INDEX",
                ValidationCategory.Constraints, classification, "Index",
                $"{table.QualifiedSourceName}.{source.Name}", nameMap.TargetQualifiedName, detail);
            comparisons.Add(new ObjectComparison(
                "Index", $"{table.QualifiedSourceName}.{source.Name}", nameMap.TargetQualifiedName,
                classification, severity, detail));
        }
    }

    private async Task<IReadOnlyList<TableDataComparison>> ValidateDataAsync(
        ValidationRequest request,
        List<ValidationFinding> findings,
        IProgress<ValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tables = request.SourceSnapshot.Objects.Where(item =>
            item.IsIncluded && item.ObjectType == InventoryObjectType.Table &&
            request.Configuration.Scope.Includes(item.SourceSchema, item.ObjectType, item.QualifiedSourceName)).ToArray();
        var results = new List<TableDataComparison>();
        await using var source = new SqlConnection(request.Connections.SourceConnectionString);
        await using var target = new NpgsqlConnection(request.Connections.TargetConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < tables.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = tables[index];
            progress?.Report(new ValidationProgress("Data", index, tables.Length, table.QualifiedSourceName));
            if (!TryMapping(request.Conversion, table.Id, table.ObjectType.ToString(), table.SourceName, out var mapping))
            {
                continue;
            }
            var sourceCount = await CountAsync(
                source, $"{QuoteSqlServer(table.SourceSchema)}.{QuoteSqlServer(table.SourceName)}",
                cancellationToken).ConfigureAwait(false);
            var targetCount = await CountAsync(
                target, $"{QuotePostgreSql(Unquote(mapping.TargetSchema))}.{QuotePostgreSql(Unquote(mapping.TargetName))}",
                cancellationToken).ConfigureAwait(false);
            string? sourceHash = null;
            string? targetHash = null;
            IReadOnlyList<ColumnDataMetric> sourceMetrics = [];
            IReadOnlyList<ColumnDataMetric> targetMetrics = [];
            var ordered = false;
            if (request.Configuration.Level is ValidationLevel.DataSampling or
                ValidationLevel.DataComprehensive or ValidationLevel.Full)
            {
                var columns = request.SourceSnapshot.Columns
                    .Where(item => item.ParentObjectId == table.Id && !item.IsComputed)
                    .OrderBy(item => item.OrdinalPosition).ToArray();
                var keyColumns = request.SourceSnapshot.Constraints
                    .FirstOrDefault(item => item.TableObjectId == table.Id && item.Kind == ConstraintKind.PrimaryKey)
                    ?.Columns.OrderBy(item => item.Ordinal).Select(item => item.Name).ToArray() ?? [];
                ordered = keyColumns.Length > 0;
                var limit = request.Configuration.Level == ValidationLevel.DataSampling
                    ? request.Configuration.SampleSize : int.MaxValue;
                var sourceProfile = await ProfileRowsAsync(
                    source, table, mapping, request.Conversion, columns, keyColumns, limit, true,
                    request.Configuration, cancellationToken).ConfigureAwait(false);
                var targetProfile = await ProfileRowsAsync(
                    target, table, mapping, request.Conversion, columns, keyColumns, limit, false,
                    request.Configuration, cancellationToken).ConfigureAwait(false);
                sourceHash = sourceProfile.Checksum;
                targetHash = targetProfile.Checksum;
                sourceMetrics = sourceProfile.Metrics;
                targetMetrics = targetProfile.Metrics;
            }
            var classification = sourceCount != targetCount
                ? ComparisonClassification.Mismatch
                : sourceHash is not null && !string.Equals(sourceHash, targetHash, StringComparison.Ordinal)
                    ? ComparisonClassification.Mismatch
                    : ComparisonClassification.Equivalent;
            var detail = sourceCount != targetCount
                ? $"Row counts differ ({sourceCount} source, {targetCount} target)."
                : sourceHash is not null && classification == ComparisonClassification.Mismatch
                    ? "Canonical data checksums differ; values are not included in the finding."
                    : sourceHash is null ? "Row counts match." : "Row counts and canonical checksums match.";
            Add(findings, request, sourceCount != targetCount ? "DATA.ROW_COUNT" : "DATA.CHECKSUM",
                ValidationCategory.DataReconciliation, classification, "Table",
                table.QualifiedSourceName, mapping.TargetQualifiedName, detail);
            results.Add(new TableDataComparison(
                table.QualifiedSourceName, mapping.TargetQualifiedName, sourceCount, targetCount,
                sourceHash, targetHash, ordered, sourceMetrics, targetMetrics, classification, detail));
        }
        return results;
    }

    private async Task<DataProfile> ProfileRowsAsync(
        DbConnection connection,
        InventoryObject table,
        IdentifierMappingEntry tableMap,
        ConversionRun conversion,
        ColumnInventory[] columns,
        IReadOnlyList<string> keys,
        int limit,
        bool source,
        ValidationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var selectColumns = columns.Select(column => source
            ? QuoteSqlServer(column.Name)
            : QuotePostgreSql(Unquote(
                FindColumnMapping(conversion, column)?.TargetName ?? column.Name))).ToArray();

        var mappedKeys = keys.Select(key =>
        {
            if (source)
            {
                return QuoteSqlServer(key);
            }

            var sourceColumn = columns.FirstOrDefault(column =>
                column.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

            return QuotePostgreSql(Unquote(
                sourceColumn is null
                    ? key
                    : FindColumnMapping(conversion, sourceColumn)?.TargetName ?? key));
        }).ToArray();
        var relation = source
            ? $"{QuoteSqlServer(table.SourceSchema)}.{QuoteSqlServer(table.SourceName)}"
            : $"{QuotePostgreSql(Unquote(tableMap.TargetSchema))}.{QuotePostgreSql(Unquote(tableMap.TargetName))}";
        var top = source && limit < int.MaxValue ? $"TOP ({limit}) " : string.Empty;
        var tail = !source && limit < int.MaxValue ? $" LIMIT {limit}" : string.Empty;
        var order = mappedKeys.Length > 0 ? $" ORDER BY {string.Join(", ", mappedKeys)}" :
            limit < int.MaxValue ? $" ORDER BY {string.Join(", ", selectColumns)}" : string.Empty;
        var sql = $"SELECT {top}{string.Join(", ", selectColumns)} FROM {relation}{order}{tail}";
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        var checksum = new StreamingChecksum(checksumService, mappedKeys.Length > 0);
        var nullCounts = new long[columns.Length];
        var minimums = new string?[columns.Length];
        var maximums = new string?[columns.Length];
        var sums = new decimal?[columns.Length];
        var numericCounts = new long[columns.Length];
        var distinct = configuration.IncludeDistinctCounts
            ? Enumerable.Range(0, columns.Length)
                .Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray()
            : null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new CanonicalValue[columns.Length];
            for (var index = 0; index < columns.Length; index++)
            {
                var raw = reader.IsDBNull(index) ? null : reader.GetValue(index);
                var sensitive = columns[index].IsMasked || IsSensitiveColumn(columns[index].Name);
                row[index] = canonicalSerializer.Serialize(
                    raw,
                    InferCanonicalKind(columns[index].SystemTypeName),
                    configuration.Canonical,
                    columns[index].SystemTypeName is "char" or "nchar",
                    sensitive);
                if (raw is null)
                {
                    nullCounts[index]++;
                    continue;
                }
                var representation = row[index].Representation;
                if (minimums[index] is null ||
                    string.CompareOrdinal(representation, minimums[index]) < 0)
                {
                    minimums[index] = representation;
                }
                if (maximums[index] is null ||
                    string.CompareOrdinal(representation, maximums[index]) > 0)
                {
                    maximums[index] = representation;
                }
                distinct?[index].Add(representation);
                if (!sensitive && IsNumeric(columns[index].SystemTypeName))
                {
                    try
                    {
                        sums[index] = (sums[index] ?? 0) +
                                      Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                        numericCounts[index]++;
                    }
                    catch (OverflowException)
                    {
                        sums[index] = null;
                        numericCounts[index] = 0;
                    }
                }
            }
            checksum.Append(row);
        }
        var metrics = columns.Select((column, index) =>
        {
            var sum = sums[index];
            var average = sum is not null && numericCounts[index] > 0
                ? sum.Value / numericCounts[index]
                : (decimal?)null;
            return new ColumnDataMetric(
                column.Name,
                nullCounts[index],
                minimums[index],
                maximums[index],
                sum?.ToString(CultureInfo.InvariantCulture),
                average?.ToString(CultureInfo.InvariantCulture),
                distinct is null ? null : distinct[index].Count);
        }).ToArray();
        return new DataProfile(checksum.Complete(), metrics);
    }

    private static async Task<IReadOnlyList<SequenceValidationResult>> ValidateSequencesAsync(
        ValidationRequest request,
        TargetDatabaseSnapshot target,
        List<ValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        var results = new List<SequenceValidationResult>();

        foreach (var source in request.SourceSnapshot.Sequences)
        {
            if (!TryMapping(
                    request.Conversion,
                    source.ObjectId,
                    InventoryObjectType.Sequence.ToString(),
                    string.Empty,
                    out var mapping))
            {
                continue;
            }

            var targetSchema = Unquote(mapping.TargetSchema);
            var targetName = Unquote(mapping.TargetName);

            var match = target.Sequences.FirstOrDefault(item =>
                item.Schema.Equals(targetSchema, StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                Add(
                    findings,
                    request,
                    "SEQUENCE.MISSING",
                    ValidationCategory.Constraints,
                    ComparisonClassification.Missing,
                    "Sequence",
                    mapping.SourceQualifiedName,
                    mapping.TargetQualifiedName,
                    "Mapped sequence is missing.");

                continue;
            }

            var expectedNext = match.CurrentValue + match.Increment;
            var classification =
                match.Increment == source.Increment &&
                match.Minimum == source.MinimumValue &&
                match.Maximum == source.MaximumValue &&
                match.IsCycling == source.IsCycling
                    ? ComparisonClassification.Equivalent
                    : ComparisonClassification.Mismatch;

            var result = new SequenceValidationResult(
                mapping.SourceQualifiedName,
                mapping.TargetQualifiedName,
                match.CurrentValue,
                null,
                match.Increment,
                match.Minimum,
                match.Maximum,
                match.IsCycling,
                expectedNext,
                false,
                classification);

            results.Add(result);

            Add(
                findings,
                request,
                "SEQUENCE.STATE",
                ValidationCategory.Constraints,
                classification,
                "Sequence",
                mapping.SourceQualifiedName,
                mapping.TargetQualifiedName,
                classification == ComparisonClassification.Equivalent
                    ? "Sequence bounds, increment, cycle state, and expected next value match."
                    : "Sequence bounds, increment, or cycle state differs from the source metadata.");
        }

        foreach (var identity in request.SourceSnapshot.Columns.Where(item => item.IsIdentity))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var table = request.SourceSnapshot.Objects.FirstOrDefault(item =>
                item.Id == identity.ParentObjectId);

            if (table is null ||
                !TryMapping(
                    request.Conversion,
                    table.Id,
                    table.ObjectType.ToString(),
                    table.SourceName,
                    out var tableMap))
            {
                continue;
            }

            var columnMap = FindColumnMapping(request.Conversion, identity);
            if (columnMap is null)
            {
                Add(
                    findings,
                    request,
                    "SEQUENCE.IDENTITY_MAPPING_MISSING",
                    ValidationCategory.Constraints,
                    ComparisonClassification.Warning,
                    "IdentitySequence",
                    $"{table.QualifiedSourceName}.{identity.Name}",
                    tableMap.TargetQualifiedName,
                    "No mapped PostgreSQL identity column was found. Sequence alignment was skipped.");

                continue;
            }

            var targetTableSchema = Unquote(tableMap.TargetSchema);
            var targetTableName = Unquote(tableMap.TargetName);
            var mappedColumnName = Unquote(columnMap.TargetName);

            await using var connection =
                new NpgsqlConnection(request.Connections.TargetConnectionString);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var actualColumnName = await ResolveTargetColumnAsync(
                    connection,
                    targetTableSchema,
                    targetTableName,
                    mappedColumnName,
                    identity.Name,
                    cancellationToken)
                .ConfigureAwait(false);

            if (actualColumnName is null)
            {
                Add(
                    findings,
                    request,
                    "SEQUENCE.IDENTITY_COLUMN_MISSING",
                    ValidationCategory.Constraints,
                    ComparisonClassification.Warning,
                    "IdentitySequence",
                    $"{table.QualifiedSourceName}.{identity.Name}",
                    $"{targetTableSchema}.{targetTableName}.{mappedColumnName}",
                    "The mapped PostgreSQL identity column does not exist. Sequence alignment was skipped.");

                continue;
            }

            var relation =
                $"{QuotePostgreSql(targetTableSchema)}.{QuotePostgreSql(targetTableName)}";
            var quotedColumn = QuotePostgreSql(actualColumnName);
            var relationLiteral = relation.Replace("'", "''", StringComparison.Ordinal);
            var columnLiteral = actualColumnName.Replace("'", "''", StringComparison.Ordinal);

            string sequenceName;
            decimal? targetMaximum;

            await using (var command = new NpgsqlCommand(
                $"""
                 SELECT
                     MAX({quotedColumn})::numeric,
                     pg_get_serial_sequence(
                         '{relationLiteral}',
                         '{columnLiteral}')
                 FROM {relation};
                 """,
                connection))
            {
                await using var reader =
                    await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                targetMaximum = GetNullableDecimal(reader, 0);

                if (reader.IsDBNull(1))
                {
                    Add(
                        findings,
                        request,
                        "SEQUENCE.IDENTITY_SEQUENCE_MISSING",
                        ValidationCategory.Constraints,
                        ComparisonClassification.Warning,
                        "IdentitySequence",
                        $"{table.QualifiedSourceName}.{identity.Name}",
                        $"{targetTableSchema}.{targetTableName}.{actualColumnName}",
                        "PostgreSQL did not report an owned identity or serial sequence for the mapped column.");

                    continue;
                }

                sequenceName = reader.GetString(1);
            }

            const string sequenceSql =
                """
                SELECT
                    s.last_value::numeric,
                    s.increment_by::numeric,
                    s.min_value::numeric,
                    s.max_value::numeric,
                    s.cycle
                FROM pg_sequences AS s
                WHERE
                    to_regclass(format('%I.%I', s.schemaname, s.sequencename))
                    = to_regclass(@sequenceName)
                LIMIT 1;
                """;

            await using var sequenceCommand =
                new NpgsqlCommand(sequenceSql, connection);

            sequenceCommand.Parameters.AddWithValue("sequenceName", sequenceName);

            await using var sequenceReader =
                await sequenceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await sequenceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Add(
                    findings,
                    request,
                    "SEQUENCE.IDENTITY_SEQUENCE_METADATA_MISSING",
                    ValidationCategory.Constraints,
                    ComparisonClassification.Warning,
                    "IdentitySequence",
                    $"{table.QualifiedSourceName}.{identity.Name}",
                    sequenceName,
                    "The owned PostgreSQL sequence exists, but its pg_sequences metadata could not be resolved.");

                continue;
            }

            var current = GetNullableDecimal(sequenceReader, 0);
            var increment = GetNullableDecimal(sequenceReader, 1);
            var minimum = GetNullableDecimal(sequenceReader, 2);
            var maximumAllowed = GetNullableDecimal(sequenceReader, 3);

            if (current is null ||
                increment is null ||
                minimum is null ||
                maximumAllowed is null)
            {
                Add(
                    findings,
                    request,
                    "SEQUENCE.METADATA_INCOMPLETE",
                    ValidationCategory.Constraints,
                    ComparisonClassification.Warning,
                    "IdentitySequence",
                    $"{table.QualifiedSourceName}.{identity.Name}",
                    sequenceName,
                    "Sequence metadata contains NULL values. Sequence alignment was skipped.");

                continue;
            }

            var cycle = sequenceReader.GetBoolean(4);

            var alignmentResult = SequenceAlignmentEvaluator.Evaluate(
                $"{table.QualifiedSourceName}.{identity.Name}",
                sequenceName,
                current.Value,
                targetMaximum,
                increment.Value,
                minimum.Value,
                maximumAllowed.Value,
                cycle);

            results.Add(alignmentResult);

            var duplicate = alignmentResult.WouldGenerateDuplicate;

            Add(
                findings,
                request,
                duplicate
                    ? "SEQUENCE.SEQUENCE_DUPLICATE"
                    : "SEQUENCE.IDENTITY_ALIGNMENT",
                ValidationCategory.Constraints,
                alignmentResult.Classification,
                "IdentitySequence",
                $"{table.QualifiedSourceName}.{identity.Name}",
                sequenceName,
                duplicate
                    ? $"Expected next value {alignmentResult.ExpectedNextValue} is not beyond target maximum " +
                      $"{targetMaximum?.ToString(CultureInfo.InvariantCulture) ?? "NULL"}; inserts can collide."
                    : $"Expected next value {alignmentResult.ExpectedNextValue} is safely beyond target maximum " +
                      $"{targetMaximum?.ToString(CultureInfo.InvariantCulture) ?? "NULL"}.");
        }

        return results;
    }

    private static void AddProgrammableObjectFindings(
        ValidationRequest request,
        TargetDatabaseSnapshot target,
        List<ValidationFinding> findings,
        IReadOnlySet<InventoryObjectId> semanticallyTestedObjects)
    {
        var types = new[]
        {
            InventoryObjectType.View, InventoryObjectType.Function,
            InventoryObjectType.StoredProcedure, InventoryObjectType.Trigger
        };
        foreach (var source in request.SourceSnapshot.Objects.Where(item => item.IsIncluded && types.Contains(item.ObjectType)))
        {
            if (!TryMapping(request.Conversion, source.Id, source.ObjectType.ToString(), source.SourceName, out var mapping))
            {
                continue;
            }
            var targetObject = target.Objects.FirstOrDefault(item =>
                item.Schema.Equals(Unquote(mapping.TargetSchema), StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(Unquote(mapping.TargetName), StringComparison.OrdinalIgnoreCase));
            var artifact = request.Conversion.Artifacts.FirstOrDefault(item => item.SourceObjectId == source.Id);
            if (targetObject is null)
            {
                Add(findings, request, "PROGRAMMABLE.MISSING", ValidationCategory.ProgrammableObjects,
                    ComparisonClassification.Missing, source.ObjectType.ToString(),
                    source.QualifiedSourceName, mapping.TargetQualifiedName, "Programmable object is missing.");
            }
            else if (artifact is null || artifact.RequiresManualReview ||
                     !semanticallyTestedObjects.Contains(source.Id))
            {
                Add(findings, request, "PROGRAMMABLE.SEMANTIC_REVIEW",
                    ValidationCategory.ProgrammableObjects, ComparisonClassification.ManualReview,
                    source.ObjectType.ToString(), source.QualifiedSourceName, mapping.TargetQualifiedName,
                    "Creation was verified, but no administrator-approved semantic test case passed.");
            }
            else
            {
                Add(findings, request, "PROGRAMMABLE.DEFINITION",
                    ValidationCategory.ProgrammableObjects, ComparisonClassification.Equivalent,
                    source.ObjectType.ToString(), source.QualifiedSourceName, mapping.TargetQualifiedName,
                    "Object exists and an administrator-approved, rollback-capable semantic test passed.");
            }
        }
    }

    private async Task<IReadOnlySet<InventoryObjectId>> ValidateRoutineTestsAsync(
        ValidationRequest request,
        List<ValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        if (request.Configuration.RoutineTestCases.Count == 0)
        {
            return new HashSet<InventoryObjectId>();
        }
        var passed = new HashSet<InventoryObjectId>();
        await using var sourceConnection = new SqlConnection(request.Connections.SourceConnectionString);
        await using var targetConnection = new NpgsqlConnection(request.Connections.TargetConnectionString);
        var sourceOpened = false;
        var targetOpened = false;
        foreach (var test in request.Configuration.RoutineTestCases)
        {
            var sourceObject = request.SourceSnapshot.Objects.FirstOrDefault(item =>
                item.QualifiedSourceName.Equals(test.Routine, StringComparison.OrdinalIgnoreCase) ||
                item.SourceName.Equals(test.Routine, StringComparison.OrdinalIgnoreCase));
            if (sourceObject is null || sourceObject.ObjectType != InventoryObjectType.Function)
            {
                Add(findings, request, "PROGRAMMABLE.UNSAFE_TEST", ValidationCategory.ProgrammableObjects,
                    ComparisonClassification.ManualReview, "RoutineTest", test.Routine, null,
                    "Only explicitly read-only function/view tests can execute automatically; procedures and triggers remain manual.");
                continue;
            }
            if (!test.IsReadOnly || (!test.SourceExecutionAllowed && !test.TargetExecutionAllowed) ||
                !TryMapping(request.Conversion, sourceObject.Id, sourceObject.ObjectType.ToString(),
                    sourceObject.SourceName, out var mapping))
            {
                Add(findings, request, "PROGRAMMABLE.TEST_NOT_AUTHORIZED",
                    ValidationCategory.ProgrammableObjects, ComparisonClassification.ManualReview,
                    "RoutineTest", test.Routine, null,
                    "The test is not read-only, has no execution authorization, or lacks an identifier mapping.");
                continue;
            }

            try
            {
                RoutineProbe? sourceProbe = null;
                RoutineProbe? targetProbe = null;
                if (test.SourceExecutionAllowed)
                {
                    if (!sourceOpened)
                    {
                        await sourceConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                        sourceOpened = true;
                    }
                    sourceProbe = await ExecuteRoutineProbeAsync(
                        sourceConnection,
                        $"{QuoteSqlServer(sourceObject.SourceSchema)}.{QuoteSqlServer(sourceObject.SourceName)}",
                        test,
                        request.Configuration,
                        cancellationToken).ConfigureAwait(false);
                }
                if (test.TargetExecutionAllowed)
                {
                    if (!targetOpened)
                    {
                        await targetConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                        targetOpened = true;
                    }
                    targetProbe = await ExecuteRoutineProbeAsync(
                        targetConnection,
                        $"{QuotePostgreSql(Unquote(mapping.TargetSchema))}.{QuotePostgreSql(Unquote(mapping.TargetName))}",
                        test,
                        request.Configuration,
                        cancellationToken).ConfigureAwait(false);
                }

                var shapeMatches = test.ExpectedResultColumns.Count == 0 ||
                                   (sourceProbe is null ||
                                    test.ExpectedResultColumns.SequenceEqual(
                                        sourceProbe.Columns, StringComparer.OrdinalIgnoreCase)) &&
                                   (targetProbe is null ||
                                    test.ExpectedResultColumns.SequenceEqual(
                                        targetProbe.Columns, StringComparer.OrdinalIgnoreCase));
                var pairMatches = sourceProbe is null || targetProbe is null ||
                                  (sourceProbe.FirstValue is null && targetProbe.FirstValue is null) ||
                                  (sourceProbe.FirstValue is not null && targetProbe.FirstValue is not null &&
                                   canonicalSerializer.AreEquivalent(
                                       sourceProbe.FirstValue, targetProbe.FirstValue,
                                       request.Configuration.Canonical));
                var expectedMatches = test.ExpectedScalarCanonicalValue is null ||
                                      (targetProbe?.FirstValue?.Representation ??
                                       sourceProbe?.FirstValue?.Representation)
                                      == test.ExpectedScalarCanonicalValue;
                var succeeded = shapeMatches && pairMatches && expectedMatches;
                Add(findings, request, "PROGRAMMABLE.SAFE_TEST",
                    ValidationCategory.ProgrammableObjects,
                    succeeded ? ComparisonClassification.Equivalent : ComparisonClassification.Mismatch,
                    sourceObject.ObjectType.ToString(), sourceObject.QualifiedSourceName,
                    mapping.TargetQualifiedName,
                    succeeded
                        ? "Administrator-approved read-only test passed inside rollback transactions."
                        : "Read-only test result shape or canonical scalar result differed; values were not retained.");
                if (succeeded)
                {
                    passed.Add(sourceObject.Id);
                }
            }
            catch (Exception exception) when (exception is DbException or InvalidOperationException)
            {
                Add(findings, request, "PROGRAMMABLE.TEST_FAILED",
                    ValidationCategory.ProgrammableObjects, ComparisonClassification.Warning,
                    sourceObject.ObjectType.ToString(), sourceObject.QualifiedSourceName,
                    mapping.TargetQualifiedName,
                    $"Read-only test failed ({exception.GetType().Name}); details are in the redacted application log.");
            }
        }
        return passed;
    }

    private async Task<RoutineProbe> ExecuteRoutineProbeAsync(
        DbConnection connection,
        string qualifiedRoutine,
        RoutineValidationTestCase test,
        ValidationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = test.TimeoutSeconds;
            var parameterNames = test.InputParameters.Select((item, index) =>
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"p{index}";
                parameter.Value = item.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
                return $"@p{index}";
            }).ToArray();
            command.CommandText = $"SELECT * FROM {qualifiedRoutine}({string.Join(", ", parameterNames)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            CanonicalValue? first = null;
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0)
            {
                var sensitive = test.SensitiveParameters.Count > 0;
                var raw = reader.IsDBNull(0) ? null : reader.GetValue(0);
                first = canonicalSerializer.Serialize(
                    raw, InferRuntimeKind(raw), configuration.Canonical, sensitive: sensitive);
            }
            await reader.DisposeAsync().ConfigureAwait(false);
            return new RoutineProbe(columns, first);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void AddSecurityFindings(
        ValidationRequest request,
        TargetDatabaseSnapshot target,
        List<ValidationFinding> findings)
    {
        foreach (var principal in request.SourceSnapshot.SecurityPrincipals)
        {
            var principalObject = request.SourceSnapshot.Objects.FirstOrDefault(item =>
                item.Id == principal.ObjectId);
            if (principalObject is null ||
                !TryMapping(request.Conversion, principal.ObjectId, principalObject.ObjectType.ToString(),
                    principalObject.SourceName, out var principalMap))
            {
                Add(findings, request, "SECURITY.PRINCIPAL_MAPPING", ValidationCategory.Security,
                    ComparisonClassification.NotComparable, "Role", principal.Name, null,
                    "Security principal has no identifier mapping.");
                continue;
            }
            var targetPrincipal = Unquote(principalMap.TargetName);
            Add(findings, request, "SECURITY.ROLE", ValidationCategory.Security,
                target.Roles.Contains(targetPrincipal, StringComparer.OrdinalIgnoreCase)
                    ? ComparisonClassification.Equivalent
                    : ComparisonClassification.Missing,
                "Role", principal.Name, targetPrincipal,
                target.Roles.Contains(targetPrincipal, StringComparer.OrdinalIgnoreCase)
                    ? "Mapped PostgreSQL role exists; passwords are intentionally not compared."
                    : "Mapped PostgreSQL role is missing.");

            foreach (var sourceRole in principal.RoleMemberships)
            {
                var roleMap = request.Conversion.IdentifierMappings.LastOrDefault(item =>
                    item.SourceName.Equals(sourceRole, StringComparison.OrdinalIgnoreCase) &&
                    item.ObjectType.Equals(InventoryObjectType.Role.ToString(), StringComparison.OrdinalIgnoreCase));
                if (roleMap is null)
                {
                    Add(findings, request, "SECURITY.MEMBERSHIP_MAPPING", ValidationCategory.Security,
                        ComparisonClassification.NotComparable, "RoleMembership",
                        $"{principal.Name} -> {sourceRole}", null,
                        "Role membership cannot be compared because the target role mapping is missing.");
                    continue;
                }
                var expected = $"{targetPrincipal} -> {Unquote(roleMap.TargetName)}";
                var exists = target.RoleMemberships.Contains(expected, StringComparer.OrdinalIgnoreCase);
                Add(findings, request, "SECURITY.ROLE_MEMBERSHIP", ValidationCategory.Security,
                    exists ? ComparisonClassification.Equivalent : ComparisonClassification.Missing,
                    "RoleMembership", $"{principal.Name} -> {sourceRole}", expected,
                    exists ? "Mapped role membership exists." : "Mapped role membership is missing.");
            }
        }
        foreach (var permission in request.SourceSnapshot.Permissions)
        {
            if (permission.State.Equals("DENY", StringComparison.OrdinalIgnoreCase))
            {
                Add(findings, request, "SECURITY.SQLSERVER_DENY", ValidationCategory.Security,
                    ComparisonClassification.ManualReview, "Permission", permission.PermissionName, null,
                    "SQL Server DENY has no direct PostgreSQL grant equivalent and requires administrator review.");
            }
        }
        if (request.SourceSnapshot.SecurityPrincipals.Count > 0 && target.RoleMemberships.Count == 0)
        {
            Add(findings, request, "SECURITY.ROLE_MEMBERSHIPS", ValidationCategory.Security,
                ComparisonClassification.Warning, "RoleMembership", "SQL Server role memberships", null,
                "Source principals exist, but no non-system PostgreSQL role memberships were observed.");
        }
    }

    private static async Task ValidateForeignKeyOrphansAsync(
        ValidationRequest request,
        TargetDatabaseSnapshot target,
        List<ValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        if (!request.Configuration.ValidateForeignKeyOrphans ||
            !ShouldValidateData(request.Configuration.Level))
        {
            return;
        }
        await using var connection = new NpgsqlConnection(request.Connections.TargetConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var foreignKey in target.Constraints.Where(item =>
                     item.ConstraintType == "ForeignKey" &&
                     item.ReferencedSchema is not null &&
                     item.ReferencedTable is not null &&
                     item.Columns.Count == item.ReferencedColumns.Count &&
                     item.Columns.Count > 0))
        {
            var child = $"{QuotePostgreSql(foreignKey.Schema)}.{QuotePostgreSql(foreignKey.Table)}";
            var parent = $"{QuotePostgreSql(foreignKey.ReferencedSchema!)}.{QuotePostgreSql(foreignKey.ReferencedTable!)}";
            var present = string.Join(" AND ", foreignKey.Columns.Select(column =>
                $"c.{QuotePostgreSql(column)} IS NOT NULL"));
            var join = string.Join(" AND ", foreignKey.Columns.Zip(
                foreignKey.ReferencedColumns,
                (column, referenced) =>
                    $"p.{QuotePostgreSql(referenced)} = c.{QuotePostgreSql(column)}"));
            await using var command = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM {child} c WHERE {present} AND NOT EXISTS (SELECT 1 FROM {parent} p WHERE {join})",
                connection)
            {
                CommandTimeout = 300
            };
            var count = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            Add(findings, request, "CONSTRAINT.FOREIGN_KEY_ORPHANS", ValidationCategory.Constraints,
                count == 0 ? ComparisonClassification.Equivalent : ComparisonClassification.Mismatch,
                "ForeignKey", $"{foreignKey.Schema}.{foreignKey.Table}.{foreignKey.Name}",
                $"{foreignKey.ReferencedSchema}.{foreignKey.ReferencedTable}",
                count == 0
                    ? "No orphaned target rows were detected."
                    : $"{count} orphaned target row(s) were detected; row values were not retained.");
        }
    }

    private async Task<IReadOnlyList<ExecutedValidationQuery>> ValidateCustomQueriesAsync(
        ValidationRequest request,
        List<ValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        if (request.Configuration.CustomQueries.Count == 0)
        {
            return [];
        }
        var executed = new List<ExecutedValidationQuery>();
        await using var source = new SqlConnection(request.Connections.SourceConnectionString);
        await using var target = new NpgsqlConnection(request.Connections.TargetConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var query in request.Configuration.CustomQueries)
        {
            var started = DateTimeOffset.UtcNow;
            string? error = null;
            var succeeded = false;
            try
            {
                if (!query.IsReadOnly ||
                    !IsReadOnlyQuery(query.SourceSql) ||
                    !IsReadOnlyQuery(query.TargetSql))
                {
                    throw new InvalidOperationException(
                        "Only administrator-configured SELECT/WITH validation queries may execute.");
                }
                var sourceValue = await ExecuteCanonicalScalarAsync(
                    source, query.SourceSql, query.TimeoutSeconds, request.Configuration,
                    query.ContainsSensitiveValues, cancellationToken).ConfigureAwait(false);
                var targetValue = await ExecuteCanonicalScalarAsync(
                    target, query.TargetSql, query.TimeoutSeconds, request.Configuration,
                    query.ContainsSensitiveValues, cancellationToken).ConfigureAwait(false);
                var equivalent = canonicalSerializer.AreEquivalent(
                    sourceValue, targetValue, request.Configuration.Canonical);
                Add(findings, request, "DATA.CUSTOM_QUERY", ValidationCategory.DataReconciliation,
                    equivalent ? ComparisonClassification.Equivalent : ComparisonClassification.Mismatch,
                    "ValidationQuery", query.Name, query.Name,
                    equivalent
                        ? "Configured read-only query results match canonically."
                        : "Configured read-only query results differ; values were not retained.");
                succeeded = true;
            }
            catch (Exception exception) when (exception is DbException or InvalidOperationException)
            {
                error = $"Query failed ({exception.GetType().Name}); details are available in the redacted application log.";
                Add(findings, request, "DATA.CUSTOM_QUERY_FAILED", ValidationCategory.DataReconciliation,
                    ComparisonClassification.Warning, "ValidationQuery", query.Name, query.Name,
                    "Configured validation query did not complete; inspect the redacted application log.");
            }
            executed.Add(new ExecutedValidationQuery(
                query.Id, query.Name, Hashing.Sha256(query.SourceSql), Hashing.Sha256(query.TargetSql),
                DateTimeOffset.UtcNow - started, succeeded, error));
        }
        return executed;
    }

    private async Task<CanonicalValue> ExecuteCanonicalScalarAsync(
        DbConnection connection,
        string sql,
        int timeout,
        ValidationConfiguration configuration,
        bool sensitive,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return canonicalSerializer.Serialize(
            value, InferRuntimeKind(value), configuration.Canonical, sensitive: sensitive);
    }

    private static bool IsReadOnlyQuery(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    private static CanonicalValueKind InferRuntimeKind(object? value) => value switch
    {
        null or DBNull => CanonicalValueKind.Null,
        bool => CanonicalValueKind.Boolean,
        byte or short or int or long => CanonicalValueKind.IntegralNumber,
        decimal => CanonicalValueKind.ExactNumber,
        float or double => CanonicalValueKind.FloatingPoint,
        DateOnly => CanonicalValueKind.Date,
        TimeOnly or TimeSpan => CanonicalValueKind.Time,
        DateTimeOffset => CanonicalValueKind.TimestampWithTimeZone,
        DateTime => CanonicalValueKind.Timestamp,
        byte[] => CanonicalValueKind.Binary,
        Guid => CanonicalValueKind.Uuid,
        _ => CanonicalValueKind.Text
    };

    private static ValidationSeverity Add(
        List<ValidationFinding> findings,
        ValidationRequest request,
        string ruleId,
        ValidationCategory category,
        ComparisonClassification classification,
        string objectType,
        string source,
        string? target,
        string summary,
        string? sourceDefinition = null,
        string? targetDefinition = null)
    {
        var severity = ValidationSeverityPolicy.Resolve(ruleId, classification, request.Configuration);
        findings.Add(new ValidationFinding(
            ruleId, category, severity, classification, objectType, source, target, summary,
            sourceDefinition, targetDefinition));
        return severity;
    }

    private static bool TryMapping(
        ConversionRun conversion,
        InventoryObjectId sourceId,
        string objectType,
        string sourceName,
        out IdentifierMappingEntry mapping)
    {
        mapping = conversion.IdentifierMappings.LastOrDefault(item =>
            item.SourceObjectId == sourceId &&
            item.ObjectType.Equals(objectType, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(sourceName) ||
             item.SourceName.Equals(sourceName, StringComparison.OrdinalIgnoreCase)))!;
        return mapping is not null;
    }

    private static IdentifierMappingEntry? FindColumnMapping(
        ConversionRun conversion,
        ColumnInventory column)
    {
        /*
         * Current packages identify a column mapping by the column object ID.
         * The table-ID fallback keeps older persisted conversion packages
         * readable without allowing a same-named column from another table
         * to be selected first.
         */
        return conversion.IdentifierMappings.LastOrDefault(item =>
                   item.SourceObjectId == column.ObjectId &&
                   item.ObjectType.Equals("Column", StringComparison.OrdinalIgnoreCase))
               ?? conversion.IdentifierMappings.LastOrDefault(item =>
                   item.SourceObjectId == column.ParentObjectId &&
                   item.ObjectType.Equals("Column", StringComparison.OrdinalIgnoreCase) &&
                   item.SourceName.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> ResolveTargetColumnAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        string mappedColumn,
        string sourceColumn,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT c.column_name
            FROM information_schema.columns AS c
            WHERE c.table_schema = @schema
              AND c.table_name = @table
              AND
              (
                  c.column_name = @mappedColumn
                  OR lower(c.column_name) = lower(@mappedColumn)
                  OR c.column_name = @sourceColumn
                  OR lower(c.column_name) = lower(@sourceColumn)
              )
            ORDER BY
                CASE
                    WHEN c.column_name = @mappedColumn THEN 0
                    WHEN lower(c.column_name) = lower(@mappedColumn) THEN 1
                    WHEN c.column_name = @sourceColumn THEN 2
                    ELSE 3
                END
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("mappedColumn", mappedColumn);
        command.Parameters.AddWithValue("sourceColumn", sourceColumn);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static decimal? GetNullableDecimal(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static string[] MapChildNames(
        ConversionRun conversion,
        InventoryObjectId owner,
        IEnumerable<string> sourceNames) =>
        sourceNames.Select(name =>
            Unquote(conversion.IdentifierMappings.LastOrDefault(item =>
                item.SourceObjectId == owner &&
                item.ObjectType.Equals("Column", StringComparison.OrdinalIgnoreCase) &&
                item.SourceName.Equals(name, StringComparison.OrdinalIgnoreCase))?.TargetName ?? name)).ToArray();

    private static bool ConstraintTypeMatches(ConstraintKind source, string target) =>
        source.ToString().Equals(target, StringComparison.OrdinalIgnoreCase);

    private static async Task<long> CountAsync(
        DbConnection connection,
        string relation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {relation}";
        if (connection is NpgsqlConnection)
        {
            command.CommandText = $"SELECT COUNT(*) FROM {relation}";
        }
        command.CommandTimeout = 300;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static string ComputeSnapshotHash(InventorySnapshot snapshot) =>
        Hashing.Sha256(JsonSerializer.Serialize(new
        {
            snapshot.FormatVersion,
            snapshot.Database.DatabaseName,
            Objects = snapshot.Objects.Select(item => new { item.Id, item.MetadataHash })
        }));

    private static bool ShouldValidateData(ValidationLevel level) =>
        level is ValidationLevel.DataCounts or ValidationLevel.DataSampling or
            ValidationLevel.DataComprehensive or ValidationLevel.Full;

    private static string TargetTypeName(InventoryObjectType type) => type switch
    {
        InventoryObjectType.StoredProcedure => "StoredProcedure",
        InventoryObjectType.UserDefinedType or InventoryObjectType.TableType => "UserDefinedType",
        _ => type.ToString()
    };

    private static string ExpectedPostgreSqlType(ColumnInventory column) =>
        column.SystemTypeName.ToLowerInvariant() switch
        {
            "bit" => "boolean",
            "tinyint" or "smallint" => "smallint",
            "int" => "integer",
            "bigint" => "bigint",
            "real" => "real",
            "float" => "double precision",
            "decimal" or "numeric" => $"numeric({column.Precision},{column.Scale})",
            "money" => "numeric(19,4)",
            "smallmoney" => "numeric(10,4)",
            "uniqueidentifier" => "uuid",
            "binary" or "varbinary" or "image" => "bytea",
            "nvarchar" or "varchar" when column.MaximumLength == -1 => "text",
            "ntext" or "text" => "text",
            "nvarchar" or "nchar" => $"varchar({Math.Max(1, column.MaximumLength / 2)})",
            "varchar" or "char" => $"varchar({column.MaximumLength})",
            "date" => "date",
            "time" => "time without time zone",
            "datetimeoffset" => "timestamp with time zone",
            "datetime" or "datetime2" or "smalldatetime" => "timestamp without time zone",
            "xml" => "xml",
            _ => column.SystemTypeName
        };

    private static CanonicalValueKind InferCanonicalKind(string sourceType) =>
        sourceType.ToLowerInvariant() switch
        {
            "bit" => CanonicalValueKind.Boolean,
            "tinyint" or "smallint" or "int" or "bigint" => CanonicalValueKind.IntegralNumber,
            "decimal" or "numeric" or "money" or "smallmoney" => CanonicalValueKind.ExactNumber,
            "real" or "float" => CanonicalValueKind.FloatingPoint,
            "date" => CanonicalValueKind.Date,
            "time" => CanonicalValueKind.Time,
            "datetime" or "datetime2" or "smalldatetime" => CanonicalValueKind.Timestamp,
            "datetimeoffset" => CanonicalValueKind.TimestampWithTimeZone,
            "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => CanonicalValueKind.Binary,
            "uniqueidentifier" => CanonicalValueKind.Uuid,
            "xml" => CanonicalValueKind.Xml,
            _ => CanonicalValueKind.Text
        };

    private static bool IsNumeric(string sourceType) =>
        sourceType.ToLowerInvariant() is "tinyint" or "smallint" or "int" or "bigint" or
            "decimal" or "numeric" or "money" or "smallmoney" or "real" or "float";

    private static bool IsSensitiveColumn(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ssn", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("creditcard", StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string value) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Unquote(value);

    private static string QuoteSqlServer(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string QuotePostgreSql(string value) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(value);

    private sealed record DataProfile(
        string Checksum,
        IReadOnlyList<ColumnDataMetric> Metrics);

    private sealed record RoutineProbe(
        IReadOnlyList<string> Columns,
        CanonicalValue? FirstValue);

    private sealed class StreamingChecksum(ICanonicalChecksumService checksums, bool ordered)
    {
        private readonly IncrementalHash? _orderedHash = ordered
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        private readonly byte[] _unorderedAccumulator = new byte[32];
        private long _rowCount;

        public void Append(IReadOnlyList<CanonicalValue> row)
        {
            var rowHash = Convert.FromHexString(checksums.HashRow(row));
            if (_orderedHash is not null)
            {
                Span<byte> length = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32BigEndian(length, rowHash.Length);
                _orderedHash.AppendData(length);
                _orderedHash.AppendData(rowHash);
            }
            else
            {
                var carry = 0;
                for (var index = _unorderedAccumulator.Length - 1; index >= 0; index--)
                {
                    var sum = _unorderedAccumulator[index] + rowHash[index] + carry;
                    _unorderedAccumulator[index] = (byte)sum;
                    carry = sum >> 8;
                }
            }
            _rowCount++;
        }

        public string Complete()
        {
            if (_orderedHash is not null)
            {
                return Convert.ToHexString(_orderedHash.GetHashAndReset()).ToLowerInvariant();
            }
            using var final = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            final.AppendData(_unorderedAccumulator);
            Span<byte> count = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(count, _rowCount);
            final.AppendData(count);
            return Convert.ToHexString(final.GetHashAndReset()).ToLowerInvariant();
        }
    }
}
