using MigrationStudio.Domain.Validation;
using MigrationStudio.Validation;

namespace MigrationStudio.Tests.Validation;

public sealed class CanonicalValidationTests
{
    private readonly CanonicalValueSerializer _serializer = new();
    private readonly CanonicalChecksumService _checksums = new();
    private readonly CanonicalComparisonOptions _options = new();

    [Fact]
    public void UnicodeIsNormalizedWithoutChangingCase()
    {
        var decomposed = _serializer.Serialize("Cafe\u0301", CanonicalValueKind.Text, _options);
        var composed = _serializer.Serialize("Café", CanonicalValueKind.Text, _options);

        Assert.Equal(composed, decomposed);
        Assert.NotEqual(
            composed,
            _serializer.Serialize("CAFÉ", CanonicalValueKind.Text, _options));
    }

    [Fact]
    public void BinaryUsesUnambiguousLowercaseHex()
    {
        var value = _serializer.Serialize(
            new byte[] { 0, 15, 16, 255 }, CanonicalValueKind.Binary, _options);

        Assert.Equal("000f10ff", value.Representation);
    }

    [Fact]
    public void DecimalScaleUsesBankersRoundingAndPreservesConfiguredScale()
    {
        var options = _options with { DecimalScale = 2 };

        Assert.Equal("12.34", _serializer.Serialize(
            12.345m, CanonicalValueKind.ExactNumber, options).Representation);
        Assert.Equal("12.30", _serializer.Serialize(
            12.3m, CanonicalValueKind.ExactNumber, options).Representation);
    }

    [Fact]
    public void FloatingPointComparisonUsesConfiguredTolerance()
    {
        var left = _serializer.Serialize(1d, CanonicalValueKind.FloatingPoint, _options);
        var close = _serializer.Serialize(1d + 5e-10, CanonicalValueKind.FloatingPoint, _options);
        var far = _serializer.Serialize(1.01d, CanonicalValueKind.FloatingPoint, _options);

        Assert.True(_serializer.AreEquivalent(left, close, _options));
        Assert.False(_serializer.AreEquivalent(left, far, _options));
    }

    [Fact]
    public void DateTimeAndOffsetsFollowDistinctRules()
    {
        var date = _serializer.Serialize(
            new DateTime(2026, 7, 24, 9, 8, 7, DateTimeKind.Unspecified),
            CanonicalValueKind.Timestamp,
            _options);
        var offset = _serializer.Serialize(
            new DateTimeOffset(2026, 7, 24, 14, 38, 7, TimeSpan.FromHours(5.5)),
            CanonicalValueKind.TimestampWithTimeZone,
            _options);

        Assert.Equal("2026-07-24T09:08:07.000000", date.Representation);
        Assert.Equal("2026-07-24T09:08:07.000000+00:00", offset.Representation);
    }

    [Fact]
    public void FixedWidthTrailingSpacesAreOnlyTrimmedWhenDeclared()
    {
        var fixedWidth = _serializer.Serialize("A  ", CanonicalValueKind.Text, _options, fixedWidth: true);
        var varying = _serializer.Serialize("A  ", CanonicalValueKind.Text, _options);

        Assert.Equal("A", fixedWidth.Representation);
        Assert.Equal("A  ", varying.Representation);
    }

    [Fact]
    public void RowAndChunkChecksumsAreFramedAndStable()
    {
        IReadOnlyList<CanonicalValue> row =
        [
            new(CanonicalValueKind.Text, "ab"),
            new(CanonicalValueKind.Text, "c")
        ];
        IReadOnlyList<CanonicalValue> differentlyFramed =
        [
            new(CanonicalValueKind.Text, "a"),
            new(CanonicalValueKind.Text, "bc")
        ];

        var first = _checksums.HashRow(row);
        Assert.Equal(first, _checksums.HashRow(row));
        Assert.NotEqual(first, _checksums.HashRow(differentlyFramed));
        Assert.Equal(_checksums.HashChunks([first]), _checksums.HashChunks([first]));
    }

    [Fact]
    public void KeylessMultisetChecksumIsOrderIndependentAndCountsDuplicates()
    {
        IReadOnlyList<CanonicalValue> a = [new(CanonicalValueKind.IntegralNumber, "1")];
        IReadOnlyList<CanonicalValue> b = [new(CanonicalValueKind.IntegralNumber, "2")];

        Assert.Equal(
            _checksums.HashUnorderedRows([a, b, a]),
            _checksums.HashUnorderedRows([b, a, a]));
        Assert.NotEqual(
            _checksums.HashUnorderedRows([a, b, a]),
            _checksums.HashUnorderedRows([a, b]));
        Assert.NotEqual(
            _checksums.HashOrderedRows([a, b]),
            _checksums.HashOrderedRows([b, a]));
    }

    [Fact]
    public void JsonOrderingIsOptionalAndXmlNormalizationIsExplicit()
    {
        var normalized = _options with { NormalizeJsonPropertyOrder = true, NormalizeXml = true };
        var jsonA = _serializer.Serialize("{\"b\":2,\"a\":1}", CanonicalValueKind.Json, normalized);
        var jsonB = _serializer.Serialize("{\"a\":1,\"b\":2}", CanonicalValueKind.Json, normalized);
        var xmlA = _serializer.Serialize("<r> <x a=\"1\" /> </r>", CanonicalValueKind.Xml, normalized);
        var xmlB = _serializer.Serialize("<r><x a=\"1\" /></r>", CanonicalValueKind.Xml, normalized);

        Assert.Equal(jsonA, jsonB);
        Assert.Equal(xmlA, xmlB);
    }

    [Fact]
    public void SensitiveValuesAreOneWayMasked()
    {
        var secret = _serializer.Serialize(
            "customer-secret", CanonicalValueKind.Text, _options, sensitive: true);

        Assert.True(secret.IsSensitive);
        Assert.StartsWith("sha256:", secret.Representation, StringComparison.Ordinal);
        Assert.DoesNotContain("customer-secret", secret.Representation, StringComparison.Ordinal);
    }
}
