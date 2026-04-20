namespace BeMusicSeeker.ViewModels;

internal sealed class DropInstallQueueStatusSnapshot
{
    public bool IsActive { get; set; }

    public bool CanCancel { get; set; }

    public bool IsCancellationRequested { get; set; }

    public int PendingBatchCount { get; set; }

    public int TotalPathCount { get; set; }

    public int CompletedPathCount { get; set; }

    public string CurrentDisplayName { get; set; } = string.Empty;
}
