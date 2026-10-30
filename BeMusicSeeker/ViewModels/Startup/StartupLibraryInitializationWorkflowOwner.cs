using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 画面側へ渡す起動ライブラリ初期化の元の失敗を表します。
/// </summary>
internal sealed class StartupLibraryInitializationFailurePresentation
{
    /// <summary>
    /// 変更不能な失敗通知要求を作成します。
    /// </summary>
    /// <param name="exception">元の初期化失敗。</param>
    internal StartupLibraryInitializationFailurePresentation(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <summary>
    /// 元の初期化失敗を取得します。
    /// </summary>
    internal Exception Exception { get; }
}

/// <summary>
/// 画面の境界で起動ライブラリ初期化の失敗を通知します。
/// </summary>
internal interface IStartupLibraryInitializationFailurePresenter
{
    /// <summary>
    /// 変更不能な起動失敗要求を通知します。
    /// </summary>
    /// <param name="presentation">失敗通知要求。</param>
    void Present(StartupLibraryInitializationFailurePresentation presentation);
}

/// <summary>
/// 既存の起動枠とL/P受付を管理し、保存済みデータ・必要出力・LR2の終端を手続きで結びます。
/// </summary>
internal sealed class StartupLibraryInitializationWorkflowOwner
{
    private readonly SemaphoreSlim operationGate;

    /// <summary>
    /// 起動ライブラリ初期化の管理主体を作成します。
    /// </summary>
    /// <param name="operationGate">起動と再読込みを直列化する既存の共有gate。</param>
    internal StartupLibraryInitializationWorkflowOwner(SemaphoreSlim operationGate)
    {
        this.operationGate = operationGate
            ?? throw new ArgumentNullException(nameof(operationGate));
    }

    /// <summary>検証済みDBの限定候補を作業スレッドで読み、親受付前に既存YesNo判断を捕捉します。成功した前段の境界で終了を伝播し、実際の読取・画面障害は保持します。予約やDB作成はしません。</summary>
    /// <param name="songDbPath">検証済み設定が指定する候補読取対象DB。</param>
    /// <param name="dialogs">受付外で利用者判断を待つ既存の画面通知先。</param>
    /// <param name="isShutdownRequested">既存の終了寿命。新しい利用者取消や共有予約は持ちません。</param>
    internal async Task<LeapYearFolderRepairApproval> PrepareLeapYearFolderRepairAsync(string songDbPath, IUiDialogService dialogs, Func<bool> isShutdownRequested = null)
    {
        void ThrowIfShutdownRequested()
        {
            if (isShutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
        }
        ThrowIfShutdownRequested();
        var service = new BmsLibraryInitializationService();
        IReadOnlyList<LeapYearFolderRepairCandidate> unreadable = [];
        IReadOnlyList<LeapYearFolderRepairCandidate> candidates = await Task.Run(() =>
            service.CaptureLeapYearFolderRepairCandidates(new BmsLibraryDbGateway(songDbPath), out unreadable));
        ThrowIfShutdownRequested();
        List<LeapYearFolderRepairCandidate> approved = [];
        foreach (LeapYearFolderRepairCandidate candidate in candidates)
        {
            ThrowIfShutdownRequested();
            UiDialogResult result = await dialogs.ConfirmAsync(new UiConfirmationRequest(candidate.ConfirmationMessage,
                BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning, UiDialogButton.YesNo, UiDialogIcon.Exclamation, UiDialogDefaultResult.No));
            if (result?.Status == UiDialogStatus.AppClosing) { ThrowIfShutdownRequested(); }
            UiDialogRoute.ThrowIfNotShown(result, "Startup leap-year folder repair confirmation");
            ThrowIfShutdownRequested();
            if (result.IsAccepted && result.DefaultResult == UiDialogDefaultResult.Yes) { approved.Add(candidate); }
        }
        return new(songDbPath, candidates.Count > 0, approved, unreadable);
    }

    /// <summary>親受付とDB/writer保護の解放後、実補正の対象と既存警告を通知します。表示失敗は修復結果に合成せず、残りの通知も試みます。</summary>
    /// <param name="notification">この要求で確定した実修復の結果。nullなら通知せず終端します。</param>
    /// <param name="dialogs">親受付外の実通知Taskを所有する既存の表示先。</param>
    /// <param name="isShutdownRequested">呼出元の終了寿命。要求済みなら後続の通知を開始しません。</param>
    /// <param name="isCurrent">捕捉した要求の一致確認。旧要求から新しい通知を開始しません。</param>
    internal async Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification notification, IUiDialogService dialogs, Func<bool> isShutdownRequested = null, Func<bool> isCurrent = null)
    {
        if (notification == null) { return; }
        foreach ((string path, Exception failure) in notification.Failures)
        {
            await PresentNotificationAsync(new UiMessageRequest(
                string.Format(BeMusicSeeker.Properties.Resources.Error_FailedToChangeDate, DisplayedExceptionMessage.Format(failure)) + Environment.NewLine + path,
                BeMusicSeeker.Properties.Resources.MessageBoxTitle_Error, UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK),
                dialogs, isShutdownRequested, isCurrent, "Startup leap-year folder repair failure notification").ConfigureAwait(false);
        }
        if (notification.ShowWarning)
        {
            await PresentNotificationAsync(new UiMessageRequest(BeMusicSeeker.Properties.Resources.Warn_LR2LeapYearBugDetected,
                BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning, UiDialogButton.OK, UiDialogIcon.Exclamation, UiDialogDefaultResult.OK),
                dialogs, isShutdownRequested, isCurrent, "Startup leap-year warning notification").ConfigureAwait(false);
        }
    }

