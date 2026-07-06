namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Identifies the main workspace refresh or tree-selection mode.
/// </summary>
internal enum MainViewUpdateMode
{
    TreeViewFilterNotChanged = 0,
    PlaylistFilterSelected = 1,
    PlaylistNotOwnedFilterSelected = 2,
    FolderFilterSelected = 17,
    FullScanAllChartsFilterSelected = 32,
    FileMissingFilterSelected = 33,
    FileMissingIgnoredFilterSelected = 34,
    DuplicateFilterSelected = 35,
    GarbledFilterSelected = 36,
    GarbleFixedFilterSelected = 37,
    UnregisteredFilterSelected = 38,
    ZeroNoteFilterSelected = 39,
    ChartInfoParseErrorFilterSelected = 40,
    PlayHistorySelected = 41,
    NewlyInstalledFolderSelected = 49,
    PendingInstallFolderSelected = 50,
    KeywordFilterUpdated = 65,
    ModeFilterUpdated = 66,
    SortUpdated = 81,
    UpdatedNone = 255
}
