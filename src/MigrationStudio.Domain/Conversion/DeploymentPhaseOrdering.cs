namespace MigrationStudio.Domain.Conversion;

public static class DeploymentPhaseOrdering
{
    public static int GetRank(
        DeploymentPhase phase,
        string? targetObjectType = null)
    {
        return phase switch
        {
            DeploymentPhase.PreDeployment => 0,
            DeploymentPhase.Extensions => 10,
            DeploymentPhase.Schemas => 20,
            DeploymentPhase.Types => 30,
            // PostgreSQL resolves sequence-backed defaults while CREATE TABLE is
            // parsed, so sequences are insertion prerequisites.
            DeploymentPhase.Sequences => 35,
            DeploymentPhase.Tables => 40,
            DeploymentPhase.PreDataFunctions => 43,
            DeploymentPhase.DefaultsAndGeneratedColumns => 45,
            DeploymentPhase.PrimaryKeys => 50,
            DeploymentPhase.UniqueConstraints => 60,
            DeploymentPhase.CheckConstraints => 70,
            DeploymentPhase.Data => 90,
            DeploymentPhase.SequenceReset => 100,
            DeploymentPhase.ForeignKeys => 110,
            DeploymentPhase.Indexes => 120,
            DeploymentPhase.Functions => 130,
            DeploymentPhase.Procedures => 140,
            DeploymentPhase.Views => 150,
            DeploymentPhase.Triggers => 160,
            DeploymentPhase.Security => 170,
            DeploymentPhase.Comments => 180,
            DeploymentPhase.PostDeployment => 190,
            DeploymentPhase.Validation => 200,
            DeploymentPhase.ManualReview => 900,
            _ => 800
        };
    }
}
