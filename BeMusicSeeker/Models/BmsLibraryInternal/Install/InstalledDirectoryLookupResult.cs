using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstalledDirectoryLookupResult
{
    public bool Success => !string.IsNullOrWhiteSpace(InstallDirectory) && Reason == InstalledDirectoryResolveReason.None;

    public string InstallDirectory { get; set; }

    public InstalledDirectoryResolveReason Reason { get; set; }

    /// <summary>要求全体で有効な候補根へ一致した異なるMD5の数です。同じMD5が複数根へ一致しても一件とし、候補根ごとの票数とは区別します。</summary>
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
