using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

/// <summary>初期化の成功、設定修復が必要な失敗、既に要求した終了を区別します。</summary>
internal enum StartupInitializationOutcome
{
    /// <summary>必須ローカル準備の成功経路です。親解放後のUIと完了公開はCompletionPublishedで区別し、解禁後の通知失敗は結果へ併記します。</summary>
    Succeeded,
    /// <summary>初期化を継続できず、設定の再編集が必要です。</summary>
    SettingsRequired,
    /// <summary>終了を要求済みです。設定画面へは戻りません。</summary>
    ShutdownRequested
}

/// <summary>必須処理の実終端と、親受付解放後の完了公開を同じ局所結果で受け渡します。共有の保留状態は持ちません。</summary>
/// <param name="Outcome">設定不備・終了・必須処理成功の分類。</param>
/// <param name="Settings">この初期化で捕捉した設定。実完了へ進む本番の成功結果は必ず保持します。</param>
/// <param name="OperationToken">既存の操作識別。警告等から新操作へ移った旧結果はその操作を完了しません。</param>
/// <param name="ValidationFailure">想定済み設定不備の診断。設定借用でも親解放後の既存案内に使います。</param>
/// <param name="Failure">元の実失敗。初回案内だけの失敗では、成立済みの必須成功と完了公開を保持して併記します。</param>
/// <param name="CompletionPublished">親受付解放後の必須UIとhost接続を終え、この結果の完了を公開した場合だけ真。</param>
/// <param name="RequiredResult">今回のファイル・必要LR2・必須譜面情報の実終端結果。限定操作では対象範囲だけ保持します。</param>
/// <param name="RepairNotification">親受付外へ持ち出す閏年修復の通知結果。</param>
/// <param name="OperationKind">今回必要な実UIと後続を選ぶ、既存の操作種別。</param>
/// <param name="ShowRootWarning">通常起動でLR2ルート未設定の既存警告を成功公開後に提示するか。</param>
/// <param name="BackupNotifications">バックアップ・走査で得た既存案内。新しい通知は開始前と成功後に寿命を確認します。</param>
internal sealed record StartupInitializationResult(StartupInitializationOutcome Outcome,
    StartupSettingsSnapshot Settings = null, long OperationToken = 0L, bool CompletionPublished = false, Exception Failure = null, string ValidationFailure = null,
    StartupRequiredInitializationResult RequiredResult = null, LeapYearFolderRepairNotification RepairNotification = null,
    StartupProgressOperationKind OperationKind = StartupProgressOperationKind.Startup, bool ShowRootWarning = false, IReadOnlyList<UiMessageRequest> BackupNotifications = null);

internal interface ISettingsDialogStatePort
{
    bool HasActiveLibraryProfile { get; }

    bool IsLibraryOperationInProgress { get; }

    /// <summary>受付外の通知から再入した後も、捕捉した既存操作識別が現在の結果を表すかを確認します。新しい世代や保留状態は作りません。</summary>
    bool IsInitializationCompletionCurrent(long operationToken);

    /// <summary>
    /// 保存済み設定の必須Taskとcleanupの実終端を待ちます。借用時は元の失敗も局所結果へ載せ、外側親解放後の共通終端へ渡します。
    /// 設定画面の再表示は呼出元が担当します。
    /// </summary>
    /// <returns>初期化の結果。終了要求を設定修復が必要な失敗へ読み替えません。</returns>
    Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability capability,
        LeapYearFolderRepairApproval leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification> repairNotificationObserver = null);

    /// <summary>初期化が必要な検証済み編集値から、保存・受付前に限定候補とYesNo判断を捕捉します。不正設定では候補DBを読みません。</summary>
    Task<LeapYearFolderRepairApproval> PrepareLibraryInitializationAsync();

    /// <summary>設定の外側L/P解放後に、捕捉済み実修復結果を既存の通知経路へ渡します。</summary>
    Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification notification);

    /// <summary>親受付解放後に必須UI・hostの実Taskを待ち、同じ結果の初回完了・解禁・任意登録と開始を終えます。</summary>
    Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result);

    /// <summary>外側の同owner生存権限でスコアだけを更新し、実公開と後片付けの終端を待ちます。元失敗・取消を伝播します。</summary>
    Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability);

    /// <summary>呼出元の共通受付内で差分再読込みと後片付けを終え、失敗は通知せず元例外で伝播します。</summary>
    Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability capability);

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
    /// <summary>予約せず限定前段の可否を読み取ります。実受付は確認後に再判定します。</summary>
    bool CanBeginOutputOperation { get; }
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
