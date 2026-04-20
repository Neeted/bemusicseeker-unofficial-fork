namespace BeMusicSeeker.Models;

internal sealed class PendingInstallEstimateQueueStatusSnapshot
{
    public bool IsActive { get; set; }

    public PendingInstallEstimateBatchSource Source { get; set; }

    public int PendingBatchCount { get; set; }

    public int CurrentPackageCount { get; set; }

    public int CompletedPackageCount { get; set; }

    public string CurrentDisplayName { get; set; } = string.Empty;

    internal PendingInstallEstimateQueueStatusSnapshot Clone()
    {
        return new PendingInstallEstimateQueueStatusSnapshot
        {
            IsActive = IsActive,
            Source = Source,
            PendingBatchCount = PendingBatchCount,
            CurrentPackageCount = CurrentPackageCount,
            CompletedPackageCount = CompletedPackageCount,
            CurrentDisplayName = CurrentDisplayName ?? string.Empty
        };
    }
}
