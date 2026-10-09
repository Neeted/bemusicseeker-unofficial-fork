using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal enum SelectedChartMutationRefreshScope
{
    Pending,
    Library
}

internal abstract class SelectedChartMutationWorkflowChangedEventArgs : EventArgs
{
}

internal sealed class SelectedChartMutationRefreshSuppressionChangedEventArgs : SelectedChartMutationWorkflowChangedEventArgs
{
    internal SelectedChartMutationRefreshSuppressionChangedEventArgs(
        bool isSuppressed,
        SelectedChartMutationRefreshScope? scope)
    {
        IsSuppressed = isSuppressed;
        Scope = scope;
    }

    internal bool IsSuppressed { get; }

    internal SelectedChartMutationRefreshScope? Scope { get; }
}

internal sealed class SelectedChartMutationAppliedEventArgs : SelectedChartMutationWorkflowChangedEventArgs
{
    internal SelectedChartMutationAppliedEventArgs(
        bool libraryPathChanged = false,
        bool encodingChanged = false)
    {
        LibraryPathChanged = libraryPathChanged;
        EncodingChanged = encodingChanged;
    }

    internal bool LibraryPathChanged { get; }

    internal bool EncodingChanged { get; }
}

internal interface IPendingDeleteConfirmationDialogPort
{
    Task<UiInteractionResult<bool>> ShowAsync();
}

internal interface ISelectedChartMutationStore
{
    /// <summary>受理済み権限を保持する変更不能な本番portを返します。独立代替portは自身を使えます。呼出元が実処理・後片付けまで元leaseを保持します。</summary>
    ISelectedChartMutationStore ForAcceptedOperation(LibraryFileMutationCapability capability) => this;

    /// <summary>固定した実変更範囲から、停止前に条件付きPを取得します。独立代替portでは管理出力を持ちません。</summary>
    bool TryBeginPhysicalMutation(BMSLibrary library, IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease lease)
    { lease = null; return true; }

    /// <summary>最初の確認前に、モデルの現在値・安全属性・確認候補を固定します。</summary>
    LibraryChartRemovalPreflight PrepareLibraryChartRemoval(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts);

    /// <summary>確認前の同じ固定対象を実行し、観測した削除事実を終端へ返します。</summary>
    LibraryChartRemovalOutcome RemoveLibraryCharts(
        BMSLibrary library,
        LibraryChartRemovalPreflight prepared,
        IReadOnlyList<string> approvedWholeFolderDeletePaths);

    void RemovePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms);

    /// <summary>最初の確認前に、通常拡張子変更の現在対象を固定します。</summary>
    LibraryFileExtensionRenameBatch PrepareLibraryFileExtensionRenameBatch(
        BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension);

    /// <summary>選択した所持譜面の拡張子変更を一つのセッションで実行し、確定結果を返します。</summary>
    LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(
        BMSLibrary library,
        IReadOnlyList<LibraryFileExtensionRenameBatch> batches);

    void RenamePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension);

    /// <summary>同じLの内側でモデルの物理移動計画を固定します。独立代替portは元の入力要求を使います。</summary>
    ChartLibraryMoveRequest PrepareLibraryMove(BMSLibrary library, ChartLibraryMoveRequest request) => request;

    /// <summary>選択した所持譜面の移動を実行し、確定結果を返します。</summary>
    LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(
        BMSLibrary library,
        ChartLibraryMoveRequest request);

    void SetBMSFilesEncoding(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string encoding);
}

internal sealed class SelectedChartDeleteRequest
{
    internal SelectedChartDeleteRequest(
        IEnumerable<ChartOperationTarget> selectedTargets,
        ChartOperationTarget contextTarget,
        MainViewOperationSection section)
    {
        SelectedTargets = (selectedTargets ?? []).Where(target => target != null).ToArray();
        ContextTarget = contextTarget;
        Section = section;
    }

    internal IReadOnlyList<ChartOperationTarget> SelectedTargets { get; }

    internal ChartOperationTarget ContextTarget { get; }

