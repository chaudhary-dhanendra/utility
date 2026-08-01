namespace MigrationStudio.Domain.Plugins;

public sealed record PluginMetadata
{
    public PluginMetadata(string id, string displayName, Version version, Version minimumHostVersion)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new ArgumentException("Plugin IDs may contain only letters, digits, periods, and hyphens.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(minimumHostVersion);

        Id = id;
        DisplayName = displayName.Trim();
        Version = version;
        MinimumHostVersion = minimumHostVersion;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Version Version { get; }

    public Version MinimumHostVersion { get; }
}
