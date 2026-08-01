using System.Text;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using Npgsql;

namespace MigrationStudio.Validation;

public sealed class GeneratedSqlValidator : IGeneratedSqlValidator
{
    public Task<SqlValidationResult> ValidateOfflineAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return Task.FromResult(new SqlValidationResult(false, false, null, "Generated SQL is empty.", null));
        }

        var error = ValidateStructure(sql);
        error ??= ValidateGeneratedPatterns(sql);
        return Task.FromResult(error is null
            ? new SqlValidationResult(true, false, null, null, null)
            : new SqlValidationResult(false, false, null, error, null));
    }

    public async Task<IReadOnlyDictionary<string, SqlValidationResult>> ValidateLiveAsync(
        IReadOnlyList<ConversionArtifact> artifacts,
        PostgreSqlValidationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(options);
        if (options.CommandTimeoutSeconds is < 1 or > 7200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The live-validation command timeout must be between 1 and 7200 seconds.");
        }

        var runId = Guid.NewGuid();
        var reusable = options.ReusableSuccessfulResults
            .Where(item =>
                artifacts.Any(artifact =>
                    artifact.ContentHash.Equals(item.Key, StringComparison.Ordinal)) &&
                item.Value.Outcome == LiveSqlValidationOutcome.Passed &&
                item.Value.WasLiveValidated &&
                item.Value.IsStructurallyValid)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var resultsBeforeExecution = reusable.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        foreach (var artifact in artifacts.Where(IsNonExecutableArtifact))
        {
            var outcome = artifact.Classification == ConversionClassification.Unsupported
                ? LiveSqlValidationOutcome.Unsupported
                : LiveSqlValidationOutcome.Manual;
            resultsBeforeExecution[artifact.ContentHash] = BaseResult(
                artifact,
                runId,
                LiveSqlValidationConfidence.None,
                outcome) with
            {
                Message = outcome == LiveSqlValidationOutcome.Unsupported
                    ? "Unsupported artifacts are never executed during live validation."
                    : "Manual-review artifacts are never executed during live validation."
            };
        }

        var changedHashes = artifacts
            .Where(item =>
                !IsNonExecutableArtifact(item) &&
                !reusable.ContainsKey(item.ContentHash))
            .Select(item => item.ContentHash)
            .ToHashSet(StringComparer.Ordinal);
        if (changedHashes.Count == 0)
        {
            options.Progress?.Report(new LiveSqlValidationProgress(
                artifacts.Count,
                artifacts.Count,
                string.Empty,
                $"Reused {artifacts.Count:N0} unchanged successful validation results."));
            return resultsBeforeExecution;
        }

        var executionArtifacts = SelectArtifactsForExecution(artifacts, changedHashes);
        if (options.PreferDisposableDatabase)
        {
            try
            {
                return await ValidateInDisposableDatabaseAsync(
                    executionArtifacts,
                    options,
                    runId,
                    resultsBeforeExecution,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DisposableValidationUnavailableException) when (
                options.AllowRollbackTransactionFallback)
            {
                // CREATEDB is intentionally optional. The fallback is labeled
                // lower-confidence on every result instead of being presented
                // as equivalent to validation in an isolated database.
            }
        }

        if (!options.AllowRollbackTransactionFallback)
        {
            throw new InvalidOperationException(
                "Disposable PostgreSQL validation could not be created and rollback-transaction fallback is disabled.");
        }

        return await ValidateInRollbackTransactionAsync(
            executionArtifacts,
            options,
            runId,
            resultsBeforeExecution,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, SqlValidationResult>>
        ValidateInDisposableDatabaseAsync(
            IReadOnlyList<ConversionArtifact> artifacts,
            PostgreSqlValidationOptions options,
            Guid runId,
            IReadOnlyDictionary<string, SqlValidationResult> reusable,
            CancellationToken cancellationToken)
    {
        var sourceBuilder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        var validationDatabase = BuildValidationDatabaseName(options.ValidationSchemaPrefix, runId);
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            Database = options.MaintenanceDatabase,
            Pooling = false
        };
        var validationBuilder = new NpgsqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            Database = validationDatabase,
            Pooling = false
        };
        var created = false;

        await using var maintenance = new NpgsqlConnection(maintenanceBuilder.ConnectionString);
        try
        {
            await maintenance.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == "3D000")
        {
            throw new DisposableValidationUnavailableException(
                "The configured PostgreSQL maintenance database is unavailable.",
                exception);
        }

        try
        {
            try
            {
                await using var create = new NpgsqlCommand(
                    $"CREATE DATABASE {QuoteIdentifier(validationDatabase)}",
                    maintenance)
                {
                    CommandTimeout = options.CommandTimeoutSeconds
                };
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                created = true;
            }
            catch (PostgresException exception) when (
                exception.SqlState is "42501" or "25001" or "0A000")
            {
                throw new DisposableValidationUnavailableException(
                    "The PostgreSQL role cannot create a disposable validation database.",
                    exception);
            }

            await using var validation = new NpgsqlConnection(validationBuilder.ConnectionString);
            await validation.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteArtifactsAsync(
                artifacts,
                validation,
                null,
                runId,
                LiveSqlValidationConfidence.DisposableDatabase,
                options.CommandTimeoutSeconds,
                reusable,
                options.Progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (created)
            {
                try
                {
                    await using var drop = new NpgsqlCommand(
                        $"DROP DATABASE IF EXISTS {QuoteIdentifier(validationDatabase)} WITH (FORCE)",
                        maintenance)
                    {
                        CommandTimeout = options.CommandTimeoutSeconds
                    };
                    await drop.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the validation result/exception. A future audit can
                    // identify the run-owned database by its unique, sanitized name.
                }
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, SqlValidationResult>>
        ValidateInRollbackTransactionAsync(
            IReadOnlyList<ConversionArtifact> artifacts,
            PostgreSqlValidationOptions options,
            Guid runId,
            IReadOnlyDictionary<string, SqlValidationResult> reusable,
            CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await ExecuteArtifactsAsync(
                artifacts,
                connection,
                transaction,
                runId,
                LiveSqlValidationConfidence.RollbackTransaction,
                options.CommandTimeoutSeconds,
                reusable,
                options.Progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyDictionary<string, SqlValidationResult>>
        ExecuteArtifactsAsync(
            IReadOnlyList<ConversionArtifact> artifacts,
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            Guid runId,
            LiveSqlValidationConfidence confidence,
            int commandTimeoutSeconds,
            IReadOnlyDictionary<string, SqlValidationResult> reusable,
            IProgress<LiveSqlValidationProgress>? progress,
            CancellationToken cancellationToken)
    {
        var results = reusable.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        var failedObjects = new HashSet<InventoryObjectId>();
        var blockedObjects = new HashSet<InventoryObjectId>();
        var unavailableObjects = new HashSet<InventoryObjectId>();
        var ordered = OrderForValidation(artifacts);
        var availableObjectIds = ordered.Select(item => item.SourceObjectId).ToHashSet();
        var artifactIndex = 0;
        var completed = 0;

        foreach (var artifact in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LiveSqlValidationProgress(
                completed,
                ordered.Count,
                artifact.TargetObjectId.QualifiedName,
                reusable.ContainsKey(artifact.ContentHash)
                    ? "Preparing unchanged dependency in the isolated validation environment."
                    : "Executing changed artifact on PostgreSQL."));
            var blockingDependencies = artifact.Dependencies
                .Where(item =>
                    failedObjects.Contains(item) ||
                    blockedObjects.Contains(item) ||
                    unavailableObjects.Contains(item) ||
                    !availableObjectIds.Contains(item))
                .Distinct()
                .OrderBy(item => item.Value)
                .ToArray();
            if (blockingDependencies.Length > 0)
            {
                blockedObjects.Add(artifact.SourceObjectId);
                results[artifact.ContentHash] = BaseResult(
                    artifact,
                    runId,
                    confidence,
                    LiveSqlValidationOutcome.BlockedByDependency) with
                {
                    Message = "Live validation was not attempted because a required dependency failed.",
                    BlockingDependencies = blockingDependencies
                };
                completed++;
                continue;
            }

            if (artifact.Classification == ConversionClassification.Unsupported)
            {
                unavailableObjects.Add(artifact.SourceObjectId);
                results[artifact.ContentHash] = BaseResult(
                    artifact,
                    runId,
                    confidence,
                    LiveSqlValidationOutcome.Unsupported) with
                {
                    Message = "Unsupported artifacts are never executed during live validation."
                };
                completed++;
                continue;
            }

            if (artifact.RequiresManualReview ||
                artifact.PostgreSqlDefinition.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                unavailableObjects.Add(artifact.SourceObjectId);
                results[artifact.ContentHash] = BaseResult(
                    artifact,
                    runId,
                    confidence,
                    LiveSqlValidationOutcome.Manual) with
                {
                    Message = "Manual-review artifacts are never executed during live validation."
                };
                completed++;
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var savepoint = transaction is null
                ? null
                : $"migrationstudio_{artifactIndex++}";
            if (savepoint is not null)
            {
                await transaction!.SaveAsync(savepoint, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await using var command = new NpgsqlCommand(
                    artifact.PostgreSqlDefinition,
                    connection,
                    transaction)
                {
                    CommandTimeout = commandTimeoutSeconds
                };
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                if (!reusable.ContainsKey(artifact.ContentHash))
                {
                    results[artifact.ContentHash] = BaseResult(
                        artifact,
                        runId,
                        confidence,
                        LiveSqlValidationOutcome.Passed) with
                    {
                        IsStructurallyValid = true,
                        WasLiveValidated = true,
                        Elapsed = stopwatch.Elapsed
                    };
                }
                if (savepoint is not null)
                {
                    await transaction!.ReleaseAsync(savepoint, cancellationToken).ConfigureAwait(false);
                }
                completed++;
            }
            catch (PostgresException exception)
            {
                stopwatch.Stop();
                failedObjects.Add(artifact.SourceObjectId);
                if (savepoint is not null)
                {
                    await transaction!.RollbackAsync(savepoint, cancellationToken).ConfigureAwait(false);
                }

                results[artifact.ContentHash] = BaseResult(
                    artifact,
                    runId,
                    confidence,
                    LiveSqlValidationOutcome.Failed) with
                {
                    IsStructurallyValid = false,
                    WasLiveValidated = true,
                    SqlState = exception.SqlState,
                    Message = exception.MessageText,
                    ErrorPosition = exception.Position,
                    Detail = exception.Detail,
                    Hint = exception.Hint,
                    Where = exception.Where,
                    SchemaName = exception.SchemaName,
                    TableName = exception.TableName,
                    ColumnName = exception.ColumnName,
                    ConstraintName = exception.ConstraintName,
                    DataTypeName = exception.DataTypeName,
                    Elapsed = stopwatch.Elapsed,
                    IsRetryable = IsRetryable(exception.SqlState)
                };
                completed++;
            }
        }

        progress?.Report(new LiveSqlValidationProgress(
            completed,
            ordered.Count,
            string.Empty,
            $"Live validation completed for {completed:N0} artifacts."));
        return results;
    }

    private static ConversionArtifact[] SelectArtifactsForExecution(
        IReadOnlyList<ConversionArtifact> artifacts,
        HashSet<string> changedHashes)
    {
        var bySource = artifacts
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var selected = artifacts
            .Where(item => changedHashes.Contains(item.ContentHash))
            .ToHashSet();
        var pending = new Queue<ConversionArtifact>(selected);
        while (pending.TryDequeue(out var artifact))
        {
            foreach (var dependency in artifact.Dependencies)
            {
                if (!bySource.TryGetValue(dependency, out var candidates))
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (selected.Add(candidate))
                    {
                        pending.Enqueue(candidate);
                    }
                }
            }
        }

        return artifacts.Where(selected.Contains).ToArray();
    }

    private static bool IsNonExecutableArtifact(ConversionArtifact artifact) =>
        artifact.Classification == ConversionClassification.Unsupported ||
        artifact.RequiresManualReview ||
        artifact.PostgreSqlDefinition.TrimStart().StartsWith("--", StringComparison.Ordinal);

    private static SqlValidationResult BaseResult(
        ConversionArtifact artifact,
        Guid runId,
        LiveSqlValidationConfidence confidence,
        LiveSqlValidationOutcome outcome) =>
        new(
            artifact.Validation.IsStructurallyValid,
            false,
            null,
            null,
            null)
        {
            ValidationRunId = runId,
            Confidence = confidence,
            Outcome = outcome,
            ValidatedSqlHash = artifact.ContentHash,
            ValidatedAt = DateTimeOffset.UtcNow
        };

    private static List<ConversionArtifact> OrderForValidation(
        IReadOnlyList<ConversionArtifact> artifacts)
        => ArtifactDependencyPlanner.Order(
                artifacts,
                item => item.SourceObjectId,
                item => item.Dependencies,
                item => DeploymentPhaseOrdering.GetRank(
                    item.DeploymentPhase,
                    item.TargetObjectId.ObjectType),
                item => $"{item.TargetObjectId.QualifiedName}|{item.ContentHash}",
                failOnCycle: false)
            .ToList();

    private static string BuildValidationDatabaseName(string prefix, Guid runId)
    {
        var safePrefix = new string(prefix
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character == '_'
                ? character
                : '_')
            .ToArray()).Trim('_');
        if (safePrefix.Length == 0)
        {
            safePrefix = "migrationstudio_validation";
        }

        var suffix = runId.ToString("N")[..12];
        var maximumPrefixLength = 63 - suffix.Length - 1;
        return $"{safePrefix[..Math.Min(safePrefix.Length, maximumPrefixLength)]}_{suffix}";
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsRetryable(string? sqlState) =>
        sqlState is not null &&
        (sqlState.StartsWith("08", StringComparison.Ordinal) ||
         sqlState is "40001" or "40P01" or "55P03" or "57014" or "57P01");

    private static string? ValidateStructure(string sql)
    {
        var parentheses = 0;
        var index = 0;
        var state = ScanState.Normal;
        var dollarTag = string.Empty;
        while (index < sql.Length)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            switch (state)
            {
                case ScanState.Normal when current == '\'':
                    state = ScanState.String;
                    break;
                case ScanState.Normal when current == '"':
                    state = ScanState.Identifier;
                    break;
                case ScanState.Normal when current == '-' && next == '-':
                    state = ScanState.LineComment;
                    index++;
                    break;
                case ScanState.Normal when current == '/' && next == '*':
                    state = ScanState.BlockComment;
                    index++;
                    break;
                case ScanState.Normal when current == '$':
                    dollarTag = ReadDollarTag(sql, index);
                    if (dollarTag.Length > 0)
                    {
                        state = ScanState.DollarString;
                        index += dollarTag.Length - 1;
                    }
                    break;
                case ScanState.Normal when current == '(':
                    parentheses++;
                    break;
                case ScanState.Normal when current == ')':
                    parentheses--;
                    if (parentheses < 0)
                    {
                        return "Generated SQL contains an unmatched closing parenthesis.";
                    }
                    break;
                case ScanState.String when current == '\'' && next == '\'':
                    index++;
                    break;
                case ScanState.String when current == '\'':
                    state = ScanState.Normal;
                    break;
                case ScanState.Identifier when current == '"' && next == '"':
                    index++;
                    break;
                case ScanState.Identifier when current == '"':
                    state = ScanState.Normal;
                    break;
                case ScanState.LineComment when current is '\r' or '\n':
                    state = ScanState.Normal;
                    break;
                case ScanState.BlockComment when current == '*' && next == '/':
                    state = ScanState.Normal;
                    index++;
                    break;
                case ScanState.DollarString when sql.AsSpan(index).StartsWith(dollarTag, StringComparison.Ordinal):
                    state = ScanState.Normal;
                    index += dollarTag.Length - 1;
                    break;
            }
            index++;
        }

        if (parentheses != 0)
        {
            return "Generated SQL contains unbalanced parentheses.";
        }
        if (state is ScanState.String or ScanState.Identifier or ScanState.BlockComment or ScanState.DollarString)
        {
            return "Generated SQL contains an unterminated literal, identifier, comment, or dollar-quoted body.";
        }
        if (ContainsBatchSeparator(sql))
        {
            return "Generated PostgreSQL contains the SQL Server GO batch separator.";
        }
        return null;
    }

    private static string? ValidateGeneratedPatterns(string sql)
    {
        var code = MaskLiteralsIdentifiersAndComments(sql);
        if (Regex.IsMatch(code, @"@\p{L}[\p{L}\p{N}_$#]*", RegexOptions.CultureInvariant))
        {
            return "Generated PostgreSQL contains an unresolved SQL Server @variable.";
        }
        if (Regex.IsMatch(code, @"\bSELECT\s+SELECT\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Generated PostgreSQL contains an invalid SELECT SELECT sequence.";
        }
        if (Regex.IsMatch(code, @"\bSELECT\s+@\p{L}[\p{L}\p{N}_$#]*\s*=",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Generated PostgreSQL contains a SQL Server SELECT @variable assignment.";
        }
        if (Regex.IsMatch(
                code,
                @"\b(?:SYSUTCDATETIME|GETUTCDATE|SYSDATETIME|GETDATE)\s*\(",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Generated PostgreSQL contains an untranslated SQL Server temporal function.";
        }
        if (Regex.IsMatch(code, @"\bLANGUAGE\s+sql\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            Regex.IsMatch(code, @"\b(?:DECLARE|SET|IF|BEGIN)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "A LANGUAGE sql routine contains procedural statements and must be converted to PL/pgSQL.";
        }
        if (Regex.IsMatch(
                code,
                @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\b[\s\S]*?\(\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Generated PostgreSQL contains an index with an empty column or expression list.";
        }
        if (Regex.IsMatch(code, @"\bRETURN\s*;",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Generated PostgreSQL contains an empty RETURN expression.";
        }
        if (Regex.IsMatch(code, @"(?:^|[^\w])\.\s*\.|(?:^|[^\w])\.\s*[;,)]",
                RegexOptions.CultureInvariant))
        {
            return "Generated PostgreSQL contains an invalid schema-qualified identity.";
        }

        return ValidateRoutineVariables(code);
    }

    private static string? ValidateRoutineVariables(string code)
    {
        if (!Regex.IsMatch(code, @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:FUNCTION|PROCEDURE)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var returnsIndex = code.IndexOf("RETURNS", StringComparison.OrdinalIgnoreCase);
        var languageIndex = code.IndexOf("LANGUAGE", StringComparison.OrdinalIgnoreCase);
        var signatureEnd = returnsIndex >= 0
            ? returnsIndex
            : languageIndex >= 0 ? languageIndex : code.Length;
        foreach (Match match in Regex.Matches(
                     code[..signatureEnd],
                     @"\b(p_[\p{L}_][\p{L}\p{N}_$]*)\s+",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            allowed.Add(match.Groups[1].Value);
        }
        foreach (Match match in Regex.Matches(
                     code,
                     @"\b(v_[\p{L}_][\p{L}\p{N}_$]*)\s+(?:""[^""]+""|[\p{L}_])",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            allowed.Add(match.Groups[1].Value);
        }

        foreach (Match match in Regex.Matches(
                     code,
                     @"\b([pv]_[\p{L}_][\p{L}\p{N}_$]*)\b",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!allowed.Contains(match.Groups[1].Value))
            {
                return $"Generated routine references undeclared variable '{match.Groups[1].Value}'.";
            }
        }
        return null;
    }

    private static string MaskLiteralsIdentifiersAndComments(string sql)
    {
        var masked = sql.ToCharArray();
        var state = ScanState.Normal;
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            if (state == ScanState.Normal && current == '$')
            {
                var tag = ReadDollarTag(sql, index);
                if (tag.Length > 0)
                {
                    Array.Fill(masked, ' ', index, tag.Length);
                    index += tag.Length - 1;
                    continue;
                }
            }
            switch (state)
            {
                case ScanState.Normal when current == '\'':
                    masked[index] = ' ';
                    state = ScanState.String;
                    break;
                case ScanState.Normal when current == '"':
                    masked[index] = ' ';
                    state = ScanState.Identifier;
                    break;
                case ScanState.Normal when current == '-' && next == '-':
                    masked[index] = masked[index + 1] = ' ';
                    index++;
                    state = ScanState.LineComment;
                    break;
                case ScanState.Normal when current == '/' && next == '*':
                    masked[index] = masked[index + 1] = ' ';
                    index++;
                    state = ScanState.BlockComment;
                    break;
                case ScanState.String:
                    masked[index] = ' ';
                    if (current == '\'' && next == '\'')
                    {
                        masked[++index] = ' ';
                    }
                    else if (current == '\'')
                    {
                        state = ScanState.Normal;
                    }
                    break;
                case ScanState.Identifier:
                    masked[index] = ' ';
                    if (current == '"' && next == '"')
                    {
                        masked[++index] = ' ';
                    }
                    else if (current == '"')
                    {
                        state = ScanState.Normal;
                    }
                    break;
                case ScanState.LineComment:
                    masked[index] = ' ';
                    if (current is '\r' or '\n')
                    {
                        state = ScanState.Normal;
                    }
                    break;
                case ScanState.BlockComment:
                    masked[index] = ' ';
                    if (current == '*' && next == '/')
                    {
                        masked[++index] = ' ';
                        state = ScanState.Normal;
                    }
                    break;
            }
        }
        return new string(masked);
    }

    private static string ReadDollarTag(string sql, int index)
    {
        var end = sql.IndexOf('$', index + 1);
        if (end < 0)
        {
            return string.Empty;
        }
        var tag = sql[(index + 1)..end];
        return tag.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? sql[index..(end + 1)]
            : string.Empty;
    }

    private static bool ContainsBatchSeparator(string sql)
    {
        using var reader = new StringReader(sql);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private enum ScanState
    {
        Normal,
        String,
        Identifier,
        LineComment,
        BlockComment,
        DollarString
    }

    private sealed class DisposableValidationUnavailableException(
        string message,
        Exception innerException) : Exception(message, innerException);
}
