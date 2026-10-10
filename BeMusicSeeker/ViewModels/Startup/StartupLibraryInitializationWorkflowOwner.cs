using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
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

    /// <summary>親受付とDB/writer保護の解放後、実補正の元対象・原因と既存警告条件を通知します。既知の終了とAppClosingだけを取消に分類し、実表示失敗は保持します。</summary>
    /// <param name="notification">この要求で確定した実修復の結果。nullなら通知せず終端します。</param>
    /// <param name="dialogs">親受付外の実通知Taskを所有する既存の表示先。</param>
    /// <param name="isShutdownRequested">呼出元の既存終了寿命。表示成功後にも確認し、実失敗を事後の終了で隠しません。</param>
    /// <param name="isCurrent">捕捉した要求の一致確認。各通知前と成功後に確認し、旧通知から新要求へ作用しません。</param>
    internal async Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification notification, IUiDialogService dialogs, Func<bool> isShutdownRequested = null, Func<bool> isCurrent = null)
    {
        if (notification == null) { return; }
        void ThrowIfShutdownRequested()
        {
            if (isShutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
        }
        void ValidateResult(UiDialogResult result, string route)
        {
            if (result?.Status == UiDialogStatus.AppClosing && isShutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
            UiDialogRoute.ThrowIfNotShown(result, route);
            if (isShutdownRequested?.Invoke() == true) { throw new OperationCanceledException(); }
        }
        foreach ((string path, Exception failure) in notification.Failures)
        {
            if (isCurrent?.Invoke() == false) { return; }
            ThrowIfShutdownRequested();
            UiDialogResult result = await dialogs.ShowMessageAsync(new UiMessageRequest(
                string.Format(BeMusicSeeker.Properties.Resources.Error_FailedToChangeDate, DisplayedExceptionMessage.Format(failure)) + Environment.NewLine + path,
                BeMusicSeeker.Properties.Resources.MessageBoxTitle_Error, UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK));
            ValidateResult(result, "Startup leap-year folder repair failure notification");
            if (isCurrent?.Invoke() == false) { return; }
        }
        if (notification.ShowWarning)
        {
            if (isCurrent?.Invoke() == false) { return; }
            ThrowIfShutdownRequested();
            UiDialogResult result = await dialogs.ShowMessageAsync(new UiMessageRequest(BeMusicSeeker.Properties.Resources.Warn_LR2LeapYearBugDetected,
                BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning, UiDialogButton.OK, UiDialogIcon.Exclamation, UiDialogDefaultResult.OK));
            ValidateResult(result, "Startup leap-year warning notification");
            if (isCurrent?.Invoke() == false) { return; }
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

    /// <summary>成功公開後の警告と初回案内を受付外で待ちます。新規通知前と成功後に寿命を確認し、実障害は後発の終了で置き換えません。</summary>
    internal async Task<Exception> PresentCompletionNotificationsAsync(LeapYearFolderRepairNotification repair, bool showRootWarning,
        bool firstStartup, IUiDialogService dialogs, Func<bool> shutdownRequested, Func<bool> isCurrent, IReadOnlyList<UiMessageRequest> backupNotifications = null, Exception lr2Failure = null)
    {
        Exception failure = null;
        async Task Present(Func<Task> notification)
        {
            if (!isCurrent()) { return; }
            if (shutdownRequested())
            {
                if (failure == null) { throw new OperationCanceledException(); }
                return;
            }
            try { await notification().ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdownRequested() && failure == null) { throw; }
            catch (Exception exception)
            {
                if (failure == null) { failure = exception; }
                else { Task.FromException(exception).ObserveFault("Startup completion notification"); }
            }
            if (failure == null && shutdownRequested()) { throw new OperationCanceledException(); }
            if (!isCurrent()) { return; }
        }
        foreach (UiMessageRequest request in backupNotifications ?? [])
        {
            await Present(async () =>
            {
                UiDialogResult result = await dialogs.ShowMessageAsync(request).ConfigureAwait(false);
                if (result?.Status == UiDialogStatus.AppClosing && shutdownRequested()) { throw new OperationCanceledException(); }
                UiDialogRoute.ThrowIfNotShown(result, "Startup backup notification");
            }).ConfigureAwait(false);
        }
        await Present(() => PresentLeapYearFolderRepairAsync(repair, dialogs, shutdownRequested, isCurrent)).ConfigureAwait(false);
        if (showRootWarning) { await Present(() => Show(BeMusicSeeker.Properties.Resources.Warning_LR2RootPathNotSet, BeMusicSeeker.Properties.Resources.Warning, UiDialogIcon.Exclamation)).ConfigureAwait(false); }
        if (lr2Failure != null)
        {
            await Present(() => Show(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_lr2_song_db_sync
                + Environment.NewLine + DisplayedExceptionMessage.Format(lr2Failure), BeMusicSeeker.Properties.Resources.Error, UiDialogIcon.Hand)).ConfigureAwait(false);
        }
        if (firstStartup) { await Present(() => Show(BeMusicSeeker.Properties.Resources.Msg_init_completed, BeMusicSeeker.Properties.Resources.Information, UiDialogIcon.Asterisk)).ConfigureAwait(false); }
        return failure;

        async Task Show(string message, string title, UiDialogIcon image)
        {
            UiDialogResult result = await dialogs.ShowMessageAsync(new UiMessageRequest(message, title, UiDialogButton.OK, image, UiDialogDefaultResult.OK)).ConfigureAwait(false);
            if (result?.Status == UiDialogStatus.AppClosing && shutdownRequested()) { throw new OperationCanceledException(); }
            UiDialogRoute.ThrowIfNotShown(result, "Startup completion notification");
        }
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
    internal async Task<StartupRequiredInitializationResult> InitializeRequiredAsync(BMSLibrary library, BMSPlaylist playlist,
        Lr2SongDbSyncWorkflowOwner lr2Workflow, PerformanceInteraction performanceInteraction, LibraryFileMutationCapability capability, OperationProgressRequest originatingRequest = null, CustomFolderOutputSettingsSnapshot customFolderSettings = null,
        LeapYearFolderRepairApproval leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification> repairNotificationObserver = null, Action<LibraryScanWarning> warningObserver = null)
    {
        Task<LibraryFileInitializationResult> filesTask = Task.Run(() => library.InitializeStartup([], null, performanceInteraction, capability, originatingRequest, leapYearRepairApproval, repairNotificationObserver, warningObserver));
        Task<bool> headersTask;
        try { headersTask = playlist.LoadStartupHeadersAsync(capability.PlaylistCapability); }
        catch (Exception failure) { headersTask = Task.FromException<bool>(failure); }
        // 一方が失敗しても開始済みの相手とcleanupを回収してから親受付を解放する。
        await Task.WhenAll(filesTask, headersTask).ConfigureAwait(false);
        return await CompleteRequiredAsync(library, playlist, lr2Workflow, await filesTask.ConfigureAwait(false),
            await headersTask.ConfigureAwait(false), capability, originatingRequest, customFolderSettings).ConfigureAwait(false);
    }

    /// <summary>再初期化の既存のファイル更新範囲を保ち、今回の結果で必要出力と同期を完了します。</summary>
    internal async Task<StartupRequiredInitializationResult> ReinitializeAsync(BMSLibrary library, BMSPlaylist playlist,
        Lr2SongDbSyncWorkflowOwner lr2Workflow, LibraryFileMutationCapability capability, OperationProgressRequest originatingRequest = null,
        LeapYearFolderRepairApproval leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification> repairNotificationObserver = null, Action<LibraryScanWarning> warningObserver = null)
    {
        LibraryFileInitializationResult result = await Task.Run(() => library.ReinitializeUnderAdmission(capability, originatingRequest, leapYearRepairApproval, repairNotificationObserver, warningObserver)).ConfigureAwait(false);
        return await CompleteRequiredAsync(library, playlist, lr2Workflow, result, false, capability, originatingRequest).ConfigureAwait(false);
    }

    private async Task<StartupRequiredInitializationResult> CompleteRequiredAsync(BMSLibrary library, BMSPlaylist playlist,
        Lr2SongDbSyncWorkflowOwner lr2Workflow, LibraryFileInitializationResult fileResult, bool verifyRows,
        LibraryFileMutationCapability capability, OperationProgressRequest originatingRequest, CustomFolderOutputSettingsSnapshot customFolderSettings = null)
    {
        if (library.IsShutdownRequested) { throw new OperationCanceledException(); }
        BmsLibraryOptionsSnapshot options = fileResult.Options ?? library.Lr2Synchronization.CurrentOptionsSnapshot;
        bool syncNeeded = options.OperationModeLR2DB && library.Lr2Synchronization.EvaluateLr2SongDbSyncStatus(
            true, Lr2SongDbSyncSignatureBuilder.Build(options), DateTime.UtcNow).IsNeeded;
        RequiredChartInfoHydrationResult chartInfo = await Task.Run(() => library.EnsureRequiredStartupData(capability, originatingRequest)).ConfigureAwait(false);
        Lr2SongDbSyncPreparedDataSurface prepared = await playlist.CompleteStartupOutputsAsync(verifyRows, syncNeeded,
            capability.PlaylistCapability, originatingRequest, customFolderSettings).ConfigureAwait(false);
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

/// <summary>ローカル成功とLR2だけの失敗を区別して返します。ローカル失敗は例外で伝播します。</summary>
internal sealed record StartupRequiredInitializationResult(LibraryFileInitializationResult FileResult, Exception Lr2Failure,
    RequiredChartInfoHydrationResult ChartInfo = null);
