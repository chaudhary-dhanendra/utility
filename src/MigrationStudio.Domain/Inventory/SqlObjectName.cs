using System.Text;

namespace MigrationStudio.Domain.Inventory;

public sealed record SqlObjectName(string? Schema, string Name)
{
    public string QualifiedName => Schema is null ? Name : $"[{Escape(Schema)}].[{Escape(Name)}]";

    public static bool TryParse(string? value, out SqlObjectName? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = new List<string>(2);
        var current = new StringBuilder();
        var insideBrackets = false;
        var text = value.Trim();

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (insideBrackets)
            {
                if (character == ']' && index + 1 < text.Length && text[index + 1] == ']')
                {
                    current.Append(']');
                    index++;
                }
                else if (character == ']')
                {
                    insideBrackets = false;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character == '[')
            {
                if (current.ToString().Trim().Length > 0)
                {
                    return false;
                }

                insideBrackets = true;
            }
            else if (character == '.')
            {
                if (!AddPart(parts, current))
                {
                    return false;
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (insideBrackets || !AddPart(parts, current) || parts.Count is < 1 or > 2)
        {
            return false;
        }

        result = parts.Count == 1
            ? new SqlObjectName(null, parts[0])
            : new SqlObjectName(parts[0], parts[1]);
        return true;
    }

    private static bool AddPart(List<string> parts, StringBuilder current)
    {
        var part = current.ToString().Trim();
        current.Clear();
        if (part.Length == 0)
        {
            return false;
        }

        parts.Add(part);
        return true;
    }

    private static string Escape(string value) => value.Replace("]", "]]", StringComparison.Ordinal);
}
