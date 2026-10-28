using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal abstract class DuplicateMaintenanceWorkflowChangedEventArgs : EventArgs
{
}

internal sealed class DuplicateMaintenanceRefreshSuppressionChangedEventArgs : DuplicateMaintenanceWorkflowChangedEventArgs
{
    internal DuplicateMaintenanceRefreshSuppressionChangedEventArgs(bool isSuppressed)
    {
        IsSuppressed = isSuppressed;
    }

    internal bool IsSuppressed { get; }
}

internal enum DuplicateFolderKeyboardActionKind
{
    None,
    Merge,
    Cleanup,
    OpenContextMenu
}

internal sealed class DuplicateFolderKeyboardAction
{
    internal DuplicateFolderKeyboardAction(
        DuplicateFolderKeyboardActionKind kind,
        string destinationPath = null)
    {
        Kind = kind;
        DestinationPath = destinationPath;
    }

    internal DuplicateFolderKeyboardActionKind Kind { get; }

    internal string DestinationPath { get; }
}

internal interface IDuplicateMaintenanceStore
{
    /// <summary>取得済み共通権限を明示的に内部変更へ渡すportを返します。呼出元が実処理・後片付けまで元leaseを保持します。</summary>
    IDuplicateMaintenanceStore ForAcceptedOperation(LibraryFileMutationCapability capability) => this;

    /// <summary>固定実対象の条件付きPを停止前に取得します。独立代替portでは管理出力を持ちません。</summary>
    bool TryBeginPhysicalMutation(BMSLibrary library, IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease lease)
    { lease = null; return true; }

    /// <summary>フォルダ統合の変更セッションを実行し、終端へ確定結果を返します。</summary>
    DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
        BMSLibrary library,
        string sourceDirectory,
        string destinationDirectory,
        long operationId);

    /// <summary>最初の件数確認前に、モデルの現在値と安全属性を固定します。</summary>
    LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts);

    /// <summary>固定対象と全確認後の明示的な承認候補を渡し、モデル内で再確認せず削除します。</summary>
    LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderPaths);
}

internal sealed class DuplicateMaintenanceMutationResult
{
    private DuplicateMaintenanceMutationResult(
        bool succeeded,
        DuplicateViewContext selectionContext,
        int removedChartCount,
        Exception failure,
        DuplicateMergeMaintenanceReceipt mutationReceipt = null,
        LibraryChartRemovalOutcome removalOutcome = null)
    {
        RemovalOutcome = removalOutcome;
        Succeeded = succeeded;
        SelectionContext = selectionContext;
        RemovedChartCount = removedChartCount;
        Failure = failure;
        MutationReceipt = mutationReceipt;
    }

    /// <summary>Confirmed deletion and independent catalog failure facts.</summary>
    internal LibraryChartRemovalOutcome RemovalOutcome { get; }

    /// <summary>捕捉済みの選択入力を保ち、計画件数でなく確認済み削除件数と必須失敗を返します。</summary>
    internal static DuplicateMaintenanceMutationResult FromRemoval(DuplicateViewContext selectionContext,
        LibraryChartRemovalOutcome outcome, Exception failure)
    {
        Exception primary = outcome?.RequiredFailure;
        Exception combined = primary == null ? failure : failure == null ? primary : new AggregateException(primary, failure);
        return new DuplicateMaintenanceMutationResult(combined == null, selectionContext,
            outcome?.ConfirmedChartCount ?? 0, combined, removalOutcome: outcome);
    }

    internal bool Succeeded { get; }

    /// <summary>操作前に捕捉した短命な所属入力です。次の対象がなければnullです。</summary>
    internal DuplicateViewContext SelectionContext { get; }

    internal int RemovedChartCount { get; }

    internal Exception Failure { get; }

    internal DuplicateMergeMaintenanceReceipt MutationReceipt { get; }

    /// <summary>マージ変更前に見つかった immutable な宛先型衝突を取得します。</summary>
    internal IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts =>
        MutationReceipt?.DestinationTypeConflicts ?? [];

    /// <summary>Includes the catalog commit observed by library deletion.</summary>
    internal bool HasDurableCommit => RemovalOutcome?.CatalogDurable == true || MutationReceipt?.HasDurableCommit == true;

