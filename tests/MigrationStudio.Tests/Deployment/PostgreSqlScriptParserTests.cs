using System.IO;
using MigrationStudio.Deployment;

namespace MigrationStudio.Tests.Deployment;

public sealed class PostgreSqlScriptParserTests
{
    private readonly PostgreSqlScriptParser _parser = new();

    [Fact]
    public void Parser_DoesNotSplitDollarQuotedRoutineBody()
    {
        const string sql =
            """
            CREATE FUNCTION public.answer() RETURNS integer
            LANGUAGE plpgsql AS $body$
            BEGIN
                PERFORM 'semi;colon';
                RETURN 42;
            END;
            $body$;
            CREATE TABLE public.after_function(id integer);
            """;

        var statements = _parser.Parse(sql);

        Assert.Equal(2, statements.Count);
        Assert.Contains("RETURN 42;", statements[0].Sql, StringComparison.Ordinal);
        Assert.StartsWith("CREATE TABLE", statements[1].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_HandlesDoBlocksQuotesEscapesAndNestedComments()
    {
        const string sql =
            """
            /* outer /* nested ; */ complete */
            DO $$
            BEGIN
              RAISE NOTICE E'escaped \'; value';
              EXECUTE 'SELECT ''x;y''';
            END
            $$;
            -- next ; comment
            COMMENT ON SCHEMA public IS 'a;b';
            """;

        var statements = _parser.Parse(sql);

        Assert.Equal(2, statements.Count);
        Assert.Contains("DO $$", statements[0].Sql, StringComparison.Ordinal);
        Assert.Contains("'a;b'", statements[1].Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE DATABASE db;", false)]
    [InlineData("VACUUM (ANALYZE) public.t;", false)]
    [InlineData("CREATE INDEX CONCURRENTLY ix ON t(id);", false)]
    [InlineData("ALTER TYPE mood ADD VALUE 'happy';", false)]
    [InlineData("CREATE TABLE t(id integer);", true)]
    public void Parser_ClassifiesTransactionSafety(string sql, bool expected)
    {
        Assert.Equal(expected, _parser.Parse(sql).Single().CanRunInTransaction);
    }

    [Fact]
    public void Parser_RejectsUnterminatedDollarQuote()
    {
        Assert.Throws<InvalidDataException>(() => _parser.Parse("DO $tag$ BEGIN NULL; END;"));
    }
}
