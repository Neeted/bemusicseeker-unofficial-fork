using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

/// <summary>初期化の成功、設定修復、処理・必須公開の失敗、既に要求した終了を区別します。</summary>
internal enum StartupInitializationOutcome
{
    /// <summary>必須ローカル準備の成功経路です。親解放後のUIと完了公開はCompletionPublishedで区別し、解禁後の通知失敗は結果へ併記します。</summary>
    Succeeded,
    /// <summary>初期化を継続できず、設定の再編集が必要です。</summary>
    SettingsRequired,
    /// <summary>処理または必須公開が失敗しました。設定修復の必要性や次操作の安全性はこの分類だけで判断しません。</summary>
    OperationFailed,
    /// <summary>終了を要求済みです。設定画面へは戻りません。</summary>
    ShutdownRequested
}

/// <summary>必須処理の実終端と、親受付解放後の完了公開を同じ局所結果で受け渡します。共有の保留状態は持ちません。</summary>
/// <param name="Outcome">設定不備・処理失敗・終了・必須処理成功の分類。</param>
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
/// <param name="ScoreNotification">本体で採用済みのスコア純通知。実親L/P解放後の共通終端で取り出し、実行後の結果へ再保存しません。</param>
internal sealed record StartupInitializationResult(StartupInitializationOutcome Outcome,
    StartupSettingsSnapshot Settings = null, long OperationToken = 0L, bool CompletionPublished = false, Exception Failure = null, string ValidationFailure = null,
    StartupRequiredInitializationResult RequiredResult = null, LeapYearFolderRepairNotification RepairNotification = null,
    StartupProgressOperationKind OperationKind = StartupProgressOperationKind.Startup, bool ShowRootWarning = false, IReadOnlyList<UiMessageRequest> BackupNotifications = null, Action ScoreNotification = null);

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
    /// <returns>初期化の結果。処理失敗と終了要求を設定修復が必要な失敗へ読み替えません。</returns>
    /// <param name="forceBeatorajaBmtProjection">今回の初期化の必要BMT出力で、署名一致による生成省略を迂回するか。</param>
    Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability capability,
        LeapYearFolderRepairApproval leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification> repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false);

    /// <summary>初期化が必要な検証済み編集値から、保存・受付前に限定候補とYesNo判断を捕捉します。不正設定では候補DBを読みません。</summary>
    Task<LeapYearFolderRepairApproval> PrepareLibraryInitializationAsync();

    /// <summary>設定の外側L/P解放後に、捕捉済み実修復結果を既存の通知経路へ渡します。</summary>
    Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification notification);

    /// <summary>実親受付解放後に採用済みスコア純通知を結果から取り出し、必須UI・hostの実Taskと同じ結果の初回完了・解禁・後続登録を終えます。後段失敗の結果でも採用済み純通知を実行します。</summary>
    Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result);

    /// <summary>外側の同owner生存権限でスコアだけを更新し、処理と後片付けの実終端を待ちます。処理失敗は元原因を伴うOperationFailed、終了による取消はShutdownRequestedで返します。</summary>
    Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability);

    /// <summary>呼出元の共通受付内で差分再読込みと後片付けを終え、処理失敗は通知せず元原因を伴うOperationFailedで返します。専用警告も親解放後に呼出元が通知します。</summary>
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
    /// <param name="forceProjection">同じ全表出力で、署名一致による生成省略を迂回するか。</param>
    /// <param name="options">局所反映が捕捉した組。指定時は出力先とURL同期先を再解決せず使い、省略時は現在の確定値とconfigの出力先を使います。</param>
    Task ExportBeatorajaBmtAsync(string reason, string cleanupTablePath, LibraryFileMutationCapability capability, bool forceProjection = false, BeatorajaBmtOptionsSnapshot options = null);

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
    /// <summary>共有Pの受付状態が変わったことを表示条件へ通知します。</summary>
    event EventHandler OutputOperationAvailabilityChanged;
    /// <summary>設定の出力配置変更・全体同期が既存Pを非待機取得します。Busyはnull、呼出元が通知・cleanup終端まで保持します。</summary>
    LibraryFileMutationLease TryBeginOutputOperation();

    CustomFolderOutputSettingsSnapshot CustomFolderOutputSettings { get; }

    /// <summary>表の変更後配置を設定・DB・XML保存前に検査し、同一・親子の管理出力先を拒否します。</summary>
    void ValidateCustomFolderOutputRegistration(CustomFolderOutputSettingsSnapshot settings, IReadOnlyDictionary<string, string> renames);

    /// <summary>通常出力の実移行を行い、header確定後の投影・整理の未了入力を局所保存担当へ返します。</summary>
    /// <param name="migrationObserver">header確定直後に対象を受け取り、投影失敗後も同じreceiptを保持する担当。指定時の旧先整理は一括再試行へ委ねます。</param>
    /// <param name="outputBaseRenames">同じ受理要求で承認した現在名から最終名への対応。追加先の改名前も最終配置として検査します。</param>
    void ChangeCustomFolderBaseDirectoryWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        string additionalOutputBaseDirsBefore,
        string additionalOutputBaseDirsAfter,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability = null, Action<CustomFolderMigrationReceipt> migrationObserver = null,
            IReadOnlyDictionary<string, string> outputBaseRenames = null);

    /// <summary>ルート型の実移行を行い、header確定後の投影・整理の未了入力を局所保存担当へ返します。</summary>
    /// <param name="migrationObserver">header確定直後に対象を受け取り、投影失敗後も同じreceiptを保持する担当。指定時の旧先整理は一括再試行へ委ねます。</param>
    void ChangeCustomFolderBaseDirectoryRootWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability = null, Action<CustomFolderMigrationReceipt> migrationObserver = null);

    /// <summary>捕捉済み登録からルート型の最終一覧を返します。文書・保存境界を共有せず、設定担当が通常出力と一回のXML保存へまとめます。</summary>
    IReadOnlyList<string> PrepareCustomFolderOutputSearchRoots(
        string previousRootOutputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings, IReadOnlyList<string> currentDirectories,
        IReadOnlyDictionary<string, string> renames = null);

    /// <summary>実登録の対応と追加出力を変更し、保存済み登録と投影・整理の未了入力を局所保存担当へ返します。</summary>
    /// <param name="migrationObserver">header確定直後に移行対象を受け取り、投影失敗後も同じreceiptで未了だけを保持する担当。指定時の旧先整理は一括再試行へ委ねます。</param>
    int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
        string previousAdditionalOutputBaseDirectories,
        IReadOnlyDictionary<string, string> pendingRenames,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability = null, Action<CustomFolderMigrationReceipt> migrationObserver = null);
    /// <summary>捕捉済み対象を今回の確定配置へ投影し、成立済み投影を繰り返さず未了の旧先整理まで明示再試行します。</summary>
    void RetryCustomFolderMigrations(IReadOnlyList<CustomFolderMigrationReceipt> receipts,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability);
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

    /// <summary>現再生を維持し、次の明示再生またはNext開始で捕捉済みの方式へ切り替えます。</summary>
    /// <param name="createPlayer">次回の新規開始時にだけ実行する、確定済み設定を捕捉したfactory。</param>
    /// <param name="supportsBmson">factoryと同じ方式の形式能力。実置換前の明示行の受付に使います。</param>
    void UsePlayerForNextPlayback(Func<IBMSPlayer> createPlayer, bool supportsBmson);

    void NotifySettingsChanged();
}
