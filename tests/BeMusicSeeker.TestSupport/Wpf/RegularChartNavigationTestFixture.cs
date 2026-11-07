using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>旧Maintenance/MainWindowと移管先の通常一覧caseで、実root・受理済み投影Task・停止後の媒体回収を同じ境界で共用します。</summary>
internal static class RegularChartNavigationTestFixture
{
    /// <summary>受付が空いた後に残る通常一覧の通知配送と表示準備Taskを、新しいTaskが現れなくなるまで待ちます。</summary>
    /// <remarks>
    /// 非公開フィールドへ依存する理由は、所有者が通知起点の配送・表示準備Taskを公開せず、テスト専用APIの追加も認めないためです。
    /// 読むのはsyncRoot、viewPreparationTasks、normalLibraryRefreshDrainCompletionの3フィールドの読取りだけで、所有者の状態は変更しません。
    /// 管理主体はTestSupportのこの補助で、本番が配送と表示準備の終端を公開した時点でその公開窓口へ置き換えて退役させます。
    /// 各回で、未待機の配送Taskを待ち、Backgroundまで予約を消化し、準備Taskのスナップショットを取ります。
    /// 未待機の準備Taskがあればその集合を待って繰り返し、配送Taskが待機済みと同一で新しい準備Taskもなければ終えます。
    /// 抑制解除のflushは配送の完了後に準備Taskを登録するため、配送Taskの待機を終了条件に含めます。
    /// 保証するのは、この手順が終えた時点で読取り可能な配送と準備Taskがすべて待機済みであることです。局所の期限は付けません。
    /// 読み取るdrainは最後に開始したもののTaskです。それが過去に失敗または取消で終わっていた場合、補助はその例外を再送出します。
    /// これは失敗を隠さないためです。
    /// 待機済みの準備Taskを返すため、呼出側は自身の回収対象へ加えられます。
    /// </remarks>
    /// <param name="owner">配送と表示準備Taskを持つ通常一覧の所有者です。</param>
    /// <param name="stage">待機失敗時の診断に使う段階名です。</param>
    /// <returns>待機した準備Taskです。</returns>
    internal static Task[] AwaitViewPreparationsOnDispatcher(RegularChartListOwner owner, string stage)
    {
        var awaited = new HashSet<Task>();
        Task? awaitedDrain = null;
        for (int round = 1; ; round++)
        {
            Task drain = ReadNotificationDrain(owner);
            bool drainPending = !ReferenceEquals(drain, awaitedDrain);
            if (drainPending)
            {
                awaitedDrain = drain;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(drain, $"{stage} notification drain round {round}");
            }
            TestUiDispatcherHost.ProcessQueuedBackground();
            Task[] pending = SnapshotViewPreparations(owner).Where(task => !awaited.Contains(task)).ToArray();
            if (pending.Length == 0 && !drainPending && ReferenceEquals(ReadNotificationDrain(owner), awaitedDrain)) { return [.. awaited]; }
            if (pending.Length == 0) { continue; }
            awaited.UnionWith(pending);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(pending), $"{stage} view preparation round {round}");
        }
    }

    /// <summary>通常一覧の表示準備Taskの現在値を、所有者の同期境界の中で複製して返します。読取り専用です。</summary>
    /// <remarks>AwaitViewPreparationsOnDispatcherと同じ例外の承認範囲（3フィールドの読取りのみ、本番が終端を公開した時点で退役）に含まれます。</remarks>
    internal static Task[] SnapshotViewPreparations(RegularChartListOwner owner)
    {
        lock (ReadSyncRoot(owner))
        {
            if (OwnerField("viewPreparationTasks", "実準備Task集合がありません。").GetValue(owner) is not HashSet<Task> tasks) { throw new AssertFailedException("実準備Task集合の型が想定と異なります。"); }
            return tasks.ToArray();
        }
    }

    /// <summary>通常一覧の通知配送の現在のdrain Taskを、所有者の同期境界の中で読み取ります。読取り専用です。</summary>
    /// <remarks>AwaitViewPreparationsOnDispatcherと同じ例外の承認範囲（3フィールドの読取りのみ、本番が終端を公開した時点で退役）に含まれます。</remarks>
    internal static Task ReadNotificationDrain(RegularChartListOwner owner)
    {
        lock (ReadSyncRoot(owner))
        {
            return OwnerField("normalLibraryRefreshDrainCompletion", "実通知drainの窓口がありません。").GetValue(owner) as Task
                ?? throw new AssertFailedException("実通知drainの型が想定と異なります。");
        }
    }

    private static object ReadSyncRoot(RegularChartListOwner owner)
        => OwnerField("syncRoot", "実準備Taskの同期境界がありません。").GetValue(owner)
            ?? throw new AssertFailedException("実準備Taskの同期境界が空です。");

    private static FieldInfo OwnerField(string name, string missing)
        => typeof(RegularChartListOwner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException(missing);

    /// <summary>UI配送が返したTaskと処理内で返されたTaskを記録し、入れ子の開始済み処理も完了まで回収します。</summary>
    internal sealed class ProjectionTaskCollector(IUiScheduler scheduler) : IUiScheduler
    {
        private sealed class Projection { internal Task? Outer; internal Task? Inner; }
        private readonly object gate = new();
        private readonly List<Projection> prior = [];
        private TaskCompletionSource<Task>? next;
        internal Task<Task> ObserveNext()
        {
            lock (gate)
            {
                if (next != null) { throw new AssertFailedException("前の実Func観測が未到達です。"); }
                next = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return next.Task;
            }
        }
        internal bool HasPrior { get { lock (gate) { return prior.Count != 0; } } }
        internal async Task DrainPriorAsync(Action<string, Task>? report = null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
            while (true)
            {
                Projection[] captured;
                lock (gate) { captured = prior.ToArray(); prior.Clear(); }
                if (captured.Length == 0) { break; }
                try
                {
                    await Task.WhenAll(captured.Select(async projection =>
                    {
                        Task outer = projection.Outer ?? throw new AssertFailedException("実Func outerを捕捉していません。");
                        report?.Invoke("projection-outer-await", outer);
                        try { await outer; }
                        finally
                        {
                            if (projection.Inner is { } inner) { report?.Invoke("projection-inner-await", inner); await inner; }
                        }
                        report?.Invoke("projection-terminal", outer);
                    }));
                }
                catch (Exception exception) { failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
            }
            failure?.Throw();
        }

        public bool IsAvailable => scheduler.IsAvailable;
        public bool CanExecuteInline => scheduler.CanExecuteInline;
        public bool CheckAccess() => scheduler.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.Schedule(action, priority);
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.Invoke(action, priority);
        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.InvokeAsync(action, priority);
        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            var projection = new Projection();
            Task outer = scheduler.InvokeAsync(() => { Task inner = action(); projection.Inner = inner; return inner; }, priority);
            projection.Outer = outer;
            lock (gate)
            {
                prior.Add(projection);
                next?.TrySetResult(outer);
                next = null;
            }
            return outer;
        }
    }

    /// <summary>画面の寿命内で専用データを所有します。失敗時は保留を解除して停止を開始し、実行中Taskを回収してからデータを解放します。</summary>
    /// <param name="test">実rootと投影Taskを所有するfixtureを借りて検証する同期callback。fixture自体の回収はhelperが行います。</param>
    /// <param name="beforeOwnerNotification">owner通知より前に挿入する任意観測。</param>
    /// <param name="scheduler">投影を配信する任意scheduler。省略時は既存UI Dispatcherへ接続します。</param>
    /// <param name="report">失敗回収時の停止開始・終端を観測する任意callback。</param>
    /// <param name="dialogs">実rootへ渡す任意dialog port。</param>
    /// <remarks>
    /// MainWindowTestLifetimeの終了処理とfixtureのTask回収を、WithTemporarySongDbが所有するworkspaceの媒体回収より先に行います。
    /// 本体失敗時はproducerを解放して停止を開始し、停止・Task回収の二次失敗は元例外のDataへ保持します。本体成功時の回収失敗はそのまま伝播します。
    /// </remarks>
    internal static void RunColdDuplicateFixture(Action<DuplicateNavigationTestFixture> test,
        PropertyChangedEventHandler? beforeOwnerNotification = null, IUiScheduler? scheduler = null,
        Action<string, Task>? report = null, BeMusicSeeker.Views.Dialogs.IUiDialogService? dialogs = null)
    {
        TestUiDispatcherHost.Invoke(() => WithTemporarySongDb(songDb =>
        {
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.StartupSelectInstallPending = false;
                values.UseBeatorajaScoreDb = false;
            });
            var tasks = new ProjectionTaskCollector(scheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            MainWindowViewModel vm = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new NoOpSettingsEditSession(settings), uiScheduler: tasks,
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                fileDbMutationDialogService: dialogs).CreateMainWindowViewModelForTest();
            var lifetime = new MainWindowTestLifetime(vm, Task.CompletedTask);
            lifetime.Run(() =>
            {
                var fixture = new DuplicateNavigationTestFixture(vm, songDb, settings, () => tasks.DrainPriorAsync(), beforeOwnerNotification);
                Exception? bodyFailure = null;
                try { test(fixture); }
                catch (Exception exception) { bodyFailure = exception; throw; }
                finally
                {
                    fixture.ReleaseProducer();
                    // テスト本体で保留を解除した後、失敗時は停止を先に開始してから実行中Taskを回収する。
                    try
                    {
                        Task stop = bodyFailure == null ? Task.CompletedTask : vm.RegularChartList.StopAsync();
                        if (bodyFailure != null) { report?.Invoke("fixture-stop-start", stop); }
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(stop, "cold fixture failure Stop before join");
                        if (bodyFailure != null) { report?.Invoke("fixture-stop-terminal", stop); }
                    }
                    catch (Exception cleanup) when (bodyFailure != null) { bodyFailure.Data["ColdFixtureStopCleanup"] = cleanup; }
                    finally
                    {
                        try { fixture.Dispose(); }
                        catch (Exception cleanup) when (bodyFailure != null) { bodyFailure.Data["ColdFixtureTaskCleanup"] = cleanup; }
                    }
                }
            });
        }));
    }

}



