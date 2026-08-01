using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Conversion;

public static class ConversionArtifactReconciler
{
    public static bool IsDeployableExecutable(ConversionArtifact artifact) =>
        artifact.Classification is ConversionClassification.Automatic or
            ConversionClassification.AutomaticWithWarning &&
        !artifact.RequiresManualReview &&
        HasExecutableSql(artifact.PostgreSqlDefinition);

    public static bool HasCurrentSuccessfulLiveValidation(ConversionArtifact artifact) =>
        IsDeployableExecutable(artifact) &&
        artifact.Validation.Outcome == LiveSqlValidationOutcome.Passed &&
        artifact.Validation.WasLiveValidated &&
        artifact.Validation.IsStructurallyValid &&
        artifact.Validation.ValidatedSqlHash.Equals(
            artifact.ContentHash,
            StringComparison.Ordinal);

    public static IReadOnlyList<ConversionArtifact>
        GetArtifactsWithoutCurrentSuccessfulLiveValidation(
            IReadOnlyList<ConversionArtifact> artifacts) =>
        artifacts.Where(item =>
                IsDeployableExecutable(item) &&
                !HasCurrentSuccessfulLiveValidation(item))
            .ToArray();

    public static bool HasExecutableSql(string sql) =>
        sql.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line => line.Length > 0 &&
                !line.StartsWith("--", StringComparison.Ordinal) &&
                !line.StartsWith("/*", StringComparison.Ordinal) &&
                !line.StartsWith("*/", StringComparison.Ordinal));

    public static IReadOnlyList<ConversionArtifact> OverlayPresentedEdits(
        IReadOnlyList<ConversionArtifact> authoritative,
        IReadOnlyList<ConversionArtifact> presented)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        ArgumentNullException.ThrowIfNull(presented);

        var authoritativeById = UniqueByArtifactIdentity(authoritative, "authoritative conversion run");
        var presentedById = UniqueByArtifactIdentity(presented, "presented conversion artifacts");
        var unknown = presentedById.Keys.Where(id => !authoritativeById.ContainsKey(id)).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidDataException(
                $"The presentation layer supplied {unknown.Length:N0} artifacts that are not part of the conversion run.");
        }

        var merged = authoritative
            .Select(item => presentedById.GetValueOrDefault(Identity(item)) ?? item)
            .ToArray();
        EnsureSameSourceObjects(authoritative, merged, "presentation edit merge");
        return merged;
    }

    public static IReadOnlyList<ConversionArtifact> ApplyValidationResults(
        IReadOnlyList<ConversionArtifact> authoritative,
        IReadOnlyList<ConversionArtifact> presented,
        IReadOnlyDictionary<InventoryObjectId, SqlValidationResult> validationBySourceId)
    {
        ArgumentNullException.ThrowIfNull(validationBySourceId);
        var merged = OverlayPresentedEdits(authoritative, presented);
        var updated = merged
            .Select(item => validationBySourceId.TryGetValue(item.SourceObjectId, out var validation)
                ? item with { Validation = validation }
                : item)
            .ToArray();
        EnsureSameSourceObjects(authoritative, updated, "live validation merge");
        return updated;
    }

    public static IReadOnlyList<ConversionArtifact> ApplyValidationResultsByContentHash(
        IReadOnlyList<ConversionArtifact> authoritative,
        IReadOnlyList<ConversionArtifact> presented,
        IReadOnlyDictionary<string, SqlValidationResult> validationByContentHash)
    {
        ArgumentNullException.ThrowIfNull(validationByContentHash);
        var merged = OverlayPresentedEdits(authoritative, presented);
        var updated = merged
            .Select(item => validationByContentHash.TryGetValue(item.ContentHash, out var validation)
                ? item with { Validation = validation }
                : item)
            .ToArray();
        EnsureSameSourceObjects(authoritative, updated, "live validation merge");
        return updated;
    }

    public static IReadOnlyList<ConversionArtifact> ApplyValidationResultsByIdentity(
        IReadOnlyList<ConversionArtifact> authoritative,
        IReadOnlyList<ConversionArtifact> presented,
        IReadOnlyDictionary<ConversionArtifactIdentity, SqlValidationResult> validationByIdentity)
    {
        ArgumentNullException.ThrowIfNull(validationByIdentity);
        var merged = OverlayPresentedEdits(authoritative, presented);
        var updated = merged
            .Select(item => validationByIdentity.TryGetValue(Identity(item), out var validation)
                ? item with
                {
                    Validation = validation with
                    {
                        ValidatedSqlHash = item.ContentHash,
                        ValidatedAt = validation.ValidatedAt ?? DateTimeOffset.UtcNow
                    }
                }
                : item)
            .ToArray();
        EnsureSameSourceObjects(authoritative, updated, "live validation identity merge");
        return updated;
    }

    public static void EnsureSameSourceObjects(
        IReadOnlyList<ConversionArtifact> before,
        IReadOnlyList<ConversionArtifact> after,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var beforeCounts = before.GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.Count());
        var afterCounts = after.GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.Count());
        var missing = beforeCounts.Sum(item =>
            Math.Max(0, item.Value - afterCounts.GetValueOrDefault(item.Key)));
        var added = afterCounts.Sum(item =>
            Math.Max(0, item.Value - beforeCounts.GetValueOrDefault(item.Key)));
        if (before.Count != after.Count || missing > 0 || added > 0)
        {
            throw new InvalidDataException(
                $"Artifact reconciliation failed during {operation}: " +
                $"before={before.Count:N0}, after={after.Count:N0}, " +
                $"missing={missing:N0}, unexpected={added:N0}.");
        }
    }

    private static Dictionary<ConversionArtifactIdentity, ConversionArtifact> UniqueByArtifactIdentity(
        IReadOnlyList<ConversionArtifact> artifacts,
        string collection)
    {
        var duplicates = artifacts
            .GroupBy(Identity)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidDataException(
                $"{collection} contains {duplicates.Length:N0} duplicate source object identifiers.");
        }

        return artifacts.ToDictionary(Identity);
    }

    public static ConversionArtifactIdentity Identity(ConversionArtifact artifact) =>
        new(
            artifact.SourceObjectId,
            artifact.TargetObjectId.ObjectType,
            artifact.TargetObjectId.Schema,
            artifact.TargetObjectId.Name,
            artifact.DeploymentPhase,
            artifact.ScriptFileName);

    public sealed record ConversionArtifactIdentity(
        InventoryObjectId SourceObjectId,
        string TargetObjectType,
        string TargetSchema,
        string TargetName,
        DeploymentPhase Phase,
        string ScriptFile);
}
