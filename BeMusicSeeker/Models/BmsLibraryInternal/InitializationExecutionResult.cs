namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InitializationExecutionResult
{
    public long Phase1MinLoadMs { get; set; }

    public long Phase2ScanMaintMs { get; set; }

    public long Phase3InstallMaintenanceMs { get; set; }

    public long WaitContinuationMs { get; set; }

    public long WaitBeforeContinuationStartMs { get; set; }

    public long WaitForContinuationSignalMs { get; set; }

    public long WaitForContinuationTasksMs { get; set; }

    public long TotalMs { get; set; }
}
