using Microsoft.Extensions.DependencyInjection;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Deployment;

namespace MigrationStudio.Deployment;

public static class DependencyInjection
{
    public static IServiceCollection AddMigrationStudioDeployment(this IServiceCollection services)
    {
        services.AddSingleton<IDeploymentPackageWriter, MigrationPackageWriter>();
        services.AddSingleton<IMigrationPackageReader, MigrationPackageReader>();
        services.AddSingleton<IPostgreSqlScriptParser, PostgreSqlScriptParser>();
        services.AddSingleton<IPostgreSqlDeploymentConnectionService, PostgreSqlDeploymentConnectionService>();
        services.AddSingleton<IDatabaseProvisioningService, DatabaseProvisioningService>();
        services.AddSingleton<IPreDeploymentAssessmentService, PreDeploymentAssessmentService>();
        services.AddSingleton<IDeploymentJournalStore, DeploymentJournalStore>();
        services.AddSingleton<IDeploymentSession, DeploymentSession>();
        services.AddSingleton<IPostgreSqlDeploymentEngine, PostgreSqlDeploymentEngine>();
        return services;
    }
}
