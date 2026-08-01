using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MigrationStudio.ScaleFixtureGenerator;

public static class SqlServerScaleFixtureWriter
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    public static async Task<ScaleFixtureManifest> WriteAsync(
        ScaleFixtureOptions options,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        options.Validate();
        Directory.CreateDirectory(outputDirectory);
        var setupPath = Path.Combine(outputDirectory, $"{options.Preset}-setup.sql");
        var cleanupPath = Path.Combine(outputDirectory, $"{options.Preset}-cleanup.sql");
        var manifestPath = Path.Combine(outputDirectory, $"{options.Preset}-manifest.json");

        await using (var stream = new FileStream(
                         setupPath, FileMode.Create, FileAccess.Write, FileShare.None, 131_072, FileOptions.Asynchronous))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await WriteSetupAsync(writer, options, cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(
            cleanupPath,
            $"USE [master];{Environment.NewLine}IF DB_ID(N'{Literal(options.DatabaseName)}') IS NOT NULL " +
            $"BEGIN ALTER DATABASE {Id(options.DatabaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE {Id(options.DatabaseName)}; END;{Environment.NewLine}",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        var manifest = new ScaleFixtureManifest(
            1, options.Preset, options.DatabaseName, options.Seed, options.SchemaCount,
            options.TableCount, EffectiveColumnCount(options),
            (long)options.TableCount * options.RowsPerTable, options.ViewCount, options.FunctionCount,
            options.ProcedureCount, options.TriggerCount, Math.Min(20, options.TableCount / 3),
            options, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private static async Task WriteSetupAsync(
        StreamWriter writer,
        ScaleFixtureOptions options,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync("SET NOCOUNT ON; SET XACT_ABORT ON;").ConfigureAwait(false);
        await writer.WriteLineAsync(
            $"IF DB_ID(N'{Literal(options.DatabaseName)}') IS NULL CREATE DATABASE {Id(options.DatabaseName)};").ConfigureAwait(false);
        await writer.WriteLineAsync($"USE {Id(options.DatabaseName)};").ConfigureAwait(false);
        await writer.WriteLineAsync("GO").ConfigureAwait(false);

        for (var schema = 0; schema < options.SchemaCount; schema++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = SchemaName(schema);
            await writer.WriteLineAsync(
                $"IF SCHEMA_ID(N'{name}') IS NULL EXEC(N'CREATE SCHEMA {Id(name)} AUTHORIZATION [dbo]');").ConfigureAwait(false);
        }

        for (var table = 0; table < options.TableCount; table++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = SchemaName(table % options.SchemaCount);
            var name = TableName(table, options);
            var columns = BuildColumns(table, options);
            await writer.WriteLineAsync(
                $"CREATE TABLE {Id(schema)}.{Id(name)} ({string.Join(", ", columns)});").ConfigureAwait(false);
            if (Selected(table, options.IndexPercent, options.Seed + 17))
            {
                await writer.WriteLineAsync(
                    $"CREATE INDEX {Id($"IX_{table:D6}_Payload")} ON {Id(schema)}.{Id(name)} ([Code]) INCLUDE ([ModifiedAt]);").ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync("GO").ConfigureAwait(false);
        await WriteDependenciesAsync(writer, options, cancellationToken).ConfigureAwait(false);
        await WriteProgrammableObjectsAsync(writer, options, cancellationToken).ConfigureAwait(false);
        await WriteDataAsync(writer, options, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> BuildColumns(int table, ScaleFixtureOptions options)
    {
        var wide = Selected(table, options.WideTablePercent, options.Seed + 29);
        var count = wide ? Math.Min(1024, Math.Max(options.ColumnsPerTable, 256)) : options.ColumnsPerTable;
        var result = new List<string>(count)
        {
            Selected(table, options.PrimaryKeyPercent, options.Seed)
                ? "[Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT " + Id($"PK_{table:D6}") + " PRIMARY KEY"
                : "[Id] bigint NULL",
            "[Code] nvarchar(128) NULL",
            "[ModifiedAt] datetime2(7) NOT NULL CONSTRAINT " + Id($"DF_{table:D6}_Modified") + " DEFAULT SYSUTCDATETIME()",
            "[ApplicationPasswordHash] varbinary(64) NULL"
        };
        if (Selected(table, options.ComputedColumnPercent, options.Seed + 3))
        {
            result.Add("[ComputedValue] AS (CONVERT(bigint,ISNULL([Id],(0)))+(1)) PERSISTED");
        }
        if (Selected(table, options.LargeTextPercent, options.Seed + 5))
        {
            result.Add("[LargeText] nvarchar(max) NULL");
        }
        if (Selected(table, options.BinaryPercent, options.Seed + 7))
        {
            result.Add("[LargeBinary] varbinary(max) NULL");
        }
        for (var column = result.Count; column < count; column++)
        {
            var type = (column % 8) switch
            {
                0 => "int",
                1 => "decimal(38,10)",
                2 => "uniqueidentifier",
                3 => "datetimeoffset(7)",
                4 => "nvarchar(256)",
                5 => "bit",
                6 => "float",
                _ => "date"
            };
            result.Add($"{Id($"Column_{column:D4}")} {type} NULL");
        }
        return result;
    }

    private static async Task WriteDependenciesAsync(
        StreamWriter writer,
        ScaleFixtureOptions options,
        CancellationToken cancellationToken)
    {
        for (var table = 1; table < options.TableCount; table++)
        {
            if (!Selected(table, options.ForeignKeyPercent, options.Seed + 11))
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var sourceSchema = SchemaName(table % options.SchemaCount);
            var target = Math.Max(0, table - Math.Max(1, options.DependencyDensity));
            var targetSchema = SchemaName(target % options.SchemaCount);
            await writer.WriteLineAsync(
                $"ALTER TABLE {Id(sourceSchema)}.{Id(TableName(table, options))} ADD {Id($"FK_{table:D6}_{target:D6}")} " +
                $"FOREIGN KEY ([Id]) REFERENCES {Id(targetSchema)}.{Id(TableName(target, options))}([Id]);").ConfigureAwait(false);
        }

        var cycles = Math.Min(20, options.TableCount / 3);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var first = cycle * 3;
            var second = first + 1;
            var firstSchema = SchemaName(first % options.SchemaCount);
            var secondSchema = SchemaName(second % options.SchemaCount);
            await writer.WriteLineAsync(
                $"ALTER TABLE {Id(firstSchema)}.{Id(TableName(first, options))} ADD [CycleRef] bigint NULL; " +
                $"ALTER TABLE {Id(secondSchema)}.{Id(TableName(second, options))} ADD [CycleRef] bigint NULL;").ConfigureAwait(false);
            await writer.WriteLineAsync(
                $"ALTER TABLE {Id(firstSchema)}.{Id(TableName(first, options))} ADD {Id($"FK_CycleA_{cycle:D3}")} FOREIGN KEY ([CycleRef]) " +
                $"REFERENCES {Id(secondSchema)}.{Id(TableName(second, options))}([Id]);").ConfigureAwait(false);
            await writer.WriteLineAsync(
                $"ALTER TABLE {Id(secondSchema)}.{Id(TableName(second, options))} ADD {Id($"FK_CycleB_{cycle:D3}")} FOREIGN KEY ([CycleRef]) " +
                $"REFERENCES {Id(firstSchema)}.{Id(TableName(first, options))}([Id]);").ConfigureAwait(false);
        }
        await writer.WriteLineAsync("GO").ConfigureAwait(false);
    }

    private static async Task WriteProgrammableObjectsAsync(
        StreamWriter writer,
        ScaleFixtureOptions options,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < options.ViewCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = index % options.TableCount;
            var schema = SchemaName(table % options.SchemaCount);
            await writer.WriteLineAsync(
                $"CREATE VIEW {Id(schema)}.{Id($"View_{index:D6}")} AS SELECT [Id],[Code] FROM {Id(schema)}.{Id(TableName(table, options))};").ConfigureAwait(false);
        }
        await writer.WriteLineAsync("GO").ConfigureAwait(false);
        for (var index = 0; index < options.FunctionCount; index++)
        {
            await writer.WriteLineAsync(
                $"CREATE FUNCTION [dbo].{Id($"Function_{index:D6}")}(@value bigint) RETURNS bigint AS BEGIN RETURN @value + {index % 17}; END;").ConfigureAwait(false);
        }
        await writer.WriteLineAsync("GO").ConfigureAwait(false);
        for (var index = 0; index < options.ProcedureCount; index++)
        {
            var table = index % options.TableCount;
            var schema = SchemaName(table % options.SchemaCount);
            await writer.WriteLineAsync(
                $"CREATE PROCEDURE [dbo].{Id($"Procedure_{index:D6}")} @id bigint AS SELECT [Id],[Code] FROM {Id(schema)}.{Id(TableName(table, options))} WHERE [Id]=@id;").ConfigureAwait(false);
        }
        await writer.WriteLineAsync("GO").ConfigureAwait(false);
        for (var index = 0; index < Math.Min(options.TriggerCount, options.TableCount); index++)
        {
            var schema = SchemaName(index % options.SchemaCount);
            await writer.WriteLineAsync(
                $"CREATE TRIGGER {Id(schema)}.{Id($"Trigger_{index:D6}")} ON {Id(schema)}.{Id(TableName(index, options))} AFTER UPDATE AS BEGIN SET NOCOUNT ON; END;").ConfigureAwait(false);
        }
        await writer.WriteLineAsync("GO").ConfigureAwait(false);
    }

    private static async Task WriteDataAsync(
        StreamWriter writer,
        ScaleFixtureOptions options,
        CancellationToken cancellationToken)
    {
        if (options.RowsPerTable == 0)
        {
            return;
        }

        await writer.WriteLineAsync("-- Data is generated on the server in bounded batches; the script never materializes the target volume client-side.").ConfigureAwait(false);
        for (var table = 0; table < options.TableCount; table++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = SchemaName(table % options.SchemaCount);
            var qualified = $"{Id(schema)}.{Id(TableName(table, options))}";
            var textValue = Selected(table, options.LargeTextPercent, options.Seed + 5)
                ? $", [LargeText]=REPLICATE(CONVERT(nvarchar(max),N'Unicode Ω {table:D6} '),{Math.Max(1, options.LargeValueBytes / 16)})"
                : string.Empty;
            var binaryValue = Selected(table, options.BinaryPercent, options.Seed + 7)
                ? $", [LargeBinary]=CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'B{table:D6}'),{Math.Max(1, options.LargeValueBytes / 8)}))"
                : string.Empty;
            await writer.WriteLineAsync($$"""
                DECLARE @remaining bigint={{options.RowsPerTable}}, @offset bigint=0;
                WHILE @remaining>0
                BEGIN
                  DECLARE @take int=CONVERT(int,IIF(@remaining>{{options.DataBatchSize}},{{options.DataBatchSize}},@remaining));
                  INSERT {{qualified}} ([Code],[ApplicationPasswordHash]{{(textValue.Length == 0 ? string.Empty : ",[LargeText]")}}{{(binaryValue.Length == 0 ? string.Empty : ",[LargeBinary]")}})
                  SELECT CONCAT(N'ROW-',@offset+n), HASHBYTES('SHA2_512',CONCAT(N'SYNTHETIC-',{{options.Seed}},N'-',{{table}},N'-',@offset+n)){{textValue}}{{binaryValue}}
                  FROM (SELECT TOP (@take) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b) q;
                  SET @remaining-=@take; SET @offset+=@take;
                END;
                """).ConfigureAwait(false);
        }
    }

    private static int EffectiveColumnCount(ScaleFixtureOptions options) =>
        Enumerable.Range(0, options.TableCount).Sum(table =>
            Selected(table, options.WideTablePercent, options.Seed + 29)
                ? Math.Min(1024, Math.Max(options.ColumnsPerTable, 256))
                : options.ColumnsPerTable);

    private static bool Selected(int index, int percent, int salt) =>
        unchecked(((uint)index * 1_103_515_245u + (uint)salt * 12_345u) % 100u) < percent;

    private static string SchemaName(int index) => $"scale_{index:D3}";

    private static string TableName(int index, ScaleFixtureOptions options) =>
        Selected(index, options.LongIdentifierPercent, options.Seed + 23)
            ? $"Table_{index:D6}_Identifier_Exceeding_PostgreSQL_Sixty_Three_Byte_Limit_For_Mapping_Validation"
            : $"Table_{index:D6}";

    private static string Id(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
