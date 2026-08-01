using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Validation;

public sealed class ValidationSession : IValidationSession
{
    public ValidationRun? Current { get; private set; }

    public event EventHandler? Changed;

    public void SetCurrent(ValidationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Current = run;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