    internal MainViewOperationSection Section { get; }
}

internal sealed class SelectedInvalidExtensionRenameRequest
{
    internal SelectedInvalidExtensionRenameRequest(
        IEnumerable<ChartOperationTarget> targets,
        bool isPendingSelected)
    {
        Targets = (targets ?? []).Where(target => target != null).ToArray();
        IsPendingSelected = isPendingSelected;
    }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal bool IsPendingSelected { get; }
}

internal sealed class SelectedChartMoveRequest
{
    internal SelectedChartMoveRequest(
        IEnumerable<ChartOperationTarget> targets,
        string newParentDirectory)
    {
        Targets = (targets ?? []).Where(target => target != null).ToArray();
        NewParentDirectory = newParentDirectory;
    }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal string NewParentDirectory { get; }
}

internal sealed class SelectedChartMutationResult
{
    private SelectedChartMutationResult(
        bool succeeded,
        Exception failure,
        LibraryMutationSessionReceipt mutationReceipt = null,
        LibraryChartRemovalOutcome removalOutcome = null)
    {
        RemovalOutcome = removalOutcome;
        Succeeded = succeeded;
        Failure = failure;
        MutationReceipt = mutationReceipt;
    }

    /// <summary>Deletion facts retained independently of workflow cleanup failures.</summary>
    internal LibraryChartRemovalOutcome RemovalOutcome { get; }

    /// <summary>Catalog failure prevents success; filesystem-only partial failure preserves continuation.</summary>
    internal static SelectedChartMutationResult FromRemoval(LibraryChartRemovalOutcome outcome, Exception failure)
    {
        Exception primary = outcome?.CatalogFailure;
        Exception combined = primary == null ? failure : failure == null ? primary : new AggregateException(primary, failure);
        return new SelectedChartMutationResult(
            combined == null,
            combined,
            mutationReceipt: outcome?.SessionReceipt,
            removalOutcome: outcome);
    }

    internal bool Succeeded { get; }

    internal Exception Failure { get; }

    /// <summary>Gets operation-scoped terminal facts for the selected library mutation, when applicable.</summary>
    internal LibraryMutationSessionReceipt MutationReceipt { get; }

    /// <summary>Includes the catalog commit observed by library deletion.</summary>
    internal bool HasDurableCommit => RemovalOutcome?.CatalogDurable == true || MutationReceipt?.DurableCommit == true;

    /// <summary>Session-based selected library mutations do not expose executor compensation recovery.</summary>
    internal bool ManualRecoveryRequired => false;

    /// <summary>
    /// Gets whether selected-chart finalization failed after durable state.
    /// </summary>
    internal bool HasDurableFinalizationFailure => RemovalOutcome?.RequiredFinalizationFailed == true || MutationReceipt?.HasDurableFinalizationFailure == true;

    internal bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    /// <summary>Gets bounded manual-inspection candidates retained by the session.</summary>
    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt?.CandidatePaths ?? [];

    internal static SelectedChartMutationResult Completed { get; } = new(true, null);

    /// <summary>Preserves operation-scoped mutation facts without synthesizing per-item receipts.</summary>
    internal static SelectedChartMutationResult FromReceipt(LibraryMutationSessionReceipt mutationReceipt)
    {
        Exception requiredFailure = mutationReceipt?.PhysicalFailure
            ?? mutationReceipt?.ApplyFailure
            ?? mutationReceipt?.FinalizationFailure;
        bool durableStateRequired = mutationReceipt?.ConfirmedChangeCount > 0;
        bool succeeded = requiredFailure == null
            && (!durableStateRequired || mutationReceipt.DurableCommit);
        if (!succeeded && requiredFailure == null)
        {
            requiredFailure = new InvalidOperationException(
                "Confirmed library mutation did not produce a durable session receipt.");
        }
        return new SelectedChartMutationResult(succeeded, requiredFailure, mutationReceipt);
    }

