using System.Security.Cryptography;
using System.Text;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Deployment;

public sealed class PostgreSqlScriptParser : IPostgreSqlScriptParser
{
    public IReadOnlyList<ParsedSqlStatement> Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var results = new List<ParsedSqlStatement>();
        var start = 0;
        var startLine = 1;
        var line = 1;
        var singleQuoted = false;
        var doubleQuoted = false;
        var lineComment = false;
        var blockCommentDepth = 0;
        var escapeString = false;
        string? dollarTag = null;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            if (current == '\n')
            {
                line++;
                lineComment = false;
            }

            if (lineComment)
            {
                continue;
            }

            if (blockCommentDepth > 0)
            {
                if (current == '/' && next == '*')
                {
                    blockCommentDepth++;
                    index++;
                }
                else if (current == '*' && next == '/')
                {
                    blockCommentDepth--;
                    index++;
                }

                continue;
            }

            if (dollarTag is not null)
            {
                if (Matches(sql, index, dollarTag))
                {
                    index += dollarTag.Length - 1;
                    dollarTag = null;
                }

                continue;
            }

            if (singleQuoted)
            {
                if (escapeString && current == '\\')
                {
                    index++;
                }
                else if (current == '\'' && next == '\'')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    singleQuoted = false;
                    escapeString = false;
                }

                continue;
            }

            if (doubleQuoted)
            {
                if (current == '"' && next == '"')
                {
                    index++;
                }
                else if (current == '"')
                {
                    doubleQuoted = false;
                }

                continue;
            }

            if (current == '-' && next == '-')
            {
                lineComment = true;
                index++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                blockCommentDepth = 1;
                index++;
                continue;
            }

            if (current == '\'')
            {
                singleQuoted = true;
                escapeString = index > 0 && (sql[index - 1] is 'E' or 'e') &&
                    (index < 2 || !char.IsLetterOrDigit(sql[index - 2]));
                continue;
            }

            if (current == '"')
            {
                doubleQuoted = true;
                continue;
            }

            if (current == '$' && TryReadDollarTag(sql, index, out var tag))
            {
                dollarTag = tag;
                index += tag.Length - 1;
                continue;
            }

            if (current == ';')
            {
                AddStatement(results, sql[start..(index + 1)], startLine, line);
                start = index + 1;
                startLine = line;
            }
        }

        if (singleQuoted || doubleQuoted || dollarTag is not null || blockCommentDepth > 0)
        {
            throw new InvalidDataException("The PostgreSQL script ends inside an unterminated quote or comment.");
        }

        AddStatement(results, sql[start..], startLine, line);
        return results;
    }

    private static void AddStatement(
        List<ParsedSqlStatement> results,
        string sql,
        int startLine,
        int endLine)
    {
        var trimmed = sql.Trim();
        if (trimmed.Length == 0 || IsOnlyComment(trimmed))
        {
            return;
        }

        results.Add(new ParsedSqlStatement(
            results.Count + 1,
            trimmed,
            startLine,
            endLine,
            Hash(trimmed),
            CanRunInTransaction(trimmed)));
    }

    private static bool TryReadDollarTag(string sql, int index, out string tag)
    {
        var end = index + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        if (end < sql.Length && sql[end] == '$')
        {
            tag = sql[index..(end + 1)];
            return true;
        }

        tag = string.Empty;
        return false;
    }

    private static bool Matches(string sql, int index, string value) =>
        index + value.Length <= sql.Length &&
        sql.AsSpan(index, value.Length).SequenceEqual(value);

    private static bool CanRunInTransaction(string sql)
    {
        var normalized = string.Join(' ', sql.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return !normalized.StartsWith("CREATE DATABASE ", StringComparison.Ordinal) &&
            !normalized.StartsWith("DROP DATABASE ", StringComparison.Ordinal) &&
            !normalized.StartsWith("VACUUM", StringComparison.Ordinal) &&
            !normalized.Contains("CREATE INDEX CONCURRENTLY", StringComparison.Ordinal) &&
            !normalized.Contains("REINDEX CONCURRENTLY", StringComparison.Ordinal) &&
            !(normalized.Contains("ALTER TYPE", StringComparison.Ordinal) &&
              normalized.Contains(" ADD VALUE", StringComparison.Ordinal));
    }

    private static bool IsOnlyComment(string sql) =>
        sql.StartsWith("--", StringComparison.Ordinal) &&
        !sql.Contains('\n', StringComparison.Ordinal);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
