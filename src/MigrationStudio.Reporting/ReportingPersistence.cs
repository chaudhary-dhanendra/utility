using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Reporting;
using MigrationStudio.Application.Security;
using MigrationStudio.Domain.Reporting;

namespace MigrationStudio.Reporting;

public sealed class JsonManualReviewStore(IApplicationPaths paths) : IManualReviewStore, IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<ManualReviewItem>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ManualReviewItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        Validate(item);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = items.FindIndex(existing => existing.Id == item.Id);
            var normalized = item with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                ReviewedAt = item.Status is ManualReviewStatus.Resolved or ManualReviewStatus.AcceptedRisk or
                    ManualReviewStatus.NotApplicable
                    ? item.ReviewedAt ?? DateTimeOffset.UtcNow
                    : null
            };
            if (index >= 0)
            {
                EnsureTransition(items[index].Status, normalized.Status);
                items[index] = normalized;
            }
            else
            {
                items.Add(normalized with
                {
                    CreatedAt = normalized.CreatedAt == default ? DateTimeOffset.UtcNow : normalized.CreatedAt
                });
            }
            await SaveCoreAsync(items, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReopenAsync(Guid id, string comment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Manual-review item {id} does not exist.");
            }
            var current = items[index];
            if (current.Status is ManualReviewStatus.Open or ManualReviewStatus.InProgress)
            {
                throw new InvalidOperationException("Only a completed manual-review item can be reopened.");
            }
            items[index] = current with
            {
                Status = ManualReviewStatus.Open,
                Comments = string.IsNullOrWhiteSpace(current.Comments)
                    ? comment.Trim()
                    : $"{current.Comments}{Environment.NewLine}{comment.Trim()}",
                Resolution = null,
                ReviewedAt = null,
                ReviewedBy = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await SaveCoreAsync(items, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ManualReviewItem>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var path = GetPath();
        if (!File.Exists(path))
        {
            return [];
        }
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await JsonSerializer.DeserializeAsync<List<ManualReviewItem>>(
                   stream, Options, cancellationToken).ConfigureAwait(false) ?? [];
    }

    private Task SaveCoreAsync(
        IReadOnlyList<ManualReviewItem> items,
        CancellationToken cancellationToken) =>
        AtomicJson.WriteAsync(GetPath(), items, Options, cancellationToken);

    private string GetPath() =>
        Path.Combine(paths.ApplicationDataDirectory, "manual-review.json");

    private static void Validate(ManualReviewItem item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.FindingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Title);
        if (item.Status is ManualReviewStatus.Resolved or ManualReviewStatus.AcceptedRisk &&
            string.IsNullOrWhiteSpace(item.Resolution))
        {
            throw new InvalidOperationException("Resolved and accepted-risk items require a recorded resolution.");
        }
    }

    private static void EnsureTransition(ManualReviewStatus from, ManualReviewStatus to)
    {
        var allowed = from == to ||
                      from == ManualReviewStatus.Open && to is ManualReviewStatus.InProgress or
                          ManualReviewStatus.Resolved or ManualReviewStatus.AcceptedRisk or
                          ManualReviewStatus.NotApplicable ||
                      from == ManualReviewStatus.InProgress && to is ManualReviewStatus.Resolved or
                          ManualReviewStatus.AcceptedRisk or ManualReviewStatus.NotApplicable;
        if (!allowed)
        {
            throw new InvalidOperationException($"Manual-review transition {from} -> {to} requires reopen.");
        }
    }
}

public sealed class JsonRunHistoryStore(
    IApplicationPaths paths,
    ISensitiveDataRedactor redactor) : IRunHistoryStore, IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task SaveAsync(RunHistoryRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = GetDirectory();
            Directory.CreateDirectory(directory);
            var payloadName = $"{record.Entry.RunId:N}.json";
            var payloadPath = Path.Combine(directory, payloadName);
            var sanitizedPayload = JsonSanitizer.Sanitize(record.Payload.GetRawText(), redactor);
            using var payload = JsonDocument.Parse(sanitizedPayload);
            var entry = record.Entry with { PayloadFile = payloadName };
            await AtomicJson.WriteAsync(
                payloadPath, payload.RootElement, Options, cancellationToken).ConfigureAwait(false);
            var entries = (await LoadIndexAsync(cancellationToken).ConfigureAwait(false)).ToList();
            entries.RemoveAll(item => item.RunId == entry.RunId);
            entries.Add(entry);
            await AtomicJson.WriteAsync(
                GetIndexPath(), entries.OrderByDescending(item => item.StartedAt).ToArray(),
                Options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RunHistoryEntry>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RunHistoryRecord?> LoadAsync(Guid runId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = (await LoadIndexAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.RunId == runId);
            if (entry is null)
            {
                return null;
            }
            var path = Path.Combine(GetDirectory(), entry.PayloadFile);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Run-history payload '{entry.PayloadFile}' is missing.");
            }
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            using var document = await JsonDocument.ParseAsync(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RunHistoryRecord(entry, document.RootElement.Clone());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<RunHistoryEntry>> LoadIndexAsync(CancellationToken cancellationToken)
    {
        var path = GetIndexPath();
        if (!File.Exists(path))
        {
            return [];
        }
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await JsonSerializer.DeserializeAsync<List<RunHistoryEntry>>(
                   stream, Options, cancellationToken).ConfigureAwait(false) ?? [];
    }

    private string GetDirectory() =>
        Path.Combine(paths.ApplicationDataDirectory, "run-history");

    private string GetIndexPath() => Path.Combine(GetDirectory(), "index.json");
}

public sealed class SanitizedLogExporter(
    IApplicationPaths paths,
    ISensitiveDataRedactor redactor) : ISanitizedLogExporter
{
    public async Task<string> ExportAsync(
        string destinationDirectory,
        IReadOnlySet<Guid> correlationIds,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(
            destinationDirectory,
            $"MigrationStudio-SanitizedLogs-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false));
        foreach (var source in Directory.EnumerateFiles(paths.LogsDirectory, "*.jsonl")
                     .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            await using var stream = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (correlationIds.Count > 0 &&
                    !correlationIds.Any(id => line.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                await writer.WriteLineAsync(redactor.Redact(line).AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        return destination;
    }
}

internal static class AtomicJson
{
    public static async Task WriteAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            await JsonSerializer.SerializeAsync(
                stream, value, options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, path, true);
    }
}

internal static class JsonSanitizer
{
    public static string Sanitize(string json, ISensitiveDataRedactor redactor)
    {
        var node = JsonNode.Parse(json) ??
                   throw new InvalidDataException("JSON payload is empty.");
        return SanitizeNode(node, redactor)!.ToJsonString();
    }

    private static JsonNode? SanitizeNode(JsonNode? node, ISensitiveDataRedactor redactor)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return JsonValue.Create(redactor.Redact(text));
        }
        if (node is JsonObject jsonObject)
        {
            var sanitized = new JsonObject();
            foreach (var property in jsonObject)
            {
                sanitized[property.Key] = SanitizeNode(property.Value, redactor);
            }
            return sanitized;
        }
        if (node is JsonArray jsonArray)
        {
            var sanitized = new JsonArray();
            foreach (var child in jsonArray)
            {
                sanitized.Add(SanitizeNode(child, redactor));
            }
            return sanitized;
        }
        return node?.DeepClone();
    }
}
