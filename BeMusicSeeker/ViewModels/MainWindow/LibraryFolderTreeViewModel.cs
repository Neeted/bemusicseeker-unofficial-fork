using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the library-folder tree presentation and its cache refresh pipeline.
/// </summary>
public sealed class LibraryFolderTreeViewModel : ViewModel, ISettingsDialogSearchRootRuntimePort
{
    private readonly ObservableCollection<string> bmsParentFolderList;

    private readonly IUiScheduler uiScheduler;

    private readonly object refreshLock = new();

    private readonly Action<string> log;

    private readonly Action<string> logWarning;

    private readonly Func<string, bool> directoryExists;

    private readonly Func<string, ExplorerOpenResult> openDirectory;

    private Func<string, Func<Task>, bool> deferredRefreshScheduler;

    private BMSLibrary library;

    private bool parentFolderListViewInitialized;

    private bool deferredRefreshQueued;

    private long refreshRequestVersion;

    private long deferredRefreshOperationToken;

    private PerformanceInteraction deferredRefreshInteraction;

    internal LibraryFolderTreeViewModel(
        Func<string, bool> directoryExists,
        Func<string, ExplorerOpenResult> openDirectory,
        IUiScheduler uiScheduler,
        Action<string> log = null,
        Action<string> logWarning = null)
    {
        this.directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        this.openDirectory = openDirectory ?? throw new ArgumentNullException(nameof(openDirectory));
        this.uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
        if (!this.uiScheduler.IsAvailable && this.uiScheduler.CanExecuteInline)
        {
            throw new InvalidOperationException("A live UI dispatcher is required for the library tree.");
        }
        bmsParentFolderList = new ObservableCollection<string>();
        this.log = log ?? (_ => { });
        this.logWarning = logWarning ?? (_ => { });
    }

    /// <summary>
    /// Opens a library folder through the injected filesystem and Explorer gateways.
    /// Missing folders remain a silent no-op, matching the former context-menu behavior.
    /// </summary>
    internal void OpenFolderInExplorer(string path)
    {
        if (!directoryExists(path))
        {
            return;
        }

        openDirectory(path);
    }

    /// <summary>
    /// Raised when the attached library invalidates its parent-folder cache.
    /// The shell decides whether suppression or startup deferral applies.
    /// </summary>
    internal event EventHandler<LibraryFolderTreeRefreshRequestedEventArgs> CacheRefreshRequested;

    /// <summary>
    /// Raised after a deferred refresh has reached an observable UI completion point.
    /// </summary>
    internal event EventHandler<LibraryFolderTreeRefreshCompletedEventArgs> DeferredRefreshCompleted;

    /// <summary>
    /// Gets the sorted parent-folder presentation shared by the library tree and move menu.
    /// </summary>
    public ObservableCollection<string> BMSParentFolderList
    {
        get
        {
            if (library != null && !parentFolderListViewInitialized)
            {
                RefreshParentFolderListView(
                    raisePropertyChanged: false,
                    allowSynchronousCacheBuild: false);
            }
            return bmsParentFolderList;
        }
    }

    /// <summary>
    /// Gets whether the library initialization lock is held for the folder tree.
    /// </summary>
    public bool IsWriteLockHeldInitializeBMSFiles
        => library?.IsWriteLockHeldInitializeBMSFiles ?? true;

