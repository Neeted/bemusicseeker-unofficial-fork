namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallEstimationResult
{
    public bool Success => !string.IsNullOrWhiteSpace(DestinationDirectory);

    public string DestinationDirectory { get; set; }

    public int CandidateDirectoryCount { get; set; }

    public bool UsedFallbackCandidateExpansion { get; set; }
}
