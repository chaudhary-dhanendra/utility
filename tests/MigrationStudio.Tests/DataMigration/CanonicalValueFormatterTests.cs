using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Infrastructure.DataMigration;

namespace MigrationStudio.Tests.DataMigration;

public sealed class CanonicalValueFormatterTests
{
    private readonly CanonicalValueFormatter _formatter = new();

    [Fact]
    public void CanonicalFormat_DistinguishesNullEmptyAndWhitespace()
    {
        var values = new[]
        {
            _formatter.Format(null, DataTransportKind.Text),
            _formatter.Format(string.Empty, DataTransportKind.Text),
            _formatter.Format(" ", DataTransportKind.Text)
        };

        Assert.Equal(3, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RowHash_IsDeterministicAndColumnOrderSensitive()
    {
        (object? Value, DataTransportKind Kind)[] values =
        [
            (42, DataTransportKind.Signed32),
            ("é", DataTransportKind.Text),
            (new byte[] { 0, 255 }, DataTransportKind.Binary)
        ];

        var first = _formatter.ComputeRowHash(values);
        var second = _formatter.ComputeRowHash(values);
        var reversed = _formatter.ComputeRowHash(values.Reverse().ToArray());

        Assert.Equal(first, second);
        Assert.NotEqual(first, reversed);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void DateTimeOffset_CanonicalizesToUtc()
    {
        var first = _formatter.Format(
            new DateTimeOffset(2024, 1, 1, 5, 30, 0, TimeSpan.FromHours(5.5)),
            DataTransportKind.DateTimeOffset);
        var second = _formatter.Format(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DataTransportKind.DateTimeOffset);

        Assert.Equal(first, second);
    }
}
