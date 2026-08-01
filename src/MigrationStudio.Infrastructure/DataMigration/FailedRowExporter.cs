using System.Globalization;
using System.Text;
using System.Text.Json;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Security;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class FailedRowExporter(IApplicationPaths paths) : IFailedRowExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<string> ExportJsonAsync(
        Guid runId,
        IReadOnlyList<FailedRowRecord> rows,
        bool includeUnmaskedSensitiveValues,
        CancellationToken cancellationToken)
    {
        var path = CreatePath(runId, "json");
        var safe = rows.Select(row => ToSerializable(row, includeUnmaskedSensitiveValues)).ToArray();
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, safe, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportCsvAsync(
        Guid runId,
        IReadOnlyList<FailedRowRecord> rows,
        bool includeUnmaskedSensitiveValues,
        CancellationToken cancellationToken)
    {
        var path = CreatePath(runId, "csv");
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteLineAsync("Table,SafeKey,Column,Value,Error").ConfigureAwait(false);
        foreach (var row in rows)
        {
            foreach (var value in row.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var safeValue = RenderValue(value.Value.Value, value.Value.IsSensitive, value.Value.IsBinary,
                    includeUnmaskedSensitiveValues);
                await writer.WriteLineAsync(string.Join(
                    ',',
                    Csv(row.Table),
                    Csv(row.SafeKey),
                    Csv(value.Key),
                    Csv(safeValue),
                    Csv(row.ErrorReason))).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return path;
    }

    private string CreatePath(Guid runId, string extension)
    {
        var directory = Path.Combine(paths.ApplicationDataDirectory, "ProtectedFailedRows");
        Directory.CreateDirectory(directory);
        return Path.Combine(
            directory,
            $"{runId:N}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{extension}");
    }

    private static object ToSerializable(FailedRowRecord row, bool unmasked) => new
    {
        row.Table,
        row.SafeKey,
        Values = row.Values.ToDictionary(
            item => item.Key,
            item => RenderValue(item.Value.Value, item.Value.IsSensitive, item.Value.IsBinary, unmasked)),
        row.ErrorReason
    };

    private static string RenderValue(object? value, bool sensitive, bool binary, bool unmasked)
    {
        if (sensitive && !unmasked)
        {
            return "***MASKED***";
        }

        if (value is null)
        {
            return "<NULL>";
        }

        if (binary && value is byte[] bytes)
        {
            return unmasked
                ? Convert.ToBase64String(bytes)
                : $"<BINARY length={bytes.LongLength.ToString(CultureInfo.InvariantCulture)}>";
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Csv(string? value) =>
        $"\"{SpreadsheetCellSanitizer.Escape(value).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
