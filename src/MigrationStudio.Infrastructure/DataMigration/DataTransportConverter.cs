using System.Data.SqlTypes;
using System.Globalization;
using MigrationStudio.Domain.DataMigration;

namespace MigrationStudio.Infrastructure.DataMigration;

internal static class DataTransportConverter
{
    public static object? ConvertValue(object? value, ColumnMapping mapping)
    {
        if (value is null or DBNull || value is INullable { IsNull: true })
        {
            return null;
        }

        return mapping.TransportKind switch
        {
            DataTransportKind.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            DataTransportKind.Signed16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            DataTransportKind.Signed32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            DataTransportKind.Signed64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            DataTransportKind.ExactNumeric => ConvertDecimal(value),
            DataTransportKind.Floating32 => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            DataTransportKind.Floating64 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            DataTransportKind.Date => DateOnly.FromDateTime(Convert.ToDateTime(value, CultureInfo.InvariantCulture)),
            DataTransportKind.Time => value is TimeSpan time
                ? time
                : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
            DataTransportKind.DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
            DataTransportKind.DateTimeOffset => (value is DateTimeOffset offset
                ? offset
                : new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture))).ToUniversalTime(),
            DataTransportKind.Uuid => value is Guid guid
                ? guid
                : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            DataTransportKind.Binary => ConvertBinary(value),
            DataTransportKind.Xml => value is SqlXml xml ? xml.Value : Convert.ToString(value, CultureInfo.InvariantCulture),
            DataTransportKind.Text or DataTransportKind.Json or DataTransportKind.Spatial =>
                Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => NormalizeProviderValue(value)
        };
    }

    public static long EstimateBytes(object? value) =>
        value switch
        {
            null => 1,
            byte[] bytes => bytes.LongLength,
            string text => System.Text.Encoding.UTF8.GetByteCount(text),
            char[] chars => System.Text.Encoding.UTF8.GetByteCount(chars),
            _ => 32
        };

    private static decimal ConvertDecimal(object value) =>
        value is SqlDecimal sqlDecimal
            ? sqlDecimal.Value
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static byte[] ConvertBinary(object value) =>
        value switch
        {
            byte[] bytes => bytes,
            SqlBinary binary => binary.Value,
            SqlBytes bytes => bytes.Value ?? [],
            _ => throw new InvalidCastException($"Cannot transport {value.GetType().Name} as binary data.")
        };

    private static object NormalizeProviderValue(object value) =>
        value switch
        {
            SqlString item => item.Value,
            SqlBoolean item => item.Value,
            SqlInt16 item => item.Value,
            SqlInt32 item => item.Value,
            SqlInt64 item => item.Value,
            SqlSingle item => item.Value,
            SqlDouble item => item.Value,
            SqlDateTime item => item.Value,
            SqlGuid item => item.Value,
            _ => value
        };
}
