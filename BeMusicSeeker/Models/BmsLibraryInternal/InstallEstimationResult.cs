namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallEstimationResult
{
    public bool Success => !string.IsNullOrWhiteSpace(DestinationDirectory);

    public string DestinationDirectory { get; set; }

    public int CandidateDirectoryCount { get; set; }

    public bool UsedFallbackCandidateExpansion { get; set; }

    public int CandidateDirectoryCountBeforeHashFilter { get; set; }

    public int CandidateDirectoryCountAfterHashFilter { get; set; }

    public int TargetResourceHashCount { get; set; }

    public long EvaluationMs { get; set; }

    public string ResourceSummary { get; set; }

    public string SelectedCandidateSummary { get; set; }

    public string TopCandidateSummary { get; set; }
}
