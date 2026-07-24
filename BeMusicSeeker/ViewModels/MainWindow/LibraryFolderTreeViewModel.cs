using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the library-folder tree presentation and its cache refresh pipeline.
/// </summary>
public sealed class LibraryFolderTreeViewModel : ViewModel
{
    private readonly DispatcherCollection<string> bmsParentFolderList;

    private readonly Dispatcher uiDispatcher;

    private readonly object refreshLock = new();

    private readonly Action<string> log;

    private readonly Action<string> logWarning;

    private readonly Func<string, bool> directoryExists;

    private readonly Func<string, ExplorerOpenResult> openDirectory;

    private BMSLibrary library;

    private bool parentFolderListViewInitialized;

    private bool deferredRefreshQueued;

    private long refreshRequestVersion;

    internal LibraryFolderTreeViewModel(
        Func<string, bool> directoryExists,
        Func<string, ExplorerOpenResult> openDirectory,
        Func<Dispatcher> uiDispatcherProvider,
        Action<string> log = null,
        Action<string> logWarning = null)
    {
        this.directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        this.openDirectory = openDirectory ?? throw new ArgumentNullException(nameof(openDirectory));
        Func<Dispatcher> dispatcherProvider = uiDispatcherProvider ?? throw new ArgumentNullException(nameof(uiDispatcherProvider));
        uiDispatcher = dispatcherProvider()
            ?? throw new InvalidOperationException("A live UI dispatcher is required for the library folder tree.");
        bmsParentFolderList = new DispatcherCollection<string>(uiDispatcher);
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
    internal event EventHandler CacheRefreshRequested;

    /// <summary>
    /// Raised after a deferred refresh has reached an observable UI completion point.
    /// </summary>
    internal event EventHandler<LibraryFolderTreeRefreshCompletedEventArgs> DeferredRefreshCompleted;

    /// <summary>
    /// Gets the sorted parent-folder presentation shared by the library tree and move menu.
    /// </summary>
    public DispatcherCollection<string> BMSParentFolderList
    {
        get
        {
            if (library != null && !parentFolderListViewInitialized)
            {
                RefreshParentFolderListView(raisePropertyChanged: false);
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
        CacheRefreshRequested?.Invoke(this, EventArgs.Empty);
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
        parentFolderListViewInitialized = false;
        MarkRefreshRequested();
        library?.NotifyBMSDirectoriesChanged();
    }

    /// <summary>
    /// Schedules one background cache preparation and one UI-thread apply.
    /// </summary>
    internal void ScheduleDeferredRefresh(long operationToken)
    {
        bool shouldSchedule = false;
        long requestVersion;
        lock (refreshLock)
        {
            requestVersion = ++refreshRequestVersion;
            if (!deferredRefreshQueued)
            {
                deferredRefreshQueued = true;
                shouldSchedule = true;
            }
        }
        if (!shouldSchedule)
        {
            return;
        }

        BMSLibrary refreshLibrary = library;
        Task.Run(delegate
        {
            BMSLibrary.ParentFolderListCacheSnapshot snapshot = null;
            Exception prepareException = null;
            try
            {
                if (refreshLibrary != null)
                {
                    snapshot = refreshLibrary.BuildBMSParentFolderListCacheSnapshot();
                }
            }
            catch (Exception ex)
            {
                prepareException = ex;
                logWarning("ui_stall_library_folder_tree_prepare_failed message=" + ex.Message);
            }

            Dispatcher dispatcher = uiDispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                lock (refreshLock)
                {
                    deferredRefreshQueued = false;
                }
                return;
            }

            DispatcherOperation operation;
            try
            {
                operation = dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                var stopwatch = Stopwatch.StartNew();
                bool shouldReschedule = false;
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
                        }
                    }
                    if (prepareException == null && !requestIsCurrent)
                    {
                        shouldReschedule = true;
                    }
                    if (prepareException == null && !shouldReschedule && requestIsCurrent)
                    {
                        if (!RefreshParentFolderListView(allowSynchronousCacheBuild: false))
                        {
                            shouldReschedule = true;
                        }
                    }
                }
                finally
                {
                    stopwatch.Stop();
                    log("ui_suppress flush_library_folder_tree_deferred_ms=" + stopwatch.ElapsedMilliseconds);
                    bool requestAnotherRefresh;
                    lock (refreshLock)
                    {
                        requestAnotherRefresh = shouldReschedule
                            || requestVersion != refreshRequestVersion
                            || !ReferenceEquals(library, refreshLibrary);
                        deferredRefreshQueued = false;
                    }
                    bool dispatcherShuttingDown = dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished;
                    if (requestAnotherRefresh && !dispatcherShuttingDown)
                    {
                        CacheRefreshRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (prepareException == null)
                    {
                        DeferredRefreshCompleted?.Invoke(
                            this,
                            new LibraryFolderTreeRefreshCompletedEventArgs(operationToken));
                    }
                }
            });
                operation.Aborted += (_, _) => ClearDeferredRefreshQueue();
                if (operation.Status == DispatcherOperationStatus.Aborted)
                {
                    ClearDeferredRefreshQueue();
                }
            }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                ClearDeferredRefreshQueue();
            }
        });
    }

    private void ClearDeferredRefreshQueue()
    {
        lock (refreshLock)
        {
            deferredRefreshQueued = false;
        }
    }

    private void LibraryPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BMSLibrary.BMSParentFolderListCacheVersion))
        {
            parentFolderListViewInitialized = false;
            MarkRefreshRequested();
            CacheRefreshRequested?.Invoke(this, EventArgs.Empty);
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
