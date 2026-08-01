using Microsoft.Extensions.DependencyInjection;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Validation;

namespace MigrationStudio.Validation;

public static class DependencyInjection
{
    public static IServiceCollection AddMigrationStudioValidation(this IServiceCollection services)
    {
        services.AddSingleton<IGeneratedSqlValidator, GeneratedSqlValidator>();
        services.AddSingleton<ICanonicalValueSerializer, CanonicalValueSerializer>();
        services.AddSingleton<ICanonicalChecksumService, CanonicalChecksumService>();
        services.AddSingleton<IPostgreSqlValidationMetadataReader, PostgreSqlValidationMetadataReader>();
        services.AddSingleton<IValidationRunStore, JsonValidationRunStore>();
        services.AddSingleton<IValidationEngine, PostMigrationValidationEngine>();
        services.AddSingleton<IValidationSession, ValidationSession>();
        return services;
    }
}
