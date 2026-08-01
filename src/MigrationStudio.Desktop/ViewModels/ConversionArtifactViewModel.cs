using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class ConversionArtifactViewModel(ConversionArtifact artifact) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private string _generatedSql = artifact.PostgreSqlDefinition;

    public ConversionArtifact Artifact { get; } = artifact;

    public string Target => Artifact.TargetObjectId.QualifiedName;

    public string SourceSql => Artifact.SourceDefinition;

    public string Phase => Artifact.DeploymentPhase.ToString();

    public string Classification => Artifact.Classification.ToString();

    public string Validation => Artifact.Validation.IsStructurallyValid ? "Offline valid" : "Validation failed";

    public string Findings => string.Join(
        Environment.NewLine,
        Artifact.Findings.Select(item => $"[{item.Severity}] {item.Code}: {item.Message}"));

    public bool IsEdited => !string.Equals(GeneratedSql, Artifact.PostgreSqlDefinition, StringComparison.Ordinal);

    public ConversionArtifact ToArtifact()
    {
        if (!IsEdited)
        {
            return Artifact;
        }
        return Artifact with
        {
            PostgreSqlDefinition = GeneratedSql,
            RuleId = "USER.MANUAL_EDIT",
            Validation = new SqlValidationResult(false, false, null, "Edited SQL must be revalidated.", null),
            ContentHash = ComputeHash(GeneratedSql),
            Findings = Artifact.Findings.Concat(
            [
                new InventoryFinding(
                    "CONVERSION.USER_EDITED",
                    FindingSeverity.Warning,
                    "Generated SQL was manually edited and requires validation.",
                    Artifact.SourceObjectId,
                    null)
            ]).ToArray()
        };
    }

    private static string ComputeHash(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))
            .ToLowerInvariant();
}

public sealed record LiveSqlValidationFailureViewModel(
    string ObjectName,
    string Script,
    string GeneratedSql,
    string PostgreSqlError,
    string SqlState,
    int? LineNumber,
    string Dependency,
    string SuggestedFix);
