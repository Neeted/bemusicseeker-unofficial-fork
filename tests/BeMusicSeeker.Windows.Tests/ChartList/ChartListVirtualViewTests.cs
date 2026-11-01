using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ChartListVirtualViewTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>仮想一覧の必要行だけの生成、形式共通投影と履歴終端の確定・公開を検証します。</summary>
/// <remarks>sort値と要求可否はWindowなしで確認します。実library・playerケースは操作接続と一時ファイル回収を担当し、compiled画面接続はMainWindow側が分担します。非並列ケースはSQLite/native初期化を共有するcompositionを通ります。</remarks>
[TestClass]
public sealed class ChartListVirtualViewTests
{
    /// <summary>通常一覧は採用時の数値を保持して言語変更に追従し、Preserveで保持、明示文言で解除する。未採用候補や元行の後続変更を集計へ混ぜない。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void MainChartSummaryLanguageUsesAdoptedCountsAndPreserveUntilExplicit()
    {
        string culture = BeMusicSeeker.Properties.Resources.Culture?.Name ?? System.Globalization.CultureInfo.CurrentUICulture.Name;
        var owner = new MainChartListViewModel(action => action());
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            owner.AttachLanguagePresentation();
            var sourceRows = Enumerable.Range(0, 12).Select(_ => new object()).ToList();
            Apply(MainChartListSummaryUpdate.NormalRows(sourceRows));
            sourceRows.Add(new object());
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual("[12" + BeMusicSeeker.Properties.Resources.Num_songs + "]", owner.SummaryText);
            Apply(MainChartListSummaryUpdate.NormalCounts(12, 3));
            object rows = owner.Rows;
            foreach (string language in new[] { "en-US", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(language);
                Assert.AreEqual("[12" + BeMusicSeeker.Properties.Resources.Num_songs + " / 3"
                    + BeMusicSeeker.Properties.Resources.Num_folders + "]", owner.SummaryText);
                Assert.AreSame(rows, owner.Rows);
                Apply(MainChartListSummaryUpdate.Preserve());
                rows = owner.Rows;
            }
            Apply(MainChartListSummaryUpdate.Explicit("fixed"));
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual("fixed", owner.SummaryText);
            owner.DetachLanguagePresentation();
            int notifications = 0;
            owner.PropertyChanged += (_, _) => notifications++;
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(0, notifications);
        }
        finally
        {
            owner.DetachLanguagePresentation();
            ResourceService.Current.ChangeCulture(culture);
        }

        void Apply(MainChartListSummaryUpdate summary) => owner.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = Array.Empty<object>(),
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD),
            SelectionPolicy = MainChartListSelectionPolicy.Preserve,
            Summary = summary,
            Stopwatch = Stopwatch.StartNew()
        });
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>導入先に同名ファイルがある実再生では改名先をplayerへ渡し、元譜面と衝突ファイルを保持して一時コピーを回収することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task PlaybackPanel_StartAtIndexUsesChartInstallDestinationWhenTemporaryRenameChangesPath()
    {
        var databaseScope = new ApplicationDatabaseScope();
        using IDisposable cultureScope = TestCultureScope.UseJapaneseCulture();
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_PlayStart_" + Guid.NewGuid().ToString("N"));
        string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
        string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
        string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
        string destinationCollisionPath = Path.Combine(destinationDirectoryPath, "chart.bms");
        string temporaryChartName = "chart_.bms";
        Directory.CreateDirectory(sourceDirectoryPath);
        Directory.CreateDirectory(destinationDirectoryPath);
        File.WriteAllText(sourceChartPath, "#PLAYER 1");
        File.WriteAllText(destinationCollisionPath, "#PLAYER 1");
        try
        {
            testSettings.UsePlayerLR2body = false;
            testSettings.OperationModeLR2DB = false;
            var player = new RecordingBmsPlayer();
            var composition = new ApplicationComposition(
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
                defaultBmsPlayerFactory: () => player,
                settingsEditSession: new NoOpSettingsEditSession(testSettings),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            var viewModel = new MainWindowViewModel(composition, composition);
            using SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
            using RegularChartListOwner rowsLifetime = viewModel.RegularChartList;
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, testSettings);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            viewModel.PlaybackPanel.AttachLibrary(library);
            ChartFile file = ChartTestValues.Empty();
            file = file with { Path = sourceChartPath, Title = "Playable", Folder = "Source", Artist = "", Genre = "", Level = null, Mode = null, Tag = "", Md5 = "0123456789abcdef0123456789abcdef", Sha256 = "", RawTitle = "Playable", RawArtist = "", LevelText = "" };
            ChartFile playableChart = (file);
            var transientEntry = PackageChartEntry.FromChart(playableChart);
            transientEntry.SetInstallDestinationPathOnly(destinationDirectoryPath);
            viewModel.MainChartList.RowProjection.UpdateTransientStates(
                [transientEntry.Chart],
                forceInstallDestinationProjection: true);
            var sourceRow = ChartListSourceRow.FromChartFile(
                playableChart,
                ChartListSourceProjectionMode.OwnerBacked,
                chartTransientStateProvider: viewModel.MainChartList.RowProjection.GetTransientState);
            LibraryChartRow viewRow = viewModel.MainChartList.RowProjection.CreateSubsetRow(
                library,
                sourceRow,
                includeResourceHealth: false);
            viewModel.MainChartList.RowProjection.ConfigureLibraryRow(library, viewRow);
            viewModel.MainChartList.Rows = new List<object>
            {
                viewRow
            };

            // 非同期の開始と一時改名・コピーの後片付けを待ってから検査します。
            await viewModel.PlaybackPanel.StartAtIndex(0);

            Assert.AreEqual(Path.Combine(destinationDirectoryPath, temporaryChartName), player.LastPlayedPath);
            Assert.AreEqual(sourceChartPath, file.Path);
            Assert.IsTrue(File.Exists(sourceChartPath));
            Assert.IsTrue(File.Exists(destinationCollisionPath));
            Assert.IsFalse(File.Exists(Path.Combine(sourceDirectoryPath, temporaryChartName)));
            Assert.IsFalse(File.Exists(Path.Combine(destinationDirectoryPath, temporaryChartName)));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>UI生成中にworkerが実項目の検索状態と通知を終えた後、同じ行の次の一読取りが新入力へ進むことを検証します。先行読取りの鮮度や後続表示の採用は要求しません。</summary>
    [TestMethod]
    public async Task PackageChartRow_NextReadUsesPublishedStateAfterInvalidationDuringProjection()
    {
        var entry = PackageChartEntry.FromChart(ChartTestValues.Empty() with
        {
            Path = @"package\cache.bms",
            Title = "Cache",
            Status = ChartFileStatus.SCORE_UNSENT
        });
        entry.SetSearchingStatus(true);
        ChartListVirtualView? view = null;
        LibraryChartRow? row = null;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<ChartFile>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        PropertyChangedEventHandler handler = (_, _) =>
        {
            Interlocked.Increment(ref notifications);
            notified.TrySetResult();
        };
        Task? read = null;
        Task? update = null;
        Exception? failure = null;
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                List<ChartListSourceRow> sources = ChartListSourceRow.BuildPackageRows([entry]);
                var projection = new MainChartRowProjectionOwner();
                view = new ChartListVirtualView(sources, ChartListOrder.CreateTitleAscending(sources),
                    source => projection.CreateSubsetRow(null, source, includeResourceHealth: false));
                row = (LibraryChartRow)view[0];
                row.PropertyChanged += handler;
                row.SetChartInfoProjectionProvider(chart =>
                {
                    if (entered.TrySetResult(chart)) { release.Wait(); }
                    return chart.ChartInfo;
                });
            });
            LibraryChartRow currentRow = row ?? throw new AssertFailedException("表示行が生成されませんでした。");
            ChartListVirtualView currentView = view ?? throw new AssertFailedException("仮想一覧が生成されませんでした。");
            read = Task.Run(() => TestUiDispatcherHost.Invoke(() => { _ = currentRow.Chart; }));
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, read, "cache provider arrival");
            Assert.AreEqual(ChartFileStatus.SCORE_UNSENT | ChartFileStatus.SEARCHING, (await entered.Task).Status);
            update = Task.Run(() => entry.SetSearchingStatus(false));
            await TestUiDispatcherHost.AwaitNotificationAsync(notified.Task, update, "package invalidation");
            await update;
            int completedNotifications = Volatile.Read(ref notifications);
            release.Set();
            await read;
            TestUiDispatcherHost.Invoke(() =>
            {
                Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, currentRow.Chart.Status,
                    "更新・通知・先行生成が終端した次の一読取りに旧SEARCHINGを残しません。");
                Assert.AreSame(currentRow, currentView[0]);
                Assert.AreEqual(1, currentView.RealizedRowCount);
            });
            Assert.AreEqual(completedNotifications, Volatile.Read(ref notifications));
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            release.Set();
            foreach (Task task in new[] { read, update }.OfType<Task>())
            {
                try { await task; }
                catch (Exception exception)
                {
                    if (failure == null) { failure = exception; }
                    else if (!ReferenceEquals(failure, exception)) { failure.Data["cache task cleanup " + failure.Data.Count] = exception; }
                }
            }
            try
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    if (row != null) { row.PropertyChanged -= handler; }
                    view?.DisposeRealizedRows();
                });
            }
            catch (Exception exception)
            {
                if (failure == null) { failure = exception; }
                else { failure.Data["cache view cleanup"] = exception; }
            }
        }
        if (failure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    /// <summary>通知延期中でも公開済みpackage版の変更を外側行が最初に読み、通知だけに依存する旧値残留を検出します。</summary>
    [TestMethod]
    public void PackageChartRow_ReadsPublishedStateBeforeDeferredNotification()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var entry = PackageChartEntry.FromChart(ChartTestValues.Empty() with
            { Path = @"package\deferred.bms", Status = ChartFileStatus.SCORE_UNSENT });
            entry.SetSearchingStatus(true);
            ChartListSourceRow source = ChartListSourceRow.BuildPackageRows([entry]).Single();
            LibraryChartRow row = new MainChartRowProjectionOwner().CreateSubsetRow(null, source, includeResourceHealth: false);
            Assert.AreEqual(ChartFileStatus.SCORE_UNSENT | ChartFileStatus.SEARCHING, row.Chart.Status);
            int notifications = 0;
            PropertyChangedEventHandler handler = (_, _) => notifications++;
            row.PropertyChanged += handler;
            try
            {
                int version = entry.ProjectionVersion;
                using (entry.DeferPropertyChangedNotifications())
                {
                    entry.SetSearchingStatus(false);
                    Assert.AreNotEqual(version, entry.ProjectionVersion);
                    Assert.AreEqual(0, notifications);
                    Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, row.Chart.Status);
                    Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, source.Chart.Status);
                }
            }
            finally { row.PropertyChanged -= handler; }
        });
    }

    /// <summary>library未接続でもpackage導入先を消去しadapterを作らず、未接続時の消去漏れを検出します。</summary>
    /// <remarks>対象識別とadapter非生成を確認するため、仮パスの譜面は参照取得済みの空集合として用意します。実ファイルの取得・配置評価はInstall側の試験へ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task InstallDestinationWorkflow_ClearPackagesWithoutLibraryClearsAdapterlessBmsonEntry()
    {
        using IDisposable cultureScope = TestCultureScope.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        var adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                (CreateBmsonSong() with { Resources = [] }),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        BmsLibraryPackageInstallService.InitializePendingResourceHealth(adapterlessBmsonEntry);
        var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);

        await viewModel.PendingPackages.ClearPackagesAsync([package]);

        Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
        Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
    }

    /// <summary>実libraryの保留packageへ消去要求を渡すと導入先を空にしadapterを作らず、実操作から旧BMS経路へ戻る誤りを検出します。</summary>
    /// <remarks>対象識別とadapter非生成を確認するため、仮パスの譜面は参照取得済みの空集合として用意します。実ファイルの取得・配置評価はInstall側の試験へ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task InstallDestinationWorkflow_ClearPendingClearsAdapterlessBmsonPackageEntryWithoutMaterializing()
    {
        var databaseScope = new ApplicationDatabaseScope();
        using IDisposable cultureScope = TestCultureScope.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        ChartFile bmsonSong = CreateBmsonSong() with { Resources = [] };
        var adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                (bmsonSong),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        BmsLibraryPackageInstallService.InitializePendingResourceHealth(adapterlessBmsonEntry);
        var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, testSettings);
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>([package]);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var selectedChart = new ChartOperationTarget(
                adapterlessBmsonEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                adapterlessBmsonEntry);

            Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate(
                [selectedChart],
                out PendingInstallDestinationClearRequest request));
            await viewModel.PendingPackages.ClearPendingAsync(request);

            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>同じpathのpackage項目と独立譜面を選んでもpackage導入先だけ消し、独立譜面の値とadapter非生成を保持して対象混同を検出します。</summary>
    /// <remarks>対象識別とadapter非生成を確認するため、仮パスの譜面は参照取得済みの空集合として用意します。実ファイルの取得・配置評価はInstall側の試験へ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task InstallDestinationWorkflow_ClearPendingDoesNotResolveLooseBmsonCompatibilityAdapterWhenStandaloneTargetSharesPath()
    {
        var databaseScope = new ApplicationDatabaseScope();
        using IDisposable cultureScope = TestCultureScope.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        ChartFile bmsonSong = CreateBmsonSong() with { Resources = [] };
        var adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                (bmsonSong),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        BmsLibraryPackageInstallService.InitializePendingResourceHealth(adapterlessBmsonEntry);
        var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, testSettings);
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>([package]);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var packageTarget = new ChartOperationTarget(
                adapterlessBmsonEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                adapterlessBmsonEntry);
            ChartFile standaloneChart = ChartFileProjection.WithPackageState(
                (bmsonSong),
                @"C:\Installed\Standalone",
                "Standalone",
                "Artist",
                []);
            var standaloneTarget = new ChartOperationTarget(
                standaloneChart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination);

            Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate(
                [packageTarget, standaloneTarget],
                out PendingInstallDestinationClearRequest request));
            await viewModel.PendingPackages.ClearPendingAsync(request);

            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
            Assert.AreEqual(@"C:\Installed\Standalone", standaloneChart.InstallDestination);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>実libraryで保留bmsonの導入先検索を完了してもadapterを生成せず、検索入口の旧形式依存を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task InstallDestinationWorkflow_SearchPendingDoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        var databaseScope = new ApplicationDatabaseScope();
        using IDisposable cultureScope = TestCultureScope.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        ChartFile bmsonSong = CreateBmsonSong();
        // 操作接続のfixtureには、リソース定義0件の解析成功結果を明示します。
        bmsonSong = bmsonSong with { Resources = [] };
        var adapterlessBmsonEntry = PackageChartEntry.FromChart((bmsonSong));
        var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, testSettings);
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>([package]);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var target = new ChartOperationTarget(
                adapterlessBmsonEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                adapterlessBmsonEntry);

            var request =
                PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([target]);
            await viewModel.PendingPackages.SearchPendingAsync(request);

            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>捕捉後に同じ譜面の項目を置換しても現在項目の導入先を消し、旧項目だけを編集する誤りを検出します。</summary>
    /// <remarks>対象識別とadapter非生成を確認するため、仮パスの譜面は参照取得済みの空集合として用意します。実ファイルの取得・配置評価はInstall側の試験へ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task InstallDestinationWorkflow_ClearPendingResolvesReplacedPackageEntryByChartIdentity()
    {
        var databaseScope = new ApplicationDatabaseScope();
        using IDisposable cultureScope = TestCultureScope.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        ChartFile bmsonSong = CreateBmsonSong() with { Resources = [] };
        var staleEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                (bmsonSong),
                @"C:\Installed\Old",
                "Old",
                "Artist",
                []));
        var currentEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                (bmsonSong),
                @"C:\Installed\Current",
                "Current",
                "Artist",
                []));
        BmsLibraryPackageInstallService.InitializePendingResourceHealth(currentEntry);
        var package = ChartPackage.FromChartEntries([currentEntry]);
        try
        {
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, testSettings);
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>([package]);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var target = new ChartOperationTarget(
                staleEntry.Chart,
                null,
                ChartOperationSourceScope.PendingPackage,
                isOwned: false,
                isPending: true,
                isPlaylistMissing: false,
                ChartOperationCapabilities.UpdateInstallDestination,
                staleEntry);

            Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate(
                [target],
                out PendingInstallDestinationClearRequest request));
            await viewModel.PendingPackages.ClearPendingAsync(request);

            Assert.IsNull(currentEntry.GetBmsChartForTest());
            Assert.AreEqual(string.Empty, currentEntry.Chart.InstallDestination);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>実重複一覧の非対応sortは、要求受信でなくRowsとソート解除の実公開を待ち、owner/UIの解除とタイトル昇順の二行を確認します。通知は要求前に購読し、失敗時も解除します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task VirtualChartSubsetUnsupportedSort_ResetsToDefaultVirtualSort()
    {
        var databaseScope = new ApplicationDatabaseScope();
        using IDisposable cultureScope = TestCultureScope.UseJapaneseCulture();
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = viewModel.RegularChartList;
        ChartFile zeta = CreateFile(Path.Combine(tempRootPath, "zeta.bms"), "Zeta", tempRootPath);
        ChartFile alpha = CreateFile(Path.Combine(tempRootPath, "alpha.bms"), "Alpha", tempRootPath, hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        try
        {
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, testSettings);
            library.BmsCharts = [zeta, alpha];
            library.DuplicateChartGroups = [new([.. library.BmsCharts], [tempRootPath])];
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            typeof(MainWindowViewModel).GetField("treeViewFilterTypeSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, MainViewUpdateMode.DuplicateFilterSelected);
            typeof(MainWindowViewModel).GetField("treeViewFilterParameterSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, DuplicateViewContext.ForRoot());
            viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
            var sortRefresh = new TaskCompletionSource<RegularChartSortRefreshRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<RegularChartSortRefreshRequestedEventArgs> sortRequested = (_, request) => sortRefresh.TrySetResult(request);
            PropertyChangedEventHandler presentationChanged = (_, change) =>
            {
                if (change.PropertyName is nameof(MainChartListViewModel.Rows) or nameof(MainChartListViewModel.SortParameters)
                    && viewModel.MainChartList.SortParameters == null
                    && viewModel.MainChartList.Rows is ChartListVirtualView { Count: 2 })
                {
                    published.TrySetResult();
                }
            };
            regularOwner.SortRefreshRequested += sortRequested;
            viewModel.MainChartList.PropertyChanged += presentationChanged;
            try
            {
                viewModel.MainChartList.RequestSort("UnsupportedColumn", ListSortDirection.Descending);
                RegularChartSortRefreshRequestedEventArgs sortRequest = await sortRefresh.Task;
                // 同じ正規要求の表示Taskと既存の実公開をそれぞれ回収します。
                await sortRequest.Presentation;
                await published.Task;

                Assert.AreEqual(MainChartListSortTarget.Regular, sortRequest.SortRequest.Target);
                Assert.IsNull(regularOwner.CaptureSortParameters());
                Assert.IsNull(viewModel.MainChartList.SortParameters);
                var view = viewModel.MainChartList.Rows as ChartListVirtualView;
                Assert.IsNotNull(view);
                Assert.AreEqual(2, view.Count);
                Assert.AreEqual("Alpha", ((LibraryChartRow)view[0]).Title);
                Assert.AreEqual("Zeta", ((LibraryChartRow)view[1]).Title);
            }
            finally
            {
                regularOwner.SortRefreshRequested -= sortRequested;
                viewModel.MainChartList.PropertyChanged -= presentationChanged;
            }
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class RecordingBmsPlayer : IBMSPlayer
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string ExePath { get; set; } = string.Empty;

        public TimeSpan Duration => TimeSpan.Zero;

        public TimeSpan CurrentTime { get; set; }

        public TimeSpan StopTime => TimeSpan.Zero;

        public TimeSpan BmsDuration => TimeSpan.Zero;

        public TimeSpan MusicDuration => TimeSpan.Zero;

        public int CurrentVoices => 0;

        public int MaxVoices => 0;

        public int NoteDensity => 0;

        public int NoteDensityMax => 0;

        public int Bpm => 0;

        public int MinBpm => 0;

        public int MaxBpm => 0;

        public double Total => 0.0;

        public int Combo => 0;

        public int Notes => 0;

        public int Measure => 0;

        public int LastMeasure => 0;

        internal string LastPlayedPath { get; private set; } = string.Empty;

        public void CloseProcess()
        {
        }

        public Task PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null)
        {
            LastPlayedPath = bmsFilePath;
            return Task.CompletedTask;
        }

        public void RestartPlayingBMSfile()
        {
        }

        public void PausePlayingBMSfileToggle()
        {
        }

        public void FastForwardPlayingBMSfileStart()
        {
        }

        public void FastForwardPlayingBMSfileEnd()
        {
        }

        public void FastBackwardPlayingBMSfileStart()
        {
        }

        public void FastBackwardPlayingBMSfileEnd()
        {
        }

        public void ShowInfo()
        {
        }

        public void ShowEffect()
        {
        }

        public void ChangePlayside()
        {
        }

        public void IncreaseHighSpeed()
        {
        }

        public void DecreaseHighSpeed()
        {
        }

        public void VolumeChanged(int volume)
        {
        }

        internal void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

}
