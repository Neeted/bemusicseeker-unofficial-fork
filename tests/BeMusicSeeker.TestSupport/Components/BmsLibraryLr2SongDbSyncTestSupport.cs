using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class BmsLibraryLr2SongDbSyncTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    // 標準構成の既存portを読取りだけで借り、故障注入用の製品APIやreadonly fieldの書換えを増やしません。
    internal static T ReadSynchronizationDependency<T>(BMSLibrary.Lr2SynchronizationOwner owner, string name) where T : class
    {
        FieldInfo field = typeof(BMSLibrary.Lr2SynchronizationOwner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing synchronization dependency: " + name);
        return field.GetValue(owner) as T ?? throw new InvalidOperationException("Unexpected synchronization dependency: " + name);
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static BMSLibrary.Lr2SynchronizationOwner GetLr2SynchronizationOwner(BMSLibrary library)
    {
        return (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static Lr2SongDbSyncPreparedDataSurface CreatePreparedLr2FolderSurface(
        string scopeDirectory,
        string filePath,
        IReadOnlyDictionary<string, RootFileEnumerationEntry>? directoryEntries = null,
        IEnumerable<string>? folderInfoFilePaths = null,
        IReadOnlyDictionary<string, RootFileEnumerationEntry>? folderInfoFileEntries = null,
        IEnumerable<string>? textFileDirectories = null,
        Lr2FolderFileDefinition? managedDefinition = null)
    {
        return new Lr2SongDbSyncPreparedDataSurface(
            [scopeDirectory],
            [filePath],
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [filePath] = new RootFileEnumerationEntry(filePath, File.GetLastWriteTimeUtc(filePath))
            },
            directoryEntries,
            folderInfoFilePaths,
            folderInfoFileEntries,
            textFileDirectories,
            discoveryComplete: true,
            managedFolderDefinitions: managedDefinition == null ? null : new Dictionary<string, Lr2FolderFileDefinition>(StringComparer.OrdinalIgnoreCase)
            { [filePath] = managedDefinition });
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static SongTableFileCheckResult CreateCompleteLr2ScanSurface(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> lr2FolderDiscoveryDirectories,
        IEnumerable<string> lr2FolderFilePaths,
        IEnumerable<string> directoryEntryPaths,
        IEnumerable<string>? folderInfoFilePaths = null,
        IEnumerable<string>? textFileDirectories = null)
    {
        string[] roots = [.. (rootDirectories ?? [])];
        string[] discoveryDirectories = [.. (lr2FolderDiscoveryDirectories ?? [])];
        string[] folderPaths = [.. (lr2FolderFilePaths ?? [])];
        string[] folderInfoPaths = [.. (folderInfoFilePaths ?? [])];
        string[] textDirectories = [.. (textFileDirectories ?? [])];
        return new SongTableFileCheckResult
        {
            Lr2ScanSurfaceAvailable = true,
            Lr2ScanNormalFolderDirectoryPaths = roots,
            Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(roots),
            Lr2ScanDirectoryEntries = CreateDirectoryEntryMap([.. (directoryEntryPaths ?? [])]),
            Lr2ScanFolderInfoFilePaths = folderInfoPaths,
            Lr2ScanFolderInfoFileEntries = CreateFileEntryMap(folderInfoPaths),
            Lr2ScanTextFileDirectories = textDirectories,
            Lr2ScanLr2FolderDiscoveryDirectories = discoveryDirectories,
            Lr2ScanLr2FolderFilePaths = folderPaths,
            Lr2ScanLr2FolderFileEntries = CreateFileEntryMap(folderPaths),
            Lr2ScanLr2FolderFileDiscoveryComplete = true
        };
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static Lr2SongDbSyncScanSurfaceSnapshot? InvokeCaptureLr2SongDbSyncScanSurface(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        SongTableFileCheckResult result)
    {
        return library.Lr2Synchronization.CreateLr2SongDbSyncScanSurface(options, rootDirectories, result);
    }

    /// <summary>ケース所有の設定だけを、共通の初期条件へ戻します。</summary>
    internal static void ResetTouchedSettings(BeMusicSeeker.Properties.Settings testSettings)
    {
        testSettings.OperationModeLR2DB = true;
        ResetLr2FolderDiscoverySettings(testSettings);
    }

    /// <summary>ケース所有のLR2 folder探索設定だけを共通の初期条件へ戻します。</summary>
    internal static void ResetLr2FolderDiscoverySettings(BeMusicSeeker.Properties.Settings testSettings)
    {
        testSettings.LR2CustomFolderOutputBaseDir = string.Empty;
        testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
        testSettings.LR2CustomFolderOutputBaseDirRootType = string.Empty;
        testSettings.LR2RootPath = string.Empty;
        testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = false;
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class CapturedRootFileEnumerator : IRootFileEnumerator
    {
        private readonly IReadOnlyList<RootFileEnumerationEntry> entries;

        internal CapturedRootFileEnumerator(
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
        {
            entries = [.. (entriesByPath?.Values ?? [])
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))];
        }

        /// <summary>実列挙の到達点だけを制御し、捕捉した列挙結果は変更しません。</summary>
        internal Action? BeforeEnumeration { get; init; }

        public RootFileEnumerationResult EnumerateFiles(
            IEnumerable<string> rootDirectories,
            IEnumerable<RootFileEnumerationGroup> groups,
            bool verboseLog = false)
        {
            BeforeEnumeration?.Invoke();
            var result = new RootFileEnumerationResult
            {
                Success = true,
                IsComplete = true,
                BackendName = "captured-test",
                TotalFileCount = entries.Count
            };
            foreach (RootFileEnumerationGroup group in groups ?? [])
            {
                result.InitializeGroup(group.Name);
                foreach (RootFileEnumerationEntry entry in entries)
                {
                    if (group.IncludeAllFiles
                        || group.Extensions.Any(extension =>
                            string.Equals(
                                Path.GetExtension(entry.Path),
                                extension,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        result.AddEntry(group.Name, entry);
                    }
                }

                result.QueryHitCountByGroup[group.Name] = (ulong)result.GetPaths(group.Name).Count;
            }

            return result;
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class QueuedUiScheduler : IUiScheduler
    {
        private readonly Queue<QueuedUiOperation> operations = [];
        private readonly object syncRoot = new();
        private TaskCompletionSource<QueuedUiOperation>? nextScheduled;

        internal int PendingCount
        {
            get
            {
                lock (syncRoot)
                {
                    return operations.Count;
                }
            }
        }

        internal int ScheduleCount { get; private set; }

        internal UiSchedulePriority NextPriority
        {
            get
            {
                lock (syncRoot)
                {
                    if (operations.Count == 0)
                    {
                        throw new InvalidOperationException("No queued UI operation is available.");
                    }
                    return operations.Peek().Priority;
                }
            }
        }

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            var operation = new QueuedUiOperation(action, priority);
            TaskCompletionSource<QueuedUiOperation>? waiter;
            lock (syncRoot)
            {
                ScheduleCount++;
                operations.Enqueue(operation);
                waiter = nextScheduled;
                nextScheduled = null;
            }
            waiter?.TrySetResult(operation);
            return operation;
        }

        internal Task<QueuedUiOperation> WaitForNextAsync()
        {
            lock (syncRoot)
            {
                if (operations.Count > 0)
                {
                    return Task.FromResult(operations.Peek());
                }
                nextScheduled = new TaskCompletionSource<QueuedUiOperation>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return nextScheduled.Task;
            }
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        internal void Drain()
        {
            while (PendingCount > 0)
            {
                ExecuteNext();
            }
        }

        internal void ExecuteNext()
        {
            QueuedUiOperation operation;
            lock (syncRoot)
            {
                if (operations.Count == 0)
                {
                    throw new InvalidOperationException("No queued UI operation is available.");
                }
                operation = operations.Dequeue();
            }
            operation.Execute();
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class QueuedUiOperation : IUiScheduledOperation
    {
        private readonly Action action;
        private readonly TaskCompletionSource<object?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int aborted;

        internal QueuedUiOperation(Action action, UiSchedulePriority priority)
        {
            this.action = action ?? throw new ArgumentNullException(nameof(action));
            Priority = priority;
        }

        internal UiSchedulePriority Priority { get; }

        public bool IsAccepted => true;

        public bool IsCompleted => Completion.IsCompleted;

        public bool IsAborted => Volatile.Read(ref aborted) != 0;

        /// <inheritdoc />
        public string? RejectionReason => null;

        public Task Completion => completion.Task;

        public void Abort()
        {
            Interlocked.Exchange(ref aborted, 1);
            completion.TrySetCanceled();
        }

        internal void Execute()
        {
            if (IsAborted)
            {
                return;
            }
            try
            {
                action();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                throw;
            }
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class InlineUiScheduler : IUiScheduler
    {
        public bool IsAvailable => true;

        public bool CanExecuteInline => true;

        public bool CheckAccess() => true;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            action();
            return CompletedUiScheduledOperation.Instance;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => action();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => action();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => action();
    }

}
