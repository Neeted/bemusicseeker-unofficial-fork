namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum PendingEstimateDeferredReason
{
    None = 0,
    CompleteSourceAudio,
    InstalledDestinationResolveFailed,
    SourceSurfaceScanLimitExceeded
}
