using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Validation;

public sealed class CanonicalValueSerializer : ICanonicalValueSerializer
{
    public CanonicalValue Serialize(
        object? value,
        CanonicalValueKind kind,
        CanonicalComparisonOptions options,
        bool fixedWidth = false,
        bool sensitive = false)
    {
        if (value is null or DBNull)
        {
            return new CanonicalValue(CanonicalValueKind.Null, "null", sensitive);
        }

        var representation = kind switch
        {
            CanonicalValueKind.Boolean =>
                Convert.ToBoolean(
                    value,
                    CultureInfo.InvariantCulture)
                    ? "true"
                    : "false",

            CanonicalValueKind.IntegralNumber =>
                Convert.ToDecimal(
                        value,
                        CultureInfo.InvariantCulture)
                    .ToString(
                        "0",
                        CultureInfo.InvariantCulture),

            CanonicalValueKind.ExactNumber =>
                FormatDecimal(
                    value,
                    options.DecimalScale),

            CanonicalValueKind.FloatingPoint =>
                Convert.ToDouble(
                        value,
                        CultureInfo.InvariantCulture)
                    .ToString(
                        "R",
                        CultureInfo.InvariantCulture),

            CanonicalValueKind.Date =>
                FormatDate(value),

            CanonicalValueKind.Time =>
                FormatTime(
                    value,
                    options.TimePrecision),

            CanonicalValueKind.Timestamp =>
                FormatTimestamp(
                    value,
                    options.TimePrecision),

            CanonicalValueKind.TimestampWithTimeZone =>
                FormatTimestampWithTimeZone(
                    value,
                    options),

            CanonicalValueKind.Text =>
                FormatString(
                    Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture)
                        ?? string.Empty,
                    fixedWidth,
                    options),

            CanonicalValueKind.Binary =>
                value switch
                {
                    byte[] bytes =>
                        Convert.ToHexString(bytes)
                            .ToLowerInvariant(),

                    ReadOnlyMemory<byte> memory =>
                        Convert.ToHexString(memory.Span)
                            .ToLowerInvariant(),

                    _ =>
                        throw new InvalidOperationException(
                            $"Unsupported binary value type: " +
                            $"{value.GetType().FullName}.")
                },

            CanonicalValueKind.Uuid =>
                value is Guid guid
                    ? guid.ToString(
                            "D",
                            CultureInfo.InvariantCulture)
                        .ToLowerInvariant()
                    : Guid.Parse(
                            Convert.ToString(
                                value,
                                CultureInfo.InvariantCulture)!)
                        .ToString("D")
                        .ToLowerInvariant(),

            CanonicalValueKind.Xml =>
                options.NormalizeXml
                    ? NormalizeXml(
                        Convert.ToString(
                            value,
                            CultureInfo.InvariantCulture)!)
                    : Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture)!,

            CanonicalValueKind.Json =>
                options.NormalizeJsonPropertyOrder
                    ? NormalizeJson(
                        Convert.ToString(
                            value,
                            CultureInfo.InvariantCulture)!)
                    : Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture)!,

            CanonicalValueKind.Spatial =>
                Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture)
                    ?? string.Empty,

            _ =>
                Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture)
                    ?? string.Empty
        };
        representation = representation.Normalize(NormalizationForm.FormC);
        if (sensitive)
        {
            representation = $"sha256:{Hashing.Sha256(representation)}";
        }
        return new CanonicalValue(kind, representation, sensitive);
    }


    private static string FormatDate(object value)
    {
        return value switch
        {
            DateOnly dateOnly =>
                dateOnly.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),

            DateTime dateTime =>
                dateTime.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),

            DateTimeOffset dateTimeOffset =>
                dateTimeOffset.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),

            _ =>
                Convert.ToDateTime(
                        value,
                        CultureInfo.InvariantCulture)
                    .ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)
        };
    }


    public bool AreEquivalent(CanonicalValue left, CanonicalValue right, CanonicalComparisonOptions options)
    {
        if (left.Kind == CanonicalValueKind.Null || right.Kind == CanonicalValueKind.Null)
        {
            return left.Kind == right.Kind;
        }
        if (left.Kind == CanonicalValueKind.FloatingPoint && right.Kind == CanonicalValueKind.FloatingPoint)
        {
            var a = double.Parse(left.Representation, CultureInfo.InvariantCulture);
            var b = double.Parse(right.Representation, CultureInfo.InvariantCulture);
            var difference = Math.Abs(a - b);
            return difference <= options.FloatingPointAbsoluteTolerance ||
                   difference <= Math.Max(Math.Abs(a), Math.Abs(b)) * options.FloatingPointRelativeTolerance;
        }
        return left.Kind == right.Kind &&
               string.Equals(left.Representation, right.Representation, StringComparison.Ordinal);
    }

    private static string FormatDecimal(object value, int? scale)
    {
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (scale is not null)
        {
            number = decimal.Round(number, scale.Value, MidpointRounding.ToEven);
            return number.ToString($"F{scale.Value}", CultureInfo.InvariantCulture);
        }
        return number.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static string FormatTime(object value, int precision)
    {
        var time = value switch
        {
            TimeOnly timeOnly => timeOnly,
            TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
            _ => TimeOnly.FromDateTime(Convert.ToDateTime(value, CultureInfo.InvariantCulture))
        };
        return TruncateFraction(time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), precision);
    }

    private static string FormatTimestamp(object value, int precision)
    {
        var date = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        return TruncateFraction(date.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture), precision);
    }

    private static string FormatTimestampWithTimeZone(object value, CanonicalComparisonOptions options)
    {
        var date = value is DateTimeOffset offset
            ? offset
            : new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture));
        if (options.NormalizeTimestampsToUtc)
        {
            date = date.ToUniversalTime();
        }
        var result = date.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
        return TruncateFraction(result, options.TimePrecision);
    }

    private static string FormatString(string value, bool fixedWidth, CanonicalComparisonOptions options)
    {
        var normalized = fixedWidth && options.TrimFixedWidthTrailingSpaces ? value.TrimEnd(' ') : value;
        return options.CaseInsensitiveStrings ? normalized.ToUpperInvariant() : normalized;
    }

    private static string TruncateFraction(string value, int precision)
    {
        precision = Math.Clamp(precision, 0, 7);
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return value;
        }
        var suffixIndex = value.IndexOfAny(['+', '-'], dot);
        var suffix = suffixIndex >= 0 ? value[suffixIndex..] : string.Empty;
        var fractionEnd = suffixIndex >= 0 ? suffixIndex : value.Length;
        return precision == 0
            ? value[..dot] + suffix
            : value[..Math.Min(dot + 1 + precision, fractionEnd)] + suffix;
    }

    private static string NormalizeXml(string value) =>
        XDocument.Parse(value, LoadOptions.None).ToString(SaveOptions.DisableFormatting);

    private static string NormalizeJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteJson(document.RootElement, writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray())
                {
                    WriteJson(child, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
