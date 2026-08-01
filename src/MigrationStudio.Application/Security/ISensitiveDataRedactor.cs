namespace MigrationStudio.Application.Security;

public interface ISensitiveDataRedactor
{
    string Redact(string? value);

    string RedactConnectionString(string? connectionString);
}
