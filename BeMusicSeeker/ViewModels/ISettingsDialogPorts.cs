using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal interface ISettingsDialogStatePort
{
    bool HasActiveLibraryProfile { get; }

    bool IsFirstStartup { get; }

    bool IsLibraryOperationInProgress { get; }

    void MarkLibraryInitializationFailed();

    void InvalidatePlayHistoryReadCache(string reason);

    void SubscribeStateChanges(PropertyChangedEventHandler handler);

    void UnsubscribeStateChanges(PropertyChangedEventHandler handler);
}

internal interface ISettingsDialogWorkspacePort
{
    bool HasPlaylistTables { get; }

    IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots();

    bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath);

    Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string operationName);

    void SubscribePlaylistTableChanges(PropertyChangedEventHandler handler);

    void UnsubscribePlaylistTableChanges(PropertyChangedEventHandler handler);

    void InvalidateLibraryFolderCache();
}

internal interface ISettingsDialogLibraryPort
{
    bool HasLibrary { get; }

    CustomFolderOutputSettingsSnapshot CustomFolderOutputSettings { get; }

    bool HasOwnedChartUnderRealPath(string directoryPath);

    void SetSearchTargets(IReadOnlyList<string> searchTargets);

    void RefreshPlayHistoryDisplayTargets(bool queueRefreshWhenSelectionChanges = true);

    void RefreshPlayHistoryDisplayTargetSetsFromSettings(bool queueRefreshWhenSelectionChanges);

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

    void SchedulePlaylistUrlCompletionRefresh(string reason);

    void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath);

    bool SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
        string previousRootOutputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings);

    int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
        string previousAdditionalOutputBaseDirectories,
        IReadOnlyDictionary<string, string> pendingRenames,
        CustomFolderOutputSettingsSnapshot settings);

}

internal interface ISettingsDialogPlaybackPort
{
    IBMSPlayer CreateDefaultBmsPlayer();

    IBMSPlayer CreateBmsPlayerForSettings(Properties.Settings settings);

    IAudioDeviceTestPlaybackPort CreateAudioDeviceTestPlaybackPort();

    void ApplyPlayerSettings(IBMSPlayer replacementPlayer);

    void NotifySettingsChanged();
}