    /// <summary>Retains receipts when an additional workflow failure occurs after mutation.</summary>
    internal static SelectedChartMutationResult Failed(Exception failure, LibraryMutationSessionReceipt mutationReceipt = null)
    {
        return new SelectedChartMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)), mutationReceipt);
    }
}

internal sealed class SelectedChartEncodingRequest
{
    internal SelectedChartEncodingRequest(
        IEnumerable<ChartOperationTarget> targets,
        string encoding)
    {
        Charts = (targets ?? [])
            .Where(target => target?.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix) == true
                && ChartFileKindResolver.IsBmsChartFile(target.Chart))
            .Select(target => target.Chart)
            .Where(ChartFileKindResolver.IsBmsChartFile)
            .ToArray();
        Encoding = encoding ?? string.Empty;
    }

    internal IReadOnlyList<ChartFile> Charts { get; }

    internal string Encoding { get; }

    internal bool HasTargets => Charts.Count > 0;
}

internal sealed class SelectedChartMutationWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly IChartMutationPlaybackPort playback;
    private readonly IUiDialogService dialogs;
    private readonly IPendingDeleteConfirmationDialogPort pendingDeleteDialog;
    private readonly ISelectedChartMutationStore store;

    internal SelectedChartMutationWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IChartMutationPlaybackPort playback,
        IUiDialogService dialogs,
        IPendingDeleteConfirmationDialogPort pendingDeleteDialog,
        ISelectedChartMutationStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.pendingDeleteDialog = pendingDeleteDialog ?? throw new ArgumentNullException(nameof(pendingDeleteDialog));
        this.store = store ?? new BmsLibrarySelectedChartMutationStore();
    }

    internal event EventHandler<SelectedChartMutationWorkflowChangedEventArgs> WorkflowChanged;

    /// <summary>UIで選択を固定し、共通受付を保持して背景で準備した同じ削除要求を確認後に実行します。</summary>
    internal async Task<SelectedChartMutationResult> DeleteAsync(SelectedChartDeleteRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!TryEnterOperation(
            request.Section == MainViewOperationSection.InstallPending,
            out IDisposable operationGate))
        {
            return SelectedChartMutationResult.Failed(
                new InvalidOperationException("A chart-file operation is already active."));
        }
        bool operationGateTransferred = false;
        try
        {
            ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
                request.SelectedTargets,
                request.ContextTarget,
                request.Section);
            if (resolution.Route == ChartDeleteRoute.None || resolution.Targets.Count == 0)
            {
                return SelectedChartMutationResult.Completed;
            }

            List<LibraryChartRef> libraryCharts = resolution.Route == ChartDeleteRoute.Library
                ? [.. resolution.Targets
                    .Where(target => target != null && target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary))
                    .Select(target => target.ToLibraryChartRef())
                    .Where(chart => chart != null)]
                : [];
            List<ChartFile> pendingCharts = resolution.Route == ChartDeleteRoute.Pending
                ? [.. resolution.Targets
                    .Where(target => target != null && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
                    .Select(target => target.Chart)
                    .Where(chart => chart != null)]
                : [];
            LibraryChartRemovalPreflight prepared = null;
            if (resolution.Route == ChartDeleteRoute.Library)
            {
                BMSLibrary library = RequireLibrary();
                LibraryChartRef[] copiedCharts = libraryCharts.ToArray();
                prepared = await Task.Run(() => store.PrepareLibraryChartRemoval(library, copiedCharts));
            }
            bool deleteContainingPackageFoldersWhenNoBms = false;
            if (resolution.Route == ChartDeleteRoute.Pending)
            {
                UiInteractionResult<bool> dialogResult = await pendingDeleteDialog.ShowAsync();
                if (dialogResult == null)
                {
                    return SelectedChartMutationResult.Failed(
                        new InvalidOperationException("Pending delete confirmation returned no result."));
                }
                if (!dialogResult.IsAccepted)
                {
                    return dialogResult.Status is UiInteractionStatus.CancelledByUser or UiInteractionStatus.ClosedByUser
                        ? SelectedChartMutationResult.Completed
                        : SelectedChartMutationResult.Failed(
                            dialogResult.Error ?? new InvalidOperationException(
                                "Pending delete confirmation could not be displayed (" + dialogResult.Status + ")."));
                }
                deleteContainingPackageFoldersWhenNoBms = dialogResult.Value;
            }
            else if (!await ConfirmMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_move_to_recycle,
                "Selected library chart deletion confirmation"))
            {
                return SelectedChartMutationResult.Completed;
            }

            List<string> approvedWholeFolderDeletePaths = [];
            if (libraryCharts.Count > 0)
            {
                foreach (string folderPath in prepared.WholeFolderCandidatePaths)
                {
                    bool approved = await ConfirmMessageAsync(
                        string.Format(BeMusicSeeker.Properties.Resources.Confirm_DeleteFolderWithNoBms, folderPath),
                        BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm,
                        MessageBoxButton.YesNo,
                        MessageBoxResult.Yes);
                    if (approved)
                    {
                        approvedWholeFolderDeletePaths.Add(folderPath);
                    }
                }
            }

            LibraryChartRemovalOutcome removalOutcome = null;
            IReadOnlyList<string> approvedFolderPaths = approvedWholeFolderDeletePaths.ToArray();
            SelectedChartMutationResult result = await Task.Run(() => ExecuteMutation(
                resolution.Route == ChartDeleteRoute.Pending
                ? SelectedChartMutationRefreshScope.Pending
                : SelectedChartMutationRefreshScope.Library,
                (library, operationStore) =>
                {
                    if (resolution.Route == ChartDeleteRoute.Pending)
                    {
                        operationStore.RemovePendingCharts(
                            library,
                            pendingCharts,
                            sendToRecycleBin: true,
                            deleteContainingPackageFoldersWhenNoBms);
                        return;
                    }

                    removalOutcome = operationStore.RemoveLibraryCharts(
                        library,
                        prepared,
                        approvedFolderPaths);
                },
                acquiredOperationGate: operationGate,
                physicalPaths: resolution.Route == ChartDeleteRoute.Library
                    ? prepared.Targets.Select(chart => chart.Path).Concat(approvedFolderPaths)
                    : pendingCharts.Select(chart => deleteContainingPackageFoldersWhenNoBms ? Path.GetDirectoryName(chart.Path) : chart.Path),
                recursivePhysicalPaths: true)).ConfigureAwait(false);
            operationGateTransferred = true;
            if (removalOutcome != null)
            {
                result = SelectedChartMutationResult.FromRemoval(removalOutcome, result.Failure);
                await LibraryChartRemovalReport.ShowAsync(dialogs, removalOutcome, result.Failure).ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
        finally
        {
            if (!operationGateTransferred)
            {
                operationGate.Dispose();
            }
        }
    }

    /// <summary>要求のnullを同期検査し、通常譜面の全形式を背景で準備してから非同期確認と実行へ進みます。</summary>
    internal Task<SelectedChartMutationResult> RenameInvalidExtensionsAsync(
        SelectedInvalidExtensionRenameRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        return RenameInvalidExtensionsCoreAsync(request);
    }

    /// <summary>UIで固定した入力を共通受付の内側で準備し、捕捉I/Oを背景処理で待ってから確認します。</summary>
    private async Task<SelectedChartMutationResult> RenameInvalidExtensionsCoreAsync(
        SelectedInvalidExtensionRenameRequest request)
    {
        try
        {
            List<ChartOperationTarget> targets = [.. request.Targets
                .Where(target => target?.HasCapability(ChartOperationCapabilities.RenameInvalidExtension) == true)
                .Where(target => ChartFileKindResolver.IsBmsChartFile(target.Chart))];
            if (targets.Count == 0)
            {
                return SelectedChartMutationResult.Completed;
            }
            if (targets.Any(target =>
                target.IsPending != request.IsPendingSelected
                || (target.SourceScope == ChartOperationSourceScope.PendingPackage)
                != request.IsPendingSelected))
            {
                return SelectedChartMutationResult.Failed(
                    new InvalidOperationException(
                        "Selected chart rename targets do not match the current operation section."));
            }

            List<ChartFile> charts = [.. targets.Select(target => target.Chart)];
            if (!TryEnterOperation(request.IsPendingSelected, out IDisposable operationGate))
            {
                return SelectedChartMutationResult.Failed(
                    new InvalidOperationException("A chart-file operation is already active."));
            }
            bool operationGateTransferred = false;
            try
            {
                IReadOnlyList<ChartFile> bCharts = [.. charts.Where(chart =>
                (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".b", StringComparison.OrdinalIgnoreCase))];
                IReadOnlyList<ChartFile> pCharts = [.. charts.Where(chart =>
                (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".p", StringComparison.OrdinalIgnoreCase))];
                IReadOnlyList<LibraryFileExtensionRenameBatch> libraryRenameBatches = [];
                if (!request.IsPendingSelected)
                {
                    BMSLibrary library = RequireLibrary();
                    libraryRenameBatches = await Task.Run(() =>
                    {
                        var batches = new List<LibraryFileExtensionRenameBatch>(2);
                        if (bCharts.Count > 0)
                        {
                            batches.Add(store.PrepareLibraryFileExtensionRenameBatch(library, bCharts, ".bmx"));
                        }
                        if (pCharts.Count > 0)
                        {
                            batches.Add(store.PrepareLibraryFileExtensionRenameBatch(library, pCharts, ".pmx"));
                        }
                        return batches.ToArray();
                    });
                }
                if (!await ConfirmMessageAsync(
                    BeMusicSeeker.Properties.Resources.Msg_rename_to_invalid,
                    "Invalid chart extension rename confirmation"))
                {
                    return SelectedChartMutationResult.Completed;
                }

                Task<SelectedChartMutationResult> task;
                if (!request.IsPendingSelected)
                {
                    task = Task.Run(() => ExecuteMutation(
                        SelectedChartMutationRefreshScope.Library,
                        mutation: null,
                        mutationWithReceipt: (library, operationStore) => operationStore.RenameLibraryChartsWithReceipt(
                            library,
                            libraryRenameBatches),
                        acquiredOperationGate: operationGate,
                        physicalPaths: libraryRenameBatches.SelectMany(batch => batch.Targets.SelectMany(chart => new[] { chart.Path, Path.ChangeExtension(chart.Path, batch.NewExtension) }))));
                }
                else
                {
                    task = Task.Run(() => ExecuteMutation(
                        SelectedChartMutationRefreshScope.Pending,
                        (library, operationStore) =>
                        {
                            if (bCharts.Count > 0)
                            {
                                operationStore.RenamePendingCharts(library, bCharts, ".bmx");
                            }
                            if (pCharts.Count > 0)
                            {
                                operationStore.RenamePendingCharts(library, pCharts, ".pmx");
                            }
                        },
                        acquiredOperationGate: operationGate,
                        physicalPaths: bCharts.SelectMany(chart => new[] { chart.Path, Path.ChangeExtension(chart.Path, ".bmx") })
                            .Concat(pCharts.SelectMany(chart => new[] { chart.Path, Path.ChangeExtension(chart.Path, ".pmx") }))));
                }
                operationGateTransferred = true;
                return !request.IsPendingSelected
                    ? await ReportInvalidExtensionRenameAsync(task)
                    : await task;
            }
            finally
            {
                if (!operationGateTransferred)
                {
                    operationGate.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
    }

    private async Task<SelectedChartMutationResult> ReportInvalidExtensionRenameAsync(
        Task<SelectedChartMutationResult> mutationTask)
    {
        SelectedChartMutationResult result = await mutationTask.ConfigureAwait(false);
        await FileDbMutationReport.ShowAsync(
            dialogs,
            BeMusicSeeker.Properties.Resources.Rename_invalid_ext,
            result.MutationReceipt,
            result.Failure).ConfigureAwait(false);
        return result;
    }

    internal async Task<SelectedChartMutationResult> MoveAsync(SelectedChartMoveRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        try
        {
            if (string.IsNullOrWhiteSpace(request.NewParentDirectory)
                || !ChartLibraryMoveRequest.TryCreate(
                    request.Targets,
                    request.NewParentDirectory,
                    out ChartLibraryMoveRequest moveRequest))
            {
                return SelectedChartMutationResult.Completed;
            }

            if (!TryEnterOperation(pending: false, out IDisposable operationGate))
            {
                return SelectedChartMutationResult.Failed(
                    new InvalidOperationException("A chart-file operation is already active."));
            }
            bool operationGateTransferred = false;
            try
            {
                moveRequest = await Task.Run(() => store.PrepareLibraryMove(RequireLibrary(), moveRequest));
                if (!await ConfirmMessageAsync(
                    BeMusicSeeker.Properties.Resources.Msg_move_to_other_root,
                    "Selected chart library move confirmation"))
                {
                    return SelectedChartMutationResult.Completed;
                }

                Task<SelectedChartMutationResult> task = Task.Run(() => ExecuteMutation(
                    SelectedChartMutationRefreshScope.Library,
                    mutation: null,
                    mutationWithReceipt: (library, operationStore) => operationStore.MoveLibraryChartsWithReceipt(library, moveRequest),
                    publishMutationApplied: true,
                    acquiredOperationGate: operationGate,
                    physicalPaths: moveRequest.PreparedPlans?.SelectMany(plan => new[] { plan.SourceDirectory, plan.DestinationDirectory }),
                    recursivePhysicalPaths: true));
                operationGateTransferred = true;
                SelectedChartMutationResult result = await task.ConfigureAwait(false);
                await FileDbMutationReport.ShowAsync(dialogs, BeMusicSeeker.Properties.Resources.FileDbMutationReport_Move,
                    result.MutationReceipt, result.Failure).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
    }

    internal SelectedChartMutationResult ApplyEncoding(SelectedChartEncodingRequest request)
    {
        if (request?.HasTargets != true)
        {
            return SelectedChartMutationResult.Completed;
        }

        if (!TryEnterOperation(pending: false, out IDisposable operationGate))
        {
            return SelectedChartMutationResult.Failed(
                new InvalidOperationException("A chart-file operation is already active."));
        }
        using IDisposable accepted = operationGate;
        try
        {
            using LibraryFileMutationCapability capability = chartFileOperations.CreateMutationCapability(operationGate);
            store.ForAcceptedOperation(capability).SetBMSFilesEncoding(RequireLibrary(), request.Charts, request.Encoding);
            PublishMutationApplied(encodingChanged: true);
            return SelectedChartMutationResult.Completed;
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
    }

    private async Task<SelectedChartMutationResult> ExecuteMutation(
        SelectedChartMutationRefreshScope refreshScope,
        Action<BMSLibrary, ISelectedChartMutationStore> mutation,
        Func<BMSLibrary, ISelectedChartMutationStore, LibraryMutationSessionReceipt> mutationWithReceipt = null,
        bool publishMutationApplied = false,
        IDisposable acquiredOperationGate = null,
        IEnumerable<string> physicalPaths = null,
        bool recursivePhysicalPaths = false)
    {
        IDisposable operationGate = acquiredOperationGate;
        if (operationGate == null
            && !TryEnterOperation(
                refreshScope == SelectedChartMutationRefreshScope.Pending,
                out operationGate))
        {
            return SelectedChartMutationResult.Failed(
                new InvalidOperationException("A chart-file operation is already active."));
        }
        BMSLibrary library;
        try
        {
            library = RequireLibrary();
        }
        catch (Exception ex)
        {
            operationGate.Dispose();
            return SelectedChartMutationResult.Failed(ex);
        }

        using LibraryFileMutationCapability capability = chartFileOperations.CreateMutationCapability(operationGate);
        ISelectedChartMutationStore operationStore = store.ForAcceptedOperation(capability);
        LibraryFileMutationLease playlistLease = null;
        LibraryFileMutationCapability playlistCapability = null;
        LibraryFileMutationCapability combinedCapability = null;
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        LibraryMutationSessionReceipt mutationReceipt = null;
        bool mutationApplied = false;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            if (physicalPaths != null)
            {
                if (!operationStore.TryBeginPhysicalMutation(library, physicalPaths, recursivePhysicalPaths, out playlistLease))
                { throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy); }
                playlistCapability = playlistLease?.CreateMutationCapability();
                combinedCapability = capability.WithPlaylistCapability(playlistCapability);
                operationStore = store.ForAcceptedOperation(combinedCapability);
            }
            activityLease = chartMutationActivity.Enter();
            await playback.StopPlaybackForMutationAsync().ConfigureAwait(false);
            dialogScope = library.BeginOperationDialogScope();
            suppressionStarted = true;
            PublishRefreshSuppressionChanged(isSuppressed: true, scope: refreshScope);
            if (mutationWithReceipt == null)
            {
                mutation(library, operationStore);
            }
            else
            {
                mutationReceipt = mutationWithReceipt(library, operationStore)
                    ?? throw new InvalidOperationException("Selected chart mutation returned no session receipt.");
            }
            if (publishMutationApplied
                && (mutationReceipt == null
                    || (mutationReceipt.DurableCommit
                        && !mutationReceipt.HasDurableFinalizationFailure)))
            {
                mutationApplied = true;
            }
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (failures.Count == 0 && mutationApplied)
            {
                CaptureNotification(() => PublishMutationApplied(libraryPathChanged: true));
            }
            if (suppressionStarted)
            {
                CaptureNotification(() => PublishRefreshSuppressionChanged(isSuppressed: false, scope: null));
            }
            if (activityLease != null)
            {
                CaptureNotification(activityLease.Dispose);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                CaptureNotification(dialogScope.Flush);
            }
            CaptureCleanupFailure(() => combinedCapability?.Dispose(), failures);
            CaptureCleanupFailure(() => playlistCapability?.Dispose(), failures);
            CaptureCleanupFailure(() => playlistLease?.Dispose(), failures);
            CaptureCleanupFailure(operationGate.Dispose, failures);
        }
        return failures.Count switch
        {
            0 => mutationReceipt == null
                ? SelectedChartMutationResult.Completed
                : SelectedChartMutationResult.FromReceipt(mutationReceipt),
            1 => SelectedChartMutationResult.Failed(CombineReceiptFailure(failures[0].SourceException), mutationReceipt),
            _ => SelectedChartMutationResult.Failed(
                CombineReceiptFailure(new AggregateException(failures.Select(failure => failure.SourceException))), mutationReceipt),
        };

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

        Exception CombineReceiptFailure(Exception failure)
        {
            Exception primary = mutationReceipt == null ? null : SelectedChartMutationResult.FromReceipt(mutationReceipt).Failure;
            return primary == null ? failure : new AggregateException(primary, failure);
        }
    }

    private void PublishRefreshSuppressionChanged(
        bool isSuppressed,
        SelectedChartMutationRefreshScope? scope)
    {
        WorkflowChanged?.Invoke(
            this,
            new SelectedChartMutationRefreshSuppressionChangedEventArgs(isSuppressed, scope));
    }

    private void PublishMutationApplied(
        bool libraryPathChanged = false,
        bool encodingChanged = false)
    {
        WorkflowChanged?.Invoke(
            this,
            new SelectedChartMutationAppliedEventArgs(libraryPathChanged, encodingChanged));
    }

    private async Task<bool> ConfirmMessageAsync(
        string message,
        string routeName,
        MessageBoxButton button = MessageBoxButton.OKCancel,
        MessageBoxResult defaultResult = MessageBoxResult.Cancel)
    {
        UiDialogResult result = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            message,
            BeMusicSeeker.Properties.Resources.Confirm,
            button,
            MessageBoxImage.Question,
            defaultResult));
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no result.");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            _ => throw result.Exception ?? new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").")
        };
    }

    private BMSLibrary RequireLibrary()
    {
        return libraryProvider()
            ?? throw new InvalidOperationException("Selected chart mutation library is not available.");
    }

    private bool TryEnterOperation(bool pending, out IDisposable operationGate) =>
        chartFileOperations.TryEnter(out operationGate);

    private static void CaptureCleanupFailure(Action action, ICollection<ExceptionDispatchInfo> failures)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
    }
}

