using System.Globalization;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Infrastructure.DataMigration;

namespace MigrationStudio.Tests.DataMigration;

public sealed class DataTransportTests
{
    [Theory]
    [MemberData(nameof(TransportCases))]
    public void BuiltInTransport_PreservesExpectedValue(
        object source,
        DataTransportKind kind,
        object expected)
    {
        var mapping = Mapping(kind);

        var actual = DataTransportConverter.ConvertValue(source, mapping);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Transport_PreservesNullAndEmptyStringAsDistinctValues()
    {
        var mapping = Mapping(DataTransportKind.Text);

        Assert.Null(DataTransportConverter.ConvertValue(DBNull.Value, mapping));
        Assert.Equal(string.Empty, DataTransportConverter.ConvertValue(string.Empty, mapping));
    }

    [Fact]
    public void Transport_PreservesUnicodeAndBinaryByteForByte()
    {
        const string unicode = "नमस्ते Καλημέρα 你好 🧪";
        var binary = Enumerable.Range(0, 256).Select(item => (byte)item).ToArray();

        Assert.Equal(unicode, DataTransportConverter.ConvertValue(unicode, Mapping(DataTransportKind.Text)));
        Assert.Same(binary, DataTransportConverter.ConvertValue(binary, Mapping(DataTransportKind.Binary)));
    }

    [Theory]
    [InlineData("$2a$12$R9h/cIPz0gi.URNNX3kh2OPST9/PgBkqquzi.Ss7KIUgO2t0jWMUW")]
    [InlineData("AQAAAAIAAYagAAAAEJ6v9GF1Qq3mTn+2XW0kTQ==")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$YWJjZA")]
    [InlineData("pbkdf2_sha256$600000$salt$hash")]
    public void PasswordHashText_IsNotNormalized(string hash)
    {
        var actual = DataTransportConverter.ConvertValue(hash, Mapping(DataTransportKind.Text));
        Assert.Equal(hash, actual);
    }

    [Fact]
    public void BinaryHash_IsNotDecodedOrCopied()
    {
        var hash = Convert.FromHexString("00FF10A5B6C7D8E9");
        var actual = DataTransportConverter.ConvertValue(hash, Mapping(DataTransportKind.Binary));
        Assert.Same(hash, actual);
    }

    public static TheoryData<object, DataTransportKind, object> TransportCases => new()
    {
        { true, DataTransportKind.Boolean, true },
        { (byte)255, DataTransportKind.Signed16, (short)255 },
        { short.MinValue, DataTransportKind.Signed16, short.MinValue },
        { int.MaxValue, DataTransportKind.Signed32, int.MaxValue },
        { long.MinValue, DataTransportKind.Signed64, long.MinValue },
        { decimal.Parse("7922816251426433759354395.0335", CultureInfo.InvariantCulture), DataTransportKind.ExactNumeric,
            decimal.Parse("7922816251426433759354395.0335", CultureInfo.InvariantCulture) },
        { 1.25f, DataTransportKind.Floating32, 1.25f },
        { Math.PI, DataTransportKind.Floating64, Math.PI },
        { new DateTime(2024, 2, 29), DataTransportKind.Date, new DateOnly(2024, 2, 29) },
        { TimeSpan.FromTicks(123456789), DataTransportKind.Time, TimeSpan.FromTicks(123456789) },
        { new DateTime(2024, 2, 29, 23, 59, 59, DateTimeKind.Unspecified), DataTransportKind.DateTime,
            new DateTime(2024, 2, 29, 23, 59, 59, DateTimeKind.Unspecified) },
        { new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.FromHours(5.5)), DataTransportKind.DateTimeOffset,
            new DateTimeOffset(2024, 2, 29, 18, 29, 59, TimeSpan.Zero) },
        { Guid.Parse("76543210-abcd-4321-9999-0123456789ab"), DataTransportKind.Uuid,
            Guid.Parse("76543210-abcd-4321-9999-0123456789ab") },
        { "<root>value</root>", DataTransportKind.Xml, "<root>value</root>" },
        { "{\"value\":1}", DataTransportKind.Json, "{\"value\":1}" }
    };

    private static ColumnMapping Mapping(DataTransportKind kind) =>
        new(1, "Source", "target", "source", "target", kind, true, false, false,
            null, null, GeneratedColumnLoadStrategy.PopulateFromSource, null, true);
}