    internal static string BuildAppSchemaRepairWarningMessage(AppSchemaPreflightResult preflightResult)
    {
        if (preflightResult == null)
        {
            throw new ArgumentNullException(nameof(preflightResult));
        }
        return BeMusicSeeker.Properties.Resources.AppSchemaRepairWarningMessage;
    }

    /// <summary>
    /// 起動前修復の承認を確認し、承認済みの場合だけ修復処理を開始します。
    /// 警告が不要な修復は確認を挟まず開始し、警告を承認した事実はこの起動セッションで共有します。
    /// </summary>
    /// <param name="preflightResult">修復前のスキーマ検査結果。</param>
    /// <param name="approvedForSession">この起動セッションで警告を承認済みかどうか。</param>
    /// <param name="confirmWarning">警告が必要なときに確認を表示する処理。</param>
    /// <param name="startStartupRepair">承認後に修復処理を開始する処理。</param>
    /// <param name="shutdown">確認を取り消したときに終了を要求する処理。</param>
    /// <returns>修復を開始して起動を続ける場合は true。</returns>
    internal static bool TryStartAppSchemaRepairForStartup(
        AppSchemaPreflightResult preflightResult,
        ref bool approvedForSession,
        Func<string, bool?> confirmWarning,
        Action startStartupRepair,
        Action shutdown)
    {
        if (preflightResult == null)
        {
            throw new ArgumentNullException(nameof(preflightResult));
        }
        if (startStartupRepair == null)
        {
            throw new ArgumentNullException(nameof(startStartupRepair));
        }
        if (shutdown == null)
        {
            throw new ArgumentNullException(nameof(shutdown));
        }
        if (preflightResult.WarnRequired && !approvedForSession)
        {
            if (confirmWarning == null)
            {
                throw new ArgumentNullException(nameof(confirmWarning));
            }
            if (confirmWarning(BuildAppSchemaRepairWarningMessage(preflightResult)) != true)
            {
                shutdown();
                return false;
            }
            approvedForSession = true;
        }
        startStartupRepair();
        return true;
    }

    private bool schemaRepairApprovedForSession;

