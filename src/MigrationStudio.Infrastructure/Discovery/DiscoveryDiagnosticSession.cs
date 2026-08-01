using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Application.Security;

namespace MigrationStudio.Infrastructure.Discovery;

public sealed class DiscoveryDiagnosticSession(
    ISensitiveDataRedactor redactor) : IDiscoveryDiagnosticSession
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private DiscoveryDiagnosticReport? _current;
    private DiscoveryDoctorReport? _doctorReport;

    public DiscoveryDiagnosticReport? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public DiscoveryDoctorReport? DoctorReport
    {
        get
        {
            lock (_gate)
            {
                return _doctorReport;
            }
        }
    }

    public event EventHandler? Changed;

    public void Publish(DiscoveryDiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            _current = Sanitize(report);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PublishDoctor(DiscoveryDoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            _doctorReport = Sanitize(report);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearDoctor()
    {
        lock (_gate)
        {
            _doctorReport = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ExportAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        DiscoveryDiagnosticReport report;
        lock (_gate)
        {
            report = _current ??
                throw new InvalidOperationException("No discovery diagnostic report is available.");
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ??
                                  throw new InvalidOperationException("A diagnostic directory is required."));
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportDoctorAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        DiscoveryDoctorReport report;
        lock (_gate)
        {
            report = _doctorReport ??
                throw new InvalidOperationException("No Discovery Doctor report is available.");
        }
        await WriteAsync(path, report, cancellationToken).ConfigureAwait(false);
    }

    private DiscoveryDiagnosticReport Sanitize(DiscoveryDiagnosticReport report) =>
        report with
        {
            Server = Pseudonymize("server", report.Server),
            Database = Pseudonymize("database", report.Database),
            Summary = redactor.Redact(report.Summary),
            Stages = report.Stages.Select(stage => stage with
            {
                Summary = redactor.Redact(stage.Summary),
                Errors = stage.Errors.Select(error => error with
                {
                    Message = redactor.Redact(error.Message),
                    Procedure = error.Procedure is null ? null : redactor.Redact(error.Procedure)
                }).ToArray()
            }).ToArray()
        };

    private static string Pseudonymize(string label, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{label}-{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}";
    }

    private DiscoveryDoctorReport Sanitize(DiscoveryDoctorReport report) =>
        report with
        {
            Server = Pseudonymize("server", report.Server),
            Database = Pseudonymize("database", report.Database),
            ProductionFailureSummary = redactor.Redact(report.ProductionFailureSummary),
            Audit = report.Audit with
            {
                Edition = redactor.Redact(report.Audit.Edition),
                Findings = report.Audit.Findings.Select(redactor.Redact).ToArray(),
                Capabilities = report.Audit.Capabilities.Select(capability => capability with
                {
                    Value = redactor.Redact(capability.Value),
                    Impact = redactor.Redact(capability.Impact)
                }).ToArray()
            },
            Queries = report.Queries.Select(query => query with
            {
                Summary = redactor.Redact(query.Summary),
                Remediation = redactor.Redact(query.Remediation),
                Phases = query.Phases.Select(phase => phase with
                {
                    Summary = redactor.Redact(phase.Summary)
                }).ToArray(),
                Errors = query.Errors.Select(error => error with
                {
                    Message = redactor.Redact(error.Message),
                    Procedure = error.Procedure is null ? null : redactor.Redact(error.Procedure)
                }).ToArray()
            }).ToArray()
        };

    private static async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ??
                                  throw new InvalidOperationException("A diagnostic directory is required."));
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(value, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }
}
