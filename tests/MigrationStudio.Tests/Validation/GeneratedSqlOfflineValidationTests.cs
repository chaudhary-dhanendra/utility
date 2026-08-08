using MigrationStudio.Validation;

namespace MigrationStudio.Tests.Validation;

public sealed class GeneratedSqlOfflineValidationTests
{
    [Theory]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS $$ SELECT SELECT 1; $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS $$ SELECT @result = 1; $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS timestamp LANGUAGE sql AS $$ SELECT SYSUTCDATETIME(); $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS $$ BEGIN RETURN 1; END; $$;")]
    [InlineData("CREATE INDEX ix_empty ON public.t ();")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN; END; $$;")]
    [InlineData("CREATE PROCEDURE p() LANGUAGE plpgsql AS $$ BEGIN PRINT 1; END; $$;")]
    [InlineData("CREATE PROCEDURE p() LANGUAGE plpgsql AS $$ BEGIN EXEC(@sql); END; $$;")]
    [InlineData("CREATE PROCEDURE p() LANGUAGE plpgsql AS $$ BEGIN SELECT SCOPE_IDENTITY(); END; $$;")]
    [InlineData("CREATE VIEW v AS SELECT * FROM #temporary;")]
    [InlineData("CREATE VIEW v AS SELECT * FROM t WITH (NOLOCK);")]
    [InlineData("CREATE VIEW v AS SELECT * FROM t FOR JSON PATH;")]
    [InlineData("CREATE FUNCTION f() RETURNS text LANGUAGE sql AS $$ SELECT 'a' || || 'b'; $$;")]
    [InlineData("CREATE PROCEDURE p() LANGUAGE plpgsql AS $$ BEGIN v_value := ; END; $$;")]
    [InlineData("CREATE TABLE t (d timestamp without time zone GENERATED ALWAYS AS (CURRENT_TIMESTAMP) STORED);")]
    [InlineData("CREATE TABLE t (d timestamp without time zone GENERATED ALWAYS AS (d - 1) STORED);")]
    [InlineData("CREATE VIEW v AS SELECT * FROM SourceTable;")]
    public async Task RejectsStructurallyInvalidGeneratedPostgreSql(string sql)
    {
        var result = await new GeneratedSqlValidator()
            .ValidateOfflineAsync(sql, CancellationToken.None);

        Assert.False(result.IsStructurallyValid);
        Assert.NotNull(result.Message);
    }

    [Theory]
    [InlineData("CREATE FUNCTION f() RETURNS text LANGUAGE sql AS $$ SELECT 'a' || || 'b'; $$;", "PGSQL101")]
    [InlineData("CREATE PROCEDURE p() LANGUAGE plpgsql AS $$ BEGIN v_value := ; END; $$;", "PGSQL102")]
    [InlineData("CREATE TABLE t (v interval GENERATED ALWAYS AS (INTERVAL * INTERVAL) STORED);", "PGSQL103")]
    [InlineData("CREATE TABLE t (d timestamp without time zone GENERATED ALWAYS AS (CURRENT_TIMESTAMP) STORED);", "PGSQL105")]
    [InlineData("CREATE VIEW v AS SELECT * FROM SourceTable;", "PGSQL107")]
    public async Task FocusedStructuralRuleReportsRuleAndLocation(string sql, string ruleId)
    {
        var result = await new GeneratedSqlValidator()
            .ValidateOfflineAsync(sql, CancellationToken.None);

        Assert.False(result.IsStructurallyValid);
        Assert.Contains(ruleId, result.Message, StringComparison.Ordinal);
        Assert.Contains("line", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsUndeclaredGeneratedRoutineVariable()
    {
        const string sql =
            "CREATE FUNCTION f(p_value integer) RETURNS integer LANGUAGE sql " +
            "AS $$ SELECT p_missing + p_value; $$;";

        var result = await new GeneratedSqlValidator()
            .ValidateOfflineAsync(sql, CancellationToken.None);

        Assert.False(result.IsStructurallyValid);
        Assert.Contains("p_missing", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoresDiagnosticPatternsInsideStringsAndComments()
    {
        const string sql =
            """
            CREATE FUNCTION f(p_value text) RETURNS text
            LANGUAGE sql AS $body$
                -- SELECT SELECT and @ignored are documentation
                SELECT 'SYSUTCDATETIME() SELECT SELECT @ignored' || p_value;
            $body$;
            """;

        var result = await new GeneratedSqlValidator()
            .ValidateOfflineAsync(sql, CancellationToken.None);

        Assert.True(result.IsStructurallyValid, result.Message);
    }

    [Fact]
    public async Task AcceptsLiteralOperandsBetweenConcatenationOperators()
    {
        const string sql =
            "CREATE FUNCTION f(p_value text) RETURNS text LANGUAGE sql " +
            "AS $$ SELECT p_value || '-' || p_value; -- || ||\n $$;";

        var result = await new GeneratedSqlValidator()
            .ValidateOfflineAsync(sql, CancellationToken.None);

        Assert.True(result.IsStructurallyValid, result.Message);
    }

    [Fact]
    public async Task AcceptsEmptyStringAssignmentExpression()
    {
        const string sql =
            "CREATE PROCEDURE p() LANGUAGE plpgsql AS $$ DECLARE v_sql text := ''; BEGIN v_sql := ''; END; $$;";

        var result = await new GeneratedSqlValidator()
            .ValidateOfflineAsync(sql, CancellationToken.None);

        Assert.True(result.IsStructurallyValid, result.Message);
    }

    [Fact]
    public async Task AcceptsDeclaredPlpgsqlParametersAndLocals()
    {
        const string sql =
            """
            CREATE FUNCTION f(p_value integer) RETURNS integer
            LANGUAGE plpgsql AS $body$
            DECLARE
                v_result integer := 0;
            BEGIN
                v_result := p_value + 1;
                RETURN v_result;
            END;
            $body$;
            """;

        var result = await new GeneratedSqlValidator()
            .ValidateOfflineAsync(sql, CancellationToken.None);

        Assert.True(result.IsStructurallyValid, result.Message);
    }
}