    /// <summary>親受付前にschemaの検査と既存の確認を終え、承認済み検査結果を局所で返します。終了中は新規確認を開始しません。</summary>
    internal async Task<AppSchemaPreflightResult> PrepareSchemaAsync(StartupSettingsSnapshot settings, IUiDialogService dialogs,
        Func<bool> isShutdownRequested, Action shutdown)
    {
        void CheckShutdown() { if (isShutdownRequested()) { throw new OperationCanceledException(); } }
        CheckShutdown();
        var service = new AppSchemaPreflightService();
        AppSchemaPreflightResult preflight = await Task.Run(() => service.Inspect(settings.LR2SongDBPath)).ConfigureAwait(false);
        CheckShutdown();
        bool approved = true;
        if (preflight.WarnRequired && !schemaRepairApprovedForSession)
        {
            CheckShutdown();
            UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(BuildAppSchemaRepairWarningMessage(preflight),
                BeMusicSeeker.Properties.Resources.AppSchemaRepairWarningTitle, UiDialogButton.OKCancel, UiDialogIcon.Exclamation, UiDialogDefaultResult.Cancel)).ConfigureAwait(false);
            if (confirmation?.Status == UiDialogStatus.AppClosing && isShutdownRequested()) { throw new OperationCanceledException(); }
            UiDialogRoute.ThrowIfNotShown(confirmation, "App schema repair startup confirmation");
            CheckShutdown();
            approved = confirmation.IsAccepted;
        }
        bool proceed = TryStartAppSchemaRepairForStartup(preflight, ref schemaRepairApprovedForSession, _ => approved,
            () => { }, shutdown);
        return proceed ? preflight : null;
    }

