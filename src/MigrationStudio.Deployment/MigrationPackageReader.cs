using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Deployment;

public sealed class MigrationPackageReader : IMigrationPackageReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<MigrationPackageManifest> ReadAndVerifyAsync(
        string packageDirectory,
        bool diagnosticMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var root = Path.GetFullPath(packageDirectory);
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("The migration package does not contain manifest.json.");
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<MigrationPackageManifest>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("The migration package manifest is empty.");
        if (manifest.FormatVersion != MigrationPackageManifest.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Manifest format {manifest.FormatVersion} is unsupported; expected {MigrationPackageManifest.CurrentFormatVersion}.");
        }

        if (manifest.PackageId == Guid.Empty || manifest.MigrationRunId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.SourceDatabase) ||
            string.IsNullOrWhiteSpace(manifest.ConversionConfigurationHash))
        {
            throw new InvalidDataException("The migration package manifest is incomplete.");
        }

        var failures = new List<string>();
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = ResolveContainedPath(root, file.RelativePath);
            if (!File.Exists(fullPath))
            {
                if (file.Required)
                {
                    failures.Add($"Required file is missing: {file.RelativePath}");
                }

                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != file.Length ||
                !HashFile(fullPath).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"File integrity check failed: {file.RelativePath}");
            }
        }

        foreach (var artifact in manifest.Artifacts)
        {
            if (!HashText(artifact.Sql).Equals(artifact.SqlSha256, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Structured SQL hash failed for {artifact.TargetSchema}.{artifact.TargetName}.");
            }
        }

        if (failures.Count > 0 && !diagnosticMode)
        {
            throw new InvalidDataException(
                "Package verification failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        return manifest;
    }

    public string ComputePackageFingerprint(MigrationPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return HashText(string.Join(
            "\n",
            manifest.Files.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .Select(item => $"{item.RelativePath}:{item.Sha256}:{item.Length}")));
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Manifest file paths must be relative.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest path escapes the package: {relativePath}");
        }

        return fullPath;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); 
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
