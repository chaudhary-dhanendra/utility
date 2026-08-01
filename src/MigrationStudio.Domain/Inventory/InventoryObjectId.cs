using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MigrationStudio.Domain.Inventory;

public readonly record struct InventoryObjectId(Guid Value)
{
    public static InventoryObjectId Create(
        string database,
        InventoryObjectType objectType,
        string schema,
        string name,
        int? sqlServerObjectId,
        InventoryObjectId? parentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var identity = string.Join(
            '\u001f',
            database.Trim().ToUpperInvariant(),
            objectType,
            schema.Trim().ToUpperInvariant(),
            name.Trim().ToUpperInvariant(),
            sqlServerObjectId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            parentId?.ToString() ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new InventoryObjectId(new Guid(guidBytes));
    }

    public override string ToString() => Value.ToString("N");
}
