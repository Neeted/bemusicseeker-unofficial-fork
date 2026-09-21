using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstalledDirectoryLookupResult
{
    public bool Success => !string.IsNullOrWhiteSpace(InstallDirectory) && Reason == InstalledDirectoryResolveReason.None;

    public string InstallDirectory { get; set; }

    public InstalledDirectoryResolveReason Reason { get; set; }

    public int MatchedHashCount { get; set; }

    public int CandidateDirectoryCount { get; set; }

    public List<string> CandidateDirectories { get; } = [];

    public Dictionary<string, int> CandidateDirectoryUniquePrimaryHashCounts { get; } = new(System.StringComparer.OrdinalIgnoreCase);

    public int GetCandidateDirectoryUniquePrimaryHashCount(string directoryPath)
    {
        return !string.IsNullOrWhiteSpace(directoryPath)
            && CandidateDirectoryUniquePrimaryHashCounts.TryGetValue(directoryPath, out int count)
            ? count
            : 0;
    }
}
