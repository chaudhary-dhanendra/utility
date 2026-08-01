using System.Security.Cryptography;
using System.Text;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

internal static class ConversionRuleSupport
{
    public static ConversionResult<string> Success(
        string sql,
        string ruleId,
        IReadOnlyList<InventoryFinding>? findings = null,
        IReadOnlyList<string>? unsupported = null,
        IReadOnlyList<string>? extensions = null,
        ConversionClassification classification = ConversionClassification.Automatic,
        decimal confidence = 1m) =>
        new(
            sql,
            classification,
            ruleId,
            confidence,
            findings ?? [],
            unsupported ?? [],
            classification is ConversionClassification.ManualConversion or ConversionClassification.Unsupported)
        {
            RequiredExtensions = extensions ?? []
        };

    public static ConversionResult<string> Manual(
        InventoryObject source,
        string reason,
        string skeleton,
        params string[] unsupported) =>
        new(
            skeleton,
            ConversionClassification.ManualConversion,
            "OBJECT.MANUAL",
            0.2m,
            [Finding(source, "CONVERSION.MANUAL", FindingSeverity.Warning, reason)],
            unsupported,
            true);

    public static InventoryFinding Finding(
        InventoryObject source,
        string code,
        FindingSeverity severity,
        string message,
        string? evidence = null) =>
        new(code, severity, message, source.Id, evidence);

    public static string Hash(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

    public static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    public static ConversionClassification Worst(
        ConversionClassification first,
        ConversionClassification second) =>
        (ConversionClassification)Math.Max((int)first, (int)second);
}
