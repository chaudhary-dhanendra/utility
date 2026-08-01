namespace MigrationStudio.Infrastructure;

public sealed class ProductionOptions
{
    public const string SectionName = "Production";

    public int ConnectionTimeoutSeconds { get; set; } = 15;

    public int CommandTimeoutSeconds { get; set; } = 120;

    public int MaximumConcurrentTables { get; set; } = 4;

    public int MaximumConcurrentReaders { get; set; } = 4;

    public int MaximumConcurrentWriters { get; set; } = 4;

    public int BatchRowCount { get; set; } = 5_000;

    public long BatchByteSize { get; set; } = 33_554_432;

    public int CheckpointFrequencyBatches { get; set; } = 1;

    public int PostgreSqlTargetVersion { get; set; } = 17;

    public string ReportOutputDirectory { get; set; } = "Reports";

    public string LoggingLevel { get; set; } = "Information";

    public string UpdateChannel { get; set; } = "Stable";

    public bool EnablePreviewFeatures { get; set; }

    public string[] SensitiveColumnPatterns { get; set; } =
        ["password", "passwd", "secret", "token", "api_key", "private_key", "credential"];
}

public sealed class PluginLoadingOptions
{
    public const string SectionName = "Plugins";

    public bool Enabled { get; set; }

    public bool RequireAuthenticodeSignature { get; set; } = true;

    public string[] TrustedPublisherThumbprints { get; set; } = [];
}