    /// <summary>
    /// Gets whether merge finalization failed after durable file/catalog state.
    /// </summary>
    internal bool HasDurableFinalizationFailure => RemovalOutcome?.RequiredFinalizationFailed == true || MutationReceipt?.HasDurableFinalizationFailure == true;

    internal bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    internal bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt?.RecoveryPaths ?? [];

    /// <summary>成功した保守の事実と、操作前に捕捉した次または存続グループの所属を返します。</summary>
    internal static DuplicateMaintenanceMutationResult Completed(
        DuplicateViewContext selectionContext,
        int removedChartCount = 0,
        DuplicateMergeMaintenanceReceipt mutationReceipt = null)
    {
        return new DuplicateMaintenanceMutationResult(true, selectionContext, removedChartCount, null, mutationReceipt);
    }

    /// <summary>受付・確認の拒否を返します。捕捉入力があっても成功時の選択へ進みません。</summary>
    internal static DuplicateMaintenanceMutationResult Rejected(DuplicateViewContext selectionContext)
    {
        return new DuplicateMaintenanceMutationResult(false, selectionContext, 0, null);
    }

    /// <summary>物理対象なしを返し、成功時の選択を行いません。</summary>
    internal static DuplicateMaintenanceMutationResult NoWork(DuplicateViewContext selectionContext)
    {
        return new DuplicateMaintenanceMutationResult(false, selectionContext, 0, null);
    }

    /// <summary>失敗の事実を保持し、捕捉した選択入力を通常成功へ読み替えません。</summary>
    internal static DuplicateMaintenanceMutationResult Failed(
        DuplicateViewContext selectionContext,
        Exception failure,
        int removedChartCount = 0,
        DuplicateMergeMaintenanceReceipt mutationReceipt = null)
    {
        return new DuplicateMaintenanceMutationResult(
            false,
            selectionContext,
            removedChartCount,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            mutationReceipt);
    }

    /// <summary>統合の必須失敗・部分確定と捕捉入力を終端へ渡し、非成功の選択適用を防ぎます。</summary>
    internal static DuplicateMaintenanceMutationResult FromMergeReceipt(
        DuplicateViewContext selectionContext,
        DuplicateMergeMaintenanceReceipt mutationReceipt)
    {
        if (mutationReceipt?.SessionReceipt.HasRequiredFailure == true)
        {
            return new DuplicateMaintenanceMutationResult(
                false,
                selectionContext,
                0,
                mutationReceipt?.SessionReceipt.PrimaryFailure,
                mutationReceipt);
        }
        if (mutationReceipt?.MergeApplied != true)
        {
            return new DuplicateMaintenanceMutationResult(false, selectionContext, 0, null, mutationReceipt);
        }
        return Completed(selectionContext, mutationReceipt: mutationReceipt);
    }
}