internal sealed class BmsLibrarySelectedChartMutationStore : ISelectedChartMutationStore
{
    private readonly LibraryFileMutationCapability capability;

    /// <summary>受理済み操作の明示権限を物理変更へ渡す窓口を作ります。元leaseの所有と実終端は呼出元が担当します。</summary>
    internal BmsLibrarySelectedChartMutationStore(LibraryFileMutationCapability capability = null) { this.capability = capability; }

    /// <summary>取得済み共通権限を明示的に内部変更へ渡すportを構成します。呼出元が実処理・後片付けまで元leaseを保持します。</summary>
    public ISelectedChartMutationStore ForAcceptedOperation(LibraryFileMutationCapability capability) => new BmsLibrarySelectedChartMutationStore(capability);

    /// <summary>本番の保存済み管理範囲で条件付きPを判定し、外側が停止・変更・通知終端まで保持するleaseを返します。</summary>
    public bool TryBeginPhysicalMutation(BMSLibrary library, IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease lease)
        => library.TryEnterManagedOutputMutation(paths, recursive, out lease, capability);

    /// <summary>モデルで確認前の固定削除要求を捕捉します。</summary>
    public LibraryChartRemovalPreflight PrepareLibraryChartRemoval(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts)
    {
        return library.PrepareLibraryChartRemoval(charts);
    }