/// <summary>初回重複解析の確定通知を保留し、標準VMへ後発の画面入力を接続する専用の準備データです。</summary>
internal sealed class DuplicateNavigationTestFixture : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource groupsApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource summaryReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PropertyChangedEventHandler libraryChanged;
    private readonly PropertyChangedEventHandler treeChanged;
    private readonly PropertyChangedEventHandler summaryChanged;
    private readonly PropertyChangedEventHandler rowsChanged;
    private readonly EventHandler rowsCanceled;
    private readonly EventHandler<RegularDuplicateDataReadyEventArgs> presentation;
    private readonly PropertyChangedEventHandler? beforeOwnerNotification;
    private int gateEnabled;
    private int gateClaimed;
    private string[] gateHeldLocks = [];
    private Task<bool>? duplicate;
    private readonly List<Task> navigationTasks = [];
    private readonly object taskLock = new();
    private readonly List<Task> dataTasks = [];
    private readonly List<(RegularChartNavigationRequest Navigation, TaskCompletionSource Changed)> expectedRows = [];
    private readonly Func<Task> drainPresentations;
    private readonly HashSet<Task> observed = [];
    private bool disposed;

    /// <summary>呼出元が所有するVMとDBへ、実ライブラリとプレイリストを接続します。VMの停止と一時ファイルの削除は呼出元が担当します。</summary>
    internal DuplicateNavigationTestFixture(MainWindowViewModel viewModel, string songDb, BeMusicSeeker.Properties.Settings settings,
        Func<Task> drainPresentations, PropertyChangedEventHandler? beforeOwnerNotification = null)
    {
        this.drainPresentations = drainPresentations ?? throw new ArgumentNullException(nameof(drainPresentations));
        this.beforeOwnerNotification = beforeOwnerNotification;
        ViewModel = viewModel;
        string directory = Path.GetDirectoryName(songDb) ?? throw new AssertFailedException();
        ChartFile CreateChart(string folder, string hash, string title, string artist)
        {
            string path = Path.Combine(directory, folder, "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new AssertFailedException());
            File.WriteAllText(path, "#PLAYER 1\n#TITLE " + title + "\n#ARTIST " + artist + "\n#BPM 120\n");
            return ChartTestValues.Empty() with { Path = path, Md5 = hash, Title = title, Artist = artist };
        }
        AllCharts = [CreateChart("duplicate-a", new string('a', 32), "duplicate", "duplicate artist"),
            CreateChart("duplicate-b", new string('a', 32), "duplicate", "duplicate artist"),
            CreateChart("target", new string('b', 32), "target", "target artist")];
        Duplicates = AllCharts[..2];
        Target = AllCharts[2];
        using (LR2SongDBExtended db = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope.OpenSongWrite(songDb)) { db.CreateTable<LR2SongDBExtended.bmson_song>(); }
        Library = MainWindowViewModelTestFactory.CreateLibrary(MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope, songDb, settings, viewModel);
        Library.BmsCharts = AllCharts;
        new BmsLibraryDbGateway(MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).UpsertSongs(AllCharts);
        PlaylistPersistenceRepository.EnsureSchema(MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope, songDb);
        TestBmsPlaylist playlist = Playlist = MainWindowViewModelTestFactory.CreatePlaylist(MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope, songDb, settings, library: Library);
        Entry = new BMSTableEntry { md5 = Target.Md5, title = "target", artist = "target artist" };
        Table = new BMSTable { playlist_id = 701, name = "target playlist", entries = [Entry] };
        playlist.BMSTables = new ObservableCollection<BMSTable>([Table]);
        Package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(Target)]);
        Library.ChartPackagesInstalled = new ObservableCollection<ChartPackage>([Package]);
        libraryChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion)
                && Volatile.Read(ref gateEnabled) != 0 && Library.NormalLibraryRefreshNotificationVersion > InitialNotificationVersion
                && Interlocked.Exchange(ref gateClaimed, 1) == 0)
            {
                // 通知先の失敗は記録だけで握られるため、ここでは表明せず、ゲート到達後に表明する。
                gateHeldLocks = [.. new[] { (Library.IsWriteLockHeldInitializeMin, "InitializeMin"),
                    (Library.IsWriteLockHeldInitializeBMSFiles, "InitializeBMSFiles"),
                    (Library.IsWriteLockHeldDuplicateChartGroups, "DuplicateChartGroups") }
                    .Where(hold => hold.Item1).Select(hold => hold.Item2)];
                reached.TrySetResult();
                release.Wait();
            }
        };
        Library.PropertyChanged += libraryChanged;
        if (beforeOwnerNotification != null) { Library.PropertyChanged += beforeOwnerNotification; }
        treeChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(MaintenanceTreeViewModel.DuplicateChartGroups) && Library.DuplicateChartGroups != null)
            { groupsApplied.TrySetResult(); }
        };
        viewModel.MaintenanceTree.PropertyChanged += treeChanged;
        summaryChanged = (_, _) =>
        {
            if (viewModel.PlaylistWorkspace.IsPlaylistSummaryMode
                && viewModel.PlaylistWorkspace.PlaylistSummaryView.Any(row => row.Name == "target playlist"))
            { summaryReady.TrySetResult(); }
        };
        viewModel.PlaylistWorkspace.PropertyChanged += summaryChanged;
        rowsChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(MainChartListViewModel.Rows)
                && viewModel.MainChartList.LastAppliedColumnMode == MainViewUpdateMode.DuplicateFilterSelected)
            { CompleteExpectedRows(); }
        };
        rowsCanceled = (_, _) => CompleteExpectedRows();
        viewModel.MainChartList.PropertyChanged += rowsChanged;
        viewModel.MainChartList.RowsReplacementCanceled += rowsCanceled;
        presentation = (_, request) =>
        {
            CacheReadyAtPresentation &= Library.DuplicateChartGroups != null && request.Input.IsCurrent;
            CaptureData();
            RegularChartNavigationRequest navigation = viewModel.RegularChartList.CaptureNavigation();
            if (navigation?.DuplicateContext != null && navigation.IsCurrent)
            {
                lock (taskLock) { expectedRows.Add((navigation, new(TaskCreationOptions.RunContinuationsAsynchronously))); }
            }
        };

        void CompleteExpectedRows()
        {
            lock (taskLock)
            {
                foreach ((RegularChartNavigationRequest Navigation, TaskCompletionSource Changed) expected in expectedRows)
                { if (expected.Navigation.IsCurrent) { expected.Changed.TrySetResult(); } }
            }
        }
        viewModel.RegularChartList.DuplicateDataReady += presentation;
        IStartupLibraryApplicationPort application = viewModel;
        application.AttachStartupLibrary(Library);
        application.AttachStartupServices(new StartupLibraryServices(new LibraryProfile(false, songDb, [directory],
            lr2ConfigOwner: null, lr2ScoreDbPath: null, canWriteLr2Config: false, canOutputLr2Folders: false,
            canUseLr2Backup: false, canUseLr2IrScore: false), Library, playlist));
        viewModel.MaintenanceTree.AttachLibrary(Library);
        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.RegularChartList.DeliverPendingLibraryChangesAsync(Library, CancellationToken.None),
            "initial duplicate fixture delivery");
        InitialNotificationVersion = Library.NormalLibraryRefreshNotificationVersion;
    }

    internal MainWindowViewModel ViewModel { get; }
    internal TestBmsLibrary Library { get; }
    /// <summary>同じ所有ライブラリへ接続したプレイリストです。履歴投影でも通常画面と同じ参照索引を使います。</summary>
    internal TestBmsPlaylist Playlist { get; }
    internal ChartFile[] AllCharts { get; }
    internal ChartFile[] Duplicates { get; }
    internal ChartFile Target { get; }
    internal ChartPackage Package { get; }
    internal BMSTable Table { get; }
    internal BMSTableEntry Entry { get; }
    internal int InitialNotificationVersion { get; }
    internal bool CacheReadyAtPresentation { get; private set; } = true;

    /// <summary>実ツリーの選択から解析を始める場合にも、確定通知の保留を有効にします。</summary>
    internal void EnableGate() => Volatile.Write(ref gateEnabled, 1);

    /// <summary>実画面の入力を一覧の所有者へ渡し、戻りTaskをこの準備データで回収します。</summary>
    internal Task<bool> NavigateDuplicate(MainViewUpdateMode mode, object? parameter)
    {
        Task<bool> task = ViewModel.RegularChartList.NavigateMaintenanceAsync(mode, parameter);
        duplicate = task;
        lock (taskLock) { navigationTasks.Add(task); }
        return task;
    }

    /// <summary>キャッシュが未準備であることを確認し、正規入口から解析を開始します。</summary>
    internal void StartDuplicate()
    {
        Assert.IsNull(Library.DuplicateChartGroups);
        EnableGate();
        _ = NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null);
    }

    /// <summary>時間経過ではなく、実生成側の全ロック解放後通知への到達を待ちます。</summary>
    internal void AwaitGate()
    {
        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task,
            duplicate ?? throw new AssertFailedException("cold要求を開始していません。"), "cold duplicate producer gate"), "cold duplicate gate");
        Assert.AreEqual(0, gateHeldLocks.Length, "確定通知の時点でライブラリの書込みロックを保持しています: " + string.Join(", ", gateHeldLocks));
        CaptureData();
    }

    /// <summary>解析側の通知到達後、または完成通知の受信中に、同じ実行中データ準備Taskを取得します。</summary>
    internal Task CaptureData()
    {
        Task task = ViewModel.RegularChartList.PrepareDuplicateDataAsync("fixture_active_data_join");
        lock (taskLock) { if (!dataTasks.Contains(task)) { dataTasks.Add(task); } }
        return task;
    }
    internal void ReleaseProducer() => release.Set();
    internal void AwaitOwned(Task task, string stage)
    {
        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(task, stage); }
        finally { lock (taskLock) { if (task.IsCompleted) { observed.Add(task); } } }
    }
    /// <summary>正規要求の戻りTaskです。失敗はUIの例外処理ではなく、このTaskの終端から観測します。</summary>
    internal Task<bool> NavigationTask => duplicate ?? throw new AssertFailedException("正規要求がありません。");
    internal void AwaitNavigation() => AwaitOwned(NavigationTask, "duplicate navigation terminal");
    /// <summary>実際の終端を観測済みのTaskを記録し、期待した失敗を後片付けで再送出しないようにします。</summary>
    internal void RecordObservedTerminal(Task task)
    {
        Assert.IsTrue(task.IsCompleted, "未終端Taskを観測済みにしません。");
        lock (taskLock) { observed.Add(task); }
    }
    /// <summary>データ完成と有効な重複選択の実際の行公開・準備取消を分けて観測し、開始された表示Taskを回収します。</summary>
    /// <param name="awaitRows">本体の期待結果を待つ場合だけtrueにします。後片付けでは未開始の行通知を待ちません。</param>
    internal async Task AwaitReadyAsync(bool awaitRows = true)
    {
        Task[] data;
        lock (taskLock) { data = dataTasks.ToArray(); }
        try { await Task.WhenAll(data).ConfigureAwait(false); }
        finally { lock (taskLock) { foreach (Task task in data) { if (task.IsCompleted) { observed.Add(task); } } } }
        if (awaitRows)
        {
            Task[] expected;
            lock (taskLock) { expected = expectedRows.Where(item => item.Navigation.IsCurrent).Select(item => item.Changed.Task).ToArray(); }
            var observation = Task.WhenAll(expected);
            Task running = drainPresentations();
            if (ReferenceEquals(await Task.WhenAny(observation, running).ConfigureAwait(false), running))
            { await running.ConfigureAwait(false); }
            await observation.ConfigureAwait(false);
            await running.ConfigureAwait(false);
        }
        await drainPresentations().ConfigureAwait(false);
    }

    /// <summary>解析の確定通知を保留したまま、グループ変更が実際にUIへ届いたことを確認します。</summary>
    internal void AwaitGroupsWhileGated()
    {
        Task<bool> active = duplicate ?? throw new AssertFailedException("cold要求を開始していません。");
        Assert.IsFalse(active.IsCompleted, "groups受信は最初のAが未完了の間に確認します。");
        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(groupsApplied.Task,
            active, "duplicate groups received while producer gated"), "duplicate groups before gate release");
        Assert.IsFalse(active.IsCompleted, "groups受信自身でproducer gateを解放しません。");
    }

    /// <summary>実プレイリストからサマリーを構築し、期待する対象と表示モードが適用されるまで待ちます。</summary>
    internal void AwaitSummary()
    {
        ViewModel.PlaylistWorkspace.RebuildPlaylistSummaryView(runAsync: false);
        summaryChanged(this, new PropertyChangedEventArgs(string.Empty));
        TestUiDispatcherHost.AwaitTaskOnDispatcher(summaryReady.Task, "later playlist summary");
    }

    /// <summary>解析、グループ変更のUI通知、必要なカタログ配送を、それぞれ完了まで回収します。</summary>
    internal void CompleteDuplicate()
    {
        release.Set();
        AwaitOwned(AwaitReadyAsync(), "duplicate data and ready presentation terminals");
        if (duplicate != null) { AwaitNavigation(); }
        TestUiDispatcherHost.AwaitTaskOnDispatcher(groupsApplied.Task, "duplicate groups actual UI notification");
        TestUiDispatcherHost.AwaitTaskOnDispatcher(ViewModel.RegularChartList.DeliverPendingLibraryChangesAsync(Library, CancellationToken.None),
            "duplicate necessary catalog delivery");
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        release.Set();
        try
        {
            Task[] started;
            lock (taskLock)
            {
                started = dataTasks.Concat(navigationTasks)
                    .Where(task => !observed.Contains(task) || !task.IsCompleted).ToArray();
            }
            AwaitOwned(Task.WhenAll(started), "duplicate fixture data/navigation cleanup");
            AwaitOwned(drainPresentations(), "duplicate fixture actual presentation cleanup");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(ViewModel.PlaylistWorkspace.WaitForDetailBuildIdleAsync(), "duplicate fixture detail cleanup");
        }
        finally
        {
            Library.PropertyChanged -= libraryChanged;
            if (beforeOwnerNotification != null) { Library.PropertyChanged -= beforeOwnerNotification; }
            ViewModel.MaintenanceTree.PropertyChanged -= treeChanged;
            ViewModel.PlaylistWorkspace.PropertyChanged -= summaryChanged;
            ViewModel.MainChartList.PropertyChanged -= rowsChanged;
            ViewModel.MainChartList.RowsReplacementCanceled -= rowsCanceled;
            ViewModel.RegularChartList.DuplicateDataReady -= presentation;
            release.Dispose();
        }
    }
}
