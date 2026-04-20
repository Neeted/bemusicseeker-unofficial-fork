namespace BeMusicSeeker.Models;

internal sealed class InstallEstimationProgressSnapshot
{
    public bool IsActive { get; set; }

    public InstallEstimationProgressSource Source { get; set; }

    public int TotalWorkCount { get; set; }

    public int CompletedWorkCount { get; set; }

    public string CurrentDisplayName { get; set; } = string.Empty;

    internal InstallEstimationProgressSnapshot Clone()
    {
        return new InstallEstimationProgressSnapshot
        {
            IsActive = IsActive,
            Source = Source,
            TotalWorkCount = TotalWorkCount,
            CompletedWorkCount = CompletedWorkCount,
            CurrentDisplayName = CurrentDisplayName ?? string.Empty
        };
    }
}
