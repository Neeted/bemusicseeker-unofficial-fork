namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum InstalledDirectoryResolveReason
{
    None,
    InvalidInput,
    InstalledIndexEmpty,
    NoInstalledDirectoryMatch,
    MissingRepresentative,
    TieHealthBelowThreshold,
    TieBreakUnresolved,
    MissingInstallDestination,
    ChartHasMultipleInstalledDirectories,
    PackageHasSplitInstalledDirectories
}