internal sealed class DuplicateMaintenanceWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly IChartMutationPlaybackPort playback;
    private readonly IUiDialogService dialogs;
    private readonly Func<bool> showConfirmationProvider;

    private readonly Func<string, bool> duplicateFolderDirectoryExists;

    private readonly Func<string, ExplorerOpenResult> duplicateFolderExplorerOpen;

    private readonly Func<DuplicateGroup, DuplicateViewContext> duplicateGroupNextSelectionProvider;

    private readonly IDuplicateMaintenanceStore store;
    private readonly Func<Exception, Task> exitForUnrestoredDestination;

    /// <summary>重複保守の操作を所有し、既存宛先の復元不能を実終端後のアプリ終了へ接続します。</summary>
    /// <param name="duplicateGroupNextSelectionProvider">現在結果の次グループを所属値で操作前に捕捉する境界です。末尾ではnullを返します。</param>
    internal DuplicateMaintenanceWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IChartMutationPlaybackPort playback,
        IUiDialogService dialogs,
        Func<bool> showConfirmationProvider,
        Func<string, bool> duplicateFolderDirectoryExists,
        Func<string, ExplorerOpenResult> duplicateFolderExplorerOpen,
        Func<DuplicateGroup, DuplicateViewContext> duplicateGroupNextSelectionProvider,
        IDuplicateMaintenanceStore store = null,
        Func<Exception, Task> exitForUnrestoredDestination = null)
    {
        this.exitForUnrestoredDestination = exitForUnrestoredDestination
            ?? (failure => Task.FromException(new InvalidOperationException("Unrestored destination exit is not connected.", failure)));
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.showConfirmationProvider = showConfirmationProvider ?? throw new ArgumentNullException(nameof(showConfirmationProvider));
        this.duplicateFolderDirectoryExists = duplicateFolderDirectoryExists
            ?? throw new ArgumentNullException(nameof(duplicateFolderDirectoryExists));
        this.duplicateFolderExplorerOpen = duplicateFolderExplorerOpen
            ?? throw new ArgumentNullException(nameof(duplicateFolderExplorerOpen));
        this.duplicateGroupNextSelectionProvider = duplicateGroupNextSelectionProvider
            ?? throw new ArgumentNullException(nameof(duplicateGroupNextSelectionProvider));
        this.store = store ?? new BmsLibraryDuplicateMaintenanceStore();
    }

    internal event EventHandler<DuplicateMaintenanceWorkflowChangedEventArgs> WorkflowChanged;

    private sealed class FolderMergeRequest
    {
        internal FolderMergeRequest(
            string sourceDirectory,
            string destinationDirectory,
            IEnumerable<string> folders,
            DuplicateViewContext selectionContext)
        {
            SourceDirectory = sourceDirectory;
            DestinationDirectory = destinationDirectory;
            Folders = (folders ?? []).Where(folder => !string.IsNullOrWhiteSpace(folder)).ToArray();
            SelectionContext = selectionContext;
        }

        internal string SourceDirectory { get; }

        internal string DestinationDirectory { get; }

        internal IReadOnlyList<string> Folders { get; }

        /// <summary>操作前に捕捉した短命な所属入力です。次の対象がなければnullです。</summary>
        internal DuplicateViewContext SelectionContext { get; }
    }

    private sealed class HashCleanupRequest
    {
        internal HashCleanupRequest(
            string folderPath,
            IEnumerable<ChartFile> charts,
            DuplicateViewContext selectionContext)
        {
            FolderPath = folderPath;
            Charts = (charts ?? []).Where(chart => chart != null).ToArray();
            SelectionContext = selectionContext;
        }

        internal string FolderPath { get; }

        internal IReadOnlyList<ChartFile> Charts { get; }

        /// <summary>操作前に捕捉した短命な所属入力です。次の対象がなければnullです。</summary>
        internal DuplicateViewContext SelectionContext { get; }
    }

    private sealed class HashCleanupPlan
    {
        internal HashCleanupPlan(
            string folderPath,
            IEnumerable<ChartFile> chartsToRemove,
            DuplicateViewContext selectionContext)
        {
            FolderPath = folderPath;
            ChartsToRemove = (chartsToRemove ?? []).Where(chart => chart != null).ToArray();
            SelectionContext = selectionContext;
        }

        internal string FolderPath { get; }

        internal IReadOnlyList<ChartFile> ChartsToRemove { get; }

        /// <summary>操作前に捕捉した短命な所属入力です。次の対象がなければnullです。</summary>
        internal DuplicateViewContext SelectionContext { get; }
    }

    private sealed class ConfirmationDecision
    {
        private ConfirmationDecision(bool accepted, Exception failure, bool cancelled = false)
        {
            Accepted = accepted;
            Failure = failure;
            Cancelled = cancelled;
        }

        internal bool Accepted { get; }

        internal bool Cancelled { get; }

        internal Exception Failure { get; }

        internal static ConfirmationDecision AcceptedResult { get; } = new(true, null);

        internal static ConfirmationDecision Rejected { get; } = new(false, null);

        internal static ConfirmationDecision CancelledResult { get; } = new(false, null, true);

        internal static ConfirmationDecision Failed(Exception failure)
        {
            return new ConfirmationDecision(false, failure ?? throw new ArgumentNullException(nameof(failure)));
        }
    }

    /// <summary>同じグループから、入力元と同一または親子関係にあるパスを除き、大小文字を無視して重複しない統合先候補を捕捉します。</summary>
    internal IReadOnlyList<string> CaptureDuplicateFolderMergeDestinations(
        DuplicateGroup duplicateGroup,
        string sourcePath)
    {
        if (duplicateGroup == null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return [];
        }

        return [.. (duplicateGroup.Folders ?? [])
            .Except([sourcePath], StringComparer.OrdinalIgnoreCase)
            .Where(destinationPath =>
                !LongPathFileSystem.IsSameOrDescendantDirectoryPath(destinationPath, sourcePath)
                && !LongPathFileSystem.IsSameOrDescendantDirectoryPath(sourcePath, destinationPath))];
    }

    /// <summary>単一フォルダの整理を維持し、複数フォルダでは有効候補があるときだけ元の件数に従って直接統合またはメニュー展開を選びます。</summary>
    internal DuplicateFolderKeyboardAction CaptureDuplicateFolderKeyboardAction(
        DuplicateGroup duplicateGroup,
        string sourcePath)
    {
        if (duplicateGroup == null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.None);
        }

        int folderCount = duplicateGroup.Folders?.Count ?? 0;
        if (folderCount == 1)
        {
            return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.Cleanup);
        }
        IReadOnlyList<string> destinations = CaptureDuplicateFolderMergeDestinations(duplicateGroup, sourcePath);
        if (destinations.Count == 0)
        {
            return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.None);
        }
        if (folderCount == 2)
        {
            string destinationPath = destinations[0];
            return string.IsNullOrWhiteSpace(destinationPath)
                ? new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.None)
                : new DuplicateFolderKeyboardAction(
                    DuplicateFolderKeyboardActionKind.Merge,
                    destinationPath);
        }
        if (folderCount >= 3)
        {
            return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.OpenContextMenu);
        }
        return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.None);
    }

    internal void OpenDuplicateFolderInExplorer(string path)
    {
        if (!duplicateFolderDirectoryExists(path))
        {
            return;
        }

        duplicateFolderExplorerOpen(path);
    }

    private FolderMergeRequest CreateFolderMergeRequest(
        string sourceDirectory,
        string destinationDirectory,
        DuplicateGroup duplicateGroup)
    {
        if (duplicateGroup == null)
        {
            throw new ArgumentNullException(nameof(duplicateGroup));
        }
        int folderCount = duplicateGroup.Folders?.Count ?? 0;
        DuplicateViewContext selectionContext = folderCount >= 3
            ? DuplicateViewContext.ForGroup(destinationDirectory)
            : duplicateGroupNextSelectionProvider(duplicateGroup);
        return new FolderMergeRequest(
            sourceDirectory,
            destinationDirectory,
            duplicateGroup.Folders,
            selectionContext);
    }

    private HashCleanupRequest CreateHashCleanupRequest(
        DuplicateGroup duplicateGroup,
        string folderPath)
    {
        if (duplicateGroup == null)
        {
            throw new ArgumentNullException(nameof(duplicateGroup));
        }
        return new HashCleanupRequest(
            folderPath,
            duplicateGroup.ChartFiles,
            duplicateGroupNextSelectionProvider(duplicateGroup));
    }

    internal Task<DuplicateMaintenanceMutationResult> RunFolderMergeAsync(
        string sourceDirectory,
        string destinationDirectory,
        DuplicateGroup duplicateGroup)
    {
        FolderMergeRequest request = CreateFolderMergeRequest(
            sourceDirectory,
            destinationDirectory,
            duplicateGroup);
        ValidateFolderMergeRequest(request);
        return RunFolderMergeCoreAsync(request);
    }

    internal Task<DuplicateMaintenanceMutationResult> RunHashCleanupAsync(
        DuplicateGroup duplicateGroup,
        string folderPath)
    {
        HashCleanupRequest request = CreateHashCleanupRequest(duplicateGroup, folderPath);
        ValidateHashCleanupRequest(request);
        List<ChartFile> chartsInFolder = [.. request.Charts
            .Where(chart => IsChartInFolder(chart, request.FolderPath))];
        List<ChartFile> deletionList;
        try
        {
            deletionList = BuildHashCleanupDeletionList(chartsInFolder);
        }
        catch (Exception exception)
        {
            return Task.FromResult(
                DuplicateMaintenanceMutationResult.Failed(request.SelectionContext, exception));
        }
        if (deletionList.Count == 0)
        {
            return Task.FromResult(
                DuplicateMaintenanceMutationResult.NoWork(request.SelectionContext));
        }
        return RunHashCleanupCoreAsync(
            new HashCleanupPlan(request.FolderPath, deletionList, request.SelectionContext));
    }

    private async Task<DuplicateMaintenanceMutationResult> RunFolderMergeCoreAsync(
        FolderMergeRequest request)
    {
        if (!chartFileOperations.TryEnter(out IDisposable operationGate))
        {
            return DuplicateMaintenanceMutationResult.Rejected(request.SelectionContext);
        }
        bool operationGateTransferred = false;
        string message = BeMusicSeeker.Properties.Resources.Msg_merge_bms_folder
            + Environment.NewLine
            + Environment.NewLine
            + BeMusicSeeker.Properties.Resources.Msg_merge_bms_target
            + ": "
            + request.SourceDirectory
            + Environment.NewLine
            + BeMusicSeeker.Properties.Resources.Msg_merge_bms_destination
            + ": "
            + request.DestinationDirectory;
        try
        {
            ConfirmationDecision confirmation = await ConfirmIfNeededAsync(
                message,
                "Duplicate folder merge confirmation");
            if (confirmation.Failure != null)
            {
                return DuplicateMaintenanceMutationResult.Failed(
                    request.SelectionContext,
                    confirmation.Failure);
            }
            if (!confirmation.Accepted)
            {
                return DuplicateMaintenanceMutationResult.Rejected(request.SelectionContext);
            }

            Task<DuplicateMaintenanceMutationResult> mutationTask = Task.Run(() => ExecuteMutation(
                request.SelectionContext,
                mutation: null,
                mutationWithReceipt: (library, operationStore) => operationStore.MergeFolderWithReceipt(
                    library,
                    request.SourceDirectory,
                    request.DestinationDirectory,
                    Stopwatch.GetTimestamp()),
                acquiredOperationGate: operationGate,
                physicalPaths: [request.SourceDirectory, request.DestinationDirectory]));
            operationGateTransferred = true;
            DuplicateMaintenanceMutationResult result = await mutationTask;
            if (result.MutationReceipt?.SessionReceipt.HasUnrestoredExistingDestination == true)
            {
                await exitForUnrestoredDestination(result.MutationReceipt.SessionReceipt.PrimaryFailure);
                return result;
            }
            await FileDbMutationReport.ShowAsync(dialogs, BeMusicSeeker.Properties.Resources.FileDbMutationReport_Merge,
                result.MutationReceipt?.SessionReceipt ?? LibraryMutationSessionReceipt.Empty,
                result.Failure,
                mergeOperation: true);
            return result;
        }
        finally
        {
            if (!operationGateTransferred)
            {
                operationGate.Dispose();
            }
        }
    }

    /// <summary>受付前に確定した削除対象を共通受付内の背景処理で準備し、全UI判断後に同じ要求を実行します。</summary>
    private async Task<DuplicateMaintenanceMutationResult> RunHashCleanupCoreAsync(
        HashCleanupPlan plan)
    {
        if (!chartFileOperations.TryEnter(out IDisposable operationGate))
        {
            return DuplicateMaintenanceMutationResult.Rejected(plan.SelectionContext);
        }
        bool operationGateTransferred = false;
        try
        {
            LibraryChartRemovalPreflight prepared;
            try
            {
                BMSLibrary library = libraryProvider();
                ChartFile[] copiedCharts = plan.ChartsToRemove.ToArray();
                prepared = await Task.Run(() => store.PrepareChartRemoval(library, copiedCharts));
            }
            catch (Exception exception)
            {
                return DuplicateMaintenanceMutationResult.Failed(plan.SelectionContext, exception, 0);
            }
            ConfirmationDecision confirmation = await ConfirmIfNeededAsync(
                string.Format(BeMusicSeeker.Properties.Resources.Msg_cleanup_duplicate_hash, plan.ChartsToRemove.Count),
                "Duplicate hash cleanup confirmation");
            if (confirmation.Failure != null)
            {
                return DuplicateMaintenanceMutationResult.Failed(
                    plan.SelectionContext,
                    confirmation.Failure,
                    0);
            }
            if (!confirmation.Accepted)
            {
                return DuplicateMaintenanceMutationResult.Rejected(plan.SelectionContext);
            }

            var approvedFolders = new List<string>();
            foreach (string folder in prepared.WholeFolderCandidatePaths)
            {
                ConfirmationDecision folderConfirmation = await ConfirmAsync(new UiConfirmationRequest(
                    prepared.GetWholeFolderConfirmationMessage(folder),
                    BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm,
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes),
                    "Duplicate whole-folder deletion confirmation", wholeFolder: true);
                if (folderConfirmation.Failure != null)
                {
                    return DuplicateMaintenanceMutationResult.Failed(plan.SelectionContext, folderConfirmation.Failure, 0);
                }
                if (folderConfirmation.Cancelled) { return DuplicateMaintenanceMutationResult.Rejected(plan.SelectionContext); }
                if (folderConfirmation.Accepted) { approvedFolders.Add(folder); }
            }
            IReadOnlyList<string> approvedPaths = prepared.GetExecutableWholeFolderPaths(approvedFolders);
            prepared = prepared.ExcludeProtectedWholeFolderApprovals(approvedFolders);

            LibraryChartRemovalOutcome removalOutcome = null;
            Task<DuplicateMaintenanceMutationResult> mutationTask = Task.Run(() => ExecuteMutation(
                plan.SelectionContext,
                (library, operationStore) => removalOutcome = operationStore.RemoveCharts(library, prepared, approvedPaths),
                removedChartCount: 0,
                acquiredOperationGate: operationGate,
                physicalPaths: prepared.Targets.Select(chart => chart.Path).Concat(approvedPaths),
                stopPlayback: prepared.Targets.Count > 0));
            operationGateTransferred = true;
            DuplicateMaintenanceMutationResult result = await mutationTask;
            result = DuplicateMaintenanceMutationResult.FromRemoval(plan.SelectionContext, removalOutcome, result.Failure);
            await LibraryChartRemovalReport.ShowAsync(dialogs, removalOutcome, result.Failure);
            return result;
        }
        finally
        {
            if (!operationGateTransferred)
            {
                operationGate.Dispose();
            }
        }
    }

    private async Task<ConfirmationDecision> ConfirmIfNeededAsync(string message, string routeName)
    {
        try
        {
            if (!showConfirmationProvider()) { return ConfirmationDecision.AcceptedResult; }
            return await ConfirmAsync(new UiConfirmationRequest(message, BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel), routeName);
        }
        catch (Exception exception) { return ConfirmationDecision.Failed(exception); }
    }

    /// <summary>件数確認の表示設定とは独立して判断を待ち、表示失敗を非承認へ読み替えません。</summary>
    private async Task<ConfirmationDecision> ConfirmAsync(UiConfirmationRequest request, string routeName, bool wholeFolder = false)
    {
        try
        {
            UiDialogResult result = await dialogs.ConfirmAsync(request);
            if (result == null)
            {
                return ConfirmationDecision.Failed(
                    new InvalidOperationException(routeName + " returned no result."));
            }
            return result.Status switch
            {
                UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser when wholeFolder => ConfirmationDecision.CancelledResult,
                UiDialogStatus.Accepted => ConfirmationDecision.AcceptedResult,
                UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => ConfirmationDecision.Rejected,
                UiDialogStatus.ClosedByUser => result.IsPositive
                    ? ConfirmationDecision.AcceptedResult
                    : ConfirmationDecision.Rejected,
                _ => ConfirmationDecision.Failed(
                    new InvalidOperationException(
                        routeName + " could not be displayed (" + result.Status + ").",
                        result.Exception))
            };
        }
        catch (Exception exception)
        {
            return ConfirmationDecision.Failed(exception);
        }
    }

    private async Task<DuplicateMaintenanceMutationResult> ExecuteMutation(
        DuplicateViewContext selectionContext,
        Action<BMSLibrary, IDuplicateMaintenanceStore> mutation,
        int removedChartCount = 0,
        Func<BMSLibrary, IDuplicateMaintenanceStore, DuplicateMergeMaintenanceReceipt> mutationWithReceipt = null,
        IDisposable acquiredOperationGate = null,
        IEnumerable<string> physicalPaths = null,
        bool stopPlayback = true)
    {
        var failures = new List<ExceptionDispatchInfo>();
        BMSLibrary library = null;
        DuplicateMergeMaintenanceReceipt mutationReceipt = null;
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable operationGate = null;
        IDisposable activityLease = null;
        LibraryFileMutationLease playlistLease = null;
        LibraryFileMutationCapability playlistCapability = null;
        LibraryFileMutationCapability combinedCapability = null;
        LibraryFileMutationCapability capability = null;
        bool suppressionStarted = false;
        try
        {
            operationGate = acquiredOperationGate;
            if (operationGate == null && !chartFileOperations.TryEnter(out operationGate))
            {
                return DuplicateMaintenanceMutationResult.Rejected(selectionContext);
            }
            library = libraryProvider();
            if (library == null)
            {
                return DuplicateMaintenanceMutationResult.Failed(
                    selectionContext,
                    new InvalidOperationException("Duplicate maintenance library is not available."));
            }
            capability = chartFileOperations.CreateMutationCapability(operationGate);
            IDuplicateMaintenanceStore operationStore = store.ForAcceptedOperation(capability);
            if (physicalPaths != null)
            {
                if (!operationStore.TryBeginPhysicalMutation(library, physicalPaths, true, out playlistLease))
                { return DuplicateMaintenanceMutationResult.Rejected(selectionContext); }
                playlistCapability = playlistLease?.CreateMutationCapability();
                combinedCapability = capability.WithPlaylistCapability(playlistCapability);
                operationStore = store.ForAcceptedOperation(combinedCapability);
            }
            activityLease = chartMutationActivity.Enter();
            if (stopPlayback) { await playback.StopPlaybackForMutationAsync().ConfigureAwait(false); }
            dialogScope = library.BeginOperationDialogScope();
            PublishRefreshSuppressionChanged(isSuppressed: true);
            suppressionStarted = true;
            if (mutationWithReceipt == null)
            {
                mutation(library, operationStore);
            }
            else
            {
                mutationReceipt = mutationWithReceipt(library, operationStore)
                    ?? throw new InvalidOperationException("Duplicate folder merge returned no receipt.");
            }
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            if (suppressionStarted)
            {
                CaptureNotification(() => PublishRefreshSuppressionChanged(isSuppressed: false));
            }
            if (activityLease != null)
            {
                CaptureNotification(activityLease.Dispose);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
            }
            if (suppressionStarted && library != null && capability != null)
            {
                // 実変更を開始した明示保守は、途中の画面移動によらず成功後選択の準備まで親Lで回収する。
                try { await library.CompleteAcceptedDuplicateDataAsync(capability, requireGroups: true).ConfigureAwait(false); }
                catch (Exception failure) { failures.Add(ExceptionDispatchInfo.Capture(failure)); }
            }
            CaptureCleanupFailure(() => capability?.Dispose(), failures);
            CaptureCleanupFailure(() => combinedCapability?.Dispose(), failures);
            CaptureCleanupFailure(() => playlistCapability?.Dispose(), failures);
            CaptureCleanupFailure(() => playlistLease?.Dispose(), failures);
            // 必須のscope回収まで受付を保持し、再入可能な結果提示は解放後に行う。
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
            }
            if (dialogScope != null)
            {
                CaptureNotification(dialogScope.Flush);
            }
        }
        if (failures.Count == 0)
        {
            return mutationReceipt == null
                ? DuplicateMaintenanceMutationResult.Completed(selectionContext, removedChartCount)
                : DuplicateMaintenanceMutationResult.FromMergeReceipt(selectionContext, mutationReceipt);
        }
        Exception additionalFailure = failures.Count == 1
            ? failures[0].SourceException
            : new AggregateException(failures.Select(failure => failure.SourceException));
        Exception primary = mutationReceipt?.SessionReceipt.PrimaryFailure;
        return DuplicateMaintenanceMutationResult.Failed(
            selectionContext,
            primary == null ? additionalFailure : new AggregateException(primary, additionalFailure),
            removedChartCount,
            mutationReceipt);

        void CaptureNotification(Action notification)
        {
            if (mutationWithReceipt != null)
            {
                FileDbMutationReport.NotifyBestEffort(notification);
            }
            else
            {
                CaptureCleanupFailure(notification, failures);
            }
        }
    }

    private void PublishRefreshSuppressionChanged(bool isSuppressed)
    {
        WorkflowChanged?.Invoke(
            this,
            new DuplicateMaintenanceRefreshSuppressionChangedEventArgs(isSuppressed));
    }

    private static List<ChartFile> BuildHashCleanupDeletionList(IEnumerable<ChartFile> charts)
    {
        var deletionList = new List<ChartFile>();
        foreach (var hashGroup in (charts ?? [])
            .Select(chart => new { Chart = chart, LookupHash = ChartLookupKey.GetPrimaryHash(chart) })
            .Where(item => !string.IsNullOrWhiteSpace(item.LookupHash))
            .GroupBy(item => item.LookupHash, StringComparer.OrdinalIgnoreCase))
        {
            List<ChartFile> grouped = [.. hashGroup.Select(item => item.Chart)];
            if (grouped.Count <= 1)
            {
                continue;
            }
            ChartFile keeper = grouped
                .OrderBy(chart =>
                {
                    try
                    {
                        return LongPathFileSystem.GetLastWriteTime(chart.Path, isDirectory: false);
                    }
                    catch
                    {
                        return DateTime.MaxValue;
                    }
                })
                .ThenBy(chart => Path.GetFileName(chart.Path).Length)
                .First();
            deletionList.AddRange(grouped.Where(chart => !ReferenceEquals(chart, keeper)));
        }
        return deletionList;
    }

    private static bool IsChartInFolder(ChartFile chart, string folderPath)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path) || string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }
        string normalizedFolderPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return chart.Path.StartsWith(normalizedFolderPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateFolderMergeRequest(FolderMergeRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.SourceDirectory)
            || string.IsNullOrWhiteSpace(request.DestinationDirectory)
            || string.Equals(request.SourceDirectory, request.DestinationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and destination directories must be distinct.", nameof(request));
        }
        if (request.Folders.Count < 2
            || !request.Folders.Any(folder => string.Equals(folder, request.SourceDirectory, StringComparison.OrdinalIgnoreCase))
            || !request.Folders.Any(folder => string.Equals(folder, request.DestinationDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The merge request must identify both folders in one duplicate group.", nameof(request));
        }
    }

    private static void ValidateHashCleanupRequest(HashCleanupRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.FolderPath))
        {
            throw new ArgumentException("A folder path is required.", nameof(request));
        }
    }

    private static void CaptureCleanupFailure(Action cleanup, List<ExceptionDispatchInfo> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
    }

}

