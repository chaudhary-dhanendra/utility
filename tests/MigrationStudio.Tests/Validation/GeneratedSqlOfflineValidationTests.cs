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
    public async Task RejectsStructurallyInvalidGeneratedPostgreSql(string sql)
    {
        var result = await new GeneratedSqlValidator()
            .ValidateOfflineAsync(sql, CancellationToken.None);

        Assert.False(result.IsStructurallyValid);
        Assert.NotNull(result.Message);
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
