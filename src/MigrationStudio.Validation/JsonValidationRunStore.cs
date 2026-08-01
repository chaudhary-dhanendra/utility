using System.Text.Json;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Validation;

public sealed class JsonValidationRunStore(IApplicationPaths paths) : IValidationRunStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<string> SaveAsync(ValidationRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var directory = Path.Combine(paths.ApplicationDataDirectory, "validation-runs");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{run.RunId:N}.json");
        var temporary = destination + ".tmp";
        await using (var stream = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        {
            await JsonSerializer.SerializeAsync(stream, run, Options, cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, destination, true);
        return destination;
    }

    public async Task<ValidationRun> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await JsonSerializer.DeserializeAsync<ValidationRun>(stream, Options, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("Validation run file is empty or invalid.");
    }
}
