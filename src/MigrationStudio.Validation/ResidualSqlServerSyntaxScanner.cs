using System.Text;
using System.Text.RegularExpressions;

namespace MigrationStudio.Validation;

public sealed record ResidualSqlServerSyntaxFinding(
    string RuleId,
    string Construct,
    int Offset,
    string Fragment);

public static class ResidualSqlServerSyntaxScanner
{
    private static readonly (string RuleId, string Construct, Regex Pattern)[] Rules =
    [
        Rule("TSQL001", "unresolved @variable", @"(?<!@)@[A-Za-z_][A-Za-z0-9_$#]*"),
        Rule("TSQL002", "DECLARE @", @"\bDECLARE\s+@"),
        Rule("TSQL003", "SET @", @"\bSET\s+@"),
        Rule("TSQL004", "SET NOCOUNT", @"\bSET\s+NOCOUNT\s+(?:ON|OFF)\b"),
        Rule("TSQL005", "PRINT", @"\bPRINT\b"),
        Rule("TSQL006", "RAISERROR", @"\bRAISERROR\b"),
        Rule("TSQL007", "BEGIN TRY", @"\bBEGIN\s+TRY\b"),
        Rule("TSQL008", "BEGIN CATCH", @"\bBEGIN\s+CATCH\b"),
        Rule("TSQL009", "SQL Server EXEC/EXECUTE", @"\bEXEC\b|\bEXECUTE\s*(?:\(|@|\[|(?:N?\s*)?sp_)"),
        Rule("TSQL010", "sp_executesql", @"\bsp_executesql\b"),
        Rule("TSQL011", "@@ system variable", @"@@[A-Za-z_][A-Za-z0-9_$#]*"),
        Rule("TSQL012", "SCOPE_IDENTITY", @"\bSCOPE_IDENTITY\s*\("),
        Rule("TSQL013", "IDENT_CURRENT", @"\bIDENT_CURRENT\s*\("),
        Rule("TSQL014", "#temporary table", @"(?<![A-Za-z0-9_$#])##?[A-Za-z_][A-Za-z0-9_$#]*"),
        Rule("TSQL015", "table-variable declaration", @"\bDECLARE\s+@[A-Za-z_]\w*\s+TABLE\b"),
        Rule("TSQL016", "OUTPUT clause", @"\bOUTPUT\s+(?:INTO\b|INSERTED\b|DELETED\b|\$ACTION\b)"),
        Rule("TSQL017", "SQL Server MERGE", @"\bMERGE\b[\s\S]{0,2000}\bWHEN\s+NOT\s+MATCHED\s+BY\s+(?:TARGET|SOURCE)\b"),
        Rule("TSQL018", "PIVOT", @"\bPIVOT\b"),
        Rule("TSQL019", "UNPIVOT", @"\bUNPIVOT\b"),
        Rule("TSQL020", "FOR XML", @"\bFOR\s+XML\b"),
        Rule("TSQL021", "FOR JSON", @"\bFOR\s+JSON\b"),
        Rule("TSQL022", "query hint", @"\bOPTION\s*\("),
        Rule("TSQL023", "table hint", @"\bWITH\s*\(\s*(?:NOLOCK|UPDLOCK|HOLDLOCK|ROWLOCK|TABLOCK|TABLOCKX|READPAST|READUNCOMMITTED)\b|\(\s*NOLOCK\s*\)"),
        Rule("TSQL024", "three/four-part name", @"(?<![A-Za-z0-9_$])(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_]\w*)\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_]\w*)\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_]\w*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_]\w*))?"),
        Rule("TSQL025", "GETDATE", @"\bGETDATE\s*\("),
        Rule("TSQL026", "DATEADD", @"\bDATEADD\s*\("),
        Rule("TSQL027", "DATEDIFF", @"\bDATEDIFF\s*\("),
        Rule("TSQL028", "DATEPART", @"\bDATEPART\s*\("),
        Rule("TSQL029", "DATENAME", @"\bDATENAME\s*\("),
        Rule("TSQL030", "ISNULL", @"\bISNULL\s*\("),
        Rule("TSQL031", "IIF", @"\bIIF\s*\("),
        Rule("TSQL032", "TRY_CAST", @"\bTRY_CAST\s*\("),
        Rule("TSQL033", "TRY_CONVERT", @"\bTRY_CONVERT\s*\("),
        Rule("TSQL034", "TOP clause", @"\bSELECT\s+(?:DISTINCT\s+)?TOP\s*(?:\(|\d)")
    ];

    public static IReadOnlyList<ResidualSqlServerSyntaxFinding> Scan(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var code = MaskCommentsAndStringLiterals(sql);
        var findings = new List<ResidualSqlServerSyntaxFinding>();
        foreach (var (ruleId, construct, pattern) in Rules)
        {
            foreach (Match match in pattern.Matches(code))
            {
                findings.Add(new ResidualSqlServerSyntaxFinding(
                    ruleId,
                    construct,
                    match.Index,
                    Fragment(sql, match.Index, match.Length)));
            }
        }
        return findings.OrderBy(item => item.Offset).ThenBy(item => item.RuleId, StringComparer.Ordinal).ToArray();
    }

    public static string MaskCommentsAndStringLiterals(string sql)
    {
        var output = new StringBuilder(sql.Length);
        var state = ScanState.Code;
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            switch (state)
            {
                case ScanState.Code when current == '-' && next == '-':
                    output.Append("  ");
                    index++;
                    state = ScanState.LineComment;
                    break;
                case ScanState.Code when current == '/' && next == '*':
                    output.Append("  ");
                    index++;
                    state = ScanState.BlockComment;
                    break;
                case ScanState.Code when current == '\'':
                    output.Append(' ');
                    state = ScanState.String;
                    break;
                case ScanState.LineComment when current is '\r' or '\n':
                    output.Append(current);
                    state = ScanState.Code;
                    break;
                case ScanState.BlockComment when current == '*' && next == '/':
                    output.Append("  ");
                    index++;
                    state = ScanState.Code;
                    break;
                case ScanState.String when current == '\'' && next == '\'':
                    output.Append("  ");
                    index++;
                    break;
                case ScanState.String when current == '\'':
                    output.Append(' ');
                    state = ScanState.Code;
                    break;
                case ScanState.Code:
                    output.Append(current);
                    break;
                default:
                    output.Append(current is '\r' or '\n' ? current : ' ');
                    break;
            }
        }
        return output.ToString();
    }

    private static (string, string, Regex) Rule(string id, string construct, string pattern) =>
        (id, construct, new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static string Fragment(string sql, int offset, int length)
    {
        var start = Math.Max(0, offset - 80);
        var end = Math.Min(sql.Length, offset + Math.Max(length, 1) + 160);
        var fragment = sql[start..end];
        fragment = Regex.Replace(fragment, @"'(?:''|[^'])*'", "'<redacted>'", RegexOptions.CultureInvariant);
        fragment = Regex.Replace(fragment, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return fragment.Length <= 320 ? fragment : fragment[..320];
    }

    private enum ScanState
    {
        Code,
        String,
        LineComment,
        BlockComment
    }
}
