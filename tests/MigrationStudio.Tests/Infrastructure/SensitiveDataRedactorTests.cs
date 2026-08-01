using MigrationStudio.Infrastructure.Security;
using MigrationStudio.Infrastructure.SqlServer;
using MigrationStudio.Application.Discovery;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void Redact_RemovesSecretsFromMessagesAndConnectionStrings()
    {
        var redactor = new SensitiveDataRedactor();

        Assert.DoesNotContain("hunter2", redactor.Redact("password=hunter2; token='abc'"), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "hunter2",
            redactor.RedactConnectionString("Server=localhost;User Id=sa;Password=hunter2"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionFactory_DisablesMarsAndPersistence()
    {
        using var connection = SqlServerConnectionFactory.Create(new SqlServerConnectionOptions
        {
            Server = "localhost",
            Database = "source",
            AuthenticationMode = SqlServerAuthenticationMode.SqlServer,
            Username = "sa",
            Password = "secret"
        });

        var connectionString = connection.ConnectionString;
        Assert.Contains("Multiple Active Result Sets=False", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Persist Security Info=False", connectionString, StringComparison.OrdinalIgnoreCase);
    }
}
