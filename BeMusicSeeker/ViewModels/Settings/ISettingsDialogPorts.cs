using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>初期化の成功、設定修復が必要な失敗、既に要求した終了を区別します。</summary>
internal enum StartupInitializationOutcome
{
    /// <summary>初期化に成功しました。</summary>
    Succeeded,
    /// <summary>初期化を継続できず、設定の再編集が必要です。</summary>
    SettingsRequired,
    /// <summary>終了を要求済みです。設定画面へは戻りません。</summary>
    ShutdownRequested
}

internal interface ISettingsDialogStatePort
{
    bool HasActiveLibraryProfile { get; }

    bool IsLibraryOperationInProgress { get; }

    /// <summary>
    /// 保存済み設定で初期化し、失敗通知と後片付けまで待ちます。設定画面の再表示は呼出元が担当します。
    /// </summary>
    /// <returns>初期化の結果。終了要求を設定修復が必要な失敗へ読み替えません。</returns>
    Task<StartupInitializationOutcome> InitializeLibraryAsync(LibraryFileMutationCapability capability);

    /// <summary>外側の同owner生存権限でスコアだけを更新し、実公開と後片付けの終端を待ちます。元失敗・取消を伝播します。</summary>
    Task ReloadScoresOnlyAsync(LibraryFileMutationCapability capability);

    /// <summary>呼出元の共通受付内で差分再読込みと後片付けを終え、失敗は通知せず元例外で伝播します。</summary>
    Task ReloadFileDiffAsync(LibraryFileMutationCapability capability);

    /// <summary>呼出元の後片付けと共通受付解放後に、再試行可能なディレクトリ失敗を一度通知します。</summary>
    /// <param name="failure">差分再読込みから伝播した元のディレクトリ検査失敗。</param>
    /// <returns>通知が終端するTask。通知から新しい明示操作を受け付けられます。</returns>
    Task PresentLibraryDirectoryWarningAsync(LibraryDirectoryPreflightException failure);

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

    /// <summary>同じP権限で全表BMTの生成・旧配置回収・設定反映の実終端を待ち、元失敗を伝播します。</summary>
    Task ExportBeatorajaBmtAsync(string reason, string cleanupTablePath, LibraryFileMutationCapability capability);

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
    /// <summary>設定の出力配置変更・全体同期が既存Pを非待機取得します。Busyはnull、呼出元が通知・cleanup終端まで保持します。</summary>
    LibraryFileMutationLease TryBeginOutputOperation();

    CustomFolderOutputSettingsSnapshot CustomFolderOutputSettings { get; }

    void ChangeCustomFolderBaseDirectoryWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        string additionalOutputBaseDirsBefore,
        string additionalOutputBaseDirsAfter,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability = null);

    void ChangeCustomFolderBaseDirectoryRootWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability = null);

    bool SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
        string previousRootOutputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings);

    int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
        string previousAdditionalOutputBaseDirectories,
        IReadOnlyDictionary<string, string> pendingRenames,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability = null);
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
