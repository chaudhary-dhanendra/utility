using System.Text.Json;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Application.Platform;
using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Deployment;

public sealed class DeploymentSession : IDeploymentSession
{
    public PreDeploymentAssessment? Assessment { get; private set; }

    public DeploymentResult? Result { get; private set; }

    public event EventHandler? Changed;

    public void SetAssessment(PreDeploymentAssessment assessment)
    {
        Assessment = assessment;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetResult(DeploymentResult result)
    {
        Result = result;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class DeploymentJournalStore(IApplicationPaths paths) :
    IDeploymentJournalStore,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> SaveAsync(
        DeploymentJournal journal,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(paths.ApplicationDataDirectory, "DeploymentJournals");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{journal.DeploymentId:N}.json");
        var temporary = path + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    journal,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeploymentJournal?> LoadAsync(
        Guid deploymentId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            paths.ApplicationDataDirectory,
            "DeploymentJournals",
            $"{deploymentId:N}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var journal = await JsonSerializer.DeserializeAsync<DeploymentJournal>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (journal is not null &&
            journal.FormatVersion != DeploymentJournal.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Deployment journal format {journal.FormatVersion} is unsupported; " +
                $"expected {DeploymentJournal.CurrentFormatVersion}. Start a new deployment.");
        }

        return journal;
    }

    public void Dispose() => _gate.Dispose();
}
