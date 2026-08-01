using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.DataMigration;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class CanonicalValueFormatter : ICanonicalValueFormatter
{
    public string Format(object? value, DataTransportKind kind)
    {
        if (value is null or DBNull)
        {
            return "N";
        }

        return kind switch
        {
            DataTransportKind.Boolean => (Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                ? "B1"
                : "B0"),
            DataTransportKind.Signed16 or DataTransportKind.Signed32 or DataTransportKind.Signed64 =>
                "I" + Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture),
            DataTransportKind.ExactNumeric => "D" + Convert.ToDecimal(value, CultureInfo.InvariantCulture)
                .ToString("G29", CultureInfo.InvariantCulture),
            DataTransportKind.Floating32 => "F" + Convert.ToSingle(value, CultureInfo.InvariantCulture)
                .ToString("R", CultureInfo.InvariantCulture),
            DataTransportKind.Floating64 => "R" + Convert.ToDouble(value, CultureInfo.InvariantCulture)
                .ToString("R", CultureInfo.InvariantCulture),
            DataTransportKind.Date => "A" + AsDateTime(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DataTransportKind.Time => "T" + AsTimeSpan(value).ToString("c", CultureInfo.InvariantCulture),
            DataTransportKind.DateTime => "M" + AsDateTime(value).ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff",
                CultureInfo.InvariantCulture),
            DataTransportKind.DateTimeOffset => "Z" + AsDateTimeOffset(value).ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            DataTransportKind.Binary => "X" + Convert.ToHexString((byte[])value).ToLowerInvariant(),
            DataTransportKind.Uuid => "G" + AsGuid(value).ToString("D"),
            _ => EncodeString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    public string ComputeRowHash(IReadOnlyList<(object? Value, DataTransportKind Kind)> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var (value, kind) in values)
        {
            var bytes = Encoding.UTF8.GetBytes(Format(value, kind));
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string EncodeString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return $"S{bytes.Length.ToString(CultureInfo.InvariantCulture)}:{Convert.ToBase64String(bytes)}";
    }

    private static DateTime AsDateTime(object value) =>
        value is DateTime dateTime
            ? dateTime
            : Convert.ToDateTime(value, CultureInfo.InvariantCulture);

    private static TimeSpan AsTimeSpan(object value) =>
        value is TimeSpan timeSpan
            ? timeSpan
            : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private static DateTimeOffset AsDateTimeOffset(object value) =>
        value is DateTimeOffset dateTimeOffset
            ? dateTimeOffset
            : new DateTimeOffset(AsDateTime(value));

    private static Guid AsGuid(object value) =>
        value is Guid guid
            ? guid
            : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
}
