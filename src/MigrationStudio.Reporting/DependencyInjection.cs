using Microsoft.Extensions.DependencyInjection;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Application.Validation;
using MigrationStudio.Application.Reporting;
using Microsoft.Extensions.Hosting;

namespace MigrationStudio.Reporting;

public static class DependencyInjection
{
    public static IServiceCollection AddMigrationStudioReporting(this IServiceCollection services)
    {
        services.AddSingleton<IConversionReportWriter, ConversionReportWriter>();
        services.AddSingleton<IDataMigrationReportWriter, DataMigrationReportWriter>();
        services.AddSingleton<IDeploymentReportWriter, DeploymentReportWriter>();
        services.AddSingleton<IValidationReportWriter, ValidationReportWriter>();
        services.AddSingleton<IReportTemplateValidator, ReportTemplateValidator>();
        services.AddSingleton<IManualReviewStore, JsonManualReviewStore>();
        services.AddSingleton<IRunHistoryStore, JsonRunHistoryStore>();
        services.AddSingleton<ISanitizedLogExporter, SanitizedLogExporter>();
        services.AddSingleton<IMigrationReportEngine, MigrationReportEngine>();
        services.AddSingleton<IMigrationReportCoordinator, MigrationReportCoordinator>();
        services.AddSingleton<RunHistoryRecorderService>();
        services.AddSingleton<IHostedService>(
            provider => provider.GetRequiredService<RunHistoryRecorderService>());
        return services;
    }
}
