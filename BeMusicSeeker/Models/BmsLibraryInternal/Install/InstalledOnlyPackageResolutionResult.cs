namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstalledOnlyPackageResolutionResult
{
    public bool Success => !string.IsNullOrWhiteSpace(DestinationDirectory) && Reason == InstalledDirectoryResolveReason.None;

    public string DestinationDirectory { get; set; }

    public InstalledDirectoryResolveReason Reason { get; set; }
}