    /// <summary>確認前に固定した要求を再確定せずモデルへ渡し、終端へ削除事実を返します。</summary>
    public LibraryChartRemovalOutcome RemoveLibraryCharts(
        BMSLibrary library,
        LibraryChartRemovalPreflight prepared,
        IReadOnlyList<string> approvedWholeFolderDeletePaths)
    {
        return library.RemoveLibraryCharts(
            prepared,
            approvedWholeFolderDeletePaths: approvedWholeFolderDeletePaths, capability: capability);
    }

    public void RemovePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms)
    {
        library.RemovePendingCharts(charts, sendToRecycleBin, deleteContainingPackageFoldersWhenNoBms, capability);
    }

    public void RenamePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension)
    {
        library.RenamePendingBmsFormatChartFileExtensions(charts, newExtension, capability);
    }

    /// <summary>モデルで確認前の固定拡張子変更要求を捕捉します。</summary>
    public LibraryFileExtensionRenameBatch PrepareLibraryFileExtensionRenameBatch(
        BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension)
        => library.PrepareLibraryFileExtensionRenameBatch(charts, newExtension);

    /// <summary>拡張子の種類が異なる譜面も一つの変更セッションで扱います。</summary>
    public LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(
        BMSLibrary library,
        IReadOnlyList<LibraryFileExtensionRenameBatch> batches)
    {
        return library.RenameBMSFilesExtensionsWithReceipt(batches, unregister: true, capability: capability);
    }

    /// <summary>本番の移動計画を一回固定し、停止と物理実行が同じ対象を使う要求へ接続します。</summary>
    public ChartLibraryMoveRequest PrepareLibraryMove(BMSLibrary library, ChartLibraryMoveRequest request)
        => request.WithPreparedPlans(library.PrepareLibraryRootMovePlans(request.Charts, request.NewParentDirectory));

    /// <summary>停止前と同じ固定計画・生存L/Pで移動し、確定事実を終端へ返します。</summary>
    public LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(
        BMSLibrary library,
        ChartLibraryMoveRequest request)
    {
        return library.MoveLibraryRootFolderWithReceipt(
            request.Charts,
            request.NewParentDirectory,
            false,
            reportAtTerminal: true, capability: capability, preparedPlans: request.PreparedPlans);
    }

    public void SetBMSFilesEncoding(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string encoding)
    {
        library.SetBMSFilesEncoding(charts, encoding, capability);
    }
}