internal sealed class BmsLibraryDuplicateMaintenanceStore : IDuplicateMaintenanceStore
{
    private readonly LibraryFileMutationCapability capability;
    /// <summary>受理済み操作の明示権限を物理変更へ渡す窓口を作ります。元leaseの所有と実終端は呼出元が担当します。</summary>
    internal BmsLibraryDuplicateMaintenanceStore(LibraryFileMutationCapability capability = null) { this.capability = capability; }
    /// <summary>同じ受付の生存権限を持つ変更不能な本番portを返します。呼出元が実処理・後片付けまで元leaseを保持します。</summary>
    public IDuplicateMaintenanceStore ForAcceptedOperation(LibraryFileMutationCapability capability) => new BmsLibraryDuplicateMaintenanceStore(capability);

    /// <summary>実モデルの管理範囲からPを取得し、外側の停止・実変更・公開終端まで保持します。</summary>
    public bool TryBeginPhysicalMutation(BMSLibrary library, IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease lease)
        => library.TryEnterManagedOutputMutation(paths, recursive, out lease, capability);

    /// <summary>統合の確定結果を返し、操作終端でまとめて報告できるようにします。</summary>
    public DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
        BMSLibrary library,
        string sourceDirectory,
        string destinationDirectory,
        long operationId)
    {
        return library.MergeChartDirectory(
            sourceDirectory,
            destinationDirectory,
            operationId,
            reportAtTerminal: true, capability: capability);
    }

    /// <summary>件数確認前に、モデルで現在値・安全属性・フォルダ候補を固定します。</summary>
    public LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts)
        => library.PrepareLibraryChartRemoval((charts ?? []).Select(chart => LibraryChartRef.FromChartFile(chart)));

    /// <summary>全UI確認後の承認候補と同じownerの生存権限をモデルへ渡し、固定対象を再確認せず削除します。</summary>
    public LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderPaths)
    {
        ArgumentNullException.ThrowIfNull(approvedWholeFolderPaths);
        return library.RemoveLibraryCharts(prepared, sendToRecycleBin: true, approvedWholeFolderDeletePaths: approvedWholeFolderPaths, capability: capability);
    }
}
