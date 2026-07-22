using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal enum SelectedChartMutationRefreshScope
{
    Pending,
    Library
}

internal interface ISelectedChartMutationActivityPort
{
    void BeginActivity();

    void EndActivity();
}

internal interface ISelectedChartMutationRefreshPort
{
    void BeginRefreshSuppression(SelectedChartMutationRefreshScope scope);

    void EndRefreshSuppression();

    void ApplyLibraryPathMutationRefresh();

    void ApplyEncodingRefresh();
}

internal interface ISelectedChartMutationPlaybackPort
{
    void StopPlaybackForPendingCharts(IReadOnlyList<ChartFile> charts);

    void StopPlaybackForLibraryCharts(IReadOnlyList<LibraryChartRef> charts);

    void StopPlaybackForChartDirectories(IReadOnlyList<string> directories);
}

internal interface ISelectedChartMutationStore
{
    IReadOnlyList<string> GetLibraryWholeFolderDeleteConfirmationPaths(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts);

    void RemoveLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts,
        IReadOnlyList<string> approvedWholeFolderDeletePaths);

    void RemovePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms);

    void RenameLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension);

    void RenamePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension);

    void MoveLibraryCharts(BMSLibrary library, ChartLibraryMoveRequest request);

    void SetBMSFilesEncoding(
        BMSLibrary library,
        IReadOnlyList<BMSFile> bmsFiles,
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
    private SelectedChartMutationResult(bool succeeded, Exception failure)
    {
        Succeeded = succeeded;
        Failure = failure;
    }

    internal bool Succeeded { get; }

    internal Exception Failure { get; }

    internal static SelectedChartMutationResult Completed { get; } = new(true, null);

    internal static SelectedChartMutationResult Failed(Exception failure)
    {
        return new SelectedChartMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

internal sealed class SelectedChartEncodingRequest
{
    internal SelectedChartEncodingRequest(
        IEnumerable<ChartOperationTarget> targets,
        string encoding)
    {
        BmsFiles = (targets ?? [])
            .Where(target => target?.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix) == true
                && ChartFileKindResolver.IsBmsChartFile(target.Chart))
            .Select(target => target.Chart.GetBmsStorageOwner())
            .Where(ChartFileKindResolver.IsBmsChartFile)
            .ToArray();
        Encoding = encoding ?? string.Empty;
    }

    internal IReadOnlyList<BMSFile> BmsFiles { get; }

    internal string Encoding { get; }

    internal bool HasTargets => BmsFiles.Count > 0;
}

internal sealed class SelectedChartMutationWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ISelectedChartMutationActivityPort activity;
    private readonly ISelectedChartMutationRefreshPort refresh;
    private readonly ISelectedChartMutationPlaybackPort playback;
    private readonly IUiDialogService dialogs;
    private readonly ISelectedChartMutationStore store;

    internal SelectedChartMutationWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        ISelectedChartMutationActivityPort activity,
        ISelectedChartMutationRefreshPort refresh,
        ISelectedChartMutationPlaybackPort playback,
        IUiDialogService dialogs,
        ISelectedChartMutationStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.activity = activity ?? throw new ArgumentNullException(nameof(activity));
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.store = store ?? new BmsLibrarySelectedChartMutationStore();
    }

    internal async Task<SelectedChartMutationResult> DeleteAsync(SelectedChartDeleteRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
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

            bool deleteContainingPackageFoldersWhenNoBms = false;
            if (resolution.Route == ChartDeleteRoute.Pending)
            {
                UiWindowDialogResult<bool> dialogResult = await dialogs.ShowWindowAsync(
                    new UiWindowDialogRequest<PendingDeleteConfirmDialog, bool>(
                        () => new PendingDeleteConfirmDialog(),
                        dialog => dialog.DeleteFolderWhenNoBmsChecked));
                if (dialogResult == null)
                {
                    return SelectedChartMutationResult.Failed(
                        new InvalidOperationException("Pending delete confirmation returned no result."));
                }
                if (!dialogResult.IsAccepted)
                {
                    return dialogResult.Status is UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser
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
            List<string> approvedWholeFolderDeletePaths = [];
            if (libraryCharts.Count > 0)
            {
                BMSLibrary library = RequireLibrary();
                IReadOnlyList<string> candidatePaths;
                using (chartFileOperations.Enter())
                {
                    candidatePaths = store.GetLibraryWholeFolderDeleteConfirmationPaths(library, libraryCharts) ?? [];
                }
                foreach (string folderPath in candidatePaths)
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

            IReadOnlyList<string> approvedFolderPaths = approvedWholeFolderDeletePaths.ToArray();
            return await Task.Run(() => ExecuteMutation(
                resolution.Route == ChartDeleteRoute.Pending
                ? SelectedChartMutationRefreshScope.Pending
                : SelectedChartMutationRefreshScope.Library,
            () =>
            {
                if (resolution.Route == ChartDeleteRoute.Pending)
                {
                    IReadOnlyList<ChartFile> playbackCharts = deleteContainingPackageFoldersWhenNoBms
                        ? pendingCharts
                        : [.. pendingCharts.Where(ChartFileKindResolver.IsBmsChartFile)];
                    playback.StopPlaybackForPendingCharts(playbackCharts);
                    return;
                }
                playback.StopPlaybackForLibraryCharts(
                    [.. libraryCharts.Where(chart => chart?.Kind == LibraryChartKind.Bms)]);
                playback.StopPlaybackForChartDirectories(approvedFolderPaths);
            },
            library =>
            {
                if (resolution.Route == ChartDeleteRoute.Pending)
                {
                    store.RemovePendingCharts(
                        library,
                        pendingCharts,
                        sendToRecycleBin: true,
                        deleteContainingPackageFoldersWhenNoBms);
                    return;
                }

                store.RemoveLibraryCharts(
                    library,
                    libraryCharts,
                    approvedFolderPaths);
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
    }

    internal Task<SelectedChartMutationResult> RenameInvalidExtensionsAsync(
        SelectedInvalidExtensionRenameRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        try
        {
            List<ChartOperationTarget> targets = [.. request.Targets
                .Where(target => target?.HasCapability(ChartOperationCapabilities.RenameInvalidExtension) == true)
                .Where(target => ChartFileKindResolver.IsBmsChartFile(target.Chart))];
            if (targets.Count == 0)
            {
                return Task.FromResult(SelectedChartMutationResult.Completed);
            }
            if (targets.Any(target =>
                target.IsPending != request.IsPendingSelected
                || (target.SourceScope == ChartOperationSourceScope.PendingPackage)
                != request.IsPendingSelected))
            {
                return Task.FromResult(SelectedChartMutationResult.Failed(
                    new InvalidOperationException(
                        "Selected chart rename targets do not match the current operation section.")));
            }

            List<ChartFile> charts = [.. targets.Select(target => target.Chart)];
            if (!ConfirmMessage(
                BeMusicSeeker.Properties.Resources.Msg_rename_to_invalid,
                "Invalid chart extension rename confirmation"))
            {
                return Task.FromResult(SelectedChartMutationResult.Completed);
            }

            IReadOnlyList<ChartFile> bCharts = [.. charts.Where(chart =>
                (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".b", StringComparison.OrdinalIgnoreCase))];
            IReadOnlyList<ChartFile> pCharts = [.. charts.Where(chart =>
                (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".p", StringComparison.OrdinalIgnoreCase))];
            IReadOnlyList<ChartFile> allCharts = [.. bCharts.Concat(pCharts)];
            Action stopPlayback;
            if (request.IsPendingSelected)
            {
                stopPlayback = () => playback.StopPlaybackForPendingCharts(allCharts);
            }
            else
            {
                stopPlayback = () => playback.StopPlaybackForLibraryCharts(
                    [.. allCharts.Select(LibraryChartRef.FromChartFile).Where(chart => chart != null)]);
            }
            return Task.Run(() => ExecuteMutation(
                request.IsPendingSelected
                    ? SelectedChartMutationRefreshScope.Pending
                    : SelectedChartMutationRefreshScope.Library,
                stopPlayback,
                library =>
                {
                    if (request.IsPendingSelected)
                    {
                        if (bCharts.Count > 0)
                        {
                            store.RenamePendingCharts(library, bCharts, ".bmx");
                        }
                        if (pCharts.Count > 0)
                        {
                            store.RenamePendingCharts(library, pCharts, ".pmx");
                        }
                        return;
                    }
                    if (bCharts.Count > 0)
                    {
                        store.RenameLibraryCharts(library, bCharts, ".bmx");
                    }
                    if (pCharts.Count > 0)
                    {
                        store.RenameLibraryCharts(library, pCharts, ".pmx");
                    }
                }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(SelectedChartMutationResult.Failed(ex));
        }
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
                    out ChartLibraryMoveRequest moveRequest)
                || !await ConfirmMessageAsync(
                    BeMusicSeeker.Properties.Resources.Msg_move_to_other_root,
                    "Selected chart library move confirmation"))
            {
                return SelectedChartMutationResult.Completed;
            }

            return await Task.Run(() => ExecuteMutation(
                SelectedChartMutationRefreshScope.Library,
                () => playback.StopPlaybackForLibraryCharts(moveRequest.Charts),
                library =>
                {
                    store.MoveLibraryCharts(library, moveRequest);
                    refresh.ApplyLibraryPathMutationRefresh();
                })).ConfigureAwait(false);
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

        try
        {
            store.SetBMSFilesEncoding(RequireLibrary(), request.BmsFiles, request.Encoding);
            refresh.ApplyEncodingRefresh();
            return SelectedChartMutationResult.Completed;
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
    }

    private SelectedChartMutationResult ExecuteMutation(
        SelectedChartMutationRefreshScope refreshScope,
        Action stopPlayback,
        Action<BMSLibrary> mutation)
    {
        BMSLibrary library;
        try
        {
            library = RequireLibrary();
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }

        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable operationGate = null;
        bool activityStarted = false;
        bool suppressionStarted = false;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            dialogScope = library.BeginOperationDialogScope();
            activityStarted = true;
            activity.BeginActivity();
            operationGate = chartFileOperations.Enter();
            stopPlayback?.Invoke();
            suppressionStarted = true;
            refresh.BeginRefreshSuppression(refreshScope);
            mutation(library);
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (suppressionStarted)
            {
                CaptureCleanupFailure(refresh.EndRefreshSuppression, failures);
            }
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
            }
            if (activityStarted)
            {
                CaptureCleanupFailure(activity.EndActivity, failures);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                CaptureCleanupFailure(dialogScope.Flush, failures);
            }
        }
        return failures.Count switch
        {
            0 => SelectedChartMutationResult.Completed,
            1 => SelectedChartMutationResult.Failed(failures[0].SourceException),
            _ => SelectedChartMutationResult.Failed(
                new AggregateException(failures.Select(failure => failure.SourceException))),
        };
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
            UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes,
            _ => throw result.Exception ?? new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").")
        };
    }

    private bool ConfirmMessage(
        string message,
        string routeName,
        MessageBoxButton button = MessageBoxButton.OKCancel,
        MessageBoxResult defaultResult = MessageBoxResult.Cancel)
    {
        UiDialogResult result = dialogs.ConfirmAsync(new UiConfirmationRequest(
            message,
            BeMusicSeeker.Properties.Resources.Confirm,
            button,
            MessageBoxImage.Question,
            defaultResult))
            .GetAwaiter()
            .GetResult();
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no result.");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes,
            _ => throw result.Exception ?? new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").")
        };
    }

    private BMSLibrary RequireLibrary()
    {
        return libraryProvider()
            ?? throw new InvalidOperationException("Selected chart mutation library is not available.");
    }

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
    public IReadOnlyList<string> GetLibraryWholeFolderDeleteConfirmationPaths(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts)
    {
        return library.GetLibraryWholeFolderDeleteConfirmationPaths(charts);
    }

    public void RemoveLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts,
        IReadOnlyList<string> approvedWholeFolderDeletePaths)
    {
        library.RemoveLibraryCharts(
            charts,
            approvedWholeFolderDeletePaths: approvedWholeFolderDeletePaths);
    }

    public void RemovePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms)
    {
        library.RemovePendingCharts(charts, sendToRecycleBin, deleteContainingPackageFoldersWhenNoBms);
    }

    public void RenameLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension)
    {
        library.RenameBMSFilesExtensions(charts, newExtension, true);
    }

    public void RenamePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension)
    {
        library.RenamePendingBmsFormatChartFileExtensions(charts, newExtension);
    }

    public void MoveLibraryCharts(BMSLibrary library, ChartLibraryMoveRequest request)
    {
        library.MoveLibraryRootFolder(request.Charts, request.NewParentDirectory, false);
    }

    public void SetBMSFilesEncoding(
        BMSLibrary library,
        IReadOnlyList<BMSFile> bmsFiles,
        string encoding)
    {
        library.SetBMSFilesEncoding(bmsFiles, encoding);
    }
}
