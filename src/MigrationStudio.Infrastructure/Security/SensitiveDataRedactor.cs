using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MigrationStudio.Application.Security;

namespace MigrationStudio.Infrastructure.Security;

public sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return SecretAssignmentPattern().Replace(value, match => $"{match.Groups[1].Value}=***");
    }

    public string RedactConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (builder.ContainsKey("Password"))
            {
                builder.Password = "***";
            }

            return Redact(builder.ConnectionString);
        }
        catch (ArgumentException)
        {
            return Redact(connectionString);
        }
    }

    [GeneratedRegex(
        @"(?i)\b(password|pwd|token|secret|access[ _-]?key)\s*=\s*(?:""[^""]*""|'[^']*'|[^;\s]+)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretAssignmentPattern();
}