    /// <summary>
    /// Attaches the current library and begins observing its folder-cache and lock changes.
    /// </summary>
    internal void AttachLibrary(BMSLibrary value)
    {
        if (ReferenceEquals(library, value))
        {
            return;
        }
        DetachLibrary();
        library = value ?? throw new ArgumentNullException(nameof(value));
        library.PropertyChanged += LibraryPropertyChanged;
        parentFolderListViewInitialized = false;
        lock (refreshLock)
        {
            refreshRequestVersion++;
        }
        RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFiles));
        CacheRefreshRequested?.Invoke(
            this,
            new LibraryFolderTreeRefreshRequestedEventArgs(
                LibraryFolderTreeRefreshRequestOrigin.SourceInvalidation,
                operationToken: 0));
    }

    /// <summary>
    /// Detaches the current library without manufacturing a replacement presentation source.
    /// </summary>
    internal void DetachLibrary()
    {
        if (library == null)
        {
            return;
        }
        library.PropertyChanged -= LibraryPropertyChanged;
        library = null;
        parentFolderListViewInitialized = false;
        bmsParentFolderList.Clear();
        lock (refreshLock)
        {
            refreshRequestVersion++;
        }
        RaisePropertyChanged(nameof(BMSParentFolderList));
        RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFiles));
    }

    /// <summary>
    /// Invalidates the attached library's parent-folder cache after settings change.
    /// </summary>
    internal void InvalidateLibraryFolderCache()
    {
        if (library == null)
        {
            return;
        }
        parentFolderListViewInitialized = false;
        MarkRefreshRequested();
        library.NotifyBMSDirectoriesChanged();
    }

    internal bool IsLibraryAttached => library != null;

    internal bool HasOwnedChartUnderRealPath(string directoryPath)
        => library?.HasOwnedChartUnderRealPath(directoryPath) == true;

    internal void ApplySearchTargets(IReadOnlyList<string> searchTargets)
    {
        if (library == null)
        {
            return;
        }
        library.SearchTargets = [.. (searchTargets ?? [])];
    }

    bool ISettingsDialogSearchRootRuntimePort.IsLibraryAttached => IsLibraryAttached;

    bool ISettingsDialogSearchRootRuntimePort.HasOwnedChartUnderRealPath(string directoryPath)
        => HasOwnedChartUnderRealPath(directoryPath);

    void ISettingsDialogSearchRootRuntimePort.ApplySearchTargets(IReadOnlyList<string> searchTargets)
        => ApplySearchTargets(searchTargets);

    void ISettingsDialogSearchRootRuntimePort.InvalidateLibraryFolderCache()
        => InvalidateLibraryFolderCache();

    /// <summary>
    /// Schedules one background cache preparation and one UI-thread apply.
    /// </summary>
    internal void ScheduleDeferredRefresh(
        long operationToken,
        PerformanceInteraction interaction = default)
    {
        bool shouldSchedule = false;
        lock (refreshLock)
        {
            deferredRefreshOperationToken = operationToken;
            deferredRefreshInteraction = interaction;
            if (!deferredRefreshQueued)
            {
                deferredRefreshQueued = true;
                shouldSchedule = true;
            }
        }
        LogRefreshStage(
            interaction,
            "request_accepted",
            "operationToken=" + operationToken);
        if (!shouldSchedule)
        {
            return;
        }

        LogRefreshStage(
            interaction,
            "worker_queued",
            "operationToken=" + operationToken);
        QueueDeferredRefresh();
    }

    internal void ConfigureDeferredRefreshScheduler(Func<string, Func<Task>, bool> scheduler)
    {
        deferredRefreshScheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    private void QueueDeferredRefresh()
    {
        Func<Task> work = () =>
        {
            RunDeferredRefreshWorker();
            return Task.CompletedTask;
        };
        if (deferredRefreshScheduler != null)
        {
            if (!deferredRefreshScheduler("library_folder_tree_refresh", work))
            {
                ClearDeferredRefreshQueue();
            }
            return;
        }
        throw new InvalidOperationException("Library-folder refresh scheduler is not configured.");
    }

    private void RunDeferredRefreshWorker()
    {
            BMSLibrary refreshLibrary;
            long requestVersion;
            long operationToken;
            PerformanceInteraction interaction;
            lock (refreshLock)
            {
                if (!deferredRefreshQueued)
                {
                    return;
                }
                refreshLibrary = library;
                requestVersion = refreshRequestVersion;
                operationToken = deferredRefreshOperationToken;
                interaction = deferredRefreshInteraction;
            }

            LogRefreshStage(
                interaction,
                "worker_started",
                "operationToken=" + operationToken);

            BMSLibrary.ParentFolderListCacheSnapshot snapshot = null;
            Exception prepareException = null;
            try
            {
                if (refreshLibrary != null)
                {
                    snapshot = refreshLibrary.BuildBMSParentFolderListCacheSnapshot(
                        (stage, fields) => LogRefreshStage(interaction, stage, fields));
                }
            }
            catch (Exception ex)
            {
                prepareException = ex;
                logWarning("ui_stall_library_folder_tree_prepare_failed message=" + ex.Message);
            }

            if (!uiScheduler.IsAvailable)
            {
                lock (refreshLock)
                {
                    deferredRefreshQueued = false;
                    deferredRefreshInteraction = default;
                }
                return;
            }

            IUiScheduledOperation operation;
            try
            {
                LogRefreshStage(
                    interaction,
                    "ui_queued",
                    "operationToken=" + operationToken);
                operation = uiScheduler.Schedule((Action)delegate
            {
                LogRefreshStage(
                    interaction,
                    "ui_started",
                    "operationToken=" + operationToken);
                var stopwatch = Stopwatch.StartNew();
                bool shouldReschedule = false;
                string rescheduleReason = null;
                try
                {
                    bool refreshed = false;
                    bool requestIsCurrent;
                    lock (refreshLock)
                    {
                        requestIsCurrent = requestVersion == refreshRequestVersion
                            && ReferenceEquals(library, refreshLibrary);
                    }
                    if (prepareException == null && requestIsCurrent && snapshot != null && refreshLibrary != null)
                    {
                        refreshed = refreshLibrary.TryApplyBMSParentFolderListCacheSnapshot(snapshot);
                        if (!refreshed && refreshLibrary.IsBMSParentFolderListCacheDirty())
                        {
                            shouldReschedule = true;
                            rescheduleReason = "cache_apply_rejected";
                        }
                    }
                    if (prepareException == null && !requestIsCurrent)
                    {
                        shouldReschedule = true;
                        rescheduleReason = "source_changed";
                    }
                    if (prepareException == null && !shouldReschedule && requestIsCurrent)
                    {
                        if (!RefreshParentFolderListView(allowSynchronousCacheBuild: false))
                        {
                            shouldReschedule = true;
                            rescheduleReason = "cache_not_ready";
                        }
                        else
                        {
                            LogRefreshStage(
                                interaction,
                                "ui_applied",
                                "operationToken=" + operationToken);
                        }
                    }
                }
                finally
                {
                    stopwatch.Stop();
                    log("ui_suppress flush_library_folder_tree_deferred_ms=" + stopwatch.ElapsedMilliseconds);
                    bool requestAnotherRefresh;
                    long latestOperationToken;
                    PerformanceInteraction latestInteraction;
                    lock (refreshLock)
                    {
                        requestAnotherRefresh = shouldReschedule
                            || requestVersion != refreshRequestVersion
                            || !ReferenceEquals(library, refreshLibrary);
                        latestOperationToken = deferredRefreshOperationToken;
                        latestInteraction = deferredRefreshInteraction;
                        deferredRefreshQueued = false;
                        deferredRefreshInteraction = default;
                    }
                    bool dispatcherShuttingDown = !uiScheduler.IsAvailable;
                    if (requestAnotherRefresh)
                    {
                        LogRefreshStage(
                            interaction,
                            "stale_reschedule",
                            "reason=" + (rescheduleReason ?? "latest_request")
                                + " operationToken=" + latestOperationToken);
                        if (dispatcherShuttingDown)
                        {
                            ClearDeferredRefreshQueue();
                        }
                        else
                        {
                            CacheRefreshRequested?.Invoke(
                                this,
                                new LibraryFolderTreeRefreshRequestedEventArgs(
                                    LibraryFolderTreeRefreshRequestOrigin.DeferredContinuation,
                                    latestOperationToken,
                                    latestInteraction));
                        }
                    }
                    else if (prepareException == null)
                    {
                        DeferredRefreshCompleted?.Invoke(
                            this,
                            new LibraryFolderTreeRefreshCompletedEventArgs(latestOperationToken));
                    }
                }
            }, UiSchedulePriority.Background);
                operation.Completion.ContinueWith(_ =>
                {
                    if (operation.IsAborted)
                    {
                        ClearDeferredRefreshQueue();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
                if (!operation.IsAccepted || operation.IsAborted)
                {
                    ClearDeferredRefreshQueue();
                }
            }
            catch (Exception) when (!uiScheduler.IsAvailable)
            {
                ClearDeferredRefreshQueue();
            }
    }

    private void ClearDeferredRefreshQueue()
    {
        lock (refreshLock)
        {
            deferredRefreshQueued = false;
            deferredRefreshInteraction = default;
        }
    }

    private void LogRefreshStage(
        PerformanceInteraction interaction,
        string stage,
        string fields = null)
    {
        if (interaction.InteractionId <= 0L)
        {
            return;
        }
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(interaction, stage, fields);
        }
        else
        {
            string message = "library_folder_tree stage=" + stage
                + " interactionId=" + interaction.InteractionId
                + " generation=" + interaction.Generation;
            if (!string.IsNullOrWhiteSpace(fields))
            {
                message += " " + fields;
            }
            log(message);
        }
    }

    private void LibraryPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BMSLibrary.BMSParentFolderListCacheVersion))
        {
            parentFolderListViewInitialized = false;
            MarkRefreshRequested();
            CacheRefreshRequested?.Invoke(
                this,
                new LibraryFolderTreeRefreshRequestedEventArgs(
                    LibraryFolderTreeRefreshRequestOrigin.SourceInvalidation,
                    operationToken: 0));
        }
        else if (e.PropertyName == nameof(BMSLibrary.IsWriteLockHeldInitializeBMSFiles))
        {
            RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFiles));
        }
    }

    private void MarkRefreshRequested()
    {
        lock (refreshLock)
        {
            refreshRequestVersion++;
        }
    }

    private bool RefreshParentFolderListView(
        bool raisePropertyChanged = true,
        bool allowSynchronousCacheBuild = true)
    {
        IEnumerable<string> source;
        if (library == null)
        {
            source = Enumerable.Empty<string>();
        }
        else if (allowSynchronousCacheBuild)
        {
            source = library.GetBMSParentFolderListSnapshot();
        }
        else if (library.TryGetBMSParentFolderListCacheSnapshot(out IReadOnlyList<string> cachedSnapshot))
        {
            source = cachedSnapshot;
        }
        else
        {
            return false;
        }
        List<string> sortedParentFolders = [.. source
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        bool changed = !bmsParentFolderList.SequenceEqual(sortedParentFolders, StringComparer.OrdinalIgnoreCase);
        if (changed)
        {
            bmsParentFolderList.Clear();
            bmsParentFolderList.AddRange(sortedParentFolders);
        }
        parentFolderListViewInitialized = true;
        if (raisePropertyChanged)
        {
            RaisePropertyChanged(nameof(BMSParentFolderList));
        }
        return true;
    }
}

internal enum LibraryFolderTreeRefreshRequestOrigin
{
    SourceInvalidation,
    DeferredContinuation
}

/// <summary>
/// Requests shell admission for a folder-tree refresh.
/// </summary>
internal sealed class LibraryFolderTreeRefreshRequestedEventArgs : EventArgs
{
    internal LibraryFolderTreeRefreshRequestedEventArgs(
        LibraryFolderTreeRefreshRequestOrigin origin,
        long operationToken,
        PerformanceInteraction interaction = default)
    {
        Origin = origin;
        OperationToken = operationToken;
        Interaction = interaction;
    }

    internal LibraryFolderTreeRefreshRequestOrigin Origin { get; }

    internal long OperationToken { get; }

    internal PerformanceInteraction Interaction { get; }
}

/// <summary>
/// Carries the startup operation token associated with a completed folder-tree refresh.
/// </summary>
internal sealed class LibraryFolderTreeRefreshCompletedEventArgs : EventArgs
{
    internal LibraryFolderTreeRefreshCompletedEventArgs(long operationToken)
    {
        OperationToken = operationToken;
    }

    internal long OperationToken { get; }
}
