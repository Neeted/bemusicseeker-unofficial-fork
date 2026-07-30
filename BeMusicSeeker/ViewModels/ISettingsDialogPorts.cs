using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal interface ISettingsDialogStatePort
{
    bool HasActiveLibraryProfile { get; }

    bool IsLibraryOperationInProgress { get; }

    Task<bool> InitializeLibraryAsync();

    Task ReloadScoresOnlyAsync();

    Task ReloadFileDiffAsync();

    event EventHandler LibraryOperationAvailabilityChanged;

    event Action<Lr2PlayHistorySchemaStatusSnapshot> Lr2PlayHistorySchemaStatusChanged;
}

internal interface ISettingsDialogWorkspacePort
{
    bool HasPlaylistTables { get; }

    long PlaylistCatalogVersion { get; }

    IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots();

    bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath);

    void SchedulePlaylistUrlCompletionRefresh(string reason);

    void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath);

    Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string operationName);

    event EventHandler<PlaylistCatalogChangedEventArgs> PlaylistCatalogChanged;
}

internal sealed class PlaylistCatalogChangedEventArgs : EventArgs
{
    internal PlaylistCatalogChangedEventArgs(long version)
    {
        Version = version;
    }

    internal long Version { get; }
}

internal interface ISettingsDialogCustomFolderOutputPort
{
    CustomFolderOutputSettingsSnapshot CustomFolderOutputSettings { get; }

    void ChangeCustomFolderBaseDirectoryWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        string additionalOutputBaseDirsBefore,
        string additionalOutputBaseDirsAfter,
        CustomFolderOutputSettingsSnapshot settings);

    void ChangeCustomFolderBaseDirectoryRootWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        CustomFolderOutputSettingsSnapshot settings);

    bool SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
        string previousRootOutputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings);

    int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
        string previousAdditionalOutputBaseDirectories,
        IReadOnlyDictionary<string, string> pendingRenames,
        CustomFolderOutputSettingsSnapshot settings);
}

internal interface ISettingsDialogPlayHistoryPort
{
    void InvalidateReadCache(string reason);

    void RefreshDisplayTargetCatalog(bool queueRefreshWhenSelectionChanges = true);

    void RefreshDisplayTargetSetsFromSettings(
        string serializedDisplayTargetSets,
        bool queueRefreshWhenSelectionChanges);
}

internal interface ISettingsDialogSearchRootRuntimePort
{
    bool IsLibraryAttached { get; }

    bool HasOwnedChartUnderRealPath(string directoryPath);

    void ApplySearchTargets(IReadOnlyList<string> searchTargets);

    void InvalidateLibraryFolderCache();
}

internal interface ISettingsDialogPlayerFactoryPort
{
    IBMSPlayer CreateDefaultBmsPlayer();

    IBMSPlayer CreateBmsPlayerForSettings(StartupSettingsSnapshot settings);
}

internal interface ISettingsDialogPlaybackRuntimePort : IAudioDeviceTestPlaybackPort
{

    Task ApplyPlayerSettingsAsync(IBMSPlayer replacementPlayer);

    void NotifySettingsChanged();
}
