using System.Globalization;
using System.IO;
using System.Text.Json;
using MigrationStudio.Application.Security;
using Serilog.Events;
using Serilog.Formatting;

namespace MigrationStudio.Desktop.Logging;

public sealed class RedactingJsonFormatter(ISensitiveDataRedactor redactor) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        var properties = logEvent.Properties.ToDictionary(
            pair => pair.Key,
            pair => redactor.Redact(Render(pair.Value)),
            StringComparer.Ordinal);
        var payload = new
        {
            timestamp = logEvent.Timestamp,
            level = logEvent.Level.ToString(),
            message = redactor.Redact(logEvent.RenderMessage(CultureInfo.InvariantCulture)),
            exception = logEvent.Exception is null ? null : redactor.Redact(logEvent.Exception.ToString()),
            properties
        };

        output.Write(JsonSerializer.Serialize(payload));
        output.WriteLine();
    }

    private static string Render(LogEventPropertyValue value)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        value.Render(writer, format: null, formatProvider: CultureInfo.InvariantCulture);
        return writer.ToString();
    }
}
