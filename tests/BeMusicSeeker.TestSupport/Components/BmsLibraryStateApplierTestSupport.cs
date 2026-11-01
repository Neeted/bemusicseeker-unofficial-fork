using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using PackageStateMutationApplier = BeMusicSeeker.Models.BmsLibraryInternal.PackageLifecycleOwner.PackageStateMutationApplier;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>状態適用テストの実DB準備と既存scheduler代替を共用します。</summary>
internal static class BmsLibraryStateApplierTestSupport
{
    /// <summary>既存WPF caller向けに実DB・共有STA scheduler・通知観測を接続します。</summary>
    internal static PackageStateMutationApplier CreateStateApplier(
        string songDbPath,
        TrackingCallbacks callbacks,
        Func<ObservableCollection<ChartPackage>> getPendingPackages,
        Action<ObservableCollection<ChartPackage>> setPendingPackages,
        Func<ObservableCollection<ChartPackage>> getInstalledPackages,
        Action<ObservableCollection<ChartPackage>> setInstalledPackages)
    {
        var databaseScope = new ApplicationDatabaseScope();
        return new PackageStateMutationApplier(
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath),
            getPendingPackages,
            delegate (ObservableCollection<ChartPackage> packages)
            {
                callbacks.PendingPackagesSetCount++;
                setPendingPackages(packages);
            },
            getInstalledPackages,
            delegate (ObservableCollection<ChartPackage> packages)
            {
                callbacks.InstalledPackagesSetCount++;
                setInstalledPackages(packages);
            },
            () =>
            {
                callbacks.InstalledPackagesChangedCount++;
                callbacks.InstalledPackagesChangedThreadId = Environment.CurrentManagedThreadId;
            },
            packages => new ObservableCollection<ChartPackage>(packages),
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            _ => false,
            mutation => mutation());
    }

    /// <summary>実DBと正式な共通現在値の適用を接続し、捕捉済み入力を変更せずに返します。</summary>
    internal static CatalogMutationReceipt ApplyCatalogRelocation(
        string songDbPath,
        LibraryCatalogMutationFacts catalogFacts,
        TrackingCallbacks callbacks)
        => ApplyCatalogRelocation(songDbPath, catalogFacts, callbacks, out _);

    /// <summary>確定後の共通現在値を観測するため、同じ局所カタログ所有者も返します。</summary>
    internal static CatalogMutationReceipt ApplyCatalogRelocation(
        string songDbPath,
        LibraryCatalogMutationFacts catalogFacts,
        TrackingCallbacks callbacks,
        out CatalogOwnedCollectionOwner currentOwner)
    {
        var databaseScope = new ApplicationDatabaseScope();
        currentOwner = new CatalogOwnedCollectionOwner();
        // 本番の形式別初期入力と同じく旧値を振り分け、BMSONをBMS入力へ混ぜて未所持にしません。
        ChartFile[] previousValues = [.. catalogFacts.ChartPathChanges.Select(change => change.Chart with
        { Path = change.OldPath, Token = change.Chart.Token ?? new OwnedChartToken() })];
        currentOwner.ReplaceCharts(previousValues.Where(chart => chart.Kind == ChartFileKind.Bms),
            previousValues.Where(chart => chart.Kind == ChartFileKind.Bmson));
        var normalizedChanges = new List<LibraryChartPathChange>();
        foreach (LibraryChartPathChange change in catalogFacts.ChartPathChanges)
        {
            ChartFile current = currentOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromPath(
                change.Chart.Kind, change.OldPath, change.Chart.Md5, change.Chart.Sha256));
            normalizedChanges.Add(new LibraryChartPathChange
            { Chart = current, OldPath = change.OldPath, NewPath = change.NewPath });
        }
        var normalizedFacts = new LibraryCatalogMutationFacts(catalogFacts.ChartRemoveRequests, normalizedChanges, catalogFacts.FolderPathChanges);
        var owner = new CatalogMutationOwner(currentOwner, new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath));
        owner.CatalogWriteFailurePublished += delegate (object? sender, CatalogWriteFailureFact fact)
        {
            callbacks.SongDbWriteFailureCount++;
            callbacks.LastSongDbWriteFailureStage = fact.Stage;
            callbacks.LastSongDbWriteFailure = fact.Exception;
        };
        return owner.ApplyCatalogMutation(normalizedFacts);
    }

    /// <summary>同じ順序と所属契約で導入packageの観測用collectionを作ります。</summary>
    internal static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    /// <summary>指定した所持譜面の解除要求だけを既存の事実形式へ変換します。</summary>
    internal static LibraryCatalogMutationFacts CreateUnregisterFacts(IEnumerable<ChartFile> charts)
    {
        List<OwnedChartRemoveRequest> removeRequests = [.. (charts ?? [])
            .Select(OwnedChartRemoveRequest.FromChart)
            .Where(request => request != null)];
        return new LibraryCatalogMutationFacts(removeRequests, [], []);
    }

    /// <summary>確定済みのpackage参照と解除事実を、既存の適用順で反映します。</summary>
    internal static void ApplyCommittedMutation(
        PackageStateMutationApplier applier,
        LibraryCatalogMutationFacts catalogFacts,
        LibraryPackageReferenceFacts? packageReferenceFacts = null,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts = null!)
    {
        applier.ApplyPackageReferenceFacts(
            packageReferenceFacts ?? LibraryPackageReferenceFacts.Empty,
            CatalogChartMutationFact.CreateRemovalFacts(catalogFacts?.ChartRemoveRequests),
            protectedPathFacts ?? []);
    }

    /// <summary>実DB接続をcallback内で終端させ、同じ寿命でケース専有の可変媒体を回収します。</summary>
    internal static void WithTemporarySongDb(Action<string> testAction)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_StateApplierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        testAction(songDbPath);
    }

    /// <summary>実適用後の通知回数・thread・失敗原因を観測する共有記録です。</summary>
    internal sealed class TrackingCallbacks
    {
        public int PendingPackagesSetCount { get; set; }

        public int InstalledPackagesSetCount { get; set; }

        public int InstalledPackagesChangedCount { get; set; }

        public int InstalledPackagesChangedThreadId { get; set; }

        public int SongDbWriteFailureCount { get; set; }

        public string LastSongDbWriteFailureStage { get; set; } = string.Empty;

        public Exception LastSongDbWriteFailure { get; set; } = null!;
    }

    /// <summary>既存のschedule拒否経路だけを再現し、同期実行へ置き換えません。</summary>
    internal sealed class CanceledScheduleUiScheduler : IUiScheduler
    {
        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
            => CanceledUiScheduledOperation.Instance;

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();
    }

    /// <summary>既存の通知キューを明示的な順序で解放する共有schedulerです。</summary>
    internal sealed class QueuedProgressUiScheduler : IUiScheduler
    {
        private readonly Queue<Action> actions = [];

        internal int PendingCount => actions.Count;

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            actions.Enqueue(action);
            return new CompletedUiScheduledOperation();
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

        internal void ExecuteNext()
        {
            actions.Dequeue()();
        }
    }

    /// <summary>完了済みscheduleの既存応答を共有します。</summary>
    internal sealed class CompletedUiScheduledOperation : IUiScheduledOperation
    {
        public bool IsAccepted => true;

        public bool IsCompleted => true;

        public bool IsAborted => false;

        public string RejectionReason => string.Empty;

        public Task Completion => Task.CompletedTask;

        public void Abort()
        {
        }
    }

    /// <summary>開始前取消済みscheduleの既存応答を共有します。</summary>
    internal sealed class CanceledUiScheduledOperation : IUiScheduledOperation
    {
        private static readonly CancellationToken CanceledToken = new(canceled: true);

        internal static CanceledUiScheduledOperation Instance { get; } = new();

        public bool IsAccepted => true;

        public bool IsCompleted => true;

        public bool IsAborted => true;

        public string RejectionReason => string.Empty;

        public Task Completion { get; } = Task.FromCanceled(CanceledToken);

        public void Abort()
        {
        }
    }

}
