using CommunityToolkit.Mvvm.ComponentModel;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class DataMigrationTableRowViewModel : ObservableObject
{
    public DataMigrationTableRowViewModel(TableLoadPlan plan)
    {
        TableId = plan.SourceTableId;
        Table = plan.SourceQualifiedName;
        Target = plan.TargetQualifiedName;
        EstimatedRows = plan.EstimatedRows;
        Strategy = plan.TransferStrategy;
        IsSensitive = plan.HasSensitiveColumns;
        IsResumable = plan.IsResumable;
        State = plan.RequiresManualAction ? TableMigrationState.Skipped : TableMigrationState.Pending;
        Message = plan.ManualReason;
    }

    public InventoryObjectId TableId { get; }

    public string Table { get; }

    public string Target { get; }

    public long EstimatedRows { get; }

    public DataTransferStrategy Strategy { get; }

    public bool IsSensitive { get; }

    public bool IsResumable { get; }

    [ObservableProperty] private TableMigrationState _state;
    [ObservableProperty] private long _rowsRead;
    [ObservableProperty] private long _rowsWritten;
    [ObservableProperty] private long _rowsRejected;
    [ObservableProperty] private long _currentBatch;
    [ObservableProperty] private int _retryCount;
    [ObservableProperty] private double _rowsPerSecond;
    [ObservableProperty] private double _bytesPerSecond;
    [ObservableProperty] private TimeSpan _elapsed;
    [ObservableProperty] private ValidationOutcome _validation;
    [ObservableProperty] private string? _message;

    public void Apply(DataMigrationProgress progress)
    {
        RowsRead = progress.RowsRead;
        RowsWritten = progress.RowsWritten;
        RowsRejected = progress.RowsRejected;
        CurrentBatch = progress.CurrentBatch;
        RetryCount = progress.RetryCount;
        RowsPerSecond = progress.RowsPerSecond;
        BytesPerSecond = progress.BytesPerSecond;
        Elapsed = progress.Elapsed;
        State = progress.TableState ?? State;
        Message = progress.Message;
    }

    public void Apply(TableMigrationMetrics metrics)
    {
        State = metrics.State;
        RowsRead = metrics.RowsRead;
        RowsWritten = metrics.RowsWritten;
        RowsRejected = metrics.RowsRejected;
        RetryCount = metrics.RetryCount;
        RowsPerSecond = metrics.RowsPerSecond;
        BytesPerSecond = metrics.BytesPerSecond;
        Elapsed = metrics.TotalDuration;
        Message = metrics.Message;
    }
}
