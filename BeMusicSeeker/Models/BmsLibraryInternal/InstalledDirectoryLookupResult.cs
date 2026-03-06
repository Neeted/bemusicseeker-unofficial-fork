namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstalledDirectoryLookupResult
{
    public bool Success => !string.IsNullOrWhiteSpace(InstallDirectory) && Reason == InstalledDirectoryResolveReason.None;

    public string InstallDirectory { get; set; }

    public InstalledDirectoryResolveReason Reason { get; set; }

    public int MatchedHashCount { get; set; }

    public int CandidateDirectoryCount { get; set; }
}
