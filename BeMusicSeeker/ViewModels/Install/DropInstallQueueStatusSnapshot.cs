namespace BeMusicSeeker.ViewModels;

internal sealed class DropInstallQueueStatusSnapshot
{
    public long Sequence { get; set; }

    public bool IsActive { get; set; }

    public bool CanCancel { get; set; }

    public bool IsCancellationRequested { get; set; }

    public int PendingBatchCount { get; set; }

    public int TotalPathCount { get; set; }

    public int CompletedPathCount { get; set; }

    public string CurrentDisplayName { get; set; } = string.Empty;

    public bool IsCurrentWorkInProgress { get; set; }

    public int CurrentWorkIndex { get; set; }

    public int CurrentWorkTotal { get; set; }

    public string CurrentWorkDisplayName { get; set; } = string.Empty;
}
