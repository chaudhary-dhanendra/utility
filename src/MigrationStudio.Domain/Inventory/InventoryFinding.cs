namespace MigrationStudio.Domain.Inventory;

public sealed record InventoryFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    InventoryObjectId? ObjectId = null,
    string? Evidence = null,
    string? Remediation = null)
{
    public InventoryFinding Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(Message);
        return this with
        {
            Code = Code.Trim().ToUpperInvariant(),
            Message = Message.Trim(),
            Evidence = Evidence?.Trim(),
            Remediation = Remediation?.Trim()
        };
    }
}
