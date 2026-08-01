namespace MigrationStudio.Application.Security;

public static class SpreadsheetCellSanitizer
{
    private static readonly char[] DangerousPrefixes = ['=', '+', '-', '@', '\t', '\r', '\n'];

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var firstNonWhitespace = 0;
        while (firstNonWhitespace < value.Length && value[firstNonWhitespace] == ' ')
        {
            firstNonWhitespace++;
        }

        return firstNonWhitespace < value.Length &&
               DangerousPrefixes.Contains(value[firstNonWhitespace])
            ? "'" + value
            : value;
    }

    public static object? EscapeObject(object? value) =>
        value is string text ? Escape(text) : value;
}
