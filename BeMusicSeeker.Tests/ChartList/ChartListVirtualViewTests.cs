using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>仮想一覧の必要行だけの生成、形式共通投影と履歴終端の確定・公開を検証します。</summary>
/// <remarks>sort値と要求可否はWindowなしで確認します。実library・playerケースは操作接続と一時ファイル回収を担当し、compiled画面接続はMainWindow側が分担します。非並列ケースはSQLite/native初期化を共有するcompositionを通ります。</remarks>
[TestClass]
public sealed class ChartListVirtualViewTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
    /// <summary>件数照会では三行とも未生成とし、先頭アクセスだけが整列済みのAlpha一行を生成することで、件数取得時の全件生成を検出します。</summary>
    [TestMethod]
    public void Count_DoesNotRealizeRowsUntilIndexed()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);

        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(3, view.RowCount);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, getCreatedCount());

        var row = (LibraryChartRow)view[0];

        Assert.AreEqual("Alpha", row.Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    /// <summary>同じ添字を二回読むと同一行を返し、生成回数を一回に保つことで、スクロール時の再生成を検出します。</summary>
    [TestMethod]
    public void SameIndex_ReturnsCachedRow()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);

        object first = view[1];
        object second = view[1];

        Assert.AreSame(first, second);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    /// <summary>生成済み行と未知objectの位置検索では未訪問行を生成せず、既存位置と非存在を判定することを確認します。</summary>
    [TestMethod]
    public void IndexOf_DoesNotRealizeUnvisitedRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        object realized = view[1];

        Assert.AreEqual(1, view.IndexOf(realized));
        Assert.AreEqual(-1, view.IndexOf(new object()));
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());
    }

    /// <summary>所持tokenのないBMS metadataから表示文字列と数値level・mode・hashを投影し、保存行必須の表示経路へ戻る誤りを検出します。</summary>
    [TestMethod]
    public void LibraryChartRow_FromChartFile_UsesChartDomainFieldsWithoutStorageRow()
    {
        ChartFile chart = ChartFileProjection.FromBmsMetadata(
            @"D:\Charts\Root\alpha.bms",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('b', 64),
            "Alpha",
            "Artist",
            "Genre",
            "Root",
            "tag",
            12,
            7,
            null);

        var row = LibraryChartRow.FromChartFile(chart);

        Assert.IsNull(row.Chart.Token);
        Assert.AreEqual(chart.Path, row.Chart.Path);
        Assert.AreSame(chart.ChartInfo, row.Chart.ChartInfo);
        Assert.AreEqual("Alpha", row.Title);
        Assert.AreEqual("Artist", row.Artist);
        Assert.AreEqual("Genre", row.genre);
        Assert.AreEqual("Root", row.Folder);
        Assert.AreEqual(@"D:\Charts\Root\alpha.bms", row.path);
        Assert.AreEqual("tag", row.tag);
        Assert.AreEqual("12", row.Level);
        Assert.AreEqual(12d, row.level);
        Assert.AreEqual(7, row.mode);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", row.hash);
        Assert.AreEqual(new string('b', 64), row.sha256);
    }

    /// <summary>譜面の重複警告を表示行の警告と全文tooltipへ渡し、警告詳細が表示から落ちる誤りを検出します。</summary>
    [TestMethod]
    public void LibraryChartRow_ProjectsChartWarningsIntoTooltip()
    {
        ChartFile file = CreateFile(
            @"D:\Charts\Warning\alpha.bms",
            "Alpha",
            "Warning",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, "live warning")] };

        var row = LibraryChartRow.FromChartFile((file));

        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        StringAssert.Contains(row.WarningTooltipText, "live warning");
    }

    /// <summary>bmsonの過渡状態providerから導入先・代表タイトル・曖昧警告を部分一覧へ反映し、元譜面だけを表示して推定結果を失う誤りを検出します。</summary>
    [TestMethod]
    public void VirtualChartSubsetRow_PreservesOwnerBackedBmsonTransientState()
    {
        ChartFile bmson = CreateBmsonSong();
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            (bmson),
            "C:\\Installed\\Bmson",
            "Installed Bmson",
            "Installed Artist",
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        var sourceRow = ChartListSourceRow.FromChartFile(
            (bmson),
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot));

        LibraryChartRow row = new MainChartRowProjectionOwner().CreateSubsetRow(null, sourceRow, includeResourceHealth: false);

        Assert.AreEqual("C:\\Installed\\Bmson", row.Chart.InstallDestination);
        Assert.AreEqual("Installed Bmson", row.InstallDestinationTitle);
        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    /// <summary>BMS部分一覧でもproviderの導入先・代表タイトル・曖昧警告を反映し、形式によって過渡投影が欠ける誤りを検出します。</summary>
    [TestMethod]
    public void VirtualChartSubsetRow_OverlaysOwnerBackedBmsTransientStateWithoutWritingStorageRow()
    {
        ChartFile file = CreateFile(
            @"D:\Charts\InstallDestination\alpha.bms",
            "Alpha",
            "Install",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            (file),
            "C:\\Installed\\Bms",
            "Installed BMS",
            "Installed Artist",
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        var sourceRow = ChartListSourceRow.FromChartFile(
            (file),
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot));

        LibraryChartRow row = new MainChartRowProjectionOwner().CreateSubsetRow(null, sourceRow, includeResourceHealth: false);

        Assert.AreEqual("C:\\Installed\\Bms", row.Chart.InstallDestination);
        Assert.AreEqual("Installed BMS", row.InstallDestinationTitle);
        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    /// <summary>生成済み行を持つ仮想一覧で導入先を消去し、依存データ更新後に旧値を残さないことを確認します。</summary>
    [TestMethod]
    public void VirtualChartSubsetRow_RefreshesRealizedRowAfterTransientStateChanges()
    {
        ChartFile file = CreateFile(
            @"D:\Charts\InstallDestination\refresh.bms",
            "Refresh",
            "Install",
            hash: "cccccccccccccccccccccccccccccccc");
        var projectionOwner = new MainChartRowProjectionOwner();
        ChartFile initialChart = ChartFileProjection.WithPackageState(
            (file),
            @"C:\Installed\Initial",
            "Initial",
            "Artist",
            []);
        projectionOwner.UpdateTransientStates([initialChart], forceInstallDestinationProjection: true);
        List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildStandardLibraryRows(
            [(file)],
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: projectionOwner.GetTransientState);
        Assert.IsTrue(ChartListOrder.TryCreate(
            sourceRows,
            nameof(LibraryChartRow.instl_dst),
            ListSortDirection.Ascending,
            out ChartListOrder order));
        var view = new ChartListVirtualView(
            sourceRows,
            order,
            row => projectionOwner.CreateSubsetRow(null, row, includeResourceHealth: false));

        var realizedRow = (LibraryChartRow)view[0];
        Assert.AreEqual(@"C:\Installed\Initial", realizedRow.instl_dst);

        projectionOwner.UpdateTransientStates(
            [(file)],
            forceInstallDestinationProjection: true);
        realizedRow.RefreshDisplayForDataDependency(MainViewDataDependency.InstallDestination);

        Assert.AreEqual(string.Empty, realizedRow.instl_dst);
    }

    /// <summary>同じhashでもpathの大文字小文字違いは別対象とし、完全一致pathではhashの表記違いを受理して、別譜面へ過渡状態を適用する誤りを検出します。</summary>
    [TestMethod]
    public void RowProjectionTransientState_UsesExactPathAndCaseInsensitiveHash()
    {
        const string path = @"D:\Charts\InstallDestination\Chart.bms";
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ChartFile sourceOwner = CreateFile(path, "Source", "InstallDestination", hash: hash);
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            (sourceOwner),
            @"C:\Installed\Exact",
            "Installed",
            string.Empty,
            []);
        var projectionOwner = new MainChartRowProjectionOwner();
        projectionOwner.UpdateTransientStates([statefulChart], forceInstallDestinationProjection: true);

        ChartFile exactOwner = CreateFile(path, "Exact", "InstallDestination", hash: hash.ToUpperInvariant());
        ChartFile aliasOwner = CreateFile(path.ToLowerInvariant(), "Alias", "InstallDestination", hash: hash);
        ChartFileTransientState exactState = projectionOwner.GetTransientState(
            (exactOwner),
            includeWarningSnapshot: false);
        ChartFileTransientState aliasState = projectionOwner.GetTransientState(
            (aliasOwner),
            includeWarningSnapshot: false);

        Assert.AreEqual(@"C:\Installed\Exact", exactState.InstallDestination);
        Assert.IsFalse(aliasState.HasInstallDestinationState);
    }

    /// <summary>スコアproviderからclear・rank・得点・率・minBPを部分一覧へ反映し、owner由来の更新を落とす誤りを検出します。</summary>
    [TestMethod]
    public void VirtualChartSubsetRow_UsesScoreProviderForOwnerBackedBmsProjection()
    {
        ChartFile file = CreateFile(
            @"D:\Charts\Score\alpha.bms",
            "Alpha",
            "Score",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var score = new ChartScoreSnapshot(
            ClearType.HARD,
            RankType.AA,
            score: 1800,
            rate: 90,
            totalNotes: 1000,
            minBp: 3,
            maxCombo: 987);
        var sourceRow = ChartListSourceRow.FromChartFile(
            (file),
            ChartListSourceProjectionMode.OwnerBacked,
            scoreSnapshotVersionProvider: () => 1,
            scoreSnapshotProjectionProvider: row => string.Equals(row.Path, file.Path, StringComparison.OrdinalIgnoreCase) ? score : null);

        LibraryChartRow row = new MainChartRowProjectionOwner().CreateSubsetRow(null, sourceRow, includeResourceHealth: false);

        Assert.AreEqual(ClearType.HARD, row.clear);
        Assert.AreEqual(RankType.AA, row.rank);
        Assert.AreEqual(1800, row.score);
        Assert.AreEqual(90, row.rate);
        Assert.AreEqual(3, row.minbp);
    }

    /// <summary>最新推定投影に警告がなければ旧曖昧警告だけを消し、重複警告を保持して無関係な警告まで消す誤りを検出します。</summary>
    [TestMethod]
    public void ChartListSourceRow_BmsTransientInstallEstimationProjectionClearsStaleSourceWarning()
    {
        ChartFile file = CreateFile(
            @"D:\Charts\InstallDestination\clear.bms",
            "Clear",
            "Install",
            hash: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile sourceWithStaleWarning = ChartFileProjection.WithWarnings(
            (file),
            [
                ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "stale warning"),
                ChartWarning.Create(ChartWarningKind.DuplicateChart, "projection-only duplicate warning")
            ]);
        ChartFile clearedProjection = ChartFileProjection.WithWarnings(
            (file),
            []);

        var sourceRow = ChartListSourceRow.FromChartFile(
            sourceWithStaleWarning,
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromInstallDestinationState(
                clearedProjection,
                includeWarningSnapshot,
                forceInstallDestinationProjection: true,
                forceWarningProjection: true));

        Assert.IsFalse(sourceRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        Assert.IsTrue(sourceRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
    }

    /// <summary>導入先に同名ファイルがある実再生では改名先をplayerへ渡し、元譜面と衝突ファイルを保持して一時コピーを回収することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task PlaybackPanel_StartAtIndexUsesChartInstallDestinationWhenTemporaryRenameChangesPath()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayStart_" + Guid.NewGuid().ToString("N"));
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
                defaultBmsPlayerFactory: () => player,
                settingsEditSession: new NoOpSettingsEditSession(testSettings),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            var viewModel = new MainWindowViewModel(composition, composition);
            using SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
            using RegularChartListOwner rowsLifetime = viewModel.RegularChartList;
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, testSettings);
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

    /// <summary>保存行のない譜面でも解析情報のlevel・difficulty・BPM・TOTALをsortキーへ投影し、解析済み値を欠損扱いにする誤りを検出します。</summary>
    [TestMethod]
    public void ChartListSourceRow_FromChartFile_UsesProjectionChartInfoWithoutStorageRow()
    {
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(
            new string('c', 64),
            "cccccccccccccccccccccccccccccccc",
            level: 12,
            difficulty: 4,
            mainBpm: 180,
            total: 360);
        ChartFile chart = ChartFileProjection.FromBmsMetadata(
            @"D:\Charts\Projection\gamma.bms",
            chartInfo.md5,
            chartInfo.sha256,
            "Gamma",
            "Artist",
            "Genre",
            "Projection",
            string.Empty,
            12,
            7,
            chartInfo);

        ChartListSourceRow row = ChartListSourceRow.BuildStandardLibraryRows([chart]).Single();

        Assert.IsNull(row.Chart.Token);
        Assert.AreEqual(chart.Path, row.Chart.Path);
        Assert.AreSame(chartInfo, row.Chart.ChartInfo);
        Assert.AreEqual(12, row.ChartLevelSortKey);
        Assert.AreEqual(4, row.ChartDifficultySortKey);
        Assert.AreEqual(180, row.ChartMainBpmSortKey);
        Assert.AreEqual(360, row.ChartTotalSortKey);
    }

    /// <summary>providerを省略したsourceの明示済みスコアを表示行へ保持し、既存clearと得点を空投影で上書きする誤りを検出します。</summary>
    [TestMethod]
    public void ChartListSourceRow_FromChartFile_PreservesExplicitSourceScoreWithoutProvider()
    {
        ChartFile file = CreateFile(
            @"folder-a\score.bms",
            "Score",
            "folder-a",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            scoreSeed: 2);
        ChartFile chart = (file);

        var row = ChartListSourceRow.FromChartFile(chart, ChartListSourceProjectionMode.OwnerBacked);

        Assert.AreEqual(chart.Score.Clear, row.Clear);
        Assert.AreEqual(chart.Score.Score, row.Score);
        Assert.AreEqual(chart.Score.Clear, row.Chart.Score.Clear);
        Assert.AreEqual(chart.Score.Score, row.Chart.Score.Score);
    }

    /// <summary>添字アクセスも列挙も禁止したsourceで仮想一覧を構築し、件数だけで生成してfolder数を全走査しないことを確認します。</summary>
    [TestMethod]
    public void Constructor_DoesNotReadFolderCountFromSourceRows()
    {
        ChartFile file = (ChartTestValues.Empty() with { Path = @"folder-a\alpha.bms", Title = "Alpha", Md5 = new string('a', 32) });
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows([file]);
        var order = ChartListOrder.CreateTitleAscending(sourceRows);

        var view = new ChartListVirtualView(new ReadForbiddenSourceRows(sourceRows.Count), order, MaterializeSourceRow);

        Assert.AreEqual(1, view.Count);
        Assert.AreEqual(-1, view.DistinctFolderCount);
    }

    /// <summary>未知folder数は譜面数だけ、既知数は両方を表示し、未知数を確定値として案内する誤りを検出します。</summary>
    [TestMethod]
    public void SummaryFormatter_OmitsUnknownFolderCount()
    {
        var mainChartList = new MainChartListViewModel();
        mainChartList.UpdateSummaryText(3, -1);
        string unknown = mainChartList.SummaryText;
        mainChartList.UpdateSummaryText(3, 2);
        string known = mainChartList.SummaryText;

        StringAssert.StartsWith(unknown, "[3");
        Assert.IsFalse(unknown.Contains("/"));
        StringAssert.Contains(known, "/ 2");
    }

    /// <summary>2譜面・2folderの行置換をsummaryとbinding通知へ反映し、直接SetRowsの件数表示や通知が欠落する誤りを検出します。</summary>
    [TestMethod]
    public void MainChartList_SetRowsUpdatesSummaryDirectly()
    {
        var mainChartList = new MainChartListViewModel();
        List<LibraryChartRow> rows = CreateSummaryRows();
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        mainChartList.SetRows(rows, updateSummary: true);

        Assert.AreSame(rows, mainChartList.Rows);
        StringAssert.StartsWith(mainChartList.SummaryText, "[2");
        StringAssert.Contains(mainChartList.SummaryText, "/ 2");
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.Rows));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SummaryText));
    }

    /// <summary>選択位置の更新をbinding通知へ渡し、選択状態が描画側へ届かない誤りを検出します。</summary>
    [TestMethod]
    public void MainChartList_SelectedIndexRaisesPropertyChanged()
    {
        var mainChartList = new MainChartListViewModel();
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        mainChartList.SelectedIndex = 3;

        Assert.AreEqual(3, mainChartList.SelectedIndex);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SelectedIndex));
    }

    /// <summary>列設定の変更で列とdrag種別を再通知し、表示と操作入口が旧設定に留まる誤りを検出します。</summary>
    [TestMethod]
    public void MainChartList_ColumnsSettingsRaisesOwnedBindingNotifications()
    {
        var mainChartList = new MainChartListViewModel();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        mainChartList.ColumnsSettings = settings;

        Assert.AreSame(settings, mainChartList.ColumnsSettings);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.ColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.RowDragKind));
    }

    /// <summary>履歴sort表示後の別列要求にも履歴配送先を保持し、完了前に表示sortを勝手に更新しないことを確認します。</summary>
    [TestMethod]
    public void MainChartList_SortPresentationOwnsBindingAndRequestScope()
    {
        var mainChartList = new MainChartListViewModel();
        var sort = new MainChartListSortPresentation(
            nameof(PlayHistoryRow.PlayedAt),
            ListSortDirection.Descending);
        MainChartListSortRequestedEventArgs? request = null;
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);
        mainChartList.SortRequested += (_, e) => request = e;

        mainChartList.SetSortPresentation(sort, MainChartListSortTarget.PlayHistory);
        mainChartList.RequestSort(nameof(PlayHistoryRow.Title), ListSortDirection.Ascending);

        Assert.AreSame(sort, mainChartList.SortParameters);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SortParameters));
        Assert.IsNotNull(request);
        Assert.AreEqual(nameof(PlayHistoryRow.Title), request.ColumnName);
        Assert.AreEqual(ListSortDirection.Ascending, request.Direction);
        Assert.AreEqual(MainChartListSortTarget.PlayHistory, request.Target);
    }

    /// <summary>受理した編集の開始から終了まで同じcontextと編集結果を配送し、終了時のセッション取り違えを検出します。</summary>
    [TestMethod]
    public void MainChartList_CellEditSessionKeepsBeginningScopeUntilEnded()
    {
        var mainChartList = new MainChartListViewModel();
        mainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
        object row = new();
        MainChartListCellEditContext? started = null;
        MainChartListCellEditEndedEventArgs? ended = null;
        mainChartList.CellEditBeginningRequested += (_, request) => request.Accepted = true;
        mainChartList.CellEditStarted += (_, context) => started = context;
        mainChartList.CellEditEndedRequested += (_, request) => ended = request;

        Assert.IsTrue(mainChartList.TryBeginCellEdit(row, nameof(LibraryChartRow.Folder)));
        mainChartList.NotifyCellEditStarted(row, nameof(LibraryChartRow.Folder));
        mainChartList.RequestCellEditEnded(row, nameof(LibraryChartRow.Folder), "renamed", commit: true);

        Assert.IsNotNull(started);
        Assert.AreSame(row, started!.Row);
        Assert.IsNotNull(ended);
        Assert.AreSame(started, ended!.Context);
        Assert.AreEqual(ChartOperationSourceScope.Library, ended.Context.SourceScope);
        Assert.AreEqual(MainViewOperationSection.Library, ended.Context.OperationSection);
        Assert.AreEqual("renamed", ended.Text);
        Assert.IsTrue(ended.Commit);
    }

    /// <summary>実開始前に捨てた編集候補はownerセッションを開かず、次の実開始だけを配送して未開始編集の混入を検出します。</summary>
    [TestMethod]
    public void MainChartList_CellEditBeginningWithoutStartedDoesNotOpenOwnerSession()
    {
        var mainChartList = new MainChartListViewModel();
        object abandonedRow = new();
        object activeRow = new();
        MainChartListCellEditContext? started = null;
        mainChartList.CellEditBeginningRequested += (_, request) => request.Accepted = true;
        mainChartList.CellEditStarted += (_, context) => started = context;

        Assert.IsTrue(mainChartList.TryBeginCellEdit(abandonedRow, nameof(LibraryChartRow.Folder)));
        Assert.IsTrue(mainChartList.TryBeginCellEdit(activeRow, nameof(LibraryChartRow.Folder)));
        mainChartList.NotifyCellEditStarted(activeRow, nameof(LibraryChartRow.Folder));

        Assert.IsNotNull(started);
        Assert.AreSame(activeRow, started!.Row);
    }

    /// <summary>保留一覧で受理した編集中にmodeを変えても入口で捕捉したscopeを保持し、現在画面で対象を再分類する誤りを検出します。</summary>
    [TestMethod]
    public void MainChartList_OperationContextMapsModeAndCapturesCellEditSnapshot()
    {
        var mainChartList = new MainChartListViewModel();
        MainChartListCellEditContext? captured = null;
        mainChartList.CellEditBeginningRequested += (_, request) =>
        {
            captured = request.Context;
            request.Accepted = true;
        };

        mainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);

        Assert.AreEqual(MainViewOperationSection.InstallPending, mainChartList.CurrentOperationContext.OperationSection);
        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, mainChartList.CurrentOperationContext.SourceScope);
        object row = new();
        Assert.IsTrue(mainChartList.TryBeginCellEdit(row, nameof(LibraryChartRow.Folder)));

        mainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

        Assert.IsNotNull(captured);
        Assert.AreSame(row, captured!.Row);
        Assert.AreEqual(MainViewOperationSection.InstallPending, captured.OperationSection);
        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, captured.SourceScope);
        Assert.AreEqual(MainViewOperationSection.Library, mainChartList.CurrentOperationContext.OperationSection);
        Assert.AreEqual(ChartOperationSourceScope.Library, mainChartList.CurrentOperationContext.SourceScope);
    }

    /// <summary>表示更新要求をownerイベントへ一回配送し、更新通知が失われる誤りを検出します。</summary>
    [TestMethod]
    public void MainChartList_DisplayRefreshRaisesDirectOwnerEvent()
    {
        var mainChartList = new MainChartListViewModel();
        int raisedCount = 0;
        mainChartList.DisplayRefreshRequested += (_, _) => raisedCount++;

        mainChartList.RequestDisplayRefresh();

        Assert.AreEqual(1, raisedCount);
    }

    /// <summary>注入dispatcherの処理までsortと表示更新を保留し、実行後に一回反映して呼出スレッドで先に更新する誤りを検出します。</summary>
    [TestMethod]
    public void MainChartList_PresentationChangesUseInjectedDispatcher()
    {
        Action? pendingAction = null;
        var mainChartList = new MainChartListViewModel(action => pendingAction = action);
        var sort = new MainChartListSortPresentation(nameof(ChartFile.Title), ListSortDirection.Ascending);
        int refreshCount = 0;
        mainChartList.DisplayRefreshRequested += (_, _) => refreshCount++;

        mainChartList.SetSortPresentation(sort, MainChartListSortTarget.Regular);

        Assert.IsNull(mainChartList.SortParameters);
        Assert.IsNotNull(pendingAction);
        pendingAction!();
        Assert.AreSame(sort, mainChartList.SortParameters);

        pendingAction = null;
        mainChartList.RequestDisplayRefresh();
        Assert.AreEqual(0, refreshCount);
        Assert.IsNotNull(pendingAction);
        pendingAction!();
        Assert.AreEqual(1, refreshCount);
    }

    private static List<LibraryChartRow> CreateSummaryRows()
    {
        return
        [
            LibraryChartRow.FromChartFile((CreateFile(@"D:\Charts\A\alpha.bms", "Alpha", @"D:\Charts\A"))),
            LibraryChartRow.FromChartFile((CreateFile(@"D:\Charts\B\bravo.bms", "Bravo", @"D:\Charts\B"))),
        ];
    }

    /// <summary>行置換準備後に行・列・選択・summaryを確定し、全binding通知が新状態を読むことで途中状態の公開を検出します。</summary>
    [TestMethod]
    public void MainChartList_ApplyRowsCommitsOwnedStateBeforeNotifications()
    {
        ChartListVirtualView view = CreateView(out _, distinctFolderCount: 2);
        var mainChartList = new MainChartListViewModel
        {
            Rows = new List<object>(),
            SelectedIndex = 4,
            SummaryText = "old"
        };
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var calls = new List<string>();
        mainChartList.RowsReplacing += (_, _) => calls.Add("prepare");
        mainChartList.PropertyChanged += (_, e) =>
        {
            calls.Add(e.PropertyName!);
            Assert.AreSame(view, mainChartList.Rows);
            Assert.AreSame(settings, mainChartList.ColumnsSettings);
            Assert.AreEqual(-1, mainChartList.SelectedIndex);
            StringAssert.StartsWith(mainChartList.SummaryText, "[3");
        };
        var stopwatch = Stopwatch.StartNew();
        long stageStartMs = stopwatch.ElapsedMilliseconds;

        MainChartListRowsApplyResult result = mainChartList.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = view,
            ColumnsSettings = settings,
            SelectionPolicy = MainChartListSelectionPolicy.Reset,
            Summary = MainChartListSummaryUpdate.NormalCounts(view.Count, 2),
            ColumnSettingReuse = true,
            ColumnPreparationMs = 0,
            TerminalStageStartMs = stageStartMs,
            Stopwatch = stopwatch
        });

        Assert.AreEqual("prepare", calls[0]);
        Assert.IsTrue(
            calls.IndexOf(nameof(MainChartListViewModel.Rows))
            < calls.IndexOf(nameof(MainChartListViewModel.ColumnsSettings)));
        CollectionAssert.Contains(calls, nameof(MainChartListViewModel.ColumnsSettings));
        CollectionAssert.Contains(calls, nameof(MainChartListViewModel.Rows));
        CollectionAssert.Contains(calls, nameof(MainChartListViewModel.SummaryText));
        CollectionAssert.Contains(calls, nameof(MainChartListViewModel.SelectedIndex));
        Assert.IsTrue(result.ColumnSettingReuse);
        Assert.IsTrue(result.PrepareSwapMs >= 0);
        Assert.IsTrue(result.ColumnSettingMs >= 0);
        Assert.IsTrue(result.SetViewMs >= 0);
        Assert.IsTrue(result.ColumnStageMs >= 0);
    }

    /// <summary>列通知例外後も行と関連状態の通知を続け、新summaryとcontextを保持して後続通知の省略を検出します。</summary>
    [TestMethod]
    public void MainChartList_ColumnNotificationFailureDoesNotSuppressRowsOrRelatedState()
    {
        var mainChartList = new MainChartListViewModel
        {
            Rows = new List<object>(),
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD),
            SummaryText = "old"
        };
        var calls = new List<string>();
        int operationContextNotifications = 0;
        mainChartList.PropertyChanged += (_, e) =>
        {
            calls.Add(e.PropertyName!);
            if (e.PropertyName == nameof(MainChartListViewModel.ColumnsSettings))
            {
                throw new InvalidOperationException("column binding failed");
            }
        };
        mainChartList.OperationContextChanged += (_, _) => operationContextNotifications++;

        Assert.ThrowsException<AggregateException>(() => mainChartList.ApplyRows(
            new MainChartListRowsApplyRequest
            {
                Rows = new List<object> { new() },
                ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL),
                SelectionPolicy = MainChartListSelectionPolicy.Reset,
                Summary = MainChartListSummaryUpdate.Explicit("new"),
                OperationContextMode = MainViewUpdateMode.PendingInstallFolderSelected,
                Stopwatch = Stopwatch.StartNew()
            }));

        Assert.AreEqual(nameof(MainChartListViewModel.Rows), calls[0]);
        CollectionAssert.Contains(calls, nameof(MainChartListViewModel.ColumnsSettings));
        CollectionAssert.Contains(calls, nameof(MainChartListViewModel.RowDragKind));
        CollectionAssert.Contains(calls, nameof(MainChartListViewModel.SummaryText));
        Assert.AreEqual(1, operationContextNotifications);
        Assert.AreEqual("new", mainChartList.SummaryText);
        Assert.AreEqual(
            MainViewOperationSection.InstallPending,
            mainChartList.CurrentOperationContext.OperationSection);
    }

    /// <summary>同じ行参照では置換準備と行通知を省き、明示summaryだけを更新して不要置換とsummary更新漏れを検出します。</summary>
    [TestMethod]
    public void MainChartList_ApplyRowsSameReferenceSkipsPrepareAndUpdatesExplicitSummary()
    {
        ChartListVirtualView view = CreateView(out _, distinctFolderCount: 2);
        var mainChartList = new MainChartListViewModel
        {
            Rows = view,
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD)
        };
        int prepareCount = 0;
        int rowsNotificationCount = 0;
        mainChartList.RowsReplacing += (_, _) => prepareCount++;
        mainChartList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                rowsNotificationCount++;
            }
        };

        mainChartList.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = view,
            ColumnsSettings = mainChartList.ColumnsSettings,
            SelectionPolicy = MainChartListSelectionPolicy.Preserve,
            Summary = MainChartListSummaryUpdate.Explicit("play history summary"),
            TerminalStageStartMs = 0,
            Stopwatch = Stopwatch.StartNew()
        });

        Assert.AreEqual(0, prepareCount);
        Assert.AreEqual(0, rowsNotificationCount);
        Assert.AreEqual("play history summary", mainChartList.SummaryText);
    }

    /// <summary>旧行Dispose失敗では列・選択・summaryも旧状態に保ち、通知前の部分確定を検出します。</summary>
    [TestMethod]
    public void MainChartList_ApplyRowsDisposeFailureDoesNotCommitOtherState()
    {
        var oldRows = new List<object> { new ThrowingDisposable() };
        var nextRows = new List<object>();
        var oldSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var nextSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
        var mainChartList = new MainChartListViewModel
        {
            Rows = oldRows,
            ColumnsSettings = oldSettings,
            SelectedIndex = 2,
            SummaryText = "old"
        };
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        Assert.ThrowsException<InvalidOperationException>(() => mainChartList.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = nextRows,
            ColumnsSettings = nextSettings,
            SelectionPolicy = MainChartListSelectionPolicy.Reset,
            Summary = MainChartListSummaryUpdate.Explicit("new"),
            TerminalStageStartMs = 0,
            Stopwatch = Stopwatch.StartNew()
        }));

        Assert.AreSame(oldRows, mainChartList.Rows);
        Assert.AreSame(oldSettings, mainChartList.ColumnsSettings);
        Assert.AreEqual(2, mainChartList.SelectedIndex);
        Assert.AreEqual("old", mainChartList.SummaryText);
        Assert.AreEqual(0, propertyNames.Count);
    }

    /// <summary>準備済み行置換を取消すると取消通知だけを出して旧行を保持し、未確定候補を公開する誤りを検出します。</summary>
    [TestMethod]
    public void MainChartList_PreparedRowsCanCancelWithoutCommit()
    {
        var oldRows = new List<object>();
        var nextRows = new List<object>();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var mainChartList = new MainChartListViewModel { Rows = oldRows, ColumnsSettings = settings };
        int preparingCount = 0;
        int canceledCount = 0;
        mainChartList.RowsReplacing += (_, _) => preparingCount++;
        mainChartList.RowsReplacementCanceled += (_, _) => canceledCount++;

        MainChartListRowsTransition transition = mainChartList.PrepareRowsTransition(
            new MainChartListRowsApplyRequest
            {
                Rows = nextRows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.Preserve(),
                TerminalStageStartMs = 0,
                Stopwatch = Stopwatch.StartNew()
            });
        transition.Cancel();

        Assert.AreEqual(1, preparingCount);
        Assert.AreEqual(1, canceledCount);
        Assert.AreSame(oldRows, mainChartList.Rows);
    }

    /// <summary>行確定後も公開まではbinding通知を出さず、Complete後に行とsummaryを通知して確定と公開の逆転を検出します。</summary>
    [TestMethod]
    public void MainChartList_CommitPreparedRowsDefersNotificationsUntilPublish()
    {
        var nextRows = new List<object>();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var mainChartList = new MainChartListViewModel
        {
            Rows = new List<object>(),
            ColumnsSettings = settings
        };
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);
        MainChartListRowsTransition transition = mainChartList.PrepareRowsTransition(
            new MainChartListRowsApplyRequest
            {
                Rows = nextRows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Reset,
                Summary = MainChartListSummaryUpdate.Explicit("committed"),
                TerminalStageStartMs = 0,
                Stopwatch = Stopwatch.StartNew()
            });
        transition.CommitOwnership();
        Assert.AreSame(nextRows, mainChartList.Rows);
        Assert.AreEqual("committed", mainChartList.SummaryText);
        Assert.AreEqual(0, propertyNames.Count);
        transition.Complete();

        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.Rows));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SummaryText));
    }

    /// <summary>行置換の通知中は旧行を生かし、その後一回だけDisposeして、binding参照中の先行解放を検出します。</summary>
    [TestMethod]
    public void MainChartList_RowsTransitionDisposesPreviousRowsAfterBindingNotification()
    {
        var previousRow = new CountingDisposable();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var mainChartList = new MainChartListViewModel
        {
            Rows = new List<object> { previousRow },
            ColumnsSettings = settings
        };
        int disposeCountDuringRowsNotification = -1;
        mainChartList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                disposeCountDuringRowsNotification = previousRow.DisposeCount;
            }
        };
        MainChartListRowsTransition transition = mainChartList.PrepareRowsTransition(
            new MainChartListRowsApplyRequest
            {
                Rows = new List<object>(),
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Reset,
                Summary = MainChartListSummaryUpdate.Preserve(),
                TerminalStageStartMs = 0,
                Stopwatch = Stopwatch.StartNew()
            });

        transition.CommitOwnership();
        transition.Complete();

        Assert.AreEqual(0, disposeCountDuringRowsNotification);
        Assert.AreEqual(1, previousRow.DisposeCount);
    }

    /// <summary>取消後の確定、確定前の完了、確定・完了の重複と完了後取消を拒否し、行置換の二重確定・公開を検出します。</summary>
    [TestMethod]
    public void MainChartList_RowsTransitionRejectsInvalidLifecycleOperations()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var mainChartList = new MainChartListViewModel
        {
            Rows = new List<object>(),
            ColumnsSettings = settings
        };
        MainChartListRowsApplyRequest CreateRequest() => new()
        {
            Rows = new List<object>(),
            ColumnsSettings = settings,
            SelectionPolicy = MainChartListSelectionPolicy.Reset,
            Summary = MainChartListSummaryUpdate.Preserve(),
            TerminalStageStartMs = 0,
            Stopwatch = Stopwatch.StartNew()
        };

        MainChartListRowsTransition canceled = mainChartList.PrepareRowsTransition(CreateRequest());
        canceled.Cancel();
        Assert.ThrowsException<InvalidOperationException>(canceled.Cancel);
        Assert.ThrowsException<InvalidOperationException>(canceled.CommitOwnership);

        MainChartListRowsTransition committed = mainChartList.PrepareRowsTransition(CreateRequest());
        Assert.ThrowsException<InvalidOperationException>(() => committed.Complete());
        committed.CommitOwnership();
        Assert.ThrowsException<InvalidOperationException>(committed.CommitOwnership);
        committed.Complete();
        Assert.ThrowsException<InvalidOperationException>(() => committed.Complete());
        Assert.ThrowsException<InvalidOperationException>(committed.Cancel);
    }

    /// <summary>確定後の行通知失敗では候補行を保持し、取消でなく確定後失敗の後片付けへ配送することを確認します。</summary>
    [TestMethod]
    public void MainChartList_PublishFailureUsesPostCommitCleanupEvent()
    {
        var mainChartList = new MainChartListViewModel
        {
            Rows = new List<object>(),
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD)
        };
        var candidateRows = new List<object> { new object() };
        int canceledCount = 0;
        int publishFailedCount = 0;
        mainChartList.RowsReplacementCanceled += (_, _) => canceledCount++;
        mainChartList.RowsReplacementPublishFailed += (_, _) => publishFailedCount++;
        mainChartList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                throw new InvalidOperationException("binding publish failed");
            }
        };

        Assert.ThrowsException<AggregateException>(() => mainChartList.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = candidateRows,
            ColumnsSettings = mainChartList.ColumnsSettings,
            SelectionPolicy = MainChartListSelectionPolicy.Reset,
            Summary = MainChartListSummaryUpdate.Explicit("committed"),
            TerminalStageStartMs = 0,
            Stopwatch = Stopwatch.StartNew()
        }));

        Assert.AreSame(candidateRows, mainChartList.Rows);
        Assert.AreEqual(0, canceledCount);
        Assert.AreEqual(1, publishFailedCount);
    }

    /// <summary>行準備中に別workerが履歴世代を更新でき、旧世代終端を取消して旧表示を保持することで、lock保持と古い結果の採用を検出します。</summary>
    [TestMethod]
    public void PlayHistoryTerminal_PreparationRunsOutsideFreshnessLockAndCancelsStaleCommit()
    {
        PlayHistoryTerminalHarness owner = CreatePlayHistoryTerminalHarness(out PlayHistoryPresentationState state, out MainChartListViewModel table);
        var oldRows = new List<object>();
        var candidateRows = new List<object> { new object() };
        table.Rows = oldRows;
        state.RequestGeneration = 1;
        int canceledCount = 0;
        table.RowsReplacementCanceled += (_, _) => canceledCount++;
        table.RowsReplacing += (_, _) =>
        {
            var invalidateTask = Task.Run(() =>
            {
                lock (state.SyncRoot)
                {
                    state.RequestGeneration++;
                }
            });
            Assert.IsTrue(invalidateTask.Wait(TimeSpan.FromSeconds(5)), "RowsReplacing must not run while the play-history freshness lock is held.");
        };

        PlayHistoryTerminalCommitResult result = owner.TryApply(CreatePlayHistoryTerminalRequest(candidateRows, "candidate summary", requestId: 1));

        Assert.IsFalse(result.Applied);
        Assert.AreSame(oldRows, table.Rows);
        Assert.AreNotEqual("candidate summary", table.SummaryText);
        Assert.AreEqual(1, canceledCount);
    }

    /// <summary>callerの可変行を後から変更しても捕捉済み入力を使い、履歴行・列・summaryと関連状態をlock外で終端へ確定することを確認します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_ApplySortedRowsBuildsPresentationAndCommitsTerminalState()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        var table = new MainChartListViewModel { Rows = new List<object>() };
        var detailBuildState = new PlaylistDetailBuildState();
        bool detailLockHeldDuringRelatedOwnerCommit = false;
        bool playHistoryLockHeldDuringRelatedOwnerCommit = false;
        table.AppliedColumnModeCommitted += _ =>
        {
            detailLockHeldDuringRelatedOwnerCommit =
                Monitor.IsEntered(detailBuildState.SyncRoot);
            playHistoryLockHeldDuringRelatedOwnerCommit =
                Monitor.IsEntered(workflowOwner.PresentationState.SyncRoot);
        };
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            detailBuildState,
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        PlayHistoryViewState state = CreateEmptyPlayHistoryViewState(activeRequest.RequestId);
        SelectSummaryFilter(workflowOwner, "exhard");
        Assert.IsTrue(workflowOwner.SnapshotSummaryFilterKeys().Contains("exhard"));
        var callerRows = new List<PlayHistoryRow>();
        var applyRequest = new PlayHistorySortedRowsApplyRequest(
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected,
            state,
            callerRows,
            sortSucceeded: true,
            sortProfile: "default",
            currentKeywordFilter: string.Empty,
            PlayHistoryDisplayTargetItem.All,
            archivePeriodTree: null,
            workspace.PrepareDetailSourceRetirementWithoutPublishing());
        callerRows.Add(null!);

        PlayHistorySortedRowsApplyResult result = workflowOwner.ApplySortedRows(
            applyRequest,
            Stopwatch.StartNew(),
            table,
            workspace);

        Assert.AreEqual(PlayHistorySortedRowsApplyStatus.Applied, result.Status);
        Assert.AreEqual(0, result.ViewCount);
        Assert.IsNotNull(result.TerminalCommit);
        Assert.IsTrue(result.TerminalCommit.Applied);
        Assert.AreSame(state, workflowOwner.PresentationState.CurrentView);
        Assert.IsInstanceOfType<PlayHistoryVirtualView>(table.Rows);
        Assert.AreEqual(-1, table.SelectedIndex);
        Assert.IsFalse(string.IsNullOrWhiteSpace(table.SummaryText));
        Assert.IsFalse(workflowOwner.SnapshotSummaryFilterKeys().Contains("exhard"));
        Assert.IsFalse(detailLockHeldDuringRelatedOwnerCommit);
        Assert.IsFalse(playHistoryLockHeldDuringRelatedOwnerCommit);
    }

    /// <summary>構築後にsort要求を更新すると最新sort世代で結果を作り直し、古いsort状態を採用する誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_ApplySortedRowsResortsWhenSortSnapshotBecomesStale()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        PlayHistoryViewState state = CreateEmptyPlayHistoryViewState(activeRequest.RequestId);
        var table = new MainChartListViewModel { Rows = new List<object>() };
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        workflowOwner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.Title),
            Direction = ListSortDirection.Descending
        });

        PlayHistorySortedRowsApplyResult result = workflowOwner.ApplySortedRows(
            new PlayHistorySortedRowsApplyRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.PlayHistorySelected,
                state,
                sortedRows: [],
                sortSucceeded: true,
                sortProfile: "stale",
                currentKeywordFilter: string.Empty,
                PlayHistoryDisplayTargetItem.All,
                archivePeriodTree: null,
                workspace.PrepareDetailSourceRetirementWithoutPublishing()),
            Stopwatch.StartNew(),
            table,
            workspace);

        Assert.AreEqual(PlayHistorySortedRowsApplyStatus.Applied, result.Status);
        Assert.AreEqual(1L, workflowOwner.PresentationState.CurrentView.SortSnapshot.Revision);
        Assert.AreNotEqual("stale", result.SortProfile);
    }

    /// <summary>表示対象変更後の旧sort結果は再更新を要求し、旧行とsummaryフィルタを保持して別対象の上書きを検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_ApplySortedRowsReturnsQueuedDisplayTargetStaleWithoutCommittingRows()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        PlayHistoryViewState state = CreateEmptyPlayHistoryViewState(activeRequest.RequestId);
        var oldRows = new List<object>();
        var table = new MainChartListViewModel { Rows = oldRows };
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var changedTarget = PlayHistoryDisplayTargetItem.FromPlaylist(new BMSTable { name = "Changed" });
        SelectSummaryFilter(workflowOwner, "exhard");

        PlayHistorySortedRowsApplyResult result = workflowOwner.ApplySortedRows(
            new PlayHistorySortedRowsApplyRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.PlayHistorySelected,
                state,
                sortedRows: [],
                sortSucceeded: true,
                sortProfile: "default",
                currentKeywordFilter: string.Empty,
                changedTarget,
                archivePeriodTree: null),
            Stopwatch.StartNew(),
            table,
            workspace);

        Assert.AreEqual(PlayHistorySortedRowsApplyStatus.DisplayTargetStale, result.Status);
        Assert.IsTrue(result.QueueRefresh);
        Assert.AreSame(oldRows, table.Rows);
        Assert.IsNull(result.TerminalCommit);
        Assert.IsTrue(workflowOwner.SnapshotSummaryFilterKeys().Contains("exhard"));
    }

    /// <summary>source退役ログが失敗しても実履歴行・旧行回収・後続公開を保ち、必須公開失敗だけを所有権移譲済みで返します。</summary>
    [DataTestMethod]
    [DataRow(false)] // 任意ログだけの故障は適用成功。
    [DataRow(true)] // 必須の列公開も故障しても確定行は保持。
    public void PlayHistoryWorkflowOwner_ApplySortedRowsKeepsCommittedRowsAfterPublishFailure(bool failPublication)
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        IReadOnlyList<PlayHistoryRow> rows = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(PlayHistorySourceProfile.Lr2("fixed-history.db"),
                [new Lr2PlayHistoryRecord { history_id = 1, hash = new string('a', 32), new_playcount = 1 }],
                [], default), PlayHistoryProjectionIndex.Empty).Rows;
        var state = new PlayHistoryViewState(activeRequest.RequestId, PlayHistoryPeriodRequest.All(),
            rows, rows, rows, [], PlayHistoryProvider.Lr2, default, rows.Count,
            new SortSnapshot(null, null, revision: 0L), string.Empty, 0, PlayHistoryDisplayTargetItem.All, 0);
        var oldRow = new CountingDisposable();
        var table = new MainChartListViewModel { Rows = new List<object> { oldRow } };
        var detailState = new PlaylistDetailViewState();
        detailState.Source.CurrentTable = new BMSTable { name = "retired source" };
        int diagnosticAttempts = 0;
        var notifications = new List<string>();
        table.PropertyChanged += (_, change) => notifications.Add(change.PropertyName!);
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            detailState,
            _ => { },
            _ => { diagnosticAttempts++; throw new IOException("source-clear diagnostic failed"); },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        workspace.PropertyChanged += (_, e) =>
        {
            if (failPublication && e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                throw new InvalidOperationException("workspace publish failed");
            }
        };

        PlayHistorySortedRowsApplyResult Apply() => workflowOwner.ApplySortedRows(
                new PlayHistorySortedRowsApplyRequest(
                    MainViewUpdateMode.PlayHistorySelected,
                    MainViewUpdateMode.PlayHistorySelected,
                    state,
                    sortedRows: rows,
                    sortSucceeded: true,
                    sortProfile: "default",
                    currentKeywordFilter: string.Empty,
                    PlayHistoryDisplayTargetItem.All,
                    archivePeriodTree: null,
                    workspace.PrepareDetailSourceRetirementWithoutPublishing()),
                Stopwatch.StartNew(),
                table,
                workspace);

        if (failPublication)
        {
            PlayHistoryTerminalPublishException exception = Assert.ThrowsException<PlayHistoryTerminalPublishException>(() => Apply());
            Assert.IsTrue(exception.OwnershipTransferred);
        }
        else { Assert.AreEqual(PlayHistorySortedRowsApplyStatus.Applied, Apply().Status); }
        Assert.AreEqual(2, diagnosticAttempts, "片方の診断故障でも残りの保持診断を試みます。");
        Assert.AreEqual(1, oldRow.DisposeCount);
        Assert.AreSame(rows[0], table.Rows[0]);
        Assert.IsNull(detailState.Source.CurrentTable);
        CollectionAssert.Contains(notifications, nameof(MainChartListViewModel.Rows));
        CollectionAssert.Contains(notifications, nameof(MainChartListViewModel.SummaryText));
        Assert.IsInstanceOfType<PlayHistoryVirtualView>(table.Rows);
        Assert.AreSame(state, workflowOwner.PresentationState.CurrentView);
    }

    /// <summary>列確定通知が失敗してもsource退役receiptを例外へ保持し、確定後復旧から退役状態を見失う誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_CommitFailureRetainsSourceClearRecoveryState()
    {
        PlayHistoryTerminalHarness owner = CreatePlayHistoryTerminalHarness(
            out PlayHistoryPresentationState state,
            out MainChartListViewModel table);
        state.RequestGeneration = 1;
        table.AppliedColumnModeCommitted += _ => throw new InvalidOperationException("column mode commit failed");

        PlayHistoryTerminalPublishException exception = Assert.ThrowsException<PlayHistoryTerminalPublishException>(
            () => owner.TryApply(CreatePlayHistoryTerminalRequest(
                new List<object> { new object() },
                "committed",
                requestId: 1)));

        Assert.IsTrue(exception.OwnershipTransferred);
        Assert.IsNotNull(exception.TerminalCommitResult);
        Assert.IsNotNull(exception.TerminalCommitResult.PlaylistSourceClear);
        Assert.IsNotNull(state.CurrentView);
    }

    /// <summary>detail退役receipt後の新要求で旧終端を拒否し、旧行とsourceを保持して新detail要求を消す誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryTerminal_DetailRequestAfterRetirementReceiptRejectsStaleApply()
    {
        PlayHistoryTerminalHarness owner = CreatePlayHistoryTerminalHarness(
            out PlayHistoryPresentationState state,
            out MainChartListViewModel table);
        state.RequestGeneration = 1;
        var oldRows = new List<object> { new() };
        table.Rows = oldRows;
        PlayHistoryTerminalRequest request = CreatePlayHistoryTerminalRequest(
            new List<object> { new() },
            "stale",
            requestId: 1);
        owner.PrepareThenInvalidateDetailRetirement(request);

        PlayHistoryTerminalCommitResult result = owner.TryApply(request);

        Assert.IsFalse(result.Applied);
        Assert.AreSame(oldRows, table.Rows);
        Assert.IsNull(result.PlaylistSourceClear);
    }

    /// <summary>表示再構築は最新keyword・sort世代と捕捉済みsummaryフィルタを使い、caller変更や旧条件で描画する誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_BuildPresentationOnlyAppliesLatestKeywordRevision()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        var projectedRows = new List<PlayHistoryRow>();
        var state = new PlayHistoryViewState(
            activeRequest.RequestId,
            PlayHistoryPeriodRequest.All(),
            projectedRows,
            projectedRows,
            projectedRows,
            diagnostics: [],
            PlayHistoryProvider.Lr2,
            schemaStatus: default,
            sourceCount: 0,
            new SortSnapshot(null, null, revision: 0L),
            keywordFilter: string.Empty,
            keywordFilterRevision: 0,
            PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: 0);
        long keywordRevision = workflowOwner.UpdateKeywordIdentity("title:missing", advanceRevision: true);
        workflowOwner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.Title),
            Direction = ListSortDirection.Descending
        });
        var summaryFilters = new List<string> { "type:score" };
        var request = new PlayHistoryPresentationOnlyBuildRequest(
            MainViewUpdateMode.KeywordFilterUpdated,
            state,
            keywordFilter: "title:missing",
            keywordRevision,
            PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: 0,
            summaryFilters);
        summaryFilters.Clear();

        PlayHistoryPresentationOnlyBuildResult result = workflowOwner.BuildPresentationOnly(request, playlist: null);
        projectedRows.Add(null!);

        Assert.AreEqual(PlayHistoryPresentationOnlyBuildStatus.Built, result.Status);
        Assert.IsTrue(result.KeywordFilterApplied);
        Assert.AreEqual(0, result.KeywordCount);
        Assert.AreEqual(keywordRevision, result.State.KeywordFilterRevision);
        Assert.AreEqual("title:missing", result.State.KeywordFilter);
        Assert.AreEqual(1L, result.State.SortSnapshot.Revision);
        Assert.AreEqual(0, result.State.ProjectedRows.Count);
        Assert.AreEqual(0, result.SortedRows.Count);
        Assert.AreEqual(1, request.SummaryFilterTexts.Count);
    }

    /// <summary>keyword世代が古いsort要求は整列せず再更新へ返し、旧検索条件の公開を検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_BuildPresentationOnlyQueuesKeywordRefreshForSortRequestWithStaleKeyword()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        PlayHistoryViewState state = CreateEmptyPlayHistoryViewState(activeRequest.RequestId);
        long keywordRevision = workflowOwner.UpdateKeywordIdentity("changed", advanceRevision: true);

        PlayHistoryPresentationOnlyBuildResult result = workflowOwner.BuildPresentationOnly(
            new PlayHistoryPresentationOnlyBuildRequest(
                MainViewUpdateMode.SortUpdated,
                state,
                keywordFilter: "changed",
                keywordRevision,
                PlayHistoryDisplayTargetItem.All,
                displayTargetRevision: 0,
                summaryFilterTexts: []),
            playlist: null);

        Assert.AreEqual(PlayHistoryPresentationOnlyBuildStatus.KeywordStale, result.Status);
        Assert.IsTrue(result.QueueRefresh);
        Assert.AreEqual(0, result.SortedRows.Count);
    }

    /// <summary>keywordと表示対象が共に古いsort要求では表示対象失効を優先し、別表示への更新続行を検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_BuildPresentationOnlyPrioritizesDisplayTargetStaleForSortRequest()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        PlayHistoryViewState state = CreateEmptyPlayHistoryViewState(activeRequest.RequestId);
        long keywordRevision = workflowOwner.UpdateKeywordIdentity("changed", advanceRevision: true);
        var changedTarget = PlayHistoryDisplayTargetItem.FromPlaylist(new BMSTable { name = "Changed" });

        PlayHistoryPresentationOnlyBuildResult result = workflowOwner.BuildPresentationOnly(
            new PlayHistoryPresentationOnlyBuildRequest(
                MainViewUpdateMode.SortUpdated,
                state,
                keywordFilter: "changed",
                keywordRevision,
                changedTarget,
                displayTargetRevision: 1,
                summaryFilterTexts: []),
            playlist: null);

        Assert.AreEqual(PlayHistoryPresentationOnlyBuildStatus.DisplayTargetStale, result.Status);
        Assert.IsTrue(result.QueueRefresh);
    }

    /// <summary>表示抽出後の検索段階取消では完了抽出の件数だけを保持してstaleを返し、未実施段階を成功件数へ混ぜる誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_BuildPresentationOnlyKeepsCompletedDisplayMetricsWhenKeywordStageIsCanceled()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        var previousTarget = PlayHistoryDisplayTargetItem.FromPlaylist(new BMSTable { name = "Previous" });
        var sourceRows = new List<PlayHistoryRow> { null! };
        var state = new PlayHistoryViewState(
            activeRequest.RequestId,
            PlayHistoryPeriodRequest.All(),
            sourceRows,
            filterSourceRows: [],
            projectedRows: [],
            diagnostics: [],
            PlayHistoryProvider.Lr2,
            schemaStatus: default,
            sourceCount: 1,
            new SortSnapshot(null, null, revision: 0L),
            keywordFilter: string.Empty,
            keywordFilterRevision: 0,
            previousTarget,
            displayTargetRevision: 0);
        workflowOwner.PresentationState.RequestCancellation.Cancel();

        PlayHistoryPresentationOnlyBuildResult result = workflowOwner.BuildPresentationOnly(
            new PlayHistoryPresentationOnlyBuildRequest(
                MainViewUpdateMode.KeywordFilterUpdated,
                state,
                keywordFilter: "changed",
                keywordRevision: 0,
                PlayHistoryDisplayTargetItem.All,
                displayTargetRevision: 0,
                summaryFilterTexts: []),
            playlist: null);

        Assert.AreEqual(PlayHistoryPresentationOnlyBuildStatus.StaleRequest, result.Status);
        Assert.IsFalse(result.QueueRefresh);
        Assert.IsTrue(result.DisplayTargetApplied);
        Assert.AreEqual(1, result.DisplayTargetSourceCount);
        Assert.AreEqual(1, result.DisplayTargetResultCount);
        Assert.IsFalse(result.KeywordFilterApplied);
    }

    /// <summary>非対応sortでも捕捉した行と診断を保持して期間診断を合流し、caller変更やsort失敗で読取り結果を失う誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_BuildReadPresentationMergesDiagnosticsAndBuildsSnapshotState()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        workflowOwner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = "LibraryChartRowOnlyColumn",
            Direction = ListSortDirection.Ascending
        });
        var projectedRows = new List<PlayHistoryRow>();
        var projectionDiagnostics = new List<PlayHistoryDiagnostic>
        {
            new() { Code = "projection" }
        };
        var periodDiagnostics = new List<PlayHistoryDiagnostic>
        {
            new() { Code = "period" }
        };
        var summaryOverride = new PlayHistoryPeriodSummaryOverride(playCount: 7, judgeCount: 11, playtimeSeconds: 13);
        var request = new PlayHistoryReadPresentationBuildRequest(
            activeRequest.RequestId,
            PlayHistoryPeriodRequest.All(),
            projectedRows,
            projectionDiagnostics,
            periodIndexPlayedAt: [DateTimeOffset.UtcNow.ToUnixTimeSeconds()],
            periodDiagnostics,
            PlayHistoryProvider.Lr2,
            Lr2PlayHistorySchemaStatus.Installed,
            sourceCount: 0,
            keywordFilter: string.Empty,
            keywordRevision: 0,
            PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: 0,
            summaryFilterTexts: [],
            summaryOverride);
        projectedRows.Add(null!);
        projectionDiagnostics.Clear();
        periodDiagnostics.Clear();

        PlayHistoryReadPresentationBuildResult result = workflowOwner.BuildReadPresentation(request, playlist: null);

        Assert.IsTrue(result.Built);
        Assert.AreEqual(0, result.SortedRows.Count);
        Assert.AreEqual(2, result.State.Diagnostics.Count);
        Assert.AreEqual("projection", result.State.Diagnostics[0].Code);
        Assert.AreEqual("period", result.State.Diagnostics[1].Code);
        Assert.AreEqual(1L, result.State.SortSnapshot.Revision);
        Assert.IsFalse(result.SortSucceeded);
        Assert.AreEqual("play_history_unknown_column", result.SortProfile);
        Assert.AreSame(summaryOverride, result.State.SummaryOverride);
        Assert.IsTrue(result.ArchivePeriodTree.Count > 0);
        Assert.AreEqual(0, result.DisplayTargetSourceCount);
        Assert.AreEqual(0, result.DisplayTargetResultCount);
    }

    /// <summary>取消済み読取りはview stateを構築せずstaleへ返し、取消後の結果採用を検出します。</summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_BuildReadPresentationReturnsStaleWhenRequestIsCanceled()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest = workflowOwner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            keywordIdentity: string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        workflowOwner.PresentationState.RequestCancellation.Cancel();

        PlayHistoryReadPresentationBuildResult result = workflowOwner.BuildReadPresentation(
            new PlayHistoryReadPresentationBuildRequest(
                activeRequest.RequestId,
                PlayHistoryPeriodRequest.All(),
                projectedRows: [],
                projectionDiagnostics: [],
                periodIndexPlayedAt: [],
                periodIndexDiagnostics: [],
                PlayHistoryProvider.Lr2,
                Lr2PlayHistorySchemaStatus.Installed,
                sourceCount: 0,
                keywordFilter: string.Empty,
                keywordRevision: 0,
                PlayHistoryDisplayTargetItem.All,
                displayTargetRevision: 0,
                summaryFilterTexts: [],
                summaryOverride: null),
            playlist: null);

        Assert.IsFalse(result.Built);
        Assert.IsNull(result.State);
        Assert.AreEqual(0, result.SortedRows.Count);
    }

    /// <summary>全終端通知から新行・列・選択解除・明示summaryを読めることで、行だけを先に公開する誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryTerminal_PublishesExplicitSummaryWithCommittedMainState()
    {
        PlayHistoryTerminalHarness owner = CreatePlayHistoryTerminalHarness(out PlayHistoryPresentationState state, out MainChartListViewModel table);
        var candidateRows = new List<object> { new object(), new object() };
        table.Rows = new List<object>();
        table.SelectedIndex = 3;
        state.RequestGeneration = 1;
        int preparingCount = 0;
        var propertyNames = new List<string>();
        table.RowsReplacing += (_, _) => preparingCount++;
        table.PropertyChanged += (_, e) =>
        {
            propertyNames.Add(e.PropertyName!);
            Assert.AreSame(candidateRows, table.Rows);
            Assert.AreEqual("play-history explicit summary", table.SummaryText);
            Assert.AreEqual(-1, table.SelectedIndex);
            Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, table.ColumnsSettings.Kind);
        };

        PlayHistoryTerminalCommitResult result = owner.TryApply(CreatePlayHistoryTerminalRequest(candidateRows, "play-history explicit summary", requestId: 1));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(1, preparingCount);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.Rows));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SummaryText));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SelectedIndex));
        Assert.AreEqual("play-history explicit summary", table.SummaryText);
    }

    /// <summary>終端後続callback失敗でも新行の所有を移譲し、旧行回収と残りのtable公開を続けることを確認します。</summary>
    [TestMethod]
    public void PlayHistoryTerminal_PostCommitFailureTransfersOwnershipAndContinuesPublishing()
    {
        var oldRow = new CountingDisposable();
        var candidateRows = new List<object> { new object() };
        PlayHistoryTerminalHarness owner = CreatePlayHistoryTerminalHarness(
            out PlayHistoryPresentationState state,
            out MainChartListViewModel table,
            _ => throw new InvalidOperationException("feature notification failed"));
        state.RequestGeneration = 1;
        table.Rows = new List<object> { oldRow };
        int tableNotifications = 0;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                tableNotifications++;
            }
        };
        PlayHistoryTerminalRequest request = CreatePlayHistoryTerminalRequest(candidateRows, "committed", requestId: 1);
        request.SummaryCards = [new PlayHistorySummaryCard("Label", "Value")];

        PlayHistoryTerminalPublishException exception = Assert.ThrowsException<PlayHistoryTerminalPublishException>(
            () => owner.TryApply(request));

        Assert.IsTrue(exception.OwnershipTransferred);
        Assert.AreSame(candidateRows, table.Rows);
        Assert.AreEqual(1, oldRow.DisposeCount);
        Assert.IsTrue(tableNotifications > 0);
        Assert.AreSame(request.SummaryCards, state.SummaryCards);
    }

    /// <summary>detail取消callback例外の報告後も取消済みreceiptで履歴終端を確定し、callback失敗だけで確定を止める誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryTerminal_PreparedRetirementReportsCancellationFailureAndStillCommits()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryPresentationState state = workflowOwner.PresentationState;
        state.RequestGeneration = 1;
        var table = new MainChartListViewModel { Rows = new List<object>() };
        var buildCancellation = new System.Threading.CancellationTokenSource();
        buildCancellation.Token.Register(() => throw new InvalidOperationException("cancel callback failed"));
        var buildState = new PlaylistDetailBuildState { CurrentBuildCancellation = buildCancellation };
        var viewState = new PlaylistDetailViewState();
        bool retentionLogged = false;
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            buildState,
            viewState,
            _ => { },
            _ => retentionLogged = true,
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var owner = new PlayHistoryTerminalHarness(
            workflowOwner,
            table,
            workspace);

        PlayHistoryTerminalRequest request = CreatePlayHistoryTerminalRequest(
            new List<object> { new object() },
            "committed",
            requestId: 1);
        request.DetailSourceRetirement = workspace.PrepareDetailSourceRetirementWithoutPublishing();

        Assert.ThrowsException<AggregateException>(
            () => workspace.PublishDetailSourceRetirement(request.DetailSourceRetirement));
        PlayHistoryTerminalCommitResult result = owner.TryApply(request);

        Assert.IsTrue(result.Applied);
        Assert.IsTrue(retentionLogged);
    }

    /// <summary>workspace列設定の通知例外がtable公開失敗となってもshellのsummary cardsを通知・確定し、前段失敗による後続公開停止を検出します。</summary>
    [TestMethod]
    public void PlayHistoryTerminal_TablePublishFailureDoesNotSuppressShellPublish()
    {
        bool shellPublished = false;
        PlayHistoryTerminalHarness owner = CreatePlayHistoryTerminalHarness(
            out PlayHistoryPresentationState state,
            out MainChartListViewModel table,
            propertyName =>
            {
                if (propertyName == nameof(PlayHistoryWorkflowOwner.SummaryCards))
                {
                    shellPublished = true;
                }
            },
            workspace =>
            {
                workspace.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
                    {
                        throw new InvalidOperationException("workspace publish failed");
                    }
                };
            });
        state.RequestGeneration = 1;
        table.Rows = new List<object>();
        PlayHistoryTerminalRequest request = CreatePlayHistoryTerminalRequest(new List<object> { new object() }, "committed", requestId: 1);
        request.SummaryCards = [new PlayHistorySummaryCard("Label", "Value")];

        PlayHistoryTerminalPublishException exception = Assert.ThrowsException<PlayHistoryTerminalPublishException>(
            () => owner.TryApply(request));

        Assert.IsTrue(exception.OwnershipTransferred);
        Assert.IsTrue(shellPublished);
        Assert.AreSame(request.SummaryCards, state.SummaryCards);
    }

    /// <summary>履歴期間通知失敗後にもsummaryと診断の通知を続け、新状態を保持して最初の例外による公開停止を検出します。</summary>
    [TestMethod]
    public void PlayHistoryTerminal_OwnerNotificationFailureDoesNotSuppressLaterNotifications()
    {
        var propertyNames = new List<string>();
        PlayHistoryTerminalHarness owner = CreatePlayHistoryTerminalHarness(
            out PlayHistoryPresentationState state,
            out MainChartListViewModel table,
            propertyName =>
            {
                propertyNames.Add(propertyName);
                if (propertyName == nameof(PlayHistoryWorkflowOwner.ArchivePeriodTree))
                {
                    throw new InvalidOperationException("archive binding failed");
                }
            });
        state.RequestGeneration = 1;
        table.Rows = new List<object>();
        PlayHistoryTerminalRequest request = CreatePlayHistoryTerminalRequest(
            new List<object> { new object() },
            "committed",
            requestId: 1);
        request.ArchivePeriodTree = [new PlayHistoryPeriodTreeItem("archive", PlayHistoryPeriodRequest.All())];
        request.SummaryCards = [new PlayHistorySummaryCard("Label", "Value")];
        request.DiagnosticText = "diagnostic";

        PlayHistoryTerminalPublishException exception = Assert.ThrowsException<PlayHistoryTerminalPublishException>(
            () => owner.TryApply(request));

        Assert.IsTrue(exception.OwnershipTransferred);
        Assert.IsNotNull(exception.TerminalCommitResult);
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(PlayHistoryWorkflowOwner.ArchivePeriodTree),
                nameof(PlayHistoryWorkflowOwner.SummaryCards),
                nameof(PlayHistoryWorkflowOwner.SummaryDiagnosticText)
            },
            propertyNames);
        Assert.AreSame(request.ArchivePeriodTree, state.ArchivePeriodTree);
        Assert.AreSame(request.SummaryCards, state.SummaryCards);
        Assert.AreEqual("diagnostic", state.DiagnosticText);
    }

    /// <summary>table適用前失敗の復旧へ退役receiptを渡すとsource保持診断を実公開し、復旧時にsource処理を忘れる誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryTerminal_PreAppliedTableFailureRecoveryPublishesSourceClearLog()
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        PlayHistoryPresentationState state = workflowOwner.PresentationState;
        state.RequestGeneration = 1;
        var table = new MainChartListViewModel { Rows = new List<object>() };
        var buildState = new PlaylistDetailBuildState();
        var viewState = new PlaylistDetailViewState();
        bool retentionLogged = false;
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            buildState,
            viewState,
            _ => { },
            _ => retentionLogged = true,
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var sourceClear = new PlaylistSourceClearCommitResult(
            [],
            new List<object>(),
            previousGenerationId: 1,
            buildCancellation: null);
        var result = new PlayHistoryTerminalCommitResult
        {
            Applied = false,
            PlaylistSourceClear = sourceClear
        };
        var exception = new PlayHistoryTerminalPublishException(
            new InvalidOperationException("table publish failed"),
            ownershipTransferred: true,
            result);

        workflowOwner.PublishTerminalShellStateAfterTablePublishFailure(exception, workspace);

        Assert.IsTrue(retentionLogged);
    }

    /// <summary>生成済み一行だけを回収し、未訪問行を生成せず次アクセスで再生成することで、回収時の全件生成と破棄済み行再利用を検出します。</summary>
    [TestMethod]
    public void DisposeRealizedRows_DoesNotRealizeUnvisitedRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        _ = view[2];

        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());

        view.DisposeRealizedRows();

        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(1, getCreatedCount());

        _ = view[2];

        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(2, getCreatedCount());
    }

    /// <summary>BMS・bmson混在のタイトル昇順を既存実体行sortと比較し、仮想経路への切替で順が変わる誤りを検出します。</summary>
    [TestMethod]
    public void TitleAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Title), ListSortDirection.Ascending);
    }

    /// <summary>BMS・bmson混在のタイトル降順を既存実体行sortと比較し、降順だけ仮想比較が異なる誤りを検出します。</summary>
    [TestMethod]
    public void TitleDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Title), ListSortDirection.Descending);
    }

    /// <summary>BMS・bmson混在のpath昇順を既存実体行sortと比較し、形式をまたぐ仮想整列の互換性を確認します。</summary>
    [TestMethod]
    public void PathAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.path), ListSortDirection.Ascending);
    }

    /// <summary>BMS・bmson混在のpath降順を既存実体行sortと比較し、仮想整列の降順互換性を確認します。</summary>
    [TestMethod]
    public void PathDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.path), ListSortDirection.Descending);
    }

    /// <summary>BMS・bmson混在のfolder昇順を通常一覧の実体行sortと比較し、仮想経路でfolder比較を変える誤りを検出します。</summary>
    [TestMethod]
    public void FolderAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Folder), ListSortDirection.Ascending);
    }

    /// <summary>BMS・bmson混在のfolder降順を通常一覧の実体行sortと比較し、降順比較の互換性を確認します。</summary>
    [TestMethod]
    public void FolderDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Folder), ListSortDirection.Descending);
    }

    /// <summary>列一覧の文字列・mode・警告・健全性・導入先・参照表を両方向で実体行sortと比較し、notes欠損を保持して数値順と同値時タイトル昇順を確認します。</summary>
    [TestMethod]
    public void AdditionalSupportedOrders_MatchExistingDefaultLibraryChartRowSort()
    {
        string[] columns =
        [
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.WarningDigestText),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
            nameof(LibraryChartRow.WAVHealth),
            nameof(LibraryChartRow.BGAHealth),
            nameof(LibraryChartRow.MovieHealth),
            nameof(LibraryChartRow.encoding),
            nameof(LibraryChartRow.instl_dst),
            nameof(LibraryChartRow.InstallDestinationTitle),
            nameof(LibraryChartRow.InstallDestinationArtist),
            nameof(LibraryChartRow.RefTablesSymbols)
        ];

        foreach (string column in columns)
        {
            AssertVirtualOrderMatchesExistingSort(column, ListSortDirection.Ascending);
            AssertVirtualOrderMatchesExistingSort(column, ListSortDirection.Descending);
        }

        List<ChartFile> nullableFiles = [.. new[] { "Charlie", "Missing", "Bravo", "Alpha" }
            .Select(title =>
            {
                ChartFile file = CreateFile(title + ".bms", title, "notes");
                return title == "Missing" ? file : ChartFileProjection.WithChartInfo(file,
                    new BeMusicSeeker.Models.ChartDetails { notes = title == "Charlie" ? 100 : 20 });
            })];
        List<ChartListSourceRow> nullableRows = BuildOwnerBackedSourceRows(nullableFiles);
        foreach (ListSortDirection direction in new[] { ListSortDirection.Ascending, ListSortDirection.Descending })
        {
            Assert.IsTrue(ChartListOrder.TryCreate(nullableRows, nameof(LibraryChartRow.ChartNotes), direction, out ChartListOrder order));
            ChartListSourceRow[] sorted = [.. order.Indexes.Select(index => nullableRows[index])];
            CollectionAssert.AreEquivalent(new[] { "Alpha", "Bravo", "Charlie", "Missing" }, sorted.Select(row => row.Title).ToArray());
            // NULLの位置は仕様で固定せず、定義値の部分列だけを独立期待と比較する。
            CollectionAssert.AreEqual(direction == ListSortDirection.Ascending
                ? new[] { "Alpha", "Bravo", "Charlie" } : new[] { "Charlie", "Alpha", "Bravo" },
                sorted.Where(row => row.ChartNotes.HasValue).Select(row => row.Title).ToArray());
            Assert.IsNull(sorted.Single(row => row.Title == "Missing").ChartNotes);
        }
    }

    /// <summary>同じpathの行はpath降順でもタイトル昇順とし、二次キーまで反転する誤りを検出します。</summary>
    [TestMethod]
    public void PathDescendingOrder_KeepsTitleAscendingSecondaryKey()
    {
        List<ChartFile> files =
        [
            CreateFile(@"folder-z\same.bms", "Gamma", "folder-z"),
            CreateFile(@"folder-z\same.bms", "Alpha", "folder-z"),
            CreateFile(@"folder-a\other.bms", "Beta", "folder-a")
        ];
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files);

        bool created = ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder order);

        Assert.IsTrue(created);
        CollectionAssert.AreEqual(
            new[] { "Alpha", "Gamma", "Beta" },
            order.Indexes.Select(index => sourceRows[index].Title).ToArray());
    }

    /// <summary>表記違いのPathと未対応キーでは仮想orderを作らず、非対応列を対応済みとして扱う誤りを検出します。</summary>
    [TestMethod]
    public void UnsupportedColumn_CannotCreateVirtualOrder()
    {
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(CreateSampleSortFiles());

        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, "Path", ListSortDirection.Ascending, out _));
        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, "EntryLevelSortKey", ListSortDirection.Ascending, out _));
    }

    /// <summary>同じsourceでtitle昇順からpath降順へ替えても未生成とし、新orderの一行読取りだけを生成してsort更新時の全件実体化を検出します。</summary>
    [TestMethod]
    public void SortUpdatedOrderSwap_DoesNotRealizeRowsUntilIndexed()
    {
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(CreateSampleSortFiles());
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, out ChartListOrder titleOrder));
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder pathOrder));
        int createdCount = 0;

        var titleView = new ChartListVirtualView(sourceRows, titleOrder, row =>
        {
            createdCount++;
            return MaterializeSourceRow(row);
        });
        var pathView = new ChartListVirtualView(sourceRows, pathOrder, row =>
        {
            createdCount++;
            return MaterializeSourceRow(row);
        });

        Assert.AreEqual(3, titleView.Count);
        Assert.AreEqual(3, pathView.Count);
        Assert.AreEqual(0, titleView.RealizedRowCount);
        Assert.AreEqual(0, pathView.RealizedRowCount);
        Assert.AreEqual(0, createdCount);

        _ = pathView[0];

        Assert.AreEqual(0, titleView.RealizedRowCount);
        Assert.AreEqual(1, pathView.RealizedRowCount);
        Assert.AreEqual(1, createdCount);
    }

    /// <summary>folder・artist keyword・複数modeの通常一覧を元order順に保持し、件数計算では生成せず先頭だけ実体化することを確認します。</summary>
    [TestMethod]
    public void NormalLibraryFilters_ReuseFullOrderSubsetWithoutRealizingRows()
    {
        List<ChartFile> files =
        [
            CreateFile(@"FOLDER-Z\delta.bms", "Delta", "folder-z", artist: "Target Artist", mode: 7),
            CreateFile(@"folder-z\bravo.bms", "Bravo", "folder-z", artist: "Target Artist", mode: 5),
            CreateFile(@"folder-y\charlie.bms", "Charlie", "folder-y", artist: "Other Artist", mode: 7),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a", artist: "Target Artist", mode: 7)
        ];
        List<ChartFile> bmsons =
        [
            ChartTestValues.Empty(ChartFileKind.Bmson) with {
                Path = @"folder-z\echo.bmson",
                Folder = "folder-z",
                Title = "Echo",
                RawTitle = "Echo",
                Artist = "Target Artist",
                RawArtist = "Target Artist",
                ModeHint = "beat-7k",
                Mode = 7,
                Level = 7,
                LevelText = "7",
                Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                Sha256 = "6666666666666666666666666666666666666666666666666666666666666666"
            }
        ];
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files, bmsons);
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder fullOrder));
        int[] existingOrderIndexes = fullOrder.Indexes.AsEnumerable().Reverse().ToArray();
        var keywordQuery = GridKeywordSearchQuery.Parse("artist:target");
        var folderFilter = RegularNormalLibraryTreeFilter.Create(
            RegularChartFolderFilterKind.Directory,
            "folder-z");

        int[] filteredIndexes = RegularChartListOwner.ApplyVirtualNormalLibraryFilters(
            sourceRows,
            existingOrderIndexes,
            folderFilter,
            keywordQuery,
            ChartModeFilter._5KEYS | ChartModeFilter._7KEYS,
            out int folderCount,
            out int keywordCount,
            out int modeCount,
            out _,
            out _,
            out _);
        ChartListOrder filteredOrder = fullOrder.WithIndexes(filteredIndexes);
        int createdCount = 0;
        var view = new ChartListVirtualView(sourceRows, filteredOrder, row =>
        {
            createdCount++;
            return MaterializeSourceRow(row);
        });

        Assert.AreEqual(RegularChartFolderFilterKind.Directory, folderFilter.Kind);
        Assert.AreEqual("folder-z" + Path.DirectorySeparatorChar, folderFilter.Term);
        CollectionAssert.AreEqual(new[] { "Bravo", "Delta", "Echo" }, filteredIndexes.Select(index => sourceRows[index].Title).ToArray());
        Assert.AreEqual(3, folderCount);
        Assert.AreEqual(3, keywordCount);
        Assert.AreEqual(3, modeCount);
        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, createdCount);

        Assert.AreEqual("Bravo", ((LibraryChartRow)view[0]).Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, createdCount);
    }

    /// <summary>artist keywordと複数modeの部分一覧を元path降順と件数のまま絞り、必要行だけを生成することを確認します。</summary>
    [TestMethod]
    public void VirtualChartSubsetFilters_ReuseFullOrderSubsetWithoutRealizingRows()
    {
        List<ChartFile> files =
        [
            CreateFile(@"folder-z\delta.bms", "Delta", "folder-z", artist: "Target Artist", mode: 7),
            CreateFile(@"folder-z\bravo.bms", "Bravo", "folder-z", artist: "Target Artist", mode: 5),
            CreateFile(@"folder-y\charlie.bms", "Charlie", "folder-y", artist: "Other Artist", mode: 7),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a", artist: "Target Artist", mode: 7)
        ];
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files);
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder fullOrder));
        var keywordQuery = GridKeywordSearchQuery.Parse("artist:target");

        int[] filteredIndexes = RegularChartListOwner.ApplyVirtualChartSubsetFilters(
            sourceRows,
            fullOrder.Indexes,
            keywordQuery,
            ChartModeFilter._5KEYS | ChartModeFilter._7KEYS,
            out int keywordCount,
            out int modeCount,
            out _,
            out _);
        ChartListOrder filteredOrder = fullOrder.WithIndexes(filteredIndexes);
        int createdCount = 0;
        var view = new ChartListVirtualView(sourceRows, filteredOrder, row =>
        {
            createdCount++;
            return MaterializeSourceRow(row);
        });

        CollectionAssert.AreEqual(new[] { "Delta", "Bravo", "Alpha" }, filteredIndexes.Select(index => sourceRows[index].Title).ToArray());
        Assert.AreEqual(3, keywordCount);
        Assert.AreEqual(3, modeCount);
        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, createdCount);

        Assert.AreEqual("Delta", ((LibraryChartRow)view[0]).Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.AreEqual(1, createdCount);
    }

    /// <summary>同値source列には同じsignature、行順反転には別signatureを返して異なる部分一覧順へ旧orderを再利用する誤りを検出します。</summary>
    [TestMethod]
    public void VirtualChartSubsetSignature_DistinguishesChangedSourceOrder()
    {
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(CreateSampleSortFiles());
        List<ChartListSourceRow> reorderedRows = [.. sourceRows.AsEnumerable().Reverse()];
        long signature = RegularChartListOwner.ComputeVirtualChartSubsetSourceRowsSignature(sourceRows);
        long sameSignature = RegularChartListOwner.ComputeVirtualChartSubsetSourceRowsSignature(BuildOwnerBackedSourceRows(CreateSampleSortFiles()));
        long reorderedSignature = RegularChartListOwner.ComputeVirtualChartSubsetSourceRowsSignature(reorderedRows);
        Assert.AreEqual(signature, sameSignature);
        Assert.AreNotEqual(signature, reorderedSignature);
    }

    /// <summary>identity・score・解析・健全性・警告・参照表の列を正しい依存種類へ分類し、データ更新で必要なsort失効を見逃す誤りを検出します。</summary>
    [TestMethod]
    public void VirtualSortRegistryMetadata_DescribesSupportedColumnsAndDependencies()
    {
        ChartListOrderColumnMetadata[] metadata = [.. ChartListOrder.GetVirtualSortColumnMetadata()];

        AssertRegistryDependency(metadata, MainViewDataDependency.IdentitySortKey, nameof(LibraryChartRow.Title));
        AssertRegistryDependency(metadata, MainViewDataDependency.Score, nameof(LibraryChartRow.rateDouble));
        AssertRegistryDependency(metadata, MainViewDataDependency.ChartInfo, nameof(LibraryChartRow.ChartTotalSortKey));
        AssertRegistryDependency(metadata, MainViewDataDependency.Maintenance, nameof(LibraryChartRow.WAVHealth));
        AssertRegistryDependency(metadata, MainViewDataDependency.Maintenance, nameof(LibraryChartRow.encoding));
        AssertRegistryDependency(metadata, MainViewDataDependency.Warning, nameof(LibraryChartRow.WarningDigestText));
        AssertRegistryDependency(metadata, MainViewDataDependency.ReferenceTables, nameof(LibraryChartRow.RefTablesSymbols));
    }

    /// <summary>0件と1件は一worker、多数件もCPU余力と上限内にし、予備整列で不要workerやCPU過剰使用を生む誤りを検出します。</summary>
    [TestMethod]
    public void VirtualOrderPrewarmDegree_IsBoundedByProcessorAndCap()
    {
        Assert.AreEqual(1, RegularChartListOwner.ResolveVirtualOrderPrewarmDegree(0));
        Assert.AreEqual(1, RegularChartListOwner.ResolveVirtualOrderPrewarmDegree(1));

        int degree = RegularChartListOwner.ResolveVirtualOrderPrewarmDegree(128);

        Assert.IsTrue(degree >= 1);
        Assert.IsTrue(degree <= 4);
        Assert.IsTrue(degree <= Math.Max(1, Environment.ProcessorCount - 1));
    }

    /// <summary>準備範囲0/1/2/3で対象列と既存の優先度内順を固定し、各昇降順・aliasの非重複と範囲外の拒否を確認します。</summary>
    [TestMethod]
    public void VirtualOrderPrewarmDescriptors_SelectExplicitPriorityColumnsAndRejectInvalidBounds()
    {
        string[][] priorities =
        [
            ["WAVHealth", "BGAHealth", "MovieHealth", "encoding", "WarningDigestText", "level"],
            ["Title", "Folder", "path", "Artist"],
            ["clear", "rateDouble", "minbp", "ChartJudgeSortKey", "ChartNotes", "ChartLongNotes", "ChartScratchNotes", "ChartMainBpmSortKey", "ChartMinBpmSortKey", "ChartMaxBpmSortKey", "ChartSoflanCount", "ChartTotalSortKey", "ChartTotalPerNoteSortKey", "ChartDurationSortKey", "ChartDensitySortKey", "ChartPeakDensitySortKey", "ChartEndDensitySortKey"],
            ["genre", "mode", "tag", "hash", "sha256", "instl_dst", "InstallDestinationTitle", "InstallDestinationArtist", "RefTablesSymbols", "score", "maxcombo", "rankingString", "rankingLastupdate", "stddevVal", "scoreDifficulty", "ChartLevelSortKey", "ChartDifficultySortKey", "ChartFeatureSortKey"]
        ];
        ChartListOrderColumnMetadata[] metadata = [.. ChartListOrder.GetVirtualSortColumnMetadata()];
        for (int priority = 0; priority <= 3; priority++)
        {
            CollectionAssert.AreEquivalent(priorities[priority], metadata.Where(column => column.PrewarmPriority == priority).Select(column => column.NormalizedColumnName).ToArray());
            VirtualNormalLibrarySortDescriptor[] descriptors = [.. RegularChartListOwner.CreateVirtualOrderPrewarmDescriptors(priority)];
            string[] expectedColumns = priorities.Skip(1).Take(priority).SelectMany(columns => columns).ToArray();
            Assert.AreEqual(new[] { 0, 8, 42, 78 }[priority], descriptors.Length);
            CollectionAssert.AreEqual(expectedColumns.SelectMany(column => new[] { column, column }).ToArray(), descriptors.Select(descriptor => descriptor.ColumnName).ToArray());
            CollectionAssert.AreEqual(expectedColumns.SelectMany(_ => new[] { ListSortDirection.Ascending, ListSortDirection.Descending }).ToArray(), descriptors.Select(descriptor => descriptor.Direction).ToArray());
            Assert.AreEqual(descriptors.Length, descriptors.Select(descriptor => (descriptor.ColumnName, descriptor.Direction)).Distinct().Count());
            Assert.IsFalse(descriptors.Any(descriptor => descriptor.ColumnName == "rank"));
        }
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => RegularChartListOwner.CreateVirtualOrderPrewarmDescriptors(-1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => RegularChartListOwner.CreateVirtualOrderPrewarmDescriptors(4));
    }

    /// <summary>本番が選んだ全予備sortをsourceだけで構築し、件数確認でも実体行を生成しないことで予備計算の全件生成を検出します。</summary>
    [TestMethod]
    public void AllVirtualOrderPrewarmDescriptors_DoNotRequireRowRealization()
    {
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(CreateSampleSortFiles());
        int createdCount = 0;

        IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors = RegularChartListOwner.CreateVirtualOrderPrewarmDescriptors(3);
        Assert.AreEqual(78, descriptors.Count);
        foreach (VirtualNormalLibrarySortDescriptor descriptor in descriptors)
        {
            Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, descriptor.ColumnName, descriptor.Direction, out ChartListOrder order));
            var view = new ChartListVirtualView(sourceRows, order, row =>
            {
                createdCount++;
                return MaterializeSourceRow(row);
            });

            Assert.AreEqual(sourceRows.Count, view.Count);
            Assert.AreEqual(0, view.RealizedRowCount);
        }
        Assert.AreEqual(0, createdCount);
    }

    /// <summary>bmson package表示でpathと合成タイトルを得てもBMS互換adapterを作らず、表示だけの不要変換を検出します。</summary>
    [TestMethod]
    public void PackageChartEntryDisplayRow_DoesNotMaterializeAdapterlessBmsonEntry()
    {
        ChartFile bmson = CreateBmsonSong();
        var entry = PackageChartEntry.FromChart((bmson));

        LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);

        Assert.IsNotNull(row);
        Assert.AreEqual(bmson.Path, row.path);
        Assert.AreEqual("BmsonTitle Subtitle", row.Title);
        Assert.IsNull(entry.GetBmsChartForTest());
    }

    /// <summary>bmson表示行は形式・元package項目・pathを保持し、BMS扱いや別項目への差替えを検出します。</summary>
    [TestMethod]
    public void PackageChartEntryDisplayRow_TreatsBmsonEntryAsBmsonRow()
    {
        ChartFile bmson = CreateBmsonSong();
        var entry = PackageChartEntry.FromChart((bmson));

        LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);

        Assert.IsNotNull(row);
        Assert.IsNull(row.Chart.Token);
        Assert.AreEqual(ChartFileKind.Bmson, row.Chart.Kind);
        Assert.AreSame(entry, row.PackageEntry);
        Assert.AreEqual(bmson.Path, row.path);
    }

    /// <summary>生成後のメタデータ不一致推定を代表タイトル・候補・警告へ反映し、導入先未確定とadapter非生成を保持して旧投影の残留を検出します。</summary>
    [TestMethod]
    public void PackageChartEntryDisplayRow_ReflectsUpdatedAdapterlessBmsonEntryState()
    {
        ChartFile bmson = CreateBmsonSong();
        var entry = PackageChartEntry.FromChart((bmson));
        LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);
        var result = new InstallEstimationResult
        {
            Confidence = InstallEstimationConfidence.Low,
            HasViableDestination = true,
            LowConfidenceKind = InstallEstimationLowConfidenceKind.MetadataMismatch
        };
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = @"C:\Candidate\A",
            MetadataRelation = InstallEstimationMetadataRelation.Related,
            DisplayTitle = "Candidate A",
            DisplayArtist = "Artist A"
        });
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = @"C:\Candidate\B",
            MetadataRelation = InstallEstimationMetadataRelation.Related,
            DisplayTitle = "Candidate B",
            DisplayArtist = "Artist B"
        });
        result.SuggestedDestinationDirectories.Add(@"C:\Candidate\A");
        result.SuggestedDestinationDirectories.Add(@"C:\Candidate\B");

        entry.ApplyInstallEstimationResult(result);

        Assert.IsNull(entry.GetBmsChartForTest());
        Assert.AreEqual(string.Empty, row.instl_dst);
        Assert.AreEqual("Candidate A", row.InstallDestinationTitle);
        Assert.AreEqual("Artist A", row.InstallDestinationArtist);
        CollectionAssert.AreEqual(new[] { @"C:\Candidate\A", @"C:\Candidate\B" }, row.Chart.InstallDestinationSuggestions.ToArray());
        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
    }

    /// <summary>推定検索の開始と終了で表示statusと再描画通知を更新し、検索中表示を出さない・消さない誤りを検出します。</summary>
    [TestMethod]
    public void PackageChartEntryDisplayRow_NotifiesWhenSearchingStatusChanges()
    {
        ChartFile bmson = CreateBmsonSong();
        var entry = PackageChartEntry.FromChart((bmson));
        LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);
        var propertyNames = new List<string>();
        row.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        entry.SetSearchingStatus(isSearching: true);

        Assert.IsTrue(row.status.HasFlag(ChartFileStatus.SEARCHING));
        CollectionAssert.Contains(propertyNames, string.Empty);

        propertyNames.Clear();

        entry.SetSearchingStatus(isSearching: false);

        Assert.IsFalse(row.status.HasFlag(ChartFileStatus.SEARCHING));
        CollectionAssert.Contains(propertyNames, string.Empty);
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

    /// <summary>BMSとbmsonのsnapshotに双方のpathを保持し、bmson adapterを作らず、形式の取り落としと不要変換を検出します。</summary>
    [TestMethod]
    public void PackageChartEntrySnapshot_UsesChartSourcesWithoutMaterializingBmson()
    {
        ChartFile bms = ChartTestValues.Empty();
        bms = bms with { Path = @"folder-a\bms.bms", Title = "BmsTitle", Folder = "folder-a", Artist = "", Genre = "", Level = null, Mode = null, Tag = "", Md5 = "0123456789abcdef0123456789abcdef", Sha256 = "", RawTitle = "BmsTitle", RawArtist = "", LevelText = "" };
        ChartFile bmson = CreateBmsonSong();
        var adapterlessBmsonEntry = PackageChartEntry.FromChart((bmson));
        var package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart((bms)),
            adapterlessBmsonEntry
        ]);

        IReadOnlyList<PackageChartEntry> snapshot = RegularChartListOwner.CreatePackageEntrySnapshot([package]);

        Assert.AreEqual(bms.Path, snapshot.Single(entry => entry.Chart.Kind == ChartFileKind.Bms).Chart.Path);
        Assert.AreEqual(bmson.Path, snapshot.Single(entry => entry.Chart.Kind == ChartFileKind.Bmson).Chart.Path);
        Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
    }

    /// <summary>警告・導入先・WAV健全性でbmson packageを仮想sortしてもadapterを生成せず、sortのための不要実体化を検出します。</summary>
    [TestMethod]
    public void PackageChartSourceRows_DoNotMaterializeAdapterlessBmsonDuringVirtualSort()
    {
        ChartFile bmson = CreateBmsonSong();
        var adapterlessBmsonEntry = PackageChartEntry.FromChart((bmson));
        var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        IReadOnlyList<PackageChartEntry> snapshot = RegularChartListOwner.CreatePackageEntrySnapshot([package]);
        List<ChartListSourceRow> rows = ChartListSourceRow.BuildPackageRows(snapshot);

        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, out _));
        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.instl_dst), ListSortDirection.Ascending, out _));
        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.WAVHealth), ListSortDirection.Ascending, out _));

        Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
    }

    /// <summary>生成済みsourceと表示行へ導入先・譜面移動・属性更新を反映し、警告全文と旧snapshotを保持してcacheの旧値残留を検出します。</summary>
    [TestMethod]
    public void PackageChartSourceRows_ReadLiveEntryProjectionForPendingInstall()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        ChartFile bmson = CreateBmsonSong();
        var entry = PackageChartEntry.FromChart((bmson));
        var maintenanceInfo = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, WavFilesDefined = 4, WavFilesExisting = 1, BgaFilesDefined = 2, BgaFilesExisting = 1, MovieFilesDefined = 1, MovieFilesExisting = 0 };
        entry.ApplyInstallDestinationMetadata(@"D:\BMS\Installed", "Resolved", "Artist");
        entry.ReplaceResourceHealthProjection(
            maintenanceInfo,
            [ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "WAV missing")]);
        ChartListSourceRow sourceRow = ChartListSourceRow.BuildPackageRows([entry]).Single();
        LibraryChartRow row = new MainChartRowProjectionOwner().CreateSubsetRow(null, sourceRow, includeResourceHealth: false);

        Assert.AreEqual(entry.Chart.InstallDestination, sourceRow.InstallDestination);
        Assert.AreEqual(entry.Chart.WAVHealth, sourceRow.WAVHealth);
        Assert.AreEqual(entry.Chart.BGAHealth, sourceRow.BGAHealth);
        Assert.AreEqual(entry.Chart.MovieHealth, sourceRow.MovieHealth);
        Assert.AreEqual(sourceRow.InstallDestination, row.instl_dst);
        Assert.AreEqual(sourceRow.WAVHealth, row.WAVHealth);
        Assert.AreEqual(sourceRow.BGAHealth, row.BGAHealth);
        Assert.AreEqual(sourceRow.MovieHealth, row.MovieHealth);
        Assert.IsTrue(sourceRow.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        Assert.IsTrue(row.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(sourceRow.Chart.Warnings), "WAV missing");
        StringAssert.Contains(row.WarningTooltipText, "WAV missing");

        entry.ApplyInstallDestinationMetadata(@"D:\BMS\Next", "Next", "Artist");

        Assert.AreEqual(@"D:\BMS\Next", sourceRow.InstallDestination);
        Assert.AreEqual(@"D:\BMS\Next", row.instl_dst);
        Assert.IsTrue(sourceRow.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        Assert.IsTrue(row.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        int healthNotifications = 0;
        bool observeHealth = true;
        entry.PropertyChanged += (_, _) =>
        {
            if (!observeHealth) { return; }
            healthNotifications++;
            Assert.AreEqual(75, sourceRow.WAVHealth);
            Assert.AreEqual(75, row.WAVHealth);
            StringAssert.Contains(row.WarningTooltipText, "Next WAV missing");
        };
        entry.ReplaceResourceHealthProjection(maintenanceInfo with { WavFilesExisting = 3 },
            [ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "Next WAV missing")]);
        observeHealth = false;
        Assert.AreEqual(1, healthNotifications);
        Assert.AreEqual(75, sourceRow.WAVHealth);
        Assert.AreEqual(75, row.WAVHealth);
        ChartFile captured = entry.Chart;
        entry.ApplyCurrentChart(captured with
        {
            Path = @"D:\BMS\Moved\chart.bmson",
            RawTitle = "Moved current",
            Title = "Moved current",
            Subtitle = string.Empty,
            Level = 13,
            Md5 = new string('b', 32),
            Sha256 = new string('c', 64)
        });
        Assert.AreEqual(@"D:\BMS\Moved\chart.bmson", sourceRow.Path);
        Assert.AreEqual(sourceRow.Path, row.Chart.Path);
        Assert.AreEqual("Moved current", sourceRow.Title);
        Assert.AreEqual(sourceRow.Title, row.Title);
        Assert.AreEqual(new string('b', 32), row.Chart.Md5);
        Assert.AreEqual(new string('c', 64), row.Chart.Sha256);
        Assert.AreEqual(13, row.Chart.Level);
        Assert.AreEqual(@"D:\BMS\Next", row.instl_dst);
        Assert.AreEqual(bmson.Path, captured.Path);

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

    /// <summary>導入先設定・有効パスと両代表値空・消去を通知中の現在値と次要求へ反映し、候補・警告保持と旧値復活を検証します。</summary>
    [TestMethod]
    public void PackageChartSourceRows_InstallDestinationEditsNotifyCurrentProjectionAndPreserveEstimation()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        ChartFile bmson = CreateBmsonSong();
        var entry = PackageChartEntry.FromChart((bmson));
        string[] candidates = [@"C:\Candidate\A", @"C:\Candidate\B"];
        var result = new InstallEstimationResult
        {
            Confidence = InstallEstimationConfidence.Low,
            HasViableDestination = true,
            LowConfidenceKind = InstallEstimationLowConfidenceKind.AmbiguousCandidates
        };
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = candidates[0],
            MetadataRelation = InstallEstimationMetadataRelation.Related,
            DisplayTitle = "Candidate A",
            DisplayArtist = "Artist A"
        });
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = candidates[1],
            MetadataRelation = InstallEstimationMetadataRelation.Related,
            DisplayTitle = "Candidate B",
            DisplayArtist = "Artist B"
        });
        result.SuggestedDestinationDirectories.AddRange(candidates);
        entry.ApplyInstallEstimationResult(result);
        ChartWarning[] estimationWarnings = entry.Chart.Warnings.ToArray();
        Assert.IsTrue(estimationWarnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        var projectionOwner = new MainChartRowProjectionOwner();
        ChartListSourceRow sourceRow = projectionOwner.BuildPackageSourceRows(null, [entry], includeResourceHealth: false).Single();
        LibraryChartRow row = projectionOwner.CreateSubsetRow(null, sourceRow, includeResourceHealth: false);

        // 両行のキャッシュを先に読み、後の通知で生成時の値へ戻らないことを確認します。
        AssertCurrentProjection(string.Empty, "Candidate A", "Artist A");
        string expectedPath = candidates[1];
        string expectedTitle = "Candidate B";
        string expectedArtist = "Artist B";
        var entryNotifications = new List<string>();
        var rowNotifications = new List<string>();
        PropertyChangedEventHandler entryChanged = (_, _) => entryNotifications.Add(entry.Chart.InstallDestination ?? string.Empty);
        PropertyChangedEventHandler rowChanged = (_, _) =>
        {
            AssertCurrentProjection(expectedPath, expectedTitle, expectedArtist);
            rowNotifications.Add(row.instl_dst);
        };
        entry.PropertyChanged += entryChanged;
        row.PropertyChanged += rowChanged;
        try
        {
            entry.ApplyInstallDestinationMetadata(expectedPath, expectedTitle, expectedArtist);
            CollectionAssert.Contains(entryNotifications, candidates[1]);
            CollectionAssert.Contains(rowNotifications, candidates[1]);
            AssertCurrentProjection(candidates[1], "Candidate B", "Artist B");

            entryNotifications.Clear();
            rowNotifications.Clear();
            expectedTitle = expectedArtist = string.Empty;
            entry.ApplyInstallDestinationMetadata(expectedPath, expectedTitle, expectedArtist);
            CollectionAssert.Contains(entryNotifications, candidates[1]);
            CollectionAssert.Contains(rowNotifications, candidates[1]);
            AssertCurrentProjection(candidates[1], string.Empty, string.Empty);
            Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget emptyDisplayTarget));
            var emptyDisplayRequest = PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([emptyDisplayTarget]);
            ChartFile emptyDisplayChart = emptyDisplayRequest.PackageTargets.Single().Chart;
            Assert.AreEqual(candidates[1], emptyDisplayChart.InstallDestination);
            Assert.AreEqual(string.Empty, emptyDisplayChart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, emptyDisplayChart.InstallDestinationArtist);

            entryNotifications.Clear();
            rowNotifications.Clear();
            expectedPath = expectedTitle = expectedArtist = string.Empty;
            entry.ApplyInstallDestinationMetadata(expectedPath, expectedTitle, expectedArtist);
            CollectionAssert.Contains(entryNotifications, string.Empty);
            CollectionAssert.Contains(rowNotifications, string.Empty);
            AssertCurrentProjection(string.Empty, string.Empty, string.Empty);

            Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));
            var request = PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([target]);
            Assert.AreSame(entry, request.PackageTargets.Single().PackageEntry);
            ChartFile nextChart = request.PackageTargets.Single().Chart;
            Assert.AreEqual(string.Empty, nextChart.InstallDestination);
            Assert.AreEqual(string.Empty, nextChart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, nextChart.InstallDestinationArtist);
            CollectionAssert.AreEqual(candidates, nextChart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(estimationWarnings, nextChart.Warnings.ToArray());
            Assert.IsNull(entry.GetBmsChartForTest());
        }
        finally
        {
            entry.PropertyChanged -= entryChanged;
            row.PropertyChanged -= rowChanged;
        }

        void AssertCurrentProjection(string path, string title, string artist)
        {
            Assert.AreEqual(path, sourceRow.InstallDestination);
            Assert.AreEqual(title, sourceRow.InstallDestinationTitle);
            Assert.AreEqual(artist, sourceRow.InstallDestinationArtist);
            Assert.AreEqual(path, sourceRow.Chart.InstallDestination);
            Assert.AreEqual(title, sourceRow.Chart.InstallDestinationTitle);
            Assert.AreEqual(artist, sourceRow.Chart.InstallDestinationArtist);
            Assert.AreEqual(path, row.instl_dst);
            Assert.AreEqual(title, row.InstallDestinationTitle);
            Assert.AreEqual(artist, row.InstallDestinationArtist);
            CollectionAssert.AreEqual(candidates, sourceRow.Chart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(candidates, row.Chart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(estimationWarnings, sourceRow.Chart.Warnings.ToArray());
            CollectionAssert.AreEqual(estimationWarnings, row.Chart.Warnings.ToArray());
            Assert.AreEqual(ChartWarningCollection.BuildDigestText(estimationWarnings, path), row.WarningDigestText);
        }
    }

    /// <summary>再生snapshotへBMSとbmsonのpathを両方渡してadapterを生成せず、形式別配送欠けと不要変換を検出します。</summary>
    [TestMethod]
    public void PackagePlaybackTargetSnapshot_UsesChartEntriesWithoutMaterializingBmsonAdapters()
    {
        ChartFile bms = ChartTestValues.Empty();
        bms = bms with { Path = @"folder-a\bms.bms", Title = "BmsTitle", Folder = "folder-a", Artist = "", Genre = "", Level = null, Mode = null, Tag = "", Md5 = "0123456789abcdef0123456789abcdef", Sha256 = "", RawTitle = "BmsTitle", RawArtist = "", LevelText = "" };
        ChartFile bmson = CreateBmsonSong();
        var adapterlessBmsonEntry = PackageChartEntry.FromChart((bmson));
        var package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart((bms)),
            adapterlessBmsonEntry
        ]);

        List<ChartFile> targets = MainWindowViewModel.CreatePackagePlaybackTargetSnapshot([package]);

        Assert.AreEqual(bms.Path, targets.Single(chart => chart.Kind == ChartFileKind.Bms).Path);
        Assert.AreEqual(bmson.Path, targets.Single(chart => chart.Kind == ChartFileKind.Bmson).Path);
        Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
    }

    /// <summary>library未接続でもpackage導入先を消去しadapterを作らず、未接続時の消去漏れを検出します。</summary>
    /// <remarks>対象識別とadapter非生成を確認するため、仮パスの譜面は参照取得済みの空集合として用意します。実ファイルの取得・配置評価はInstall側の試験へ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task InstallDestinationWorkflow_ClearPackagesWithoutLibraryClearsAdapterlessBmsonEntry()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
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
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
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
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, testSettings);
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
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
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
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, testSettings);
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

    /// <summary>表示行から操作targetと逆変換まで元package項目を保持し、譜面だけで操作して項目を失う誤りを検出します。</summary>
    [TestMethod]
    public void PackageEntryRowsCarryPackageEntryIntoChartOperationTarget()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        ChartFile adapter = CreateFile(
            @"C:\Pkg\chart.bms",
            "BMS",
            "Pkg",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var entry = PackageChartEntry.FromChart((adapter));
        LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));
        Assert.AreSame(entry, target.PackageEntry);
        Assert.AreSame(entry, target.ToPackageChartEntry());
    }

    /// <summary>実libraryで保留bmsonの導入先検索を完了してもadapterを生成せず、検索入口の旧形式依存を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task InstallDestinationWorkflow_SearchPendingDoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
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
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, testSettings);
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

    /// <summary>実storeで保留bmsonの統合先を検索してもadapterを生成せず、統合検索だけの旧形式依存を検出します。</summary>
    [TestMethod]
    public void InstallDestinationStore_SearchMergeDoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
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
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, testSettings);
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>([package]);
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
            new BmsLibraryPendingPackageStore().SearchPending(library, request);

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
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
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
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, testSettings);
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

    /// <summary>全mode組合せで通常folder・全走査に対応する要求だけ仮想経路へ配送し、別画面の更新を通常一覧として受理する誤りを検出します。</summary>
    [TestMethod]
    public void VirtualNormalLibraryRequestModes_CoverRootAndFullScanTrees()
    {
        MainViewUpdateMode[] treeModes =
        [
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FullScanAllChartsFilterSelected
        ];
        MainViewUpdateMode[] refreshModes =
        [
            MainViewUpdateMode.TreeViewFilterNotChanged,
            MainViewUpdateMode.KeywordFilterUpdated,
            MainViewUpdateMode.ModeFilterUpdated,
            MainViewUpdateMode.SortUpdated
        ];

        MainViewUpdateMode[] allModes = [.. Enum.GetValues(typeof(MainViewUpdateMode)).Cast<MainViewUpdateMode>()];
        foreach (MainViewUpdateMode treeMode in allModes)
        {
            bool expectedTreeSupport = treeModes.Contains(treeMode);
            Assert.AreEqual(
                expectedTreeSupport,
                RegularChartListOwner.IsDefaultVirtualRequest(treeMode, treeMode),
                treeMode + " tree support");
            foreach (MainViewUpdateMode requestMode in allModes)
            {
                bool expectedRequestSupport = expectedTreeSupport
                    && (requestMode == treeMode || refreshModes.Contains(requestMode));
                Assert.AreEqual(
                    expectedRequestSupport,
                    RegularChartListOwner.IsDefaultVirtualRequest(requestMode, treeMode),
                    treeMode + " request " + requestMode);
            }
        }
    }

    /// <summary>通常folderはfolder条件を適用し、全走査は適用しないことで全件表示へ残存条件を掛ける誤りを検出します。</summary>
    [TestMethod]
    public void VirtualNormalLibraryFullScanTree_IgnoresFolderFilter()
    {
        Assert.IsTrue(RegularChartListOwner.ShouldApplyDefaultTreeFilter(MainViewUpdateMode.FolderFilterSelected));
        Assert.IsFalse(RegularChartListOwner.ShouldApplyDefaultTreeFilter(MainViewUpdateMode.FullScanAllChartsFilterSelected));
    }

    /// <summary>全mode組合せで譜面集合とその検索・mode・sort更新だけを受理し、非対応画面へ部分一覧を配送する誤りを検出します。</summary>
    [TestMethod]
    public void VirtualChartSubsetRequestModes_CoverFilterAndSortUpdates()
    {
        MainViewUpdateMode[] treeModes =
        [
            MainViewUpdateMode.FileMissingFilterSelected,
            MainViewUpdateMode.FileMissingIgnoredFilterSelected,
            MainViewUpdateMode.DuplicateFilterSelected,
            MainViewUpdateMode.GarbledFilterSelected,
            MainViewUpdateMode.GarbleFixedFilterSelected,
            MainViewUpdateMode.UnregisteredFilterSelected,
            MainViewUpdateMode.ZeroNoteFilterSelected,
            MainViewUpdateMode.ChartInfoParseErrorFilterSelected,
            MainViewUpdateMode.NewlyInstalledFolderSelected,
            MainViewUpdateMode.PendingInstallFolderSelected
        ];
        MainViewUpdateMode[] refreshModes =
        [
            MainViewUpdateMode.TreeViewFilterNotChanged,
            MainViewUpdateMode.KeywordFilterUpdated,
            MainViewUpdateMode.ModeFilterUpdated,
            MainViewUpdateMode.SortUpdated
        ];

        MainViewUpdateMode[] allModes = [.. Enum.GetValues(typeof(MainViewUpdateMode)).Cast<MainViewUpdateMode>()];
        foreach (MainViewUpdateMode treeMode in allModes)
        {
            bool expectedTreeSupport = treeModes.Contains(treeMode);
            Assert.AreEqual(
                expectedTreeSupport,
                RegularChartListOwner.IsSubsetVirtualTreeMode(treeMode),
                treeMode + " tree support");
            foreach (MainViewUpdateMode requestMode in allModes)
            {
                bool expectedRequestSupport = expectedTreeSupport
                    && (requestMode == treeMode || refreshModes.Contains(requestMode));
                Assert.AreEqual(
                    expectedRequestSupport,
                    RegularChartListOwner.IsSubsetVirtualRequest(requestMode, treeMode),
                    treeMode + " request " + requestMode);
                bool expectedRequired = expectedRequestSupport || treeModes.Contains(requestMode);
                Assert.AreEqual(
                    expectedRequired,
                    RegularChartListOwner.IsSubsetVirtualRequired(requestMode, treeMode),
                    treeMode + " required " + requestMode);
            }
        }
    }

    /// <summary>BMS・bmson混在重複groupから双方のpathとbmson重複警告を持つsourceを生成し、形式による行・警告脱落を検出します。</summary>
    [TestMethod]
    public void DuplicateVirtualSourceRows_PreserveCommonFormatAndWarnings()
    {
        ChartFile bmsFile = CreateFile(
            Path.Combine("C:\\BMS", "DirA", "a.bms"),
            "BMS Alpha",
            "DirA",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
            Folder = Path.Combine("C:\\BMS", "DirB"),
            RawTitle = "Bmson Beta",
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var duplicateGroup = new DuplicateGroup(
            [
                ChartFileProjection.WithWarnings(
                    (bmsFile),
                    [ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")]),
                ChartFileProjection.WithWarnings(
                    (bmsonSong),
                    [ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")])
            ],
            [Path.Combine("C:\\BMS", "DirA"), Path.Combine("C:\\BMS", "DirB")]);

        Assert.IsTrue(RegularChartListOwner.TryResolveDuplicateSource(
            [duplicateGroup],
            DuplicateViewContext.CaptureGroup(duplicateGroup),
            out RegularChartListSubsetSource source));
        List<ChartListSourceRow> rows = ChartListSourceRow.BuildStandardLibraryRows(
            source.SourceCharts,
            ChartListSourceProjectionMode.PreserveSourceProjection,
            resourceHealthProjectionProvider: null,
            playlistReferenceDisplayProvider: null);

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(bmsFile.Path, rows.Single(row => row.Kind == ChartFileKind.Bms).Path);
        ChartListSourceRow bmsonRow = rows.Single(row => row.Kind == ChartFileKind.Bmson);
        Assert.AreEqual(bmsonSong.Path, bmsonRow.Path);
        Assert.IsNull(bmsonRow.Chart.Token);
        StringAssert.Contains(bmsonRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_DuplicateChart);
    }

    /// <summary>通常・重複・全走査・導入の実列factoryが配送するsortキーを混在sourceで受理し、UI列と仮想sortの接続不整合を検出します。</summary>
    [TestMethod]
    public void VirtualSortRouteColumns_CreateOrdersForAllChartListViewKinds()
    {
        var settings = new[]
        {
            new { Name = "STANDARD", Setting = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD) },
            new { Name = "DUPLICATE", Setting = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.DUPLICATE) },
            new { Name = "FULLSCAN", Setting = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.FULLSCAN) },
            new { Name = "INSTALL", Setting = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL) }
        };
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(CreateSampleSortFiles(), CreateSampleSortBmsons());

        foreach (var item in settings)
        {
            foreach (CustomTableColumn column in CustomTableColumnFactory.CreateMainColumns(item.Setting).Where(column => !string.IsNullOrWhiteSpace(column.SortMemberPath)))
            {
                Assert.IsTrue(
                    ChartListOrder.TryCreate(sourceRows, column.SortMemberPath, ListSortDirection.Ascending, out _),
                    item.Name + "." + column.Id + " uses unsupported SortMemberPath " + column.SortMemberPath);
            }
        }
    }

    /// <summary>昇降順とも副キーTitleは昇順・同値は元添字順を保つcacheだけを受理し、順序を保つ値変更・空入力も再利用できます。旧値の順序と全値同値化での不安定順序を検出します。</summary>
    [DataTestMethod]
    [DataRow(ListSortDirection.Ascending)]
    [DataRow(ListSortDirection.Descending)]
    public void CapturedOrder_CurrentValuesRespectDirectionTitleAndStableTies(ListSortDirection direction)
    {
        ChartListSourceRow Source(string title, string artist) => ChartListSourceRow.FromChartFile(
            CreateFile(title + ".bms", title, string.Empty, artist: artist));
        var rows = new List<ChartListSourceRow> { Source("Beta", "A"), Source("Alpha", "A"), Source("Alpha", "A"), Source("Middle", "B") };
        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.Artist), direction, out ChartListOrder order));
        CollectionAssert.AreEqual(direction == ListSortDirection.Ascending ? new[] { 1, 2, 0, 3 } : new[] { 3, 1, 2, 0 }, order.Indexes.ToArray());
        Assert.IsTrue(order.IsSortedFor(rows));
        rows[0] = Source("Zulu", "A");
        Assert.IsTrue(order.IsSortedFor(rows), "値が変わっても既存の並びが正しければ再利用します。");
        rows[0] = Source("Aardvark", "A");
        Assert.IsFalse(order.IsSortedFor(rows), "副キーの変化で位置が逆転した旧順序は使いません。");
        rows[0] = Source("Alpha", "A");
        Assert.IsFalse(order.IsSortedFor(rows), "同値化した行も元添字の0/1/2順へ揃えます。");
        Assert.IsTrue(ChartListOrder.TryCreate([], nameof(LibraryChartRow.Artist), direction, out ChartListOrder empty));
        Assert.IsTrue(empty.IsSortedFor([]));
        Assert.IsFalse(empty.IsSortedFor(rows));
    }

    /// <summary>実重複一覧の非対応sortは、要求受信でなくRowsとソート解除の実公開を待ち、owner/UIの解除とタイトル昇順の二行を確認します。通知は要求前に購読し、失敗時も解除します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task VirtualChartSubsetUnsupportedSort_ResetsToDefaultVirtualSort()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
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
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, testSettings);
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

    /// <summary>欠損・導入済みでは健全性投影を使い、保留・解析失敗等では使わない判断で画面条件に合わない警告投影を検出します。</summary>
    [TestMethod]
    public void VirtualChartSubsetResourceHealthProjection_MatchesMaterializedModes()
    {
        Assert.IsTrue(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.FileMissingFilterSelected));
        Assert.IsTrue(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.FileMissingIgnoredFilterSelected));
        Assert.IsTrue(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.NewlyInstalledFolderSelected));

        Assert.IsFalse(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.FullScanAllChartsFilterSelected));
        Assert.IsFalse(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.GarbledFilterSelected));
        Assert.IsFalse(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.UnregisteredFilterSelected));
        Assert.IsFalse(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.ZeroNoteFilterSelected));
        Assert.IsFalse(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.ChartInfoParseErrorFilterSelected));
        Assert.IsFalse(RegularChartListOwner.ShouldApplyResourceHealthProjection(MainViewUpdateMode.PendingInstallFolderSelected));
    }

    /// <summary>現在譜面更新でも旧snapshotとtokenを保持し、新基本値・導入先・参照表を読むことで更新先混同と専門投影脱落を検出します。</summary>
    [TestMethod]
    public void SourceRow_CapturedValuesKeepOldSnapshotAndReadSpecialistProjections()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Token = new OwnedChartToken(), Path = @"folder-b\old.bms", Title = "Old", Folder = "folder-b", Artist = "", Genre = "", Level = null, Mode = null, Tag = "", Md5 = "0123456789abcdef0123456789abcdef", Sha256 = "", RawTitle = "Old", RawArtist = "", LevelText = "" };
        PlaylistReferenceIndex playlistReferenceIndex = PlaylistReferenceIndex.Empty;
        ChartFileTransientState installDestinationState = ChartFileTransientState.Empty;
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(
            [file],
            playlistReferenceDisplayProvider: row => playlistReferenceIndex.Find(row.Chart),
            chartTransientStateProvider: (_, _) => installDestinationState);

        ChartListSourceRow previousSource = sourceRows[0];
        ChartFile captured = file;
        file = file with { Path = @"folder-a\new.bms", Title = "New", RawTitle = "New", Folder = "folder-a", Artist = "Artist", RawArtist = "Artist", Genre = "Genre", Mode = 7, Tag = "Tag", Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" };
        installDestinationState = ChartFileTransientState.FromInstallDestinationState(
            ChartFileProjection.WithPackageState(
                (file),
                "Destination",
                "Destination Title",
                "Destination Artist",
                []),
            forceInstallDestinationProjection: true);
        var referenceTable = new BMSTable
        {
            symbol = "REF",
            name = "Reference Table",
            entries = [new BMSTableEntry(file)]
        };
        playlistReferenceIndex.ReplaceTables([new PlaylistReferenceTableSnapshot(referenceTable, referenceTable.symbol, referenceTable.name, referenceTable.entries)]);

        sourceRows = BuildOwnerBackedSourceRows([file],
            playlistReferenceDisplayProvider: row => playlistReferenceIndex.Find(row.Chart),
            chartTransientStateProvider: (_, _) => installDestinationState);
        Assert.AreEqual("Old", captured.Title);
        Assert.AreEqual("Old", previousSource.Title);
        Assert.AreEqual(@"folder-b\old.bms", previousSource.Path);
        Assert.AreEqual(@"folder-b\old.bms", captured.Path);
        Assert.AreSame(captured.Token, sourceRows[0].Token);
        Assert.AreEqual("New", sourceRows[0].Title);
        Assert.AreEqual("Artist", sourceRows[0].Artist);
        Assert.AreEqual("Genre", sourceRows[0].Genre);
        Assert.AreEqual(@"folder-a\new.bms", sourceRows[0].Path);
        Assert.AreEqual("folder-a", sourceRows[0].Folder);
        Assert.AreEqual(7, sourceRows[0].Mode);
        Assert.AreEqual("Tag", sourceRows[0].Tag);
        Assert.AreEqual("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sourceRows[0].Hash);
        Assert.AreEqual("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", sourceRows[0].Sha256);
        Assert.AreEqual("Destination", sourceRows[0].InstallDestination);
        Assert.AreEqual("Destination Title", sourceRows[0].InstallDestinationTitle);
        Assert.AreEqual("Destination Artist", sourceRows[0].InstallDestinationArtist);
        Assert.AreEqual("REF", sourceRows[0].RefTablesSymbols);
        Assert.AreEqual("Reference Table", sourceRows[0].RefTablesNames);
    }

    /// <summary>SHA-256登録のbmson参照表symbol・名称をsourceと表示行へ反映し、MD5専用解決へ戻る誤りを検出します。</summary>
    [TestMethod]
    public void SourceRow_AndLibraryRowUsePlaylistReferenceProjectionForBmson()
    {
        ChartFile bmson = CreateBmsonSong();
        var table = new BMSTable
        {
            symbol = "BMSN",
            name = "Bmson Table",
            entries =
            [
                new TestablePlaylistEntry(null, bmson.Sha256)
            ]
        };
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceTables([new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)]);
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(
            null,
            [bmson],
            playlistReferenceDisplayProvider: row => index.Find(row.Hash, row.Sha256));
        var chartRow = LibraryChartRow.FromChartFile((bmson));
        chartRow.SetPlaylistReferenceDisplayProvider(row => index.Find(row.hash, row.sha256));

        Assert.AreEqual("BMSN", sourceRows[0].RefTablesSymbols);
        Assert.AreEqual("Bmson Table", sourceRows[0].RefTablesNames);
        Assert.AreEqual("BMSN", chartRow.RefTablesSymbols);
        Assert.AreEqual("Bmson Table", chartRow.RefTablesNames);
    }

    /// <summary>BMS・bmsonのスコア・解析・健全性・警告・導入先・参照表を持つ全sortキーを実体行と比較し、経路間の表示・整列差を検出します。</summary>
    [TestMethod]
    public void SourceRow_SortKeysMatchLibraryChartRowForBmsAndBmson()
    {
        ChartFile file = CreateFile(
            @"folder-a\bms.bms",
            "BmsTitle",
            "folder-a",
            artist: "BmsArtist",
            genre: "BmsGenre",
            level: 12,
            mode: 5,
            tag: "BmsTag",
            hash: "11111111111111111111111111111111",
            sha256: "1111111111111111111111111111111111111111111111111111111111111111");
        file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(CreateScore(file.Md5, ClearType.HARD, RankType.AA, perfect: 800, great: 200, totalNotes: 1200, maxCombo: 999, minBp: 12, ranking: 42, rankingNum: 500, rankingLastUpdate: new DateTime(2026, 5, 15, 1, 2, 3, DateTimeKind.Local), stdDevVal: 51.5, scoreDifficulty: 78.25), file.Path));
        BeMusicSeeker.Models.ChartDetails bmsChartInfo = CreateChartInfo(file.Sha256, file.Md5, level: 7, difficulty: 3, mainBpm: 150.5, total: 340.0);
        file = ChartFileProjection.WithMaintenance(file, CreateMaintenanceInfo(file.Path, file.Md5, 3));
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")] };
        ChartFile bmsInstallDestinationChart = ChartFileProjection.WithPackageState(
            (file),
            "Installed",
            "Installed Title",
            "Installed Artist",
            [],
            file.Warnings);
        var bmsTable = new BMSTable
        {
            symbol = "BMS",
            name = "BMS Table",
            entries = [new BMSTableEntry(file)]
        };
        PlaylistReferenceIndex playlistReferenceIndex = PlaylistReferenceIndex.Empty;
        playlistReferenceIndex.ReplaceTables([new PlaylistReferenceTableSnapshot(bmsTable, bmsTable.symbol, bmsTable.name, bmsTable.entries)]);
        ChartFileTransientState ResolveTransientState(ChartFile chart, bool includeWarningSnapshot)
        {
            return chart?.Kind == ChartFileKind.Bms
                ? ChartFileTransientState.FromInstallDestinationState(bmsInstallDestinationChart, includeWarningSnapshot, forceInstallDestinationProjection: true)
                : ChartFileTransientState.Empty;
        }
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"folder-b\bmson.bmson",
            Folder = "folder-b",
            Title = "BmsonTitle Another",
            RawTitle = "BmsonTitle",
            Subtitle = "Another",
            Artist = "BmsonArtist",
            RawArtist = "BmsonArtist",
            Genre = "BmsonGenre",
            ModeHint = "beat-7k",
            Mode = 7,
            Level = 9,
            LevelText = "9",
            Md5 = "22222222222222222222222222222222",
            Sha256 = "2222222222222222222222222222222222222222222222222222222222222222",
            ResourceHealthMaintenanceSnapshot = CreateMaintenanceInfo(@"folder-b\bmson.bmson", "22222222222222222222222222222222", 4)
        };
        BeMusicSeeker.Models.ChartDetails bmsonChartInfo = CreateChartInfo("2222222222222222222222222222222222222222222222222222222222222222", "22222222222222222222222222222222", level: 4, difficulty: 1, mainBpm: 99.5, total: 240.0);
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(
            [file],
            [bmson],
            playlistReferenceDisplayProvider: row => playlistReferenceIndex.Find(row.Chart),
            chartTransientStateProvider: ResolveTransientState,
            chartInfoProjectionProvider: CreateChartInfoProvider(bmsChartInfo, bmsonChartInfo));
        var bmsRow = LibraryChartRow.FromChartFile(file);
        bmsRow.SetChartTransientStateProvider(ResolveTransientState);
        bmsRow.SetPlaylistReferenceDisplayProvider(row => playlistReferenceIndex.Find(row.Chart));
        bmsRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(bmsChartInfo));
        var bmsonRow = LibraryChartRow.FromChartFile((bmson));
        bmsonRow.SetPlaylistReferenceDisplayProvider(row => playlistReferenceIndex.Find(row.Chart));
        bmsonRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(bmsonChartInfo));

        AssertSourceRowMatchesLibraryChartRow(sourceRows.Single(row => row.Kind == ChartFileKind.Bms), bmsRow);
        AssertSourceRowMatchesLibraryChartRow(sourceRows.Single(row => row.Kind == ChartFileKind.Bmson), bmsonRow);
    }

    /// <summary>解析の明示更新と同版のsource/provider接続変更で次回値を替え、数値版だけに依存するcacheの流用を検出します。</summary>
    [TestMethod]
    public void LibraryChartRow_DisplayRefreshInvalidatesProviderBackedChartCache()
    {
        ChartFile file = CreateFile(
            @"folder-a\chart-info.bms",
            "ChartInfo",
            "folder-a",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BeMusicSeeker.Models.ChartDetails currentChartInfo = CreateChartInfo(file.Sha256, file.Md5, level: 3, difficulty: 1, mainBpm: 120.0, total: 240.0);
        var row = LibraryChartRow.FromChartFile(file);
        row.SetChartInfoProjectionProvider(_ => currentChartInfo);

        Assert.AreEqual(3.0, row.ChartLevelSortKey);

        currentChartInfo = CreateChartInfo(file.Sha256, file.Md5, level: 9, difficulty: 4, mainBpm: 180.0, total: 360.0);
        row.RefreshDisplayForDataDependency(MainViewDataDependency.ChartInfo);

        Assert.AreEqual(9.0, row.ChartLevelSortKey);

        row.SetChartInfoProjectionProvider(_ => CreateChartInfo(file.Sha256, file.Md5, level: 12, difficulty: 1, mainBpm: 120, total: 240));
        Assert.AreEqual(12.0, row.ChartLevelSortKey);
        row.SetScoreProjectionProvider(_ => new ChartScoreSnapshot(ClearType.NO_PLAY, score: 100), () => 7);
        Assert.AreEqual(100, row.score);
        row.SetScoreProjectionProvider(_ => new ChartScoreSnapshot(ClearType.NO_PLAY, score: 900), () => 7);
        Assert.AreEqual(900, row.score);
        row.UpdateSourceProjection(file with { Title = "New direct source" });
        Assert.AreEqual("New direct source", row.Title);

        ChartListSourceRow source = BuildOwnerBackedSourceRows([file]).Single();
        var subsetRow = LibraryChartRow.FromSourceRow(source);
        _ = subsetRow.Chart;
        subsetRow.UpdateSubsetSourceProjection(BuildOwnerBackedSourceRows([file with { Title = "New subset source" }]).Single());
        Assert.AreEqual("New subset source", subsetRow.Title);
        subsetRow.SetChartTransientStateProvider((_, _) => ChartFileTransientState.FromChartFile(file with { InstallDestination = @"D:\First" }));
        Assert.AreEqual(@"D:\First", subsetRow.instl_dst);
        subsetRow.SetChartTransientStateProvider((_, _) => ChartFileTransientState.FromChartFile(file with { InstallDestination = @"D:\Second" }));
        Assert.AreEqual(@"D:\Second", subsetRow.instl_dst);

        int detailResolutions = 0;
        subsetRow.SetChartInfoProjectionProvider(_ => { detailResolutions++; return currentChartInfo; });
        _ = subsetRow.Chart;
        int before = detailResolutions;
        subsetRow.SetResourceHealthProjectionProvider(_ => ResourceHealthWarningProjection.Empty);
        _ = subsetRow.Chart;
        Assert.AreEqual(before + 1, detailResolutions, "通常のChart値が同じでも明示接続変更は失効します。");
    }

    /// <summary>非package sourceの高頻度列は同じ投影を再利用し、一行miss後にも再解決が列数に比例しないことを確認します。</summary>
    [TestMethod]
    public void LibraryChartRow_UnchangedColumnsReuseProjectionAfterOneLocalMiss()
    {
        ChartFile file = CreateFile(@"cache\reuse.bms", "Reuse", "cache");
        ChartDetails detail = CreateChartInfo(file.Sha256, file.Md5, 3, 1, 120, 240);
        var score = new ChartScoreSnapshot(ClearType.NO_PLAY, score: 100);
        int sourceTransient = 0, sourceDetail = 0, rowDetail = 0, rowScore = 0;
        ChartListSourceRow source = BuildOwnerBackedSourceRows([file],
            chartTransientStateProvider: (_, _) => { sourceTransient++; return ChartFileTransientState.Empty; },
            chartInfoProjectionProvider: _ => { sourceDetail++; return detail; }).Single();
        var row = LibraryChartRow.FromSourceRow(source);
        row.SetChartInfoProjectionProvider(_ => { rowDetail++; return detail; });
        row.SetScoreProjectionProvider(_ => { rowScore++; return score; }, () => 7);
        foreach (int level in new[] { 3, 9 })
        {
            if (level == 9)
            {
                detail = CreateChartInfo(file.Sha256, file.Md5, 9, 1, 120, 240);
                row.RefreshDisplayForDataDependency(MainViewDataDependency.ChartInfo);
            }
            ChartFile captured = row.Chart;
            (int sourceTransient, int sourceDetail, int rowDetail, int rowScore) counts = (sourceTransient, sourceDetail, rowDetail, rowScore);
            Assert.AreEqual((double)level, row.ChartLevelSortKey);
            Assert.AreEqual(100, row.score);
            _ = row.Title; _ = row.path; _ = row.instl_dst; _ = row.status; _ = row.HasHighlightedWarning;
            Assert.AreSame(captured, row.Chart);
            Assert.AreEqual(counts, (sourceTransient, sourceDetail, rowDetail, rowScore));
            Assert.AreEqual(level == 3 ? 1 : 2, rowDetail);
            Assert.AreEqual(level == 3 ? 1 : 2, rowScore);
        }
    }

    /// <summary>健全性警告を最新投影へ替えても重複警告を保持し、sourceと実体行のdigestを一致させて経路別警告欠けを検出します。</summary>
    [TestMethod]
    public void SourceRow_WarningDigestMatchesLibraryChartRowWithResourceProjection()
    {
        ChartFile file = CreateFile(
            @"folder-a\warning.bms",
            "Warning",
            "folder-a",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceWavMissing), ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "stale resource warning")] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")] };
        var projection = new ResourceHealthWarningProjection(
            1,
            [ChartWarning.Create(ChartWarningKind.ResourceBgaMissing, "projected resource warning")],
            isIgnored: false);
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(
            [file],
            resourceHealthProjectionProvider: _ => projection);
        var chartRow = LibraryChartRow.FromChartFile(file);
        chartRow.SetResourceHealthProjectionProvider(_ => projection);

        Assert.AreEqual(chartRow.WarningDigestText, sourceRows[0].WarningDigestText);
        StringAssert.Contains(sourceRows[0].WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(sourceRows[0].WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
    }

    private static ChartListVirtualView CreateView(out Func<int> getCreatedCount, int distinctFolderCount = -1)
    {
        List<ChartFile> files =
        [
            CreateFile(@"folder-b\charlie.bms", "Charlie", "folder-b"),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a"),
            CreateFile(@"folder-a\bravo.bms", "Bravo", "folder-a")
        ];
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files);
        var order = ChartListOrder.CreateTitleAscending(sourceRows);
        int localCreatedCount = 0;
        var view = new ChartListVirtualView(
            sourceRows,
            order,
            row =>
            {
                localCreatedCount++;
                return MaterializeSourceRow(row);
            },
            distinctFolderCount);
        getCreatedCount = () => localCreatedCount;
        return view;
    }

    private static LibraryChartRow MaterializeSourceRow(ChartListSourceRow row)
    {
        ChartFile? chart = row?.Chart;
        if (chart?.Token != null)
        {
            var chartRow = LibraryChartRow.FromChartFile(chart);
            chartRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(chart.ChartInfo));
            return chartRow;
        }
        return LibraryChartRow.FromChartFile(chart);
    }

    private static void AssertVirtualOrderMatchesExistingSort(string columnName, ListSortDirection direction)
    {
        List<ChartFile> files = CreateSampleSortFiles();
        List<ChartFile> bmsons = CreateSampleSortBmsons();
        Dictionary<string, ChartFileTransientState> bmsInstallDestinationStates = CreateSampleSortInstallDestinationStates(files);
        ChartFileTransientState ResolveTransientState(ChartFile chart, bool includeWarningSnapshot)
        {
            return chart != null
                && bmsInstallDestinationStates.TryGetValue(chart.Path ?? string.Empty, out ChartFileTransientState? state)
                    ? state!
                    : ChartFileTransientState.Empty;
        }
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files, bmsons, chartTransientStateProvider: ResolveTransientState);
        bool created = ChartListOrder.TryCreate(sourceRows, columnName, direction, out ChartListOrder order);
        Assert.IsTrue(created);
        var view = new ChartListVirtualView(
            sourceRows,
            order,
            MaterializeSourceRow);
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = columnName,
            Direction = direction
        };

        IEnumerable<LibraryChartRow> bmsRows = files.Select(file =>
        {
            var row = LibraryChartRow.FromChartFile(file);
            row.SetChartTransientStateProvider(ResolveTransientState);
            return row;
        });
        List<LibraryChartRow> legacySorted = LibraryChartRowSortEngine.SortForMainView(
            bmsRows.Concat(bmsons.Select(chart => LibraryChartRow.FromChartFile(chart))),
            ChartListSortSpecification.Create(
                sortParameters.ColumnsName,
                sortParameters.Direction,
                hasValue: true),
            isPlaylistDetailView: false,
            out _);

        CollectionAssert.AreEqual(
            legacySorted.Select(row => row.path).ToArray(),
            Enumerable.Range(0, view.Count).Select(index => ((LibraryChartRow)view[index]).path).ToArray(),
            columnName + " " + direction + " order mismatch.");
    }

    private static List<ChartFile> CreateSampleSortFiles()
    {
        return
        [
            CreateFile(
                @"folder-a\z_item10.bms",
                "item10",
                "folder-a",
                artist: "Zulu",
                genre: "GenreC",
                level: 10,
                mode: 14,
                tag: "TagC",
                hash: "cccccccccccccccccccccccccccccccc",
                sha256: "3333333333333333333333333333333333333333333333333333333333333333",
                scoreSeed: 3,
                chartSeed: 3,
                maintenanceSeed: 3,
                warningSeed: 3),
            CreateFile(
                @"folder-a\a_item2.bms",
                "item2",
                "folder-a",
                artist: "Alpha",
                genre: "GenreB",
                level: 2,
                mode: 5,
                tag: "TagB",
                hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256: "1111111111111111111111111111111111111111111111111111111111111111",
                scoreSeed: 1,
                chartSeed: 1,
                maintenanceSeed: 1,
                warningSeed: 1),
            CreateFile(
                @"folder-b\m_alpha.bms",
                "Alpha",
                "folder-b",
                artist: "Middle",
                genre: "GenreA",
                level: 7,
                mode: null,
                tag: "TagA",
                hash: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                sha256: "2222222222222222222222222222222222222222222222222222222222222222",
                scoreSeed: 2,
                chartSeed: 2,
                maintenanceSeed: 2,
                warningSeed: 2)
        ];
    }

    private static Dictionary<string, ChartFileTransientState> CreateSampleSortInstallDestinationStates(IEnumerable<ChartFile> files)
    {
        var states = new Dictionary<string, ChartFileTransientState>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile file in files ?? [])
        {
            string suffix = file.Md5?.StartsWith("a", StringComparison.OrdinalIgnoreCase) == true
                ? "A"
                : file.Md5?.StartsWith("b", StringComparison.OrdinalIgnoreCase) == true
                    ? "B"
                    : "C";
            ChartFile chart = ChartFileProjection.WithPackageState(
                (file),
                "Install" + suffix,
                "InstallTitle" + suffix,
                "InstallArtist" + suffix,
                [],
                file.Warnings);
            states[file.Path] = ChartFileTransientState.FromInstallDestinationState(
                chart,
                includeWarningSnapshot: false,
                forceInstallDestinationProjection: true);
        }
        return states;
    }

    private static List<ChartFile> CreateSampleSortBmsons()
    {
        return
        [
            ChartTestValues.Empty(ChartFileKind.Bmson) with {
                Path = @"folder-c\bmson-beta.bmson",
                Folder = "folder-c",
                RawTitle = "BmsonBeta",
                RawArtist = "Beta",
                Genre = "GenreD",
                ModeHint = "beat-7k",
                Level = 8,
                Md5 = "dddddddddddddddddddddddddddddddd",
                Sha256 = "4444444444444444444444444444444444444444444444444444444444444444",
                ResourceHealthMaintenanceSnapshot = CreateMaintenanceInfo(@"folder-c\bmson-beta.bmson", "dddddddddddddddddddddddddddddddd", 4)
            },
            ChartTestValues.Empty(ChartFileKind.Bmson) with {
                Path = @"folder-d\bmson-alpha.bmson",
                Folder = "folder-d",
                RawTitle = "BmsonAlpha",
                RawArtist = "AlphaBmson",
                Genre = "Genre0",
                ModeHint = "beat-5k",
                Level = 3,
                Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                Sha256 = "5555555555555555555555555555555555555555555555555555555555555555",
                ResourceHealthMaintenanceSnapshot = CreateMaintenanceInfo(@"folder-d\bmson-alpha.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", 5)
            }
        ];
    }

    private static ChartFile CreateFile(
        string path,
        string title,
        string folder,
        string artist = "",
        string genre = "",
        int? level = null,
        int? mode = null,
        string tag = "",
        string hash = "0123456789abcdef0123456789abcdef",
        string sha256 = "",
        int? scoreSeed = null,
        int? chartSeed = null,
        int? maintenanceSeed = null,
        int? warningSeed = null)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Path = path, Title = title, Folder = folder, Artist = artist, Genre = genre, Level = level, Mode = mode, Tag = tag, Md5 = hash, Sha256 = sha256, RawTitle = title, RawArtist = artist, LevelText = level?.ToString() ?? "" };
        if (scoreSeed.HasValue)
        {
            int seed = scoreSeed.Value;
            file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(CreateScore(
                file.Md5,
                seed == 1 ? ClearType.HARD : seed == 2 ? ClearType.EASY : ClearType.FC,
                seed == 1 ? RankType.A : seed == 2 ? RankType.AA : RankType.B,
                perfect: 300 + seed * 100,
                great: 50 + seed * 20,
                totalNotes: 600 + seed * 100,
                maxCombo: 400 + seed * 10,
                minBp: seed == 2 ? -1 : seed * 5,
                ranking: seed * 10,
                rankingNum: 100,
                rankingLastUpdate: new DateTime(2026, 5, 15, seed, 0, 0, DateTimeKind.Local),
                stdDevVal: 40.0 + seed,
                scoreDifficulty: 70.0 + seed), file.Path));
        }
        if (maintenanceSeed.HasValue)
        {
            file = ChartFileProjection.WithMaintenance(file, CreateMaintenanceInfo(file.Path, file.Md5, maintenanceSeed.Value));
        }
        if (warningSeed.HasValue)
        {
            switch (warningSeed.Value)
            {
                case 1:
                    file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, "zero note warning")] };
                    break;
                case 2:
                    file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")] };
                    break;
                default:
                    file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ChartInfoParseFailure), ChartWarning.Create(ChartWarningKind.ChartInfoParseFailure, "chart info warning")] };
                    break;
            }
        }
        return file;
    }

    private static void AssertRegistryDependency(IEnumerable<ChartListOrderColumnMetadata> metadata, MainViewDataDependency dependency, string columnName)
    {
        Assert.AreEqual(
            dependency,
            metadata.Single(column => column.NormalizedColumnName == columnName).Dependency,
            columnName);
    }

    private static void AssertSourceRowMatchesLibraryChartRow(ChartListSourceRow sourceRow, LibraryChartRow chartRow)
    {
        Assert.AreEqual(chartRow.Title, sourceRow.Title);
        Assert.AreEqual(chartRow.Artist, sourceRow.Artist);
        Assert.AreEqual(chartRow.genre, sourceRow.Genre);
        Assert.AreEqual(chartRow.Level, sourceRow.Level);
        Assert.AreEqual(chartRow.level, sourceRow.LevelValue);
        Assert.AreEqual(chartRow.Folder, sourceRow.Folder);
        Assert.AreEqual(chartRow.path, sourceRow.Path);
        Assert.AreEqual(chartRow.mode, sourceRow.Mode);
        Assert.AreEqual(chartRow.tag, sourceRow.Tag);
        Assert.AreEqual(chartRow.hash, sourceRow.Hash);
        Assert.AreEqual(chartRow.sha256, sourceRow.Sha256);
        Assert.AreEqual(chartRow.clear, sourceRow.Clear);
        Assert.AreEqual(chartRow.rank, sourceRow.Rank);
        Assert.AreEqual(chartRow.rateDouble, sourceRow.RateDouble);
        Assert.AreEqual(chartRow.rate, sourceRow.Rate);
        Assert.AreEqual(chartRow.score, sourceRow.Score);
        Assert.AreEqual(chartRow.totalnotes, sourceRow.TotalNotes);
        Assert.AreEqual(chartRow.maxcombo, sourceRow.MaxCombo);
        Assert.AreEqual(chartRow.minbp, sourceRow.MinBp);
        Assert.AreEqual(chartRow.ranking, sourceRow.Ranking);
        Assert.AreEqual(chartRow.rankingNum, sourceRow.RankingNum);
        Assert.AreEqual(chartRow.rankingString, sourceRow.RankingString);
        Assert.AreEqual(chartRow.rankingLastupdate, sourceRow.RankingLastUpdate);
        Assert.AreEqual(chartRow.stddevVal, sourceRow.StdDevVal);
        Assert.AreEqual(chartRow.scoreDifficulty, sourceRow.ScoreDifficulty);
        Assert.AreEqual(chartRow.ChartLevelSortKey, sourceRow.ChartLevelSortKey);
        Assert.AreEqual(chartRow.ChartDifficultySortKey, sourceRow.ChartDifficultySortKey);
        Assert.AreEqual(chartRow.ChartMainBpmSortKey, sourceRow.ChartMainBpmSortKey);
        Assert.AreEqual(chartRow.ChartMaxBpmSortKey, sourceRow.ChartMaxBpmSortKey);
        Assert.AreEqual(chartRow.ChartMinBpmSortKey, sourceRow.ChartMinBpmSortKey);
        Assert.AreEqual(chartRow.ChartDurationSortKey, sourceRow.ChartDurationSortKey);
        Assert.AreEqual(chartRow.ChartJudgeSortKey, sourceRow.ChartJudgeSortKey);
        Assert.AreEqual(chartRow.ChartFeatureSortKey, sourceRow.ChartFeatureSortKey);
        Assert.AreEqual(chartRow.ChartNotes, sourceRow.ChartNotes);
        Assert.AreEqual(chartRow.ChartLongNotes, sourceRow.ChartLongNotes);
        Assert.AreEqual(chartRow.ChartScratchNotes, sourceRow.ChartScratchNotes);
        Assert.AreEqual(chartRow.ChartTotalSortKey, sourceRow.ChartTotalSortKey);
        Assert.AreEqual(chartRow.ChartTotalPerNoteSortKey, sourceRow.ChartTotalPerNoteSortKey);
        Assert.AreEqual(chartRow.ChartDensitySortKey, sourceRow.ChartDensitySortKey);
        Assert.AreEqual(chartRow.ChartPeakDensitySortKey, sourceRow.ChartPeakDensitySortKey);
        Assert.AreEqual(chartRow.ChartEndDensitySortKey, sourceRow.ChartEndDensitySortKey);
        Assert.AreEqual(chartRow.ChartSoflanCount, sourceRow.ChartSoflanCount);
        Assert.AreEqual(chartRow.instl_dst, sourceRow.InstallDestination);
        Assert.AreEqual(chartRow.InstallDestinationTitle, sourceRow.InstallDestinationTitle);
        Assert.AreEqual(chartRow.InstallDestinationArtist, sourceRow.InstallDestinationArtist);
        Assert.AreEqual(chartRow.RefTablesSymbols, sourceRow.RefTablesSymbols);
        Assert.AreEqual(chartRow.WarningDigestText, sourceRow.WarningDigestText);
        Assert.AreEqual(chartRow.WAVHealth, sourceRow.WAVHealth);
        Assert.AreEqual(chartRow.BGAHealth, sourceRow.BGAHealth);
        Assert.AreEqual(chartRow.MovieHealth, sourceRow.MovieHealth);
        Assert.AreEqual(chartRow.encoding, sourceRow.EncodingName);
    }

    private static ChartListSourceRow CreateOwnerBackedSourceRow(ChartFile file)
    {
        return ChartListSourceRow.FromChartFile(
            (file),
            ChartListSourceProjectionMode.OwnerBacked);
    }

    private static List<ChartListSourceRow> BuildOwnerBackedSourceRows(
        IEnumerable<ChartFile>? files,
        IEnumerable<ChartFile>? bmsonSongs = null,
        Func<ChartListSourceRow, ResourceHealthWarningProjection>? resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay>? playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState>? chartTransientStateProvider = null,
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails>? chartInfoProjectionProvider = null)
    {
        List<ChartFile> charts =
        [
            .. (files ?? [])
                .Where(file => file != null)
                .Select(file => (file)),
            .. (bmsonSongs ?? [])
                .Where(song => song != null && !string.IsNullOrWhiteSpace(song.Path))
                .OrderBy(song => song.Path, StringComparer.OrdinalIgnoreCase)
                .Select(song => (song)),
        ];
        return ChartListSourceRow.BuildStandardLibraryRows(
            charts,
            ChartListSourceProjectionMode.OwnerBacked,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider,
            chartInfoProjectionProvider);
    }

    private static ChartFile CreateBmsonSong()
    {
        return ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"folder-b\bmson.bmson",
            Folder = "folder-b",
            Title = "BmsonTitle Subtitle",
            RawTitle = "BmsonTitle",
            Subtitle = "Subtitle",
            Artist = "BmsonArtist",
            RawArtist = "BmsonArtist",
            Genre = "BmsonGenre",
            ModeHint = "beat-7k",
            Mode = 7,
            Level = 6,
            LevelText = "6",
            Md5 = "22222222222222222222222222222222",
            Sha256 = "2222222222222222222222222222222222222222222222222222222222222222",
            ResourceHealthMaintenanceSnapshot = CreateMaintenanceInfo(@"folder-b\bmson.bmson", "22222222222222222222222222222222", 3)
        };
    }

    private static BMSScore CreateScore(
        string hash,
        ClearType clear,
        RankType rank,
        int perfect,
        int great,
        int totalNotes,
        int maxCombo,
        int minBp,
        int ranking,
        int rankingNum,
        DateTime rankingLastUpdate,
        double stdDevVal,
        double scoreDifficulty)
    {
        return new BMSScore
        {
            hash = hash,
            clear = clear,
            rank = rank,
            perfect = perfect,
            great = great,
            totalnotes = totalNotes,
            maxcombo = maxCombo,
            minbp = minBp,
            ranking = ranking,
            rankingNum = rankingNum,
            rankingLastupdate = rankingLastUpdate,
            stddevVal = stdDevVal,
            scoreDifficulty = scoreDifficulty
        };
    }

    private static BeMusicSeeker.Models.ChartDetails CreateChartInfo(string sha256, string md5, int level, int difficulty, double mainBpm, double total)
    {
        return new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = sha256,
            md5 = md5,
            level = level,
            difficulty = difficulty,
            difficulty_defined = true,
            mainbpm = mainBpm,
            maxbpm = mainBpm + 25.0,
            minbpm = Math.Max(1.0, mainBpm - 25.0),
            length = 120000 + level * 1000,
            judge = 100 + difficulty,
            feature = difficulty + 1,
            notes = 1000 + level * 10,
            ln = 100 + level,
            s = 20 + difficulty,
            ls = 5 + difficulty,
            total = total,
            total_defined = true,
            density = 8.0 + level,
            peakdensity = 16.0 + level,
            enddensity = 4.0 + difficulty,
            speedchange_count = difficulty + 2
        };
    }

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateChartInfoProvider(params BeMusicSeeker.Models.ChartDetails[] rows)
    {
        return chart => ResolveChartInfoByIdentity(chart, rows);
    }

    private static BeMusicSeeker.Models.ChartDetails ResolveChartInfoByIdentity(ChartFile chart, IEnumerable<BeMusicSeeker.Models.ChartDetails> rows)
    {
        if (chart == null)
        {
            return null!;
        }
        return (rows ?? [])
            .Where(row => row != null)
            .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Sha256) && string.Equals(row.sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase))
            ?? (rows ?? [])
                .Where(row => row != null)
                .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Md5) && string.Equals(row.md5, chart.Md5, StringComparison.OrdinalIgnoreCase))
            ?? null!;
    }

    private static ResourceHealthMaintenanceSnapshot CreateMaintenanceInfo(string path, string hash, int seed)
    {
        return new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = path, Hash = hash, Encoding = seed % 2 == 0 ? "utf-8" : "shift_jis", WavFilesDefined = 100, WavFilesExisting = 20 + seed * 10, BgaFilesDefined = 50, BgaFilesExisting = 10 + seed * 5, MovieFilesDefined = 20, MovieFilesExisting = seed };
    }


    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        internal TestablePlaylistEntry(string? md5Value, string? sha256Value)
        {
            md5 = md5Value;
            sha256 = sha256Value;
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

    private static PlayHistoryTerminalHarness CreatePlayHistoryTerminalHarness(
        out PlayHistoryPresentationState state,
        out MainChartListViewModel table)
    {
        return CreatePlayHistoryTerminalHarness(out state, out table, _ => { });
    }

    private static PlayHistoryTerminalHarness CreatePlayHistoryTerminalHarness(
        out PlayHistoryPresentationState state,
        out MainChartListViewModel table,
        Action<string> publishPropertyChanged)
    {
        return CreatePlayHistoryTerminalHarness(out state, out table, publishPropertyChanged, _ => { });
    }

    private static PlayHistoryTerminalHarness CreatePlayHistoryTerminalHarness(
        out PlayHistoryPresentationState state,
        out MainChartListViewModel table,
        Action<string> publishPropertyChanged,
        Action<PlaylistWorkspaceViewModel> configureWorkspace)
    {
        var workflowOwner = new PlayHistoryWorkflowOwner();
        workflowOwner.PropertyChanged += (_, e) => publishPropertyChanged(e.PropertyName!);
        state = workflowOwner.PresentationState;
        table = new MainChartListViewModel();
        var playlistBuildState = new PlaylistDetailBuildState();
        var playlistViewState = new PlaylistDetailViewState();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            playlistBuildState,
            playlistViewState,
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        configureWorkspace(workspace);
        return new PlayHistoryTerminalHarness(
            workflowOwner,
            table,
            workspace);
    }

    private sealed class PlayHistoryTerminalHarness
    {
        private readonly PlayHistoryWorkflowOwner workflowOwner;
        private readonly MainChartListViewModel table;
        private readonly PlaylistWorkspaceViewModel workspace;
        internal PlayHistoryTerminalHarness(
            PlayHistoryWorkflowOwner workflowOwner,
            MainChartListViewModel table,
            PlaylistWorkspaceViewModel workspace)
        {
            this.workflowOwner = workflowOwner;
            this.table = table;
            this.workspace = workspace;
        }

        internal PlayHistoryTerminalCommitResult TryApply(PlayHistoryTerminalRequest request)
        {
            request.DetailSourceRetirement ??=
                workspace.PrepareDetailSourceRetirementWithoutPublishing();
            return workflowOwner.ApplyTerminal(
                request,
                table,
                workspace);
        }

        internal void PrepareThenInvalidateDetailRetirement(PlayHistoryTerminalRequest request)
        {
            request.DetailSourceRetirement =
                workspace.PrepareDetailSourceRetirementWithoutPublishing();
            workspace.PrepareDetailSourceRetirementWithoutPublishing();
        }
    }

    private static PlayHistoryTerminalRequest CreatePlayHistoryTerminalRequest(System.Collections.IList rows, string summaryText, long requestId)
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        var columnSelection = new MainChartListColumnSelection(
            settings,
            reused: false,
            elapsedMs: 0,
            MainViewUpdateMode.PlayHistorySelected,
            System.Windows.Visibility.Collapsed,
            new PlaylistSummaryColumnSettings());
        var viewState = new PlayHistoryViewState(
            requestId,
            PlayHistoryPeriodRequest.All(),
            [],
            [],
            [],
            [],
            PlayHistoryProvider.Lr2,
            default,
            sourceCount: 0,
            new SortSnapshot(null, null, revision: 0L),
            keywordFilter: string.Empty,
            keywordFilterRevision: 0,
            PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: 0);
        return new PlayHistoryTerminalRequest
        {
            ViewState = viewState,
            ColumnSelection = columnSelection,
            SummaryCards = [],
            DiagnosticText = string.Empty,
            MainRowsRequest = new MainChartListRowsApplyRequest
            {
                Rows = rows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Reset,
                Summary = MainChartListSummaryUpdate.Explicit(summaryText),
                Stopwatch = Stopwatch.StartNew()
            }
        };
    }

    private static PlayHistoryViewState CreateEmptyPlayHistoryViewState(long requestId)
    {
        return new PlayHistoryViewState(
            requestId,
            PlayHistoryPeriodRequest.All(),
            allProjectedRows: [],
            filterSourceRows: [],
            projectedRows: [],
            diagnostics: [],
            PlayHistoryProvider.Lr2,
            schemaStatus: default,
            sourceCount: 0,
            new SortSnapshot(null, null, revision: 0L),
            keywordFilter: string.Empty,
            keywordFilterRevision: 0,
            PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: 0);
    }

    private static void SelectSummaryFilter(PlayHistoryWorkflowOwner owner, string filterKey)
    {
        PlayHistorySummaryCard card = PlayHistoryPresentationState.CreateSummaryCards(
                PlayHistoryPeriodSummary.FromRows("All", []),
                PlayHistoryProvider.Beatoraja)
            .Single(item => item.FilterKey == filterKey);
        owner.PresentationState.SetSummaryCards([card]);
        owner.ToggleSummaryFilterCommand.Execute(card);
    }

    /// <summary>構築時の全行読取りを検出し、件数だけを許可するfixtureです。</summary>
    private sealed class ReadForbiddenSourceRows(int count) : IReadOnlyList<ChartListSourceRow>
    {
        public int Count => count;
        public ChartListSourceRow this[int index] => throw new InvalidOperationException("Source rows must not be read during view construction.");
        public IEnumerator<ChartListSourceRow> GetEnumerator() => throw new InvalidOperationException("Source rows must not be enumerated during view construction.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose()
        {
            throw new InvalidOperationException("dispose failed");
        }
    }

    private sealed class CountingDisposable : IDisposable
    {
        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
