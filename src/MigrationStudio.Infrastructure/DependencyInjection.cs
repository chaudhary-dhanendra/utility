using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MigrationStudio.Application.Operations;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Security;
using MigrationStudio.Application.Settings;
using MigrationStudio.Infrastructure.Discovery;
using MigrationStudio.Infrastructure.DataMigration;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Conversion.Converters;
using MigrationStudio.Infrastructure.Excel;
using MigrationStudio.Infrastructure.Operations;
using MigrationStudio.Infrastructure.Platform;
using MigrationStudio.Infrastructure.Security;
using MigrationStudio.Infrastructure.Settings;
using MigrationStudio.Infrastructure.SqlServer;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMigrationStudioInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<InfrastructureOptions>()
            .Bind(configuration.GetSection(InfrastructureOptions.SectionName))
            .Validate(
                options => options.OperationQueueCapacity is >= 1 and <= 10_000,
                "Operation queue capacity must be between 1 and 10,000.")
            .ValidateOnStart();
        services.AddOptions<ProductionOptions>()
            .Bind(configuration.GetSection(ProductionOptions.SectionName))
            .Validate(
                options => options.ConnectionTimeoutSeconds is >= 1 and <= 300,
                "Connection timeout must be between 1 and 300 seconds.")
            .Validate(
                options => options.CommandTimeoutSeconds is >= 5 and <= 86_400,
                "Command timeout must be between 5 seconds and 24 hours.")
            .Validate(
                options => options.MaximumConcurrentTables is >= 1 and <= 64 &&
                           options.MaximumConcurrentReaders is >= 1 and <= 64 &&
                           options.MaximumConcurrentWriters is >= 1 and <= 64,
                "Migration parallelism values must be between 1 and 64.")
            .Validate(
                options => options.BatchRowCount is >= 1 and <= 1_000_000 &&
                           options.BatchByteSize is >= 65_536 and <= 1_073_741_824,
                "Migration batch limits are outside the supported range.")
            .Validate(
                options => options.CheckpointFrequencyBatches is >= 1 and <= 10_000,
                "Checkpoint frequency must be between 1 and 10,000 batches.")
            .Validate(
                options => options.PostgreSqlTargetVersion is >= 14 and <= 18,
                "PostgreSQL target version must be between 14 and 18.")
            .Validate(
                options => options.UpdateChannel is "Stable" or "Preview",
                "Update channel must be Stable or Preview.")
            .ValidateOnStart();
        services.AddOptions<PluginLoadingOptions>()
            .Bind(configuration.GetSection(PluginLoadingOptions.SectionName))
            .Validate(
                options => !options.Enabled ||
                           options.RequireAuthenticodeSignature ||
                           options.TrustedPublisherThumbprints.Length == 0,
                "Trusted publisher thumbprints require Authenticode verification.")
            .ValidateOnStart();

        services.TryAddSingleton<IApplicationPaths, ApplicationPaths>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<ISensitiveDataRedactor, SensitiveDataRedactor>();
        services.AddSingleton<ISqlServerConnectionService, SqlServerConnectionService>();
        services.AddSingleton<ISourceObjectScopePolicy, SqlServerUserObjectScopePolicy>();
        services.AddSingleton<IInventoryDiscoveryService, SqlServerInventoryDiscoveryService>();
        services.AddSingleton<IDiscoveryDoctorService, SqlServerDiscoveryDoctorService>();
        services.AddSingleton<IDiscoveryDiagnosticSession, DiscoveryDiagnosticSession>();
        services.AddSingleton<IInventorySnapshotStore, CompressedJsonInventorySnapshotStore>();
        services.AddSingleton<IExcelTableSelectionService, ClosedXmlTableSelectionService>();
        services.AddSingleton<IInventorySession, InventorySession>();
        services.AddSingleton<IConversionSession, ConversionSession>();
        services.AddSingleton<IIdentifierMappingService, PostgreSqlIdentifierMappingService>();
        services.AddSingleton<ITypeMappingRegistry, PostgreSqlTypeMappingRegistry>();
        services.AddSingleton<ISqlExpressionTranslator, StructuredSqlExpressionTranslator>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, SchemaConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, TableConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, ConstraintConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, IndexConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, SequenceConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, UserDefinedTypeConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, ProgrammableObjectConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, SecurityConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, SynonymConverter>();
        services.AddSingleton<IObjectConverter<InventoryObject, string>, FallbackObjectConverter>();
        services.AddSingleton<IConversionEngine, ConversionEngine>();
        services.AddSingleton<ISensitiveColumnClassifier, SensitiveColumnClassifier>();
        services.AddSingleton<ICanonicalValueFormatter, CanonicalValueFormatter>();
        services.AddSingleton<ITransientErrorClassifier, TransientErrorClassifier>();
        services.AddSingleton<IMigrationWavePlanner, MigrationWavePlanner>();
        services.AddSingleton<IDataMigrationPlanner, DataMigrationPlanner>();
        services.AddSingleton<IMigrationCheckpointStore, JsonMigrationCheckpointStore>();
        services.AddSingleton<IDataMigrationSession, DataMigrationSession>();
        services.AddSingleton<IMigrationPauseController, MigrationPauseController>();
        services.AddSingleton<IDataTransferStrategy, PostgreSqlBinaryCopyStrategy>();
        services.AddSingleton<IDataTransferStrategy, PostgreSqlTextCopyStrategy>();
        services.AddSingleton<IDataTransferStrategy, PostgreSqlBatchInsertStrategy>();
        services.AddSingleton<IDataMigrationValidator, DataMigrationValidator>();
        services.AddSingleton<ISequenceResetService, SequenceResetService>();
        services.AddSingleton<IFailedRowExporter, FailedRowExporter>();
        services.AddSingleton<IDataMigrationEngine, DataMigrationEngine>();
        services.AddSingleton<OperationMonitor>();
        services.AddSingleton<IOperationMonitor>(provider => provider.GetRequiredService<OperationMonitor>());
        services.AddSingleton<BackgroundOperationService>();
        services.AddSingleton<IBackgroundOperationScheduler>(
            provider => provider.GetRequiredService<BackgroundOperationService>());
        services.AddSingleton<IHostedService>(
            provider => provider.GetRequiredService<BackgroundOperationService>());

        return services;
    }
}
