using System.Text;

namespace MigrationStudio.Infrastructure.Conversion;

internal enum TSqlTokenKind
{
    Word,
    Number,
    String,
    QuotedIdentifier,
    Comment,
    Whitespace,
    Symbol
}

internal sealed record TSqlToken(TSqlTokenKind Kind, string Text);

internal static class TSqlTokenizer
{
    public static IReadOnlyList<TSqlToken> Tokenize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = new List<TSqlToken>();
        var index = 0;
        while (index < sql.Length)
        {
            var character = sql[index];
            if (char.IsWhiteSpace(character))
            {
                tokens.Add(ReadWhile(sql, ref index, TSqlTokenKind.Whitespace, char.IsWhiteSpace));
            }
            else if (character == '\'')
            {
                tokens.Add(ReadDelimited(sql, ref index, '\'', '\'', TSqlTokenKind.String));
            }
            else if (character == '[')
            {
                tokens.Add(ReadDelimited(sql, ref index, '[', ']', TSqlTokenKind.QuotedIdentifier));
            }
            else if (character == '"')
            {
                tokens.Add(ReadDelimited(sql, ref index, '"', '"', TSqlTokenKind.QuotedIdentifier));
            }
            else if (character == '-' && Peek(sql, index + 1) == '-')
            {
                var start = index;
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                {
                    index++;
                }
                tokens.Add(new TSqlToken(TSqlTokenKind.Comment, sql[start..index]));
            }
            else if (character == '/' && Peek(sql, index + 1) == '*')
            {
                var start = index;
                index += 2;
                var depth = 1;
                while (index < sql.Length && depth > 0)
                {
                    if (sql[index] == '/' && Peek(sql, index + 1) == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (sql[index] == '*' && Peek(sql, index + 1) == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }
                tokens.Add(new TSqlToken(TSqlTokenKind.Comment, sql[start..index]));
            }
            else if (char.IsLetter(character) || character is '_' or '@' or '#')
            {
                tokens.Add(ReadWhile(
                    sql,
                    ref index,
                    TSqlTokenKind.Word,
                    item => char.IsLetterOrDigit(item) || item is '_' or '@' or '#' or '$'));
            }
            else if (char.IsDigit(character))
            {
                tokens.Add(ReadWhile(
                    sql,
                    ref index,
                    TSqlTokenKind.Number,
                    item => char.IsLetterOrDigit(item) || item is '.' or 'x' or 'X'));
            }
            else
            {
                var length = index + 1 < sql.Length &&
                             sql.Substring(index, 2) is "<=" or ">=" or "<>" or "!=" or "||" or "::"
                    ? 2
                    : 1;
                tokens.Add(new TSqlToken(TSqlTokenKind.Symbol, sql.Substring(index, length)));
                index += length;
            }
        }

        return tokens;
    }

    private static TSqlToken ReadWhile(
        string sql,
        ref int index,
        TSqlTokenKind kind,
        Func<char, bool> predicate)
    {
        var start = index;
        while (index < sql.Length && predicate(sql[index]))
        {
            index++;
        }
        return new TSqlToken(kind, sql[start..index]);
    }

    private static TSqlToken ReadDelimited(
        string sql,
        ref int index,
        char opening,
        char closing,
        TSqlTokenKind kind)
    {
        var start = index++;
        while (index < sql.Length)
        {
            if (sql[index] != closing)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == closing)
            {
                index += 2;
                continue;
            }

            index++;
            return new TSqlToken(kind, sql[start..index]);
        }

        return new TSqlToken(kind, sql[start..]);
    }

    private static char? Peek(string value, int index) => index < value.Length ? value[index] : null;
}
