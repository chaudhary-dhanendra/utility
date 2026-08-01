using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion;

public sealed class PostgreSqlTypeMappingRegistry : ITypeMappingRegistry
{
    public TypeMappingResult Map(
        ColumnInventory column,
        InventoryObject table,
        ConversionOptions options) =>
        Map(
            column.SystemTypeName,
            column.MaximumLength,
            column.Precision,
            column.Scale,
            options,
            table.SourceSchema,
            table.SourceName,
            column.Name);

    public TypeMappingResult Map(
        string sourceType,
        short maximumLength,
        byte precision,
        byte scale,
        ConversionOptions options,
        string? schema = null,
        string? table = null,
        string? column = null)
    {
        var normalized = sourceType.Trim().ToLowerInvariant();
        var overrideRule = options.TypeOverrides
            .Where(item => string.Equals(item.SourceType, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => Specificity(item, schema, table, column))
            .FirstOrDefault(item => Matches(item, schema, table, column));
        if (overrideRule is not null)
        {
            return Result(normalized, overrideRule.TargetType, ConversionClassification.AutomaticWithWarning, "TYPE.OVERRIDE");
        }

        return normalized switch
        {
            "bit" => Result(normalized, "boolean"),
            "tinyint" => Result(normalized, "smallint"),
            "smallint" => Result(normalized, "smallint"),
            "int" or "integer" => Result(normalized, "integer"),
            "bigint" => Result(normalized, "bigint"),
            "decimal" or "numeric" => Result(normalized, Numeric(precision, scale)),
            "money" => Result(normalized, options.MoneyAsNumeric ? "numeric(19,4)" : "money"),
            "smallmoney" => Result(normalized, "numeric(10,4)"),
            "float" => Result(normalized, precision is > 0 and <= 24 ? "real" : "double precision"),
            "real" => Result(normalized, "real"),
            "date" => Result(normalized, "date"),
            "time" when scale > 6 => Warning(
                normalized, "time", "TYPE.PRECISION_REDUCED", "PostgreSQL time precision is limited to 6 fractional digits."),
            "time" => Result(normalized, $"time({scale})"),
            "smalldatetime" or "datetime" => Result(normalized, "timestamp without time zone"),
            "datetime2" when scale > 6 => Warning(
                normalized, Timestamp(scale, false), "TYPE.PRECISION_REDUCED", "PostgreSQL timestamp precision is limited to 6 fractional digits."),
            "datetime2" => Result(normalized, Timestamp(scale, false)),
            "datetimeoffset" when scale > 6 => Warning(
                normalized, Timestamp(scale, true), "TYPE.PRECISION_REDUCED", "PostgreSQL timestamp precision is limited to 6 fractional digits."),
            "datetimeoffset" => Result(normalized, Timestamp(scale, true)),
            "char" => Result(normalized, Character("char", maximumLength, false)),
            "varchar" => Result(normalized, Character("varchar", maximumLength, false)),
            "nchar" => Result(normalized, Character("char", maximumLength, true)),
            "nvarchar" => Result(normalized, Character("varchar", maximumLength, true)),
            "text" or "ntext" => Result(normalized, "text", deprecated: normalized == "ntext"),
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" =>
                Result(normalized, "bytea", deprecated: normalized is "image" or "timestamp"),
            "uniqueidentifier" => Result(normalized, "uuid"),
            "xml" => Result(normalized, "xml"),
            "geography" when options.EnablePostGis => Result(normalized, "geography", extensions: ["postgis"]),
            "geometry" when options.EnablePostGis => Result(normalized, "geometry", extensions: ["postgis"]),
            "geography" or "geometry" => Manual(normalized, "PostGIS is not enabled."),
            "sql_variant" or "hierarchyid" => Manual(normalized, $"No safe default mapping exists for {normalized}."),
            _ => Manual(normalized, $"SQL Server type '{sourceType}' requires an explicit mapping.")
        };
    }

    private static string Numeric(byte precision, byte scale) =>
        precision == 0 ? "numeric" : $"numeric({precision},{scale})";

    private static string Timestamp(byte scale, bool withTimeZone)
    {
        var qualifier = withTimeZone ? "with time zone" : "without time zone";
        return scale <= 6 ? $"timestamp({scale}) {qualifier}" : $"timestamp {qualifier}";
    }

    private static string Character(string target, short maximumLength, bool unicode)
    {
        if (maximumLength < 0)
        {
            return "text";
        }
        var length = unicode ? maximumLength / 2 : maximumLength;
        return length <= 0 ? target : $"{target}({length})";
    }

    private static TypeMappingResult Result(
        string source,
        string target,
        ConversionClassification classification = ConversionClassification.Automatic,
        string rule = "TYPE.BUILTIN",
        bool deprecated = false,
        IReadOnlyList<string>? extensions = null)
    {
        var findings = deprecated
            ? new[] { Finding("TYPE.DEPRECATED", $"SQL Server type '{source}' is deprecated.") }
            : [];
        return new TypeMappingResult(
            source,
            target,
            deprecated ? ConversionClassification.AutomaticWithWarning : classification,
            extensions ?? [],
            findings,
            rule);
    }

    private static TypeMappingResult Manual(string source, string reason) =>
        new(
            source,
            "text",
            ConversionClassification.ManualConversion,
            [],
            [Finding("TYPE.MANUAL", reason)],
            "TYPE.MANUAL");

    private static TypeMappingResult Warning(
        string source,
        string target,
        string code,
        string message) =>
        new(
            source,
            target,
            ConversionClassification.AutomaticWithWarning,
            [],
            [Finding(code, message)],
            code);

    private static InventoryFinding Finding(string code, string message) =>
        new(code, FindingSeverity.Warning, message, null, null);

    private static bool Matches(
        TypeMappingOverride rule,
        string? schema,
        string? table,
        string? column) =>
        (rule.Schema is null || string.Equals(rule.Schema, schema, StringComparison.OrdinalIgnoreCase)) &&
        (rule.Table is null || string.Equals(rule.Table, table, StringComparison.OrdinalIgnoreCase)) &&
        (rule.Column is null || string.Equals(rule.Column, column, StringComparison.OrdinalIgnoreCase));

    private static int Specificity(
        TypeMappingOverride rule,
        string? schema,
        string? table,
        string? column) =>
        (rule.Schema is not null && schema is not null ? 1 : 0) +
        (rule.Table is not null && table is not null ? 2 : 0) +
        (rule.Column is not null && column is not null ? 4 : 0);
}