    /// <summary>親L/P取得後に再検査し、受付前に承認した修復範囲でDB準備を終えます。新たに警告が必要な変更は受付外で再判断するため失敗させます。</summary>
    internal Task ApplyPreparedSchemaAsync(StartupSettingsSnapshot settings, AppSchemaPreflightResult approved)
        => Task.Run(() =>
        {
            var service = new AppSchemaPreflightService();
            AppSchemaPreflightResult current = service.Inspect(settings.LR2SongDBPath);
            if (current.WarnRequired && !approved.WarnRequired && !schemaRepairApprovedForSession)
            {
                throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.AppSchemaRepairWarningMessage);
            }
            ApplyAppSchemaRepairForStartupOrThrow(service, current, settings.LR2SongDBPath);
        });

    private static void ApplyAppSchemaRepairForStartupOrThrow(
        AppSchemaPreflightService appSchemaPreflightService,
        AppSchemaPreflightResult preflightResult,
        string songDbPath)
    {
        var gateway = new BmsLibraryDbGateway(songDbPath);
        if (preflightResult.WarnRequired || preflightResult.NeedsAppSchemaVersionRepair || preflightResult.RepairRequired)
        {
            gateway.RepairAppOwnedSchema();
        }
        else
        {
            gateway.EnsureAppOwnedSchema();
        }
        AppSchemaPreflightResult finalResult = appSchemaPreflightService.Inspect(songDbPath);
        if (finalResult.NeedsAppSchemaVersionRepair
            || finalResult.RepairRequired)
        {
            throw new InvalidOperationException("app schema repair did not converge.");
        }
    }

    /// <summary>親がL/Pを一度所有し、設定適用では呼出元の生存権限を借用します。部分取得時は副作用なく解放します。</summary>
    internal StartupRequiredOperationLease AcquireRequiredOperation(ChartFileOperationSynchronizer libraryAdmission,
        ChartFileOperationSynchronizer playlistAdmission, LibraryFileMutationCapability capability = null)
    {
        LibraryFileMutationLease libraryLease = capability != null ? libraryAdmission.Borrow(capability)
            : libraryAdmission.TryEnter(out IDisposable acquiredLibrary) ? (LibraryFileMutationLease)acquiredLibrary
            : throw CreateAdmissionFailure(libraryAdmission);
        try
        {
            LibraryFileMutationLease playlistLease = capability?.PlaylistCapability != null ? playlistAdmission.Borrow(capability.PlaylistCapability)
                : playlistAdmission.TryEnter(out IDisposable acquiredPlaylist) ? (LibraryFileMutationLease)acquiredPlaylist
                : throw CreateAdmissionFailure(playlistAdmission);
            return new StartupRequiredOperationLease(libraryLease, playlistLease);
        }
        catch { libraryLease.Dispose(); throw; }
    }

    private static Exception CreateAdmissionFailure(ChartFileOperationSynchronizer admission)
        => admission.IsAdmissionClosed ? new OperationCanceledException()
            : new InvalidOperationException(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);

    /// <summary>成功公開後の警告と初回案内を受付外で待ちます。通知だけの失敗を初期化結果へ返さず、終了と要求の鮮度は維持します。</summary>
    internal async Task PresentCompletionNotificationsAsync(LeapYearFolderRepairNotification repair, bool showRootWarning,
        bool firstStartup, IUiDialogService dialogs, Func<bool> shutdownRequested, Func<bool> isCurrent, IReadOnlyList<UiMessageRequest> backupNotifications = null, Exception lr2Failure = null)
    {
        foreach (UiMessageRequest request in backupNotifications ?? [])
        {
            await PresentNotificationAsync(request, dialogs, shutdownRequested, isCurrent, "Startup backup notification").ConfigureAwait(false);
        }
        await PresentLeapYearFolderRepairAsync(repair, dialogs, shutdownRequested, isCurrent).ConfigureAwait(false);
        if (showRootWarning) { await Show(BeMusicSeeker.Properties.Resources.Warning_LR2RootPathNotSet, BeMusicSeeker.Properties.Resources.Warning, UiDialogIcon.Exclamation).ConfigureAwait(false); }
        if (lr2Failure != null)
        {
            await Show(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_lr2_song_db_sync
                + Environment.NewLine + DisplayedExceptionMessage.Format(lr2Failure), BeMusicSeeker.Properties.Resources.Error, UiDialogIcon.Hand).ConfigureAwait(false);
        }
        if (firstStartup) { await Show(BeMusicSeeker.Properties.Resources.Msg_init_completed, BeMusicSeeker.Properties.Resources.Information, UiDialogIcon.Asterisk).ConfigureAwait(false); }

        Task Show(string message, string title, UiDialogIcon image)
            => PresentNotificationAsync(new UiMessageRequest(message, title, UiDialogButton.OK, image, UiDialogDefaultResult.OK),
                dialogs, shutdownRequested, isCurrent, "Startup completion notification");
    }

    // 起動後の案内だけを扱います。事前のConfirmAsyncには適用しません。
    private static async Task PresentNotificationAsync(UiMessageRequest request, IUiDialogService dialogs,
        Func<bool> shutdownRequested, Func<bool> isCurrent, string route)
    {
        if (isCurrent?.Invoke() == false) { return; }
        if (shutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
        try
        {
            UiDialogResult result = await dialogs.ShowMessageAsync(request).ConfigureAwait(false);
            if (result?.Status == UiDialogStatus.AppClosing && shutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
            UiDialogRoute.ThrowIfNotShown(result, route);
        }
        catch (OperationCanceledException) when (shutdownRequested?.Invoke() == true) { throw; }
        catch (Exception failure)
        {
            try { NLogWrapper.FileLogger?.Warn(failure, "startup_notification_failed route=" + route); }
            catch { }
        }
        if (shutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
    }

    /// <summary>モデルの既存走査警告結果を、受付外の既存表示要求へ変換します。</summary>
    internal static UiMessageRequest CreateScanWarningNotification(LibraryScanWarning warning)
    {
        string reason = string.IsNullOrWhiteSpace(warning.Reason) ? "unknown" : warning.Reason;
        string format = warning.Kind switch
        {
            LibraryScanWarningKind.EverythingFallback => BeMusicSeeker.Properties.Resources.Warn_EverythingFallbackScanUsed,
            LibraryScanWarningKind.Incomplete => BeMusicSeeker.Properties.Resources.Warn_FileScanSkippedIncomplete,
            LibraryScanWarningKind.EmptyWithExistingData => BeMusicSeeker.Properties.Resources.Warn_EmptyScanWithExistingDbSkipped,
            _ => throw new ArgumentOutOfRangeException(nameof(warning))
        };
        return new(string.Format(format, reason), BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            UiDialogButton.OK, UiDialogIcon.Exclamation, UiDialogDefaultResult.OK);
    }

    /// <summary>独立した保存ヘッダーとファイル走査を並列に読み、ローカル出力、必要LR2同期を順に直接待ちます。</summary>
    /// <param name="isShutdownRequested">既存Shellの終了受付も含め、選択の前後で確認する寿命。</param>
    /// <param name="dialogs">保留復元の未確定失敗に限り明示選択を待つ画面窓口。</param>
    /// <param name="scoreNotificationObserver">親の局所結果への単純受渡し。採用は本体で完了し、返された純通知は実親L/P解放後に実行します。</param>
    /// <param name="forceBeatorajaBmtProjection">今回の必要BMT出力に署名一致による省略を適用しないか。</param>
    internal async Task<StartupRequiredInitializationResult> InitializeRequiredAsync(BMSLibrary library, BMSPlaylist playlist,
        Lr2SongDbSyncWorkflowOwner lr2Workflow, PerformanceInteraction performanceInteraction, LibraryFileMutationCapability capability, IUiDialogService dialogs, OperationProgressRequest originatingRequest = null, CustomFolderOutputSettingsSnapshot customFolderSettings = null,
        LeapYearFolderRepairApproval leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification> repairNotificationObserver = null, Action<LibraryScanWarning> warningObserver = null, Action<Action> scoreNotificationObserver = null, bool forceBeatorajaBmtProjection = false, Func<bool> isShutdownRequested = null)
    {
        Task<LibraryFileInitializationResult> filesTask = Task.Run(() => library.InitializeStartup([], null, performanceInteraction, capability, originatingRequest, leapYearRepairApproval, repairNotificationObserver, warningObserver, scoreNotificationObserver));
        Task<bool> headersTask;
        try { headersTask = playlist.LoadStartupHeadersAsync(capability.PlaylistCapability); }
        catch (Exception failure) { headersTask = Task.FromException<bool>(failure); }
        // 一方が失敗しても開始済みの相手とcleanupを回収してから親受付を解放する。
        await Task.WhenAll(filesTask, headersTask).ConfigureAwait(false);
        LibraryFileInitializationResult fileResult = await filesTask.ConfigureAwait(false);
        if (await RestorePendingPackagesAsync(library, capability, fileResult.Options, dialogs, isShutdownRequested).ConfigureAwait(false))
        { return new(fileResult, null, ExitRequested: true); }
        return await CompleteRequiredAsync(library, playlist, lr2Workflow, fileResult,
            await headersTask.ConfigureAwait(false), capability, originatingRequest, customFolderSettings, forceBeatorajaBmtProjection).ConfigureAwait(false);
    }

    /// <summary>再初期化の既存のファイル更新範囲を保ち、今回の結果で必要出力と同期を完了します。</summary>
    /// <param name="isShutdownRequested">既存Shellの終了受付も含め、選択の前後で確認する寿命。</param>
    /// <param name="dialogs">保留復元の未確定失敗に限り明示選択を待つ画面窓口。</param>
    /// <param name="scoreNotificationObserver">親の局所結果への単純受渡し。採用は本体で完了し、返された純通知は実親L/P解放後に実行します。</param>
    internal async Task<StartupRequiredInitializationResult> ReinitializeAsync(BMSLibrary library, BMSPlaylist playlist,
        Lr2SongDbSyncWorkflowOwner lr2Workflow, LibraryFileMutationCapability capability, IUiDialogService dialogs, OperationProgressRequest originatingRequest = null,
        LeapYearFolderRepairApproval leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification> repairNotificationObserver = null, Action<LibraryScanWarning> warningObserver = null, Action<Action> scoreNotificationObserver = null, Func<bool> isShutdownRequested = null)
    {
        LibraryFileInitializationResult result = await Task.Run(() => library.ReinitializeUnderAdmission(capability, originatingRequest, leapYearRepairApproval, repairNotificationObserver, warningObserver, scoreNotificationObserver)).ConfigureAwait(false);
        if (await RestorePendingPackagesAsync(library, capability, result.Options, dialogs, isShutdownRequested).ConfigureAwait(false))
        { return new(result, null, ExitRequested: true); }
        return await CompleteRequiredAsync(library, playlist, lr2Workflow, result, false, capability, originatingRequest).ConfigureAwait(false);
    }

    /// <summary>保留全復元だけを再試行し、未確定失敗に対する明示選択を同じ親L/P内で待ちます。モデルの保護は各APIの終端で解放済みです。</summary>
    /// <param name="library">今回のライブラリ。</param>
    /// <param name="capability">呼出元が実終端まで保持するL/P権限。</param>
    /// <param name="options">ファイル段階が捕捉した今回の設定。</param>
    /// <param name="dialogs">型付き選択を表示する既存の窓口。</param>
    /// <param name="isShutdownRequested">モデル終了通知前の既存Shell終了受付も確認する寿命。</param>
    /// <returns>通常の終了経路への引渡しを明示選択した場合だけ真。</returns>
    internal async Task<bool> RestorePendingPackagesAsync(BMSLibrary library, LibraryFileMutationCapability capability,
        BmsLibraryOptionsSnapshot options, IUiDialogService dialogs, Func<bool> isShutdownRequested = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        PendingPackageRestoreException currentFailure;
        try
        {
            await library.RestorePendingPackagesUnderAdmissionAsync(capability, options).ConfigureAwait(false);
            return false;
        }
        catch (PendingPackageRestoreException failure) { currentFailure = failure; }
        while (true)
        {
            // DB確定後の通知・配送・Readyの失敗はこのcatchへ含めず、通常の初期化失敗へ返す。
            try { NLogWrapper.FileLogger?.Warn(currentFailure.InnerException ?? currentFailure, "pending_package_restore_failed"); }
            catch { /* 元の復元原因を診断の失敗で置き換えません。 */ }
            if (library.IsShutdownRequested || isShutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
            UiWindowDialogResult<PendingPackageRestoreChoice> response = await dialogs.ShowWindowAsync(
                new UiWindowDialogRequest<PendingPackageRestoreWindow, PendingPackageRestoreChoice>(
                    () => new PendingPackageRestoreWindow(currentFailure.InnerException?.Message ?? currentFailure.Message),
                    window => window.Choice)).ConfigureAwait(false);
            if (library.IsShutdownRequested || isShutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
            if (response.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
            { throw new InvalidOperationException(string.Format(BeMusicSeeker.Properties.Resources.Error_PendingRestoreDialogFailedFormat, response.Status), response.Error); }
            if (!response.IsAccepted || response.Value == PendingPackageRestoreChoice.None)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(currentFailure).Throw(); }
            if (response.Value == PendingPackageRestoreChoice.Exit) { return true; }
            try
            {
                if (response.Value == PendingPackageRestoreChoice.Reset)
                { await library.ResetPendingPackagesUnderAdmissionAsync(capability).ConfigureAwait(false); }
                else if (response.Value == PendingPackageRestoreChoice.Retry)
                { await library.RestorePendingPackagesUnderAdmissionAsync(capability, options).ConfigureAwait(false); }
                else { throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_PendingRestoreChoiceUnknown); }
                return false;
            }
            catch (PendingPackageRestoreException failure) { currentFailure = failure; }
        }
    }

    private async Task<StartupRequiredInitializationResult> CompleteRequiredAsync(BMSLibrary library, BMSPlaylist playlist,
        Lr2SongDbSyncWorkflowOwner lr2Workflow, LibraryFileInitializationResult fileResult, bool verifyRows,
        LibraryFileMutationCapability capability, OperationProgressRequest originatingRequest, CustomFolderOutputSettingsSnapshot customFolderSettings = null, bool forceBeatorajaBmtProjection = false)
    {
        if (library.IsShutdownRequested) { throw new OperationCanceledException(); }
        BmsLibraryOptionsSnapshot options = fileResult.Options ?? library.Lr2Synchronization.CurrentOptionsSnapshot;
        bool syncNeeded = options.OperationModeLR2DB && library.Lr2Synchronization.EvaluateLr2SongDbSyncStatus(
            true, Lr2SongDbSyncSignatureBuilder.Build(options), DateTime.UtcNow).IsNeeded;
        if (fileResult.NormalOutputBasePathChange is { } pathChange)
        {
            customFolderSettings = (customFolderSettings ?? playlist.GetCustomFolderOutputSettings()).WithDirectoryPaths(
                path => BmsLibraryOptionsSnapshot.ApplyConfirmedRootPrefixes(path, [pathChange.Previous], [pathChange.Current]));
        }
        RequiredChartInfoHydrationResult chartInfo = await Task.Run(() => library.EnsureRequiredStartupData(capability, originatingRequest)).ConfigureAwait(false);
        Lr2SongDbSyncPreparedDataSurface prepared = await playlist.CompleteStartupOutputsAsync(verifyRows, syncNeeded,
            capability.PlaylistCapability, originatingRequest, customFolderSettings, forceBeatorajaBmtProjection,
            fileResult.NormalOutputBasePathChange).ConfigureAwait(false);
        Exception lr2Failure = null;
        try
        {
            await lr2Workflow.SynchronizeRequiredAsync("required_initialization", fileResult, prepared, capability, originatingRequest, options).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (library.IsShutdownRequested) { throw; }
        catch (Exception failure)
        {
            library.Lr2Synchronization.RecordLr2SongDbSyncFailure(Lr2SongDbSyncSignatureBuilder.Build(options), string.Empty,
                library.Lr2SongDbSyncRequestedVersion, failure, "failed");
            lr2Failure = failure;
            Task.FromException(failure).ObserveFault("RequiredStartupLr2Sync");
        }
        if (library.IsShutdownRequested) { throw new OperationCanceledException(); }
        return new(fileResult, lr2Failure, chartInfo);
    }

    /// <summary>
    /// 共有gateを取得し、一度だけ解放するleaseを返します。
    /// </summary>
    /// <returns>取得した共有gateの型付きlease。</returns>
    internal async Task<StartupLibraryInitializationGateLease> AcquireGateAsync()
    {
        await operationGate.WaitAsync();
        return new StartupLibraryInitializationGateLease(operationGate);
    }
}

/// <summary>
/// 取得済みの起動gateを所有し、一度だけ解放します。
/// </summary>
internal sealed class StartupLibraryInitializationGateLease : IDisposable
{
    private SemaphoreSlim operationGate;

    /// <summary>
    /// 取得済みgateのleaseを作成します。
    /// </summary>
    /// <param name="operationGate">このleaseが所有するgate。</param>
    internal StartupLibraryInitializationGateLease(SemaphoreSlim operationGate)
    {
        this.operationGate = operationGate
            ?? throw new ArgumentNullException(nameof(operationGate));
    }

    /// <summary>
    /// 取得したgateを一度だけ解放します。
    /// </summary>
    public void Dispose()
    {
        SemaphoreSlim gate = Interlocked.Exchange(ref operationGate, null);
        gate?.Release();
    }
}

/// <summary>親が保持する受付と派生権限を一括解放します。子の借用は親受付を解放しません。</summary>
internal sealed class StartupRequiredOperationLease : IDisposable
{
    private readonly LibraryFileMutationLease libraryLease;
    private readonly LibraryFileMutationLease playlistLease;
    private readonly LibraryFileMutationCapability libraryAuthority;
    private readonly LibraryFileMutationCapability playlistAuthority;

    internal StartupRequiredOperationLease(LibraryFileMutationLease libraryLease, LibraryFileMutationLease playlistLease)
    {
        this.libraryLease = libraryLease;
        this.playlistLease = playlistLease;
        libraryAuthority = libraryLease.CreateMutationCapability();
        playlistAuthority = playlistLease.CreateMutationCapability();
        Capability = libraryAuthority.WithPlaylistCapability(playlistAuthority);
    }

    /// <summary>全必須処理とcleanupの終端まで生存するL/P権限です。</summary>
    internal LibraryFileMutationCapability Capability { get; }

    public void Dispose()
    {
        Capability.Dispose();
        playlistAuthority.Dispose();
        libraryAuthority.Dispose();
        playlistLease.Dispose();
        libraryLease.Dispose();
    }
}

/// <summary>ローカル成功・LR2単独失敗と、親受付解放後に引き渡す明示終了選択を区別します。ローカル失敗は例外で伝播します。</summary>
/// <param name="ExitRequested">保留復元の選択で明示した終了です。通常成功と出力・後続の開始より前に、親受付解放後の既存Closeへ渡します。</param>
internal sealed record StartupRequiredInitializationResult(LibraryFileInitializationResult FileResult, Exception Lr2Failure,
    RequiredChartInfoHydrationResult ChartInfo = null, bool ExitRequested = false);
