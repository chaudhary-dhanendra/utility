using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using Microsoft.Extensions.Logging;

namespace MigrationStudio.Infrastructure.Conversion;

public sealed partial class ConversionSession(
    ILogger<ConversionSession>? logger = null) : IConversionSession
{
    public ConversionRun? Current { get; private set; }

    public event EventHandler? Changed;

    public void SetCurrent(ConversionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.MappingSet.SchemaVersion != IdentifierMappingSchema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Identifier mapping cache version {run.MappingSet.SchemaVersion} is stale; " +
                $"version {IdentifierMappingSchema.CurrentVersion} is required. Reconvert the current inventory.");
        }
        if (run.MappingSet.IncludedColumnCount != run.MappingSet.MappedColumnCount)
        {
            throw new InvalidOperationException(
                "Identifier mapping publication rejected because included and mapped column counts differ.");
        }
        if (run.MappingSet.UnresolvedRequiredCount != 0 ||
            run.MappingSet.Coverage.Any(item => item.IncludedCount != item.MappedCount))
        {
            throw new InvalidOperationException(
                "Identifier mapping publication rejected because required object mappings are incomplete.");
        }

        Current = run;
        LogDiagnosticPublication(run, "Publish");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (Current is null)
        {
            return;
        }

        var previous = Current;
        Current = null;
        LogDiagnosticPublication(previous, "Clear");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LogDiagnosticPublication(ConversionRun run, string action)
    {
        if (logger is null)
        {
            return;
        }

        var mapping = run.IdentifierMappings.FirstOrDefault(item =>
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase) &&
            item.ParentObject.Contains("verify_observe1819", StringComparison.OrdinalIgnoreCase));
        if (logger.IsEnabled(LogLevel.Information))
        {
            var details =
                $"MappingSet{action}; ObjectId={mapping?.SourceKey.ObjectId}; " +
                $"ParentTableObjectId={mapping?.SourceKey.ParentObjectId}; ColumnId={mapping?.SourceKey.ColumnId}; " +
                $"Schema={mapping?.SourceSchema ?? "nrega_SK"}; Table=verify_observe1819; Column=discre_obsrv; " +
                $"CanonicalKey={mapping?.SourceKey.ColumnKey?.ToString() ?? string.Empty}; " +
                $"TargetIdentifier={mapping?.TargetName ?? string.Empty}; MappingSetId={run.MappingSet.MappingSetId}; " +
                $"MappingVersion={run.MappingSet.SchemaVersion}; Exists={mapping is not null}; " +
                $"Included={mapping?.IncludedInScope ?? false}; LoadedFromCache={run.MappingSet.LoadedFromCache}; " +
                $"TemporaryMapCount={run.MappingSet.TemporaryMapCount}; PublishedMapCount={run.MappingSet.PublishedMapCount}; " +
                $"IncludedColumnCount={run.MappingSet.IncludedColumnCount}; MappedColumnCount={run.MappingSet.MappedColumnCount}; " +
                $"PublicationTimestamp={run.MappingSet.PublishedAt:O}";
            LogIdentifierLifecycle(logger, details);
        }

        var trigger = run.IdentifierMappings.FirstOrDefault(item =>
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals(
                "TRG_DigiPay_TrainerDetailsHistory_Del",
                StringComparison.OrdinalIgnoreCase));
        if (logger.IsEnabled(LogLevel.Information))
        {
            var triggerDetails =
                $"MappingSet{action}; ObjectId={trigger?.SourceKey.ObjectId}; " +
                $"ParentTableObjectId={trigger?.SourceKey.ParentObjectId}; " +
                $"Schema={trigger?.SourceSchema ?? "nrega_SK"}; " +
                $"Object=TRG_DigiPay_TrainerDetailsHistory_Del; " +
                $"CanonicalKey={trigger?.SourceKey.TriggerKey?.ToString() ?? string.Empty}; " +
                $"TargetIdentifier={trigger?.TargetName ?? string.Empty}; " +
                $"MappingSetId={run.MappingSet.MappingSetId}; MappingVersion={run.MappingSet.SchemaVersion}; " +
                $"Exists={trigger is not null}; Included={trigger?.IncludedInScope ?? false}; " +
                $"AutoRecovered={trigger?.AutoRecovered ?? false}; " +
                $"LoadedFromCache={run.MappingSet.LoadedFromCache}; " +
                $"TemporaryMapCount={run.MappingSet.TemporaryMapCount}; " +
                $"PublishedMapCount={run.MappingSet.PublishedMapCount}; " +
                $"UnresolvedRequired={run.MappingSet.UnresolvedRequiredCount}; " +
                $"PublicationTimestamp={run.MappingSet.PublishedAt:O}";
            LogIdentifierLifecycle(logger, triggerDetails);
        }
    }

    [LoggerMessage(EventId = 2214, Level = LogLevel.Information, Message = "Identifier lifecycle {Details}")]
    private static partial void LogIdentifierLifecycle(ILogger logger, string details);
}
