namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum InstalledDirectoryResolveReason
{
    None,
    InvalidInput,
    InstalledIndexEmpty,
    NoInstalledDirectoryMatch,
    MultipleCandidateDirectories,
    MissingRepresentative,
    TieHealthBelowThreshold,
    TieBreakUnresolved,
    MissingInstallDestination,
    ChartHasMultipleInstalledDirectories,
    PackageHasSplitInstalledDirectories
}
