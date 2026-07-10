using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ChartListVirtualViewTests
{
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

    [TestMethod]
    public void SummaryConverter_UsesMetadataWithoutEnumeratingRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount);
        var converter = new ChartRowsViewToSummaryTextConverter();

        object text = converter.Convert(view, typeof(string), null, CultureInfo.InvariantCulture);

        StringAssert.StartsWith(text.ToString(), "[3");
        Assert.AreEqual(-1, view.DistinctFolderCount);
        Assert.IsFalse(text.ToString().Contains("/"));
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, getCreatedCount());
    }

    [TestMethod]
    public void SummaryConverter_UsesSuppliedFolderCountWithoutEnumeratingRows()
    {
        ChartListVirtualView view = CreateView(out Func<int> getCreatedCount, distinctFolderCount: 2);
        var converter = new ChartRowsViewToSummaryTextConverter();

        object text = converter.Convert(view, typeof(string), null, CultureInfo.InvariantCulture);

        StringAssert.StartsWith(text.ToString(), "[3");
        StringAssert.Contains(text.ToString(), "/ 2");
        Assert.AreEqual(2, view.DistinctFolderCount);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(0, getCreatedCount());
    }

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

        LibraryChartRow row = LibraryChartRow.FromChartFile(chart);

        Assert.IsNull(row.Chart.GetBmsStorageOwner());
        Assert.IsNull(row.Chart.GetBmsonStorageOwner());
        Assert.AreSame(chart, row.Chart);
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

    [TestMethod]
    public void SummaryConverter_UsesChartFileFolderForMetadataOnlyRows()
    {
        ChartFile first = ChartFileProjection.FromBmsMetadata(
            @"D:\Charts\Root\alpha.bms",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            null,
            "Alpha",
            "Artist",
            "Genre",
            "Root",
            string.Empty,
            12,
            7,
            null);
        ChartFile second = ChartFileProjection.FromBmsMetadata(
            @"D:\Charts\Other\beta.bms",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            null,
            "Beta",
            "Artist",
            "Genre",
            "Other",
            string.Empty,
            10,
            7,
            null);
        var converter = new ChartRowsViewToSummaryTextConverter();

        object text = converter.Convert(
            new[] { LibraryChartRow.FromChartFile(first), LibraryChartRow.FromChartFile(second) },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);

        StringAssert.StartsWith(text.ToString(), "[2");
        StringAssert.Contains(text.ToString(), "/ 2");
    }

    [TestMethod]
    public void SummaryConverter_UsesLiveOwnerFolderForOwnerBackedChartRows()
    {
        BMSFile file = CreateFile(
            @"D:\Charts\Old\alpha.bms",
            "Alpha",
            "Old",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        LibraryChartRow row = LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(file));
        file.path = @"D:\Charts\New\alpha.bms";
        var converter = new ChartRowsViewToSummaryTextConverter();

        object text = converter.Convert(
            new[] { row, row },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);

        Assert.AreEqual("New", row.Folder);
        StringAssert.StartsWith(text.ToString(), "[2");
        Assert.IsFalse(text.ToString().Contains("/"));
    }

    [TestMethod]
    public void LibraryChartRow_FromWarninglessOwnerBackedProjectionUsesLiveOwnerWarnings()
    {
        BMSFile file = CreateFile(
            @"D:\Charts\Warning\alpha.bms",
            "Alpha",
            "Warning",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetWarning(ChartWarningKind.DuplicateChart, "live warning");

        LibraryChartRow row = LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false));

        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        StringAssert.Contains(row.WarningTooltipText, "live warning");
    }

    [TestMethod]
    public void VirtualChartSubsetRow_PreservesOwnerBackedBmsonTransientState()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false),
            "C:\\Installed\\Bmson",
            "Installed Bmson",
            "Installed Artist",
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        ChartListSourceRow sourceRow = ChartListSourceRow.FromChartFile(
            ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false),
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot));

        LibraryChartRow row = MainWindowViewModel.CreateVirtualChartSubsetRowForTest(sourceRow);

        Assert.AreSame(bmson, row.Chart.GetBmsonStorageOwner());
        Assert.AreEqual("C:\\Installed\\Bmson", row.Chart.InstallDestination);
        Assert.AreEqual("Installed Bmson", row.InstallDestinationTitle);
        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    [TestMethod]
    public void VirtualChartSubsetRow_OverlaysOwnerBackedBmsTransientStateWithoutWritingStorageRow()
    {
        BMSFile file = CreateFile(
            @"D:\Charts\InstallDestination\alpha.bms",
            "Alpha",
            "Install",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
            "C:\\Installed\\Bms",
            "Installed BMS",
            "Installed Artist",
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        ChartListSourceRow sourceRow = ChartListSourceRow.FromChartFile(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot));

        LibraryChartRow row = MainWindowViewModel.CreateVirtualChartSubsetRowForTest(sourceRow);

        Assert.AreSame(file, row.Chart.GetBmsStorageOwner());
        Assert.AreEqual("C:\\Installed\\Bms", row.Chart.InstallDestination);
        Assert.AreEqual("Installed BMS", row.InstallDestinationTitle);
        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    [TestMethod]
    public void VirtualChartSubsetRow_UsesScoreProviderForOwnerBackedBmsProjection()
    {
        BMSFile file = CreateFile(
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
        ChartListSourceRow sourceRow = ChartListSourceRow.FromChartFile(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false, includeScoreSnapshot: false),
            ChartListSourceProjectionMode.OwnerBacked,
            scoreSnapshotVersionProvider: () => 1,
            scoreSnapshotProjectionProvider: row => string.Equals(row.Path, file.path, StringComparison.OrdinalIgnoreCase) ? score : null);

        LibraryChartRow row = MainWindowViewModel.CreateVirtualChartSubsetRowForTest(sourceRow);

        Assert.AreSame(file, row.Chart.GetBmsStorageOwner());
        Assert.AreEqual(ClearType.HARD, row.clear);
        Assert.AreEqual(RankType.AA, row.rank);
        Assert.AreEqual(1800, row.score);
        Assert.AreEqual(90, row.rate);
        Assert.AreEqual(3, row.minbp);
    }

    [TestMethod]
    public void ChartListSourceRow_BmsTransientInstallEstimationProjectionClearsStaleSourceWarning()
    {
        BMSFile file = CreateFile(
            @"D:\Charts\InstallDestination\clear.bms",
            "Clear",
            "Install",
            hash: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile sourceWithStaleWarning = ChartFileProjection.WithWarnings(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
            [
                ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "stale warning"),
                ChartWarning.Create(ChartWarningKind.DuplicateChart, "projection-only duplicate warning")
            ]);
        ChartFile clearedProjection = ChartFileProjection.WithWarnings(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
            []);

        ChartListSourceRow sourceRow = ChartListSourceRow.FromChartFile(
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

    [TestMethod]
    public void PlayStartBmsFile_UsesChartInstallDestinationWhenTemporaryRenameChangesPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        bool originalUsePlayerLR2body = BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body;
        bool originalOperationModeLR2Db = BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB;
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
            BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body = false;
            BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB = false;
            var viewModel = new MainWindowViewModel();
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            var library = new BMSLibrary(songDbPath);
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            var player = new RecordingBmsPlayer();
            typeof(MainWindowViewModel).GetField("bmsPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, player);
            var file = new TestableBmsFile();
            file.Apply(sourceChartPath, "Playable", "Source");
            ChartFile playableChart = ChartFileProjection.FromBmsFile(file);
            PackageChartEntry transientEntry = PackageChartEntry.FromChart(playableChart);
            transientEntry.SetInstallDestinationPathOnly(destinationDirectoryPath);
            typeof(MainWindowViewModel)
                .GetMethod("UpdateSharedChartTransientStates", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [new[] { transientEntry.Chart }, true]);
            var sourceRow = ChartListSourceRow.FromChartFile(playableChart);
            var viewRow = (LibraryChartRow)typeof(MainWindowViewModel)
                .GetMethod("CreateVirtualChartSubsetRow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [sourceRow, false])!;
            typeof(MainWindowViewModel)
                .GetMethod("ApplyLibraryChartRowProviders", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(LibraryChartRow)], null)!
                .Invoke(viewModel, [viewRow]);
            viewModel.MainChartList.Rows = new List<object>
            {
                viewRow
            };

            typeof(MainWindowViewModel)
                .GetMethod("PlayStartBmsFile", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [0]);

            Assert.AreEqual(Path.Combine(destinationDirectoryPath, temporaryChartName), player.LastPlayedPath);
            Assert.AreEqual(sourceChartPath, file.path);
            Assert.IsTrue(File.Exists(sourceChartPath));
            Assert.IsTrue(File.Exists(destinationCollisionPath));
            Assert.IsFalse(File.Exists(Path.Combine(sourceDirectoryPath, temporaryChartName)));
            Assert.IsFalse(File.Exists(Path.Combine(destinationDirectoryPath, temporaryChartName)));
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body = originalUsePlayerLR2body;
            BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB = originalOperationModeLR2Db;
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ChartListSourceRow_FromChartFile_UsesProjectionChartInfoWithoutStorageRow()
    {
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(
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

        Assert.IsNull(row.Chart.GetBmsStorageOwner());
        Assert.IsNull(row.Chart.GetBmsonStorageOwner());
        Assert.AreSame(chart, row.Chart);
        Assert.AreEqual(12, row.ChartLevelSortKey);
        Assert.AreEqual(4, row.ChartDifficultySortKey);
        Assert.AreEqual(180, row.ChartMainBpmSortKey);
        Assert.AreEqual(360, row.ChartTotalSortKey);
    }

    [TestMethod]
    public void ChartListSourceRow_FromChartFile_PreservesExplicitSourceScoreWithoutProvider()
    {
        BMSFile file = CreateFile(
            @"folder-a\score.bms",
            "Score",
            "folder-a",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            scoreSeed: 2);
        ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false, includeScoreSnapshot: true);

        ChartListSourceRow row = ChartListSourceRow.FromChartFile(chart, ChartListSourceProjectionMode.OwnerBacked);

        Assert.AreEqual(chart.Score.Clear, row.Clear);
        Assert.AreEqual(chart.Score.Score, row.Score);
        Assert.AreEqual(chart.Score.Clear, row.Chart.Score.Clear);
        Assert.AreEqual(chart.Score.Score, row.Chart.Score.Score);
    }

    [TestMethod]
    public void Constructor_DoesNotReadFolderCountFromSourceRows()
    {
        var file = new ThrowingFolderBmsFile();
        file.Apply(@"folder-a\alpha.bms", "Alpha");
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows([file]);
        var order = ChartListOrder.CreateTitleAscending(sourceRows);

        var view = new ChartListVirtualView(sourceRows, order, MaterializeSourceRow);

        Assert.AreEqual(1, view.Count);
        Assert.AreEqual(-1, view.DistinctFolderCount);
    }

    [TestMethod]
    public void SummaryFormatter_OmitsUnknownFolderCount()
    {
        string unknown = MainChartListViewModel.FormatSummaryTextForTest(3, -1);
        string known = MainChartListViewModel.FormatSummaryTextForTest(3, 2);

        StringAssert.StartsWith(unknown, "[3");
        Assert.IsFalse(unknown.Contains("/"));
        StringAssert.Contains(known, "/ 2");
    }

    [TestMethod]
    public void MainChartList_SetRowsUpdatesSummaryDirectly()
    {
        var mainChartList = new MainChartListViewModel();
        var rows = CreateSummaryRows();
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        mainChartList.SetRows(rows, updateSummary: true);

        Assert.AreSame(rows, mainChartList.Rows);
        StringAssert.StartsWith(mainChartList.SummaryText, "[2");
        StringAssert.Contains(mainChartList.SummaryText, "/ 2");
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.Rows));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SummaryText));
    }

    [TestMethod]
    public void MainChartList_SelectedIndexRaisesPropertyChanged()
    {
        var mainChartList = new MainChartListViewModel();
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        mainChartList.SelectedIndex = 3;

        Assert.AreEqual(3, mainChartList.SelectedIndex);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SelectedIndex));
    }

    [TestMethod]
    public void MainChartList_ColumnsSettingsRaisesOwnedBindingNotifications()
    {
        var mainChartList = new MainChartListViewModel();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        mainChartList.ColumnsSettings = settings;

        Assert.AreSame(settings, mainChartList.ColumnsSettings);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.ColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.RowDragKind));
    }

    [TestMethod]
    public void MainChartList_SortPresentationOwnsBindingAndRequestScope()
    {
        var mainChartList = new MainChartListViewModel();
        var sort = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.PlayedAt),
            Direction = ListSortDirection.Descending
        };
        MainChartListSortRequestedEventArgs? request = null;
        var propertyNames = new List<string>();
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);
        mainChartList.SortRequested += (_, e) => request = e;

        mainChartList.SetSortPresentation(sort, MainChartListSortTarget.PlayHistory);
        MainChartListSortRequestedEventArgs captured = mainChartList.CaptureSortRequest(
            nameof(PlayHistoryRow.Title),
            ListSortDirection.Ascending);
        mainChartList.RequestSort(captured);

        Assert.AreSame(sort, mainChartList.SortParameters);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SortParameters));
        Assert.IsNotNull(request);
        Assert.AreEqual(nameof(PlayHistoryRow.Title), request.ColumnName);
        Assert.AreEqual(ListSortDirection.Ascending, request.Direction);
        Assert.AreEqual(MainChartListSortTarget.PlayHistory, request.Target);
    }

    [TestMethod]
    public void MainChartList_DisplayRefreshRaisesDirectOwnerEvent()
    {
        var mainChartList = new MainChartListViewModel();
        int raisedCount = 0;
        mainChartList.DisplayRefreshRequested += (_, _) => raisedCount++;

        mainChartList.RequestDisplayRefresh();

        Assert.AreEqual(1, raisedCount);
    }

    private static List<LibraryChartRow> CreateSummaryRows()
    {
        return
        [
            LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(CreateFile(@"D:\Charts\A\alpha.bms", "Alpha", @"D:\Charts\A"))),
            LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(CreateFile(@"D:\Charts\B\bravo.bms", "Bravo", @"D:\Charts\B"))),
        ];
    }

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
            calls.Add(e.PropertyName);
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
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

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

        MainChartListPreparedRowsApply prepared = mainChartList.PrepareRowsApply(new MainChartListRowsApplyRequest
        {
            Rows = nextRows,
            ColumnsSettings = settings,
            SelectionPolicy = MainChartListSelectionPolicy.Preserve,
            Summary = MainChartListSummaryUpdate.Preserve(),
            TerminalStageStartMs = 0,
            Stopwatch = Stopwatch.StartNew()
        });
        mainChartList.CancelPreparedRowsApply(prepared);

        Assert.AreEqual(1, preparingCount);
        Assert.AreEqual(1, canceledCount);
        Assert.AreSame(oldRows, mainChartList.Rows);
    }

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
        mainChartList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);
        MainChartListPreparedRowsApply prepared = mainChartList.PrepareRowsApply(new MainChartListRowsApplyRequest
        {
            Rows = nextRows,
            ColumnsSettings = settings,
            SelectionPolicy = MainChartListSelectionPolicy.Reset,
            Summary = MainChartListSummaryUpdate.Explicit("committed"),
            TerminalStageStartMs = 0,
            Stopwatch = Stopwatch.StartNew()
        });

        MainChartListRowsCommit commit = mainChartList.CommitPreparedRows(prepared);

        Assert.AreSame(nextRows, mainChartList.Rows);
        Assert.AreEqual("committed", mainChartList.SummaryText);
        Assert.AreEqual(0, propertyNames.Count);

        mainChartList.PublishRowsCommit(commit);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.Rows));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SummaryText));
    }

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

        Assert.ThrowsException<InvalidOperationException>(() => mainChartList.ApplyRows(new MainChartListRowsApplyRequest
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

    [TestMethod]
    public void PlayHistoryTerminal_PreparationRunsOutsideFreshnessLockAndCancelsStaleCommit()
    {
        var viewModel = new MainWindowViewModel();
        var oldRows = new List<object>();
        var candidateRows = new List<object> { new object() };
        viewModel.MainChartList.Rows = oldRows;
        long requestId = viewModel.BeginPlayHistoryFilterRequest(PlayHistoryPeriodRequest.All());
        int canceledCount = 0;
        bool cancellationCallbackCompleted = false;
        using IDisposable cancellationRegistration = viewModel.RegisterPlayHistoryCancellationCallbackForTest(
            requestId,
            () =>
            {
                Task<long> nestedInvalidateTask = Task.Run(viewModel.RegisterPlayHistoryFilterRequest);
                cancellationCallbackCompleted = nestedInvalidateTask.Wait(TimeSpan.FromSeconds(5));
            });
        viewModel.MainChartList.RowsReplacementCanceled += (_, _) => canceledCount++;
        viewModel.MainChartList.RowsReplacing += (_, _) =>
        {
            Task<long> invalidateTask = Task.Run(viewModel.RegisterPlayHistoryFilterRequest);
            Assert.IsTrue(invalidateTask.Wait(TimeSpan.FromSeconds(5)), "RowsReplacing must not run while the play-history freshness lock is held.");
        };

        bool applied = viewModel.TryCommitPlayHistoryRowsForTest(requestId, candidateRows, "candidate summary");

        Assert.IsFalse(applied);
        Assert.AreSame(oldRows, viewModel.MainChartList.Rows);
        Assert.AreNotEqual("candidate summary", viewModel.MainChartList.SummaryText);
        Assert.AreEqual(1, canceledCount);
        Assert.IsTrue(cancellationCallbackCompleted, "Cancellation callbacks must run after releasing the play-history freshness lock.");
    }

    [TestMethod]
    public void PlayHistoryTerminal_PublishesExplicitSummaryWithCommittedMainState()
    {
        var viewModel = new MainWindowViewModel();
        var candidateRows = new List<object> { new object(), new object() };
        viewModel.MainChartList.Rows = new List<object>();
        viewModel.MainChartList.SelectedIndex = 3;
        long requestId = viewModel.BeginPlayHistoryFilterRequest(PlayHistoryPeriodRequest.All());
        int preparingCount = 0;
        var propertyNames = new List<string>();
        viewModel.MainChartList.RowsReplacing += (_, _) => preparingCount++;
        viewModel.MainChartList.PropertyChanged += (_, e) =>
        {
            propertyNames.Add(e.PropertyName);
            Assert.AreSame(candidateRows, viewModel.MainChartList.Rows);
            Assert.AreEqual("play-history explicit summary", viewModel.MainChartList.SummaryText);
            Assert.AreEqual(-1, viewModel.MainChartList.SelectedIndex);
            Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, viewModel.MainChartList.ColumnsSettings.Kind);
        };

        bool applied = viewModel.TryCommitPlayHistoryRowsForTest(requestId, candidateRows, "play-history explicit summary");

        Assert.IsTrue(applied);
        Assert.AreEqual(1, preparingCount);
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.Rows));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SummaryText));
        CollectionAssert.Contains(propertyNames, nameof(MainChartListViewModel.SelectedIndex));
        Assert.AreEqual("play-history explicit summary", viewModel.MainChartList.SummaryText);
    }

    [TestMethod]
    public void ChartListRefreshCoordinator_CreateVirtualSortMetricsPreservesOrderFields()
    {
        List<BMSFile> files =
        [
            CreateFile(@"folder-b\charlie.bms", "Charlie", "folder-b"),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a")
        ];
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files);
        ChartListOrder order = ChartListOrder.CreateTitleAscending(sourceRows);

        LibraryChartSortMetrics metrics = ChartListRefreshCoordinator.CreateVirtualSortMetrics(
            order,
            sortStageMs: 12,
            sortCacheHit: true,
            sortCacheGeneration: 34,
            orderCacheLookupMs: 56,
            orderBuildMs: 78);

        Assert.AreEqual(order.Count, metrics.RowCount);
        Assert.AreEqual(order.ColumnName, metrics.ColumnName);
        Assert.AreEqual(order.Direction, metrics.Direction);
        Assert.AreEqual(order.PropertyTypeName, metrics.PropertyTypeName);
        Assert.AreEqual(order.SortProfile, metrics.SortProfile);
        Assert.AreEqual(order.StringSortKind, metrics.StringSortKind);
        Assert.AreEqual(12, metrics.SortMs);
        Assert.IsTrue(metrics.SortReuse);
        Assert.AreEqual(order.ColumnName, metrics.SortCacheKey);
        Assert.AreEqual(34, metrics.SortCacheGeneration);
        Assert.IsTrue(metrics.SortCacheHit);
        Assert.AreEqual(56, metrics.OrderCacheLookupMs);
        Assert.AreEqual(78, metrics.OrderBuildMs);
    }

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

    [TestMethod]
    public void TitleAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Title), ListSortDirection.Ascending);
    }

    [TestMethod]
    public void TitleDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Title), ListSortDirection.Descending);
    }

    [TestMethod]
    public void PathAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.path), ListSortDirection.Ascending);
    }

    [TestMethod]
    public void PathDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.path), ListSortDirection.Descending);
    }

    [TestMethod]
    public void FolderAscendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Folder), ListSortDirection.Ascending);
    }

    [TestMethod]
    public void FolderDescendingOrder_MatchesExistingDefaultLibraryChartRowSort()
    {
        AssertVirtualOrderMatchesExistingSort(nameof(LibraryChartRow.Folder), ListSortDirection.Descending);
    }

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
    }

    [TestMethod]
    public void PathDescendingOrder_KeepsTitleAscendingSecondaryKey()
    {
        List<BMSFile> files =
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

    [TestMethod]
    public void UnsupportedColumn_CannotCreateVirtualOrder()
    {
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(CreateSampleSortFiles());

        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, "Path", ListSortDirection.Ascending, out _));
        Assert.IsFalse(ChartListOrder.TryCreate(sourceRows, "EntryLevelSortKey", ListSortDirection.Ascending, out _));
    }

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

    [TestMethod]
    public void NormalLibraryFilters_ReuseFullOrderSubsetWithoutRealizingRows()
    {
        List<BMSFile> files =
        [
            CreateFile(@"FOLDER-Z\delta.bms", "Delta", "folder-z", artist: "Target Artist", mode: 7),
            CreateFile(@"folder-z\bravo.bms", "Bravo", "folder-z", artist: "Target Artist", mode: 5),
            CreateFile(@"folder-y\charlie.bms", "Charlie", "folder-y", artist: "Other Artist", mode: 7),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a", artist: "Target Artist", mode: 7)
        ];
        List<LR2SongDBExtended.bmson_song> bmsons =
        [
            new LR2SongDBExtended.bmson_song
            {
                path = @"folder-z\echo.bmson",
                folder = "folder-z",
                title = "Echo",
                artist = "Target Artist",
                mode_hint = "beat-7k",
                level = 7,
                md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                sha256 = "6666666666666666666666666666666666666666666666666666666666666666"
            }
        ];
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files, bmsons);
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder fullOrder));
        int[] existingOrderIndexes = fullOrder.Indexes.AsEnumerable().Reverse().ToArray();
        var keywordQuery = GridKeywordSearchQuery.Parse("artist:target");
        Func<ChartListSourceRow, bool> folderFilter = MainWindowViewModel.CreateVirtualNormalLibraryFolderFilterForTest(
            MainWindowViewModel.FolderFilterType.DirectoryFilter,
            "folder-z",
            out string folderFilterIdentity);

        int[] filteredIndexes = MainWindowViewModel.ApplyVirtualNormalLibraryFiltersForTest(
            sourceRows,
            existingOrderIndexes,
            folderFilter,
            keywordQuery,
            MainWindowViewModel.ModeFilterType._5KEYS | MainWindowViewModel.ModeFilterType._7KEYS,
            out int folderCount,
            out int keywordCount,
            out int modeCount);
        ChartListOrder filteredOrder = fullOrder.WithIndexes(filteredIndexes);
        int createdCount = 0;
        var view = new ChartListVirtualView(sourceRows, filteredOrder, row =>
        {
            createdCount++;
            return MaterializeSourceRow(row);
        });

        Assert.AreEqual("directory:folder-z" + Path.DirectorySeparatorChar, folderFilterIdentity);
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

    [TestMethod]
    public void VirtualNormalLibraryFilterIdentity_UsesStableFolderFilterIdentity()
    {
        _ = MainWindowViewModel.CreateVirtualNormalLibraryFolderFilterForTest(
            MainWindowViewModel.FolderFilterType.DirectoryFilter,
            @"D:\BMS\1 EVENT",
            out string folderIdentity);
        _ = MainWindowViewModel.CreateVirtualNormalLibraryFolderFilterForTest(
            MainWindowViewModel.FolderFilterType.DirectoryFilter,
            @"D:\BMS\1 EVENT\",
            out string sameFolderIdentity);

        string identity = MainWindowViewModel.CreateVirtualNormalLibraryFilterIdentityForTest(folderIdentity, string.Empty, MainWindowViewModel.ModeFilterType.All, 1, 1);
        string sameIdentity = MainWindowViewModel.CreateVirtualNormalLibraryFilterIdentityForTest(sameFolderIdentity, string.Empty, MainWindowViewModel.ModeFilterType.All, 9, 9);

        Assert.AreEqual(folderIdentity, sameFolderIdentity);
        Assert.AreEqual(identity, sameIdentity);
        Assert.AreNotEqual("normal_default", identity);
    }

    [TestMethod]
    public void VirtualChartSubsetFilters_ReuseFullOrderSubsetWithoutRealizingRows()
    {
        List<BMSFile> files =
        [
            CreateFile(@"folder-z\delta.bms", "Delta", "folder-z", artist: "Target Artist", mode: 7),
            CreateFile(@"folder-z\bravo.bms", "Bravo", "folder-z", artist: "Target Artist", mode: 5),
            CreateFile(@"folder-y\charlie.bms", "Charlie", "folder-y", artist: "Other Artist", mode: 7),
            CreateFile(@"folder-a\alpha.bms", "Alpha", "folder-a", artist: "Target Artist", mode: 7)
        ];
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files);
        Assert.IsTrue(ChartListOrder.TryCreate(sourceRows, nameof(LibraryChartRow.path), ListSortDirection.Descending, out ChartListOrder fullOrder));
        var keywordQuery = GridKeywordSearchQuery.Parse("artist:target");

        int[] filteredIndexes = MainWindowViewModel.ApplyVirtualChartSubsetFiltersForTest(
            sourceRows,
            fullOrder.Indexes,
            keywordQuery,
            MainWindowViewModel.ModeFilterType._5KEYS | MainWindowViewModel.ModeFilterType._7KEYS,
            out int keywordCount,
            out int modeCount);
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

    [TestMethod]
    public void VirtualChartSubsetSortCacheKey_UsesSubsetSignatureAndDependencyGeneration()
    {
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(CreateSampleSortFiles());
        List<ChartListSourceRow> reorderedRows = [.. sourceRows.AsEnumerable().Reverse()];
        long signature = MainWindowViewModel.ComputeVirtualChartSubsetSourceRowsSignatureForTest(sourceRows);
        long sameSignature = MainWindowViewModel.ComputeVirtualChartSubsetSourceRowsSignatureForTest(BuildOwnerBackedSourceRows(CreateSampleSortFiles()));
        long reorderedSignature = MainWindowViewModel.ComputeVirtualChartSubsetSourceRowsSignatureForTest(reorderedRows);
        int treeMode = (int)MainViewUpdateMode.FileMissingFilterSelected;

        var current = new VirtualChartSubsetSortCacheKey(
            7,
            11,
            1,
            2,
            3,
            treeMode,
            "file_missing",
            signature,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            sourceRows.Count);
        var same = new VirtualChartSubsetSortCacheKey(
            7,
            11,
            1,
            2,
            3,
            treeMode,
            "file_missing",
            sameSignature,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            sourceRows.Count);

        Assert.AreEqual(signature, sameSignature);
        Assert.AreNotEqual(signature, reorderedSignature);
        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(8, 11, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 12, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 2, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 3, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 4, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        var dependencyAware = new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, 4, 5, 6, treeMode, "file_missing", signature, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, sourceRows.Count);
        Assert.AreNotEqual(dependencyAware, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, 7, 5, 6, treeMode, "file_missing", signature, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(dependencyAware, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, 4, 8, 6, treeMode, "file_missing", signature, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(dependencyAware, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, 4, 5, 9, treeMode, "file_missing", signature, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, (int)MainViewUpdateMode.DuplicateFilterSelected, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "duplicate_all", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "file_missing", reorderedSignature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Descending, sourceRows.Count));
        Assert.AreNotEqual(current, new VirtualChartSubsetSortCacheKey(7, 11, 1, 2, 3, treeMode, "file_missing", signature, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, sourceRows.Count + 1));
    }

    [TestMethod]
    public void DefaultVirtualOrderPrewarmDescriptors_AreRegistryOrderAscDesc()
    {
        IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors = MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest();

        CollectionAssert.AreEqual(
            CreateExpectedDefaultPrewarmDescriptors(),
            descriptors.ToArray());
    }

    [TestMethod]
    public void VirtualSortRegistryMetadata_DescribesSupportedColumnsAndDependencies()
    {
        ChartListOrderColumnMetadata[] metadata = [.. ChartListOrder.GetVirtualSortColumnMetadata()];

        CollectionAssert.AreEqual(
            CreateExpectedVirtualSortColumnNames(),
            metadata.Select(column => column.NormalizedColumnName).ToArray());
        foreach (ChartListOrderColumnMetadata column in metadata)
        {
            Assert.IsTrue(ChartListOrder.TryGetVirtualSortColumnMetadata(column.NormalizedColumnName, out ChartListOrderColumnMetadata resolved));
            Assert.AreEqual(column.Dependency, resolved.Dependency);
        }

        AssertRegistryDependency(metadata, MainViewDataDependency.IdentitySortKey, nameof(LibraryChartRow.Title));
        AssertRegistryDependency(metadata, MainViewDataDependency.Score, nameof(LibraryChartRow.rateDouble));
        AssertRegistryDependency(metadata, MainViewDataDependency.ChartInfo, nameof(LibraryChartRow.ChartTotalSortKey));
        AssertRegistryDependency(metadata, MainViewDataDependency.Maintenance, nameof(LibraryChartRow.WAVHealth));
        AssertRegistryDependency(metadata, MainViewDataDependency.Maintenance, nameof(LibraryChartRow.encoding));
        AssertRegistryDependency(metadata, MainViewDataDependency.Warning, nameof(LibraryChartRow.WarningDigestText));
        AssertRegistryDependency(metadata, MainViewDataDependency.ReferenceTables, nameof(LibraryChartRow.RefTablesSymbols));
        AssertRegistryPrewarmPriority(metadata, 1, nameof(LibraryChartRow.Title));
        AssertRegistryPrewarmPriority(metadata, 1, nameof(LibraryChartRow.Folder));
        AssertRegistryPrewarmPriority(metadata, 2, nameof(LibraryChartRow.rateDouble));
        AssertRegistryPrewarmPriority(metadata, 2, nameof(LibraryChartRow.minbp));
        AssertRegistryPrewarmPriority(metadata, 3, nameof(LibraryChartRow.maxcombo));
        AssertRegistryPrewarmPriority(metadata, 0, nameof(LibraryChartRow.WAVHealth));
        AssertRegistryPrewarmPriority(metadata, 0, nameof(LibraryChartRow.encoding));
        AssertRegistryPrewarmPriority(metadata, 0, nameof(LibraryChartRow.WarningDigestText));
        AssertRegistryPrewarmPriority(metadata, 0, nameof(LibraryChartRow.level));
    }

    [TestMethod]
    public void DefaultVirtualOrderPrewarmDescriptors_IncludePriorityOneToThree()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        string[] columns = [.. MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest(settings)
            .Where(descriptor => descriptor.Direction == ListSortDirection.Ascending)
            .Select(descriptor => descriptor.ColumnName)];

        CollectionAssert.Contains(columns, nameof(LibraryChartRow.Title));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.Folder));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.path));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.Artist));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.rateDouble));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.ChartTotalSortKey));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.ChartFeatureSortKey));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.hash));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.score));
        CollectionAssert.Contains(columns, nameof(LibraryChartRow.maxcombo));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.WarningDigestText));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.WAVHealth));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.encoding));
        CollectionAssert.DoesNotContain(columns, nameof(LibraryChartRow.level));

        settings.Combo.Visibility = Visibility.Visible;
        columns = [.. MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest(settings)
            .Where(descriptor => descriptor.Direction == ListSortDirection.Ascending)
            .Select(descriptor => descriptor.ColumnName)];

        CollectionAssert.Contains(columns, nameof(LibraryChartRow.maxcombo));
    }

    [TestMethod]
    public void VirtualOrderPrewarmDegree_IsBoundedByProcessorAndCap()
    {
        Assert.AreEqual(1, MainWindowViewModel.ResolveVirtualNormalLibraryOrderPrewarmDegreeForTest(0));
        Assert.AreEqual(1, MainWindowViewModel.ResolveVirtualNormalLibraryOrderPrewarmDegreeForTest(1));

        int degree = MainWindowViewModel.ResolveVirtualNormalLibraryOrderPrewarmDegreeForTest(128);

        Assert.IsTrue(degree >= 1);
        Assert.IsTrue(degree <= 4);
        Assert.IsTrue(degree <= Math.Max(1, Environment.ProcessorCount - 1));
    }

    [TestMethod]
    public void DefaultVirtualOrderPrewarmDescriptors_DoNotRequireRowRealization()
    {
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(CreateSampleSortFiles());
        int createdCount = 0;

        foreach (VirtualNormalLibrarySortDescriptor descriptor in MainWindowViewModel.CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest())
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

    [TestMethod]
    public void VirtualOrderPrewarmStaleCheck_RequiresBothGenerationsToMatch()
    {
        Assert.IsFalse(MainWindowViewModel.IsVirtualNormalLibraryPrewarmStaleForTest(1, 2, 1, 2));
        Assert.IsTrue(MainWindowViewModel.IsVirtualNormalLibraryPrewarmStaleForTest(1, 2, 3, 2));
        Assert.IsTrue(MainWindowViewModel.IsVirtualNormalLibraryPrewarmStaleForTest(1, 2, 1, 3));
    }

    [TestMethod]
    public void VirtualChartSubsetTreeModes_AreLimitedToChartCollections()
    {
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.FileMissingFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.FileMissingIgnoredFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.DuplicateFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.GarbledFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.GarbleFixedFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.UnregisteredFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.ZeroNoteFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.ChartInfoParseErrorFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.NewlyInstalledFolderSelected));
        Assert.IsTrue(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.PendingInstallFolderSelected));

        Assert.IsFalse(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.FullScanAllChartsFilterSelected));
        Assert.IsFalse(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.FolderFilterSelected));
        Assert.IsFalse(MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)MainViewUpdateMode.PlaylistFilterSelected));
    }

    [TestMethod]
    public void PackageChartEntryDisplayRow_DoesNotMaterializeAdapterlessBmsonEntry()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));

        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);

        Assert.IsNotNull(row);
        Assert.AreEqual(bmson.path, row.path);
        Assert.AreEqual("BmsonTitle Subtitle", row.Title);
        Assert.IsNull(entry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void PackageChartEntryDisplayRow_TreatsBmsonEntryAsBmsonRow()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));

        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);

        Assert.IsNotNull(row);
        Assert.IsNull(row.Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, row.Chart.GetBmsonStorageOwner());
        Assert.AreEqual(ChartFileKind.Bmson, row.Chart.Kind);
        Assert.AreSame(entry, row.PackageEntry);
        Assert.AreEqual(bmson.path, row.path);
    }

    [TestMethod]
    public void PackageChartEntryDisplayRow_ReflectsUpdatedAdapterlessBmsonEntryState()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);
        var result = new InstallEstimationResult
        {
            Confidence = InstallEstimationConfidence.Low,
            HasViableDestination = true,
            LowConfidenceKind = InstallEstimationLowConfidenceKind.AmbiguousCandidates
        };
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = @"C:\Candidate\A",
            RepresentativeTitle = "Candidate A",
            RepresentativeArtist = "Artist A"
        });
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = @"C:\Candidate\B",
            RepresentativeTitle = "Candidate B",
            RepresentativeArtist = "Artist B"
        });
        result.SuggestedDestinationDirectories.Add(@"C:\Candidate\A");
        result.SuggestedDestinationDirectories.Add(@"C:\Candidate\B");

        entry.ApplyInstallEstimationResult(result);

        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.AreEqual(string.Empty, row.instl_dst);
        Assert.AreEqual("Candidate A", row.InstallDestinationTitle);
        Assert.AreEqual("Artist A", row.InstallDestinationArtist);
        CollectionAssert.AreEqual(new[] { @"C:\Candidate\A", @"C:\Candidate\B" }, row.Chart.InstallDestinationSuggestions.ToArray());
        Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    [TestMethod]
    public void PackageChartEntryDisplayRow_NotifiesWhenSearchingStatusChanges()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);
        var propertyNames = new List<string>();
        row.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        entry.SetSearchingStatus(isSearching: true);

        Assert.IsTrue(row.status.HasFlag(ChartFileStatus.SEARCHING));
        CollectionAssert.Contains(propertyNames, string.Empty);

        propertyNames.Clear();

        entry.SetSearchingStatus(isSearching: false);

        Assert.IsFalse(row.status.HasFlag(ChartFileStatus.SEARCHING));
        CollectionAssert.Contains(propertyNames, string.Empty);
    }

    [TestMethod]
    public void PackageChartSourceSnapshot_UsesChartSourcesWithoutMaterializingBmson()
    {
        var bms = new TestableBmsFile();
        bms.Apply(@"folder-a\bms.bms", "BmsTitle", "folder-a");
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(bms)),
            adapterlessBmsonEntry
        ]);

        PackageChartSourceSnapshot snapshot = MainWindowViewModel.CreatePackageChartSourceSnapshot([package]);

        Assert.AreSame(bms, snapshot.Entries.Single(entry => entry.Chart.Kind == ChartFileKind.Bms).Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, snapshot.Entries.Single(entry => entry.Chart.Kind == ChartFileKind.Bmson).Chart.GetBmsonStorageOwner());
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void PackageChartSourceSnapshot_TreatsBmsonEntryAsChartSource()
    {
        var bms = new TestableBmsFile();
        bms.Apply(@"folder-a\bms.bms", "BmsTitle", "folder-a");
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(bms)),
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson))
        ]);

        PackageChartSourceSnapshot snapshot = MainWindowViewModel.CreatePackageChartSourceSnapshot([package]);

        Assert.AreSame(bms, snapshot.Entries.Single(entry => entry.Chart.Kind == ChartFileKind.Bms).Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, snapshot.Entries.Single(entry => entry.Chart.Kind == ChartFileKind.Bmson).Chart.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void PackageChartSourceRows_DoNotMaterializeAdapterlessBmsonDuringVirtualSort()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        PackageChartSourceSnapshot snapshot = MainWindowViewModel.CreatePackageChartSourceSnapshot([package]);
        List<ChartListSourceRow> rows = ChartListSourceRow.BuildPackageRows(snapshot.Entries);

        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, out _));
        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.instl_dst), ListSortDirection.Ascending, out _));
        Assert.IsTrue(ChartListOrder.TryCreate(rows, nameof(LibraryChartRow.WAVHealth), ListSortDirection.Ascending, out _));

        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        Assert.AreSame(bmson, rows.Single().Chart.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void PackageChartSourceRows_ReadLiveEntryProjectionForPendingInstall()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        var maintenanceInfo = new BMSFileMaintenanceInfo
        {
            wav_files_defined = 4,
            wav_files_existing = 1,
            bga_files_defined = 2,
            bga_files_existing = 1,
            movie_files_defined = 1,
            movie_files_existing = 0
        };
        entry.ApplyInstallDestination(@"D:\BMS\Installed", "Resolved", "Artist");
        entry.ReplaceResourceHealthProjection(
            maintenanceInfo,
            [ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "WAV missing")]);
        ChartListSourceRow sourceRow = ChartListSourceRow.BuildPackageRows([entry]).Single();
        LibraryChartRow row = MainWindowViewModel.CreateVirtualChartSubsetRowForTest(sourceRow);

        Assert.AreEqual(entry.Chart.InstallDestination, sourceRow.InstallDestination);
        Assert.AreEqual(entry.Chart.WAVHealth, sourceRow.WAVHealth);
        Assert.AreEqual(entry.Chart.BGAHealth, sourceRow.BGAHealth);
        Assert.AreEqual(entry.Chart.MovieHealth, sourceRow.MovieHealth);
        Assert.AreEqual(sourceRow.InstallDestination, row.instl_dst);
        Assert.AreEqual(sourceRow.WAVHealth, row.WAVHealth);
        Assert.AreEqual(sourceRow.BGAHealth, row.BGAHealth);
        Assert.AreEqual(sourceRow.MovieHealth, row.MovieHealth);
        Assert.IsFalse(sourceRow.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        Assert.IsFalse(row.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(sourceRow.Chart.Warnings), "WAV missing");
        StringAssert.Contains(row.WarningTooltipText, "WAV missing");

        entry.ApplyInstallDestination(@"D:\BMS\Next", "Next", "Artist");

        Assert.AreEqual(@"D:\BMS\Next", sourceRow.InstallDestination);
        Assert.AreEqual(@"D:\BMS\Next", row.instl_dst);
        Assert.IsFalse(sourceRow.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        Assert.IsFalse(row.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
    }

    [TestMethod]
    public void PackagePlaybackTargetSnapshot_UsesChartEntriesWithoutMaterializingBmsonAdapters()
    {
        var bms = new TestableBmsFile();
        bms.Apply(@"folder-a\bms.bms", "BmsTitle", "folder-a");
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(bms)),
            adapterlessBmsonEntry
        ]);

        List<ChartFile> targets = MainWindowViewModel.CreatePackagePlaybackTargetSnapshot([package]);

        Assert.AreSame(bms, targets.Single(chart => chart.Kind == ChartFileKind.Bms).GetBmsStorageOwner());
        Assert.AreSame(bmson, targets.Single(chart => chart.Kind == ChartFileKind.Bmson).GetBmsonStorageOwner());
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void ClearInstallDestinationForPendingPackages_ClearsAdapterlessBmsonEntryWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(CreateBmsonSong()),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);

        viewModel.ClearInstallDestinationForPendingPackages([package]);

        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ClearInstallDestinationForPendingCharts_ClearsAdapterlessBmsonPackageEntryWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
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

            viewModel.ClearInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([selectedChart]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
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

    [TestMethod]
    public void ClearInstallDestinationForPendingChartTargets_DoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
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

            viewModel.ClearInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([target]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
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

    [TestMethod]
    public void ClearInstallDestinationForPendingChartTargets_DoesNotResolveLooseBmsonCompatibilityAdapterWhenStandaloneTargetSharesPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Target",
                "Installed",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
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
                ChartFileProjection.FromBmsonSong(bmsonSong),
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

            viewModel.ClearInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([packageTarget, standaloneTarget]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
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

    [TestMethod]
    public void PackageEntryRowsCarryPackageEntryIntoChartOperationTarget()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile adapter = CreateFile(
            @"C:\Pkg\chart.bms",
            "BMS",
            "Pkg",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(adapter));
        LibraryChartRow row = MainWindowViewModel.CreateLibraryChartRowFromPackageEntryForTest(entry);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));
        Assert.AreSame(entry, target.PackageEntry);
        Assert.AreSame(entry, target.ToPackageChartEntry());
    }

    [TestMethod]
    public void SearchInstallDestinationForPendingChartTargets_DoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
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

            viewModel.SearchInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([target]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingChartTargets_DoesNotResolveAdapterlessBmsonCompatibilityFile()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
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

            viewModel.SearchMergeDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([target]));

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ClearInstallDestinationForPendingChartTargets_ResolvesReplacedPackageEntryByChartIdentity()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var viewModel = new MainWindowViewModel();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong();
        PackageChartEntry staleEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Old",
                "Old",
                "Artist",
                []));
        PackageChartEntry currentEntry = PackageChartEntry.FromChart(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                @"C:\Installed\Current",
                "Current",
                "Artist",
                []));
        ChartPackage package = ChartPackage.FromChartEntries([currentEntry]);
        try
        {
            var library = new BMSLibrary(songDbPath);
            library.ChartPackagesPending = new DispatcherCollection<ChartPackage>(
                new ObservableCollection<ChartPackage>([package]),
                Dispatcher.CurrentDispatcher);
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

            viewModel.ClearInstallDestinationForPendingCharts(viewModel.CreatePendingInstallDestinationTargetSnapshot([target]));

            Assert.IsNull(currentEntry.GetBmsOwnerForTest());
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
                MainWindowViewModel.IsVirtualNormalLibraryTreeModeSupportedForTest((int)treeMode),
                treeMode + " tree support");
            foreach (MainViewUpdateMode requestMode in allModes)
            {
                bool expectedRequestSupport = expectedTreeSupport
                    && (requestMode == treeMode || refreshModes.Contains(requestMode));
                Assert.AreEqual(
                    expectedRequestSupport,
                    MainWindowViewModel.IsVirtualNormalLibraryRequestModeSupportedForTest((int)requestMode, (int)treeMode),
                    treeMode + " request " + requestMode);
            }
        }
    }

    [TestMethod]
    public void VirtualNormalLibraryFullScanTree_IgnoresFolderFilter()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldApplyVirtualNormalLibraryFolderFilterForTest((int)MainViewUpdateMode.FolderFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyVirtualNormalLibraryFolderFilterForTest((int)MainViewUpdateMode.FullScanAllChartsFilterSelected));
    }

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
                MainWindowViewModel.IsVirtualChartSubsetTreeModeSupportedForTest((int)treeMode),
                treeMode + " tree support");
            foreach (MainViewUpdateMode requestMode in allModes)
            {
                bool expectedRequestSupport = expectedTreeSupport
                    && (requestMode == treeMode || refreshModes.Contains(requestMode));
                Assert.AreEqual(
                    expectedRequestSupport,
                    MainWindowViewModel.IsVirtualChartSubsetRequestModeSupportedForTest((int)requestMode, (int)treeMode),
                    treeMode + " request " + requestMode);
                bool expectedRequired = expectedRequestSupport || treeModes.Contains(requestMode);
                Assert.AreEqual(
                    expectedRequired,
                    MainWindowViewModel.IsVirtualChartSubsetRequiredForRequestForTest((int)requestMode, (int)treeMode),
                    treeMode + " required " + requestMode);
            }
        }
    }

    [TestMethod]
    public void DuplicateVirtualSourceRows_PreserveBmsonStorageRowsWithoutCompatibilityFiles()
    {
        BMSFile bmsFile = CreateFile(
            Path.Combine("C:\\BMS", "DirA", "a.bms"),
            "BMS Alpha",
            "DirA",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
            folder = Path.Combine("C:\\BMS", "DirB"),
            title = "Bmson Beta",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var duplicateGroup = new DuplicateGroup(
            [
                ChartFileProjection.WithWarnings(
                    ChartFileProjection.FromBmsFile(bmsFile),
                    [ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")]),
                ChartFileProjection.WithWarnings(
                    ChartFileProjection.FromBmsonSong(bmsonSong),
                    [ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")])
            ],
            [Path.Combine("C:\\BMS", "DirA"), Path.Combine("C:\\BMS", "DirB")]);

        List<ChartListSourceRow> rows = MainWindowViewModel.CreateDuplicateVirtualSourceRowsForTest([duplicateGroup], duplicateGroup);

        Assert.AreEqual(2, rows.Count);
        Assert.AreSame(bmsFile, rows.Single(row => row.Chart.GetBmsStorageOwner() != null).Chart.GetBmsStorageOwner());
        ChartListSourceRow bmsonRow = rows.Single(row => row.Chart.GetBmsonStorageOwner() != null);
        Assert.AreSame(bmsonSong, bmsonRow.Chart.GetBmsonStorageOwner());
        Assert.IsNull(bmsonRow.Chart.GetBmsStorageOwner());
        StringAssert.Contains(bmsonRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_DuplicateChart);
    }

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

    [TestMethod]
    public void VirtualChartSubsetUnsupportedSort_ResetsToDefaultVirtualSort()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartListVirtualViewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var viewModel = new MainWindowViewModel();
        BMSFile zeta = CreateFile(Path.Combine(tempRootPath, "zeta.bms"), "Zeta", tempRootPath);
        BMSFile alpha = CreateFile(Path.Combine(tempRootPath, "alpha.bms"), "Alpha", tempRootPath, hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var duplicateGroup = new DuplicateGroup(
            [ChartFileProjection.FromBmsFile(zeta), ChartFileProjection.FromBmsFile(alpha)],
            [tempRootPath]);
        try
        {
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [zeta, alpha],
                DuplicateChartGroups = [duplicateGroup]
            };
            typeof(MainWindowViewModel).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, library);
            typeof(MainWindowViewModel).GetField("treeViewFilterTypeSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, MainViewUpdateMode.DuplicateFilterSelected);
            typeof(MainWindowViewModel).GetField("_SortParameters", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                viewModel,
                new MainWindowViewModel.cSortParameters
                {
                    ColumnsName = "UnsupportedColumn",
                    Direction = ListSortDirection.Descending
                });

            typeof(MainWindowViewModel)
                .GetMethod("RefreshChartRowsView", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(MainViewUpdateMode), typeof(object)], null)!
                .Invoke(viewModel, [MainViewUpdateMode.DuplicateFilterSelected, null]);

            Assert.IsNull(viewModel.SortParameters);
            var view = viewModel.MainChartList.Rows as ChartListVirtualView;
            Assert.IsNotNull(view);
            Assert.AreEqual(2, view.Count);
            Assert.AreEqual("Alpha", ((LibraryChartRow)view[0]).Title);
            Assert.AreEqual("Zeta", ((LibraryChartRow)view[1]).Title);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void VirtualChartSubsetResourceHealthProjection_MatchesMaterializedModes()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.FileMissingFilterSelected));
        Assert.IsTrue(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.FileMissingIgnoredFilterSelected));
        Assert.IsTrue(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.NewlyInstalledFolderSelected));

        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.FullScanAllChartsFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.GarbledFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.UnregisteredFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.ZeroNoteFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.ChartInfoParseErrorFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldApplyResourceHealthProjectionForVirtualSubsetForTest((int)MainViewUpdateMode.PendingInstallFolderSelected));
    }

    [TestMethod]
    public void NormalLibrarySortCacheKey_UsesSortKeyGenerationForOrderIdentity()
    {
        var current = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        var same = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        var changedSourceGeneration = new NormalLibrarySortCacheKey(8, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        var changedSortKeyGeneration = new NormalLibrarySortCacheKey(7, 12, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 3);
        var changedColumn = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Artist), ListSortDirection.Ascending, 3);
        var changedDirection = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Descending, 3);
        var changedRowCount = new NormalLibrarySortCacheKey(7, 11, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 4);

        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, changedSourceGeneration);
        Assert.AreNotEqual(current, changedSortKeyGeneration);
        Assert.AreNotEqual(current, changedColumn);
        Assert.AreNotEqual(current, changedDirection);
        Assert.AreNotEqual(current, changedRowCount);

        var scoreAware = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var scoreAwareSame = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var changedScoreGeneration = new NormalLibrarySortCacheKey(7, 11, 2, 2, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var changedChartInfoGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 3, 3, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var changedMaintenanceGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 2, 4, nameof(LibraryChartRow.rateDouble), ListSortDirection.Ascending, 3);
        var dependencyAware = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, 4, 5, 6, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, 3);
        var dependencyAwareSame = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, 4, 5, 6, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, 3);
        var changedWarningGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, 7, 5, 6, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, 3);
        var changedInstallDestinationGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, 4, 8, 6, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, 3);
        var changedReferenceTablesGeneration = new NormalLibrarySortCacheKey(7, 11, 1, 2, 3, 4, 5, 9, nameof(LibraryChartRow.WarningDigestText), ListSortDirection.Ascending, 3);

        Assert.AreEqual(scoreAware, scoreAwareSame);
        Assert.AreNotEqual(scoreAware, changedScoreGeneration);
        Assert.AreNotEqual(scoreAware, changedChartInfoGeneration);
        Assert.AreNotEqual(scoreAware, changedMaintenanceGeneration);
        Assert.AreEqual(dependencyAware, dependencyAwareSame);
        Assert.AreNotEqual(dependencyAware, changedWarningGeneration);
        Assert.AreNotEqual(dependencyAware, changedInstallDestinationGeneration);
        Assert.AreNotEqual(dependencyAware, changedReferenceTablesGeneration);
    }

    [TestMethod]
    public void NormalLibrarySourceGeneration_UsesOwnedVersionToSeparateSourceAndOverlayChanges()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldConsumeNormalLibrarySourceGenerationForOwnedCollectionVersionForTest(0, 0));
        Assert.IsTrue(MainWindowViewModel.ShouldConsumeNormalLibrarySourceGenerationForOwnedCollectionVersionForTest(2, 1));
        Assert.IsFalse(MainWindowViewModel.ShouldConsumeNormalLibrarySourceGenerationForOwnedCollectionVersionForTest(2, 2));
    }

    [TestMethod]
    public void MainSummaryCacheKey_UsesGenerationsForIdentity()
    {
        var current = new MainViewSummaryCacheKey(7, 11, 3, includeBmsonRows: true, "normal_default");
        var same = new MainViewSummaryCacheKey(7, 11, 3, includeBmsonRows: true, "normal_default");
        var changedSortKeyGeneration = new MainViewSummaryCacheKey(7, 12, 3, includeBmsonRows: true, "normal_default");

        Assert.AreEqual(current, same);
        Assert.AreNotEqual(current, changedSortKeyGeneration);
        Assert.IsFalse(MainWindowViewModel.IsMainSummaryFolderCountStaleForTest(current, 7, 11));
        Assert.IsTrue(MainWindowViewModel.IsMainSummaryFolderCountStaleForTest(current, 7, 12));
    }

    [TestMethod]
    public void LibraryChartSortMetrics_CarriesVirtualOrderTimingBreakdown()
    {
        var metrics = new LibraryChartSortMetrics(
            3,
            nameof(LibraryChartRow.path),
            ListSortDirection.Descending,
            nameof(String),
            "virtual_path_order",
            "ordinal_ignore_case",
            42,
            sortReuse: true,
            sortCacheKey: nameof(LibraryChartRow.path),
            sortCacheGeneration: 5,
            sortCacheHit: true,
            orderCacheLookupMs: 1,
            orderBuildMs: 0);

        Assert.AreEqual(1, metrics.OrderCacheLookupMs);
        Assert.AreEqual(0, metrics.OrderBuildMs);
    }

    [TestMethod]
    public void SourceRow_UsesStorageOwnerStateAndCurrentMutableState()
    {
        var file = new TestableBmsFile();
        file.Apply(@"folder-b\old.bms", "Old", "folder-b");
        PlaylistReferenceIndex playlistReferenceIndex = PlaylistReferenceIndex.Empty;
        ChartFileTransientState installDestinationState = ChartFileTransientState.Empty;
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(
            [file],
            playlistReferenceDisplayProvider: row => playlistReferenceIndex.Find(row.Chart),
            chartTransientStateProvider: (_, _) => installDestinationState);

        file.Apply(
            @"folder-a\new.bms",
            "New",
            "folder-a",
            artistName: "Artist",
            genreName: "Genre",
            modeValue: 7,
            tagText: "Tag",
            md5: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256Text: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        installDestinationState = ChartFileTransientState.FromInstallDestinationState(
            ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(file),
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
        playlistReferenceIndex.ReplaceTable(referenceTable, referenceTable.entries);

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

    [TestMethod]
    public void SourceRow_AndLibraryRowUsePlaylistReferenceProjectionForBmson()
    {
        LR2SongDBExtended.bmson_song bmson = CreateBmsonSong();
        var table = new BMSTable
        {
            symbol = "BMSN",
            name = "Bmson Table",
            entries =
            [
                new TestablePlaylistEntry(null, bmson.sha256)
            ]
        };
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceTable(table, table.entries);
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(
            null,
            [bmson],
            playlistReferenceDisplayProvider: row => index.Find(row.Hash, row.Sha256));
        var chartRow = LibraryChartRow.FromBmsonSong(bmson);
        chartRow.SetPlaylistReferenceDisplayProvider(row => index.Find(row.hash, row.sha256));

        Assert.AreEqual("BMSN", sourceRows[0].RefTablesSymbols);
        Assert.AreEqual("Bmson Table", sourceRows[0].RefTablesNames);
        Assert.AreEqual("BMSN", chartRow.RefTablesSymbols);
        Assert.AreEqual("Bmson Table", chartRow.RefTablesNames);
    }

    [TestMethod]
    public void SourceRow_SortKeysMatchLibraryChartRowForBmsAndBmson()
    {
        BMSFile file = CreateFile(
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
        file.bmsScore = CreateScore(file.hash, ClearType.HARD, RankType.AA, perfect: 800, great: 200, totalNotes: 1200, maxCombo: 999, minBp: 12, ranking: 42, rankingNum: 500, rankingLastUpdate: new DateTime(2026, 5, 15, 1, 2, 3, DateTimeKind.Local), stdDevVal: 51.5, scoreDifficulty: 78.25);
        LR2SongDBExtended.chart_info bmsChartInfo = CreateChartInfo(file.sha256, file.hash, level: 7, difficulty: 3, mainBpm: 150.5, total: 340.0);
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file.path, file.hash, 3), suppressPropertyChanged: true);
        file.SetWarning(ChartWarningKind.DuplicateChart, "duplicate warning");
        ChartFile bmsInstallDestinationChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: true),
            "Installed",
            "Installed Title",
            "Installed Artist",
            [],
            file.Warnings.ToStructuredList());
        var bmsTable = new BMSTable
        {
            symbol = "BMS",
            name = "BMS Table",
            entries = [new BMSTableEntry(file)]
        };
        PlaylistReferenceIndex playlistReferenceIndex = PlaylistReferenceIndex.Empty;
        playlistReferenceIndex.ReplaceTable(bmsTable, bmsTable.entries);
        ChartFileTransientState ResolveTransientState(ChartFile chart, bool includeWarningSnapshot)
        {
            return ReferenceEquals(chart?.GetBmsStorageOwner(), file)
                ? ChartFileTransientState.FromInstallDestinationState(bmsInstallDestinationChart, includeWarningSnapshot, forceInstallDestinationProjection: true)
                : ChartFileTransientState.Empty;
        }
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = @"folder-b\bmson.bmson",
            folder = "folder-b",
            title = "BmsonTitle",
            subtitle = "Another",
            artist = "BmsonArtist",
            genre = "BmsonGenre",
            mode_hint = "beat-7k",
            level = 9,
            md5 = "22222222222222222222222222222222",
            sha256 = "2222222222222222222222222222222222222222222222222222222222222222",
            MaintenanceInfo = CreateMaintenanceInfo(@"folder-b\bmson.bmson", "22222222222222222222222222222222", 4)
        };
        LR2SongDBExtended.chart_info bmsonChartInfo = CreateChartInfo("2222222222222222222222222222222222222222222222222222222222222222", "22222222222222222222222222222222", level: 4, difficulty: 1, mainBpm: 99.5, total: 240.0);
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(
            [file],
            [bmson],
            playlistReferenceDisplayProvider: row => playlistReferenceIndex.Find(row.Chart),
            chartTransientStateProvider: ResolveTransientState,
            chartInfoProjectionProvider: CreateChartInfoProvider(bmsChartInfo, bmsonChartInfo));
        LibraryChartRow bmsRow = LibraryChartRow.FromBmsFile(file);
        bmsRow.SetChartTransientStateProvider(ResolveTransientState);
        bmsRow.SetPlaylistReferenceDisplayProvider(row => playlistReferenceIndex.Find(row.Chart));
        bmsRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(bmsChartInfo));
        LibraryChartRow bmsonRow = LibraryChartRow.FromBmsonSong(bmson);
        bmsonRow.SetPlaylistReferenceDisplayProvider(row => playlistReferenceIndex.Find(row.Chart));
        bmsonRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(bmsonChartInfo));

        AssertSourceRowMatchesLibraryChartRow(sourceRows.Single(row => row.Chart.GetBmsStorageOwner() != null), bmsRow);
        AssertSourceRowMatchesLibraryChartRow(sourceRows.Single(row => row.Chart.GetBmsonStorageOwner() != null), bmsonRow);
    }

    [TestMethod]
    public void LibraryChartRow_DisplayRefreshInvalidatesProviderBackedChartCache()
    {
        BMSFile file = CreateFile(
            @"folder-a\chart-info.bms",
            "ChartInfo",
            "folder-a",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        LR2SongDBExtended.chart_info currentChartInfo = CreateChartInfo(file.sha256, file.hash, level: 3, difficulty: 1, mainBpm: 120.0, total: 240.0);
        var row = LibraryChartRow.FromBmsFile(file);
        row.SetChartInfoProjectionProvider(_ => currentChartInfo);

        Assert.AreEqual(3.0, row.ChartLevelSortKey);

        currentChartInfo = CreateChartInfo(file.sha256, file.hash, level: 9, difficulty: 4, mainBpm: 180.0, total: 360.0);
        row.RefreshDisplayForDataDependency(MainViewDataDependency.ChartInfo);

        Assert.AreEqual(9.0, row.ChartLevelSortKey);
    }

    [TestMethod]
    public void SourceRow_WarningDigestMatchesLibraryChartRowWithResourceProjection()
    {
        BMSFile file = CreateFile(
            @"folder-a\warning.bms",
            "Warning",
            "folder-a",
            hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetWarning(ChartWarningKind.ResourceWavMissing, "stale resource warning");
        file.SetWarning(ChartWarningKind.DuplicateChart, "duplicate warning");
        var projection = new ResourceHealthWarningProjection(
            1,
            [ChartWarning.Create(ChartWarningKind.ResourceBgaMissing, "projected resource warning")],
            isIgnored: false);
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(
            [file],
            resourceHealthProjectionProvider: _ => projection);
        var chartRow = LibraryChartRow.FromBmsFile(file);
        chartRow.SetResourceHealthProjectionProvider(_ => projection);

        Assert.AreEqual(chartRow.WarningDigestText, sourceRows[0].WarningDigestText);
        StringAssert.Contains(sourceRows[0].WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(sourceRows[0].WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
    }

    [TestMethod]
    public void BmsonSortKeyChangeDetection_CoversVirtualRegistryColumns()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(LibraryChartRow.Title),
                nameof(LibraryChartRow.Artist),
                nameof(LibraryChartRow.genre),
                nameof(LibraryChartRow.level),
                nameof(LibraryChartRow.mode),
                nameof(LibraryChartRow.Folder),
                nameof(LibraryChartRow.path),
                nameof(LibraryChartRow.tag),
                nameof(LibraryChartRow.hash),
                nameof(LibraryChartRow.sha256)
            },
            MainWindowViewModel.GetBmsonLibrarySortKeySnapshotColumnNamesForTest().ToArray());

        AssertBmsonSortKeyChange(song => song.title = "ChangedTitle");
        AssertBmsonSortKeyChange(song => song.folder = "changed-folder");
        AssertBmsonSortKeyChange(song => song.path = @"changed-folder\changed.bmson");
        AssertBmsonSortKeyChange(song => song.artist = "ChangedArtist");
        AssertBmsonSortKeyChange(song => song.genre = "ChangedGenre");
        AssertBmsonSortKeyChange(song => song.mode_hint = "beat-5k");
        AssertBmsonSortKeyChange(song => song.md5 = "33333333333333333333333333333333");
        AssertBmsonSortKeyChange(song => song.sha256 = "3333333333333333333333333333333333333333333333333333333333333333");
        AssertBmsonSortKeyChange(song => song.level = 10);
        AssertBmsonSortKeyNotChanged(song => song.MaintenanceInfo = CreateMaintenanceInfo(song.path, song.md5, 5));
    }

    [TestMethod]
    public void BmsonSourceIdentityChangeDetection_CoversOnlyCachedSourceRowIdentityColumns()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(LibraryChartRow.Title),
                nameof(LibraryChartRow.Artist),
                nameof(LibraryChartRow.genre),
                nameof(LibraryChartRow.level),
                nameof(LibraryChartRow.mode),
                nameof(LibraryChartRow.Folder),
                nameof(LibraryChartRow.path),
                nameof(LibraryChartRow.tag),
                nameof(LibraryChartRow.hash),
                nameof(LibraryChartRow.sha256)
            },
            MainWindowViewModel.GetBmsonLibrarySourceIdentitySnapshotColumnNamesForTest().ToArray());

        AssertBmsonSourceIdentityChange(song => song.title = "ChangedTitle");
        AssertBmsonSourceIdentityChange(song => song.folder = "changed-folder");
        AssertBmsonSourceIdentityChange(song => song.path = @"changed-folder\changed.bmson");
        AssertBmsonSourceIdentityChange(song => song.artist = "ChangedArtist");
        AssertBmsonSourceIdentityChange(song => song.genre = "ChangedGenre");
        AssertBmsonSourceIdentityChange(song => song.mode_hint = "beat-5k");
        AssertBmsonSourceIdentityChange(song => song.md5 = "33333333333333333333333333333333");
        AssertBmsonSourceIdentityChange(song => song.sha256 = "3333333333333333333333333333333333333333333333333333333333333333");
        AssertBmsonSourceIdentityChange(song => song.level = 10);

        AssertBmsonSourceIdentityNotChanged(song => song.MaintenanceInfo = CreateMaintenanceInfo(song.path, song.md5, 5));
        AssertBmsonSourceIdentityNotChanged(song => song.MaintenanceInfo.encoding = "utf-16");
    }

    [TestMethod]
    public void BmsonSortKeyChangeDetection_CoversSameReferenceMutation()
    {
        AssertBmsonSameReferenceSortKeyChange(song => song.title = "ChangedTitle");
        AssertBmsonSameReferenceSortKeyChange(song => song.folder = "changed-folder");
        AssertBmsonSameReferenceSortKeyChange(song => song.path = @"changed-folder\changed.bmson");
        AssertBmsonSameReferenceSortKeyChange(song => song.artist = "ChangedArtist");
        AssertBmsonSameReferenceSortKeyChange(song => song.genre = "ChangedGenre");
        AssertBmsonSameReferenceSortKeyChange(song => song.mode_hint = "beat-5k");
        AssertBmsonSameReferenceSortKeyChange(song => song.md5 = "33333333333333333333333333333333");
        AssertBmsonSameReferenceSortKeyChange(song => song.sha256 = "3333333333333333333333333333333333333333333333333333333333333333");
        AssertBmsonSameReferenceSortKeyChange(song => song.level = 10);
        AssertBmsonSameReferenceSortKeyNotChanged(song => song.MaintenanceInfo.encoding = "utf-16");
        AssertBmsonSameReferenceSortKeyNotChanged(song => song.MaintenanceInfo.wav_files_existing = 1);
    }

    private static ChartListVirtualView CreateView(out Func<int> getCreatedCount, int distinctFolderCount = -1)
    {
        List<BMSFile> files =
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
        var chart = row?.Chart;
        if (chart?.GetBmsStorageOwner() != null)
        {
            LibraryChartRow chartRow = LibraryChartRow.FromBmsFile(chart.GetBmsStorageOwner());
            chartRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(chart.ChartInfo));
            return chartRow;
        }
        if (chart?.GetBmsonStorageOwner() != null)
        {
            LibraryChartRow chartRow = LibraryChartRow.FromBmsonSong(chart.GetBmsonStorageOwner());
            chartRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(chart.ChartInfo));
            return chartRow;
        }
        return LibraryChartRow.FromChartFile(chart);
    }

    private static void AssertVirtualOrderMatchesExistingSort(string columnName, ListSortDirection direction)
    {
        List<BMSFile> files = CreateSampleSortFiles();
        List<LR2SongDBExtended.bmson_song> bmsons = CreateSampleSortBmsons();
        Dictionary<string, ChartFileTransientState> bmsInstallDestinationStates = CreateSampleSortInstallDestinationStates(files);
        ChartFileTransientState ResolveTransientState(ChartFile chart, bool includeWarningSnapshot)
        {
            return chart != null
                && bmsInstallDestinationStates.TryGetValue(chart.Path ?? string.Empty, out ChartFileTransientState state)
                    ? state
                    : ChartFileTransientState.Empty;
        }
        List<ChartListSourceRow> sourceRows = BuildOwnerBackedSourceRows(files, bmsons, chartTransientStateProvider: ResolveTransientState);
        bool created = ChartListOrder.TryCreate(sourceRows, columnName, direction, out ChartListOrder order);
        Assert.IsTrue(created);
        var view = new ChartListVirtualView(
            sourceRows,
            order,
            MaterializeSourceRow);
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = columnName,
            Direction = direction
        };

        IEnumerable<LibraryChartRow> bmsRows = files.Select(file =>
        {
            LibraryChartRow row = LibraryChartRow.FromBmsFile(file);
            row.SetChartTransientStateProvider(ResolveTransientState);
            return row;
        });
        List<LibraryChartRow> legacySorted = LibraryChartRowSortEngine.SortForMainView(
            bmsRows.Concat(bmsons.Select(LibraryChartRow.FromBmsonSong)),
            sortParameters,
            isPlaylistDetailView: false,
            useLegacySortForDataGrid: false,
            out _);

        CollectionAssert.AreEqual(
            legacySorted.Select(row => row.path).ToArray(),
            Enumerable.Range(0, view.Count).Select(index => ((LibraryChartRow)view[index]).path).ToArray(),
            columnName + " " + direction + " order mismatch.");
    }

    private static List<BMSFile> CreateSampleSortFiles()
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

    private static Dictionary<string, ChartFileTransientState> CreateSampleSortInstallDestinationStates(IEnumerable<BMSFile> files)
    {
        var states = new Dictionary<string, ChartFileTransientState>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile file in files ?? [])
        {
            string suffix = file.hash?.StartsWith("a", StringComparison.OrdinalIgnoreCase) == true
                ? "A"
                : file.hash?.StartsWith("b", StringComparison.OrdinalIgnoreCase) == true
                    ? "B"
                    : "C";
            ChartFile chart = ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
                "Install" + suffix,
                "InstallTitle" + suffix,
                "InstallArtist" + suffix,
                [],
                file.Warnings.ToStructuredList());
            states[file.path] = ChartFileTransientState.FromInstallDestinationState(
                chart,
                includeWarningSnapshot: false,
                forceInstallDestinationProjection: true);
        }
        return states;
    }

    private static List<LR2SongDBExtended.bmson_song> CreateSampleSortBmsons()
    {
        return
        [
            new LR2SongDBExtended.bmson_song
            {
                path = @"folder-c\bmson-beta.bmson",
                folder = "folder-c",
                title = "BmsonBeta",
                artist = "Beta",
                genre = "GenreD",
                mode_hint = "beat-7k",
                level = 8,
                md5 = "dddddddddddddddddddddddddddddddd",
                sha256 = "4444444444444444444444444444444444444444444444444444444444444444",
                MaintenanceInfo = CreateMaintenanceInfo(@"folder-c\bmson-beta.bmson", "dddddddddddddddddddddddddddddddd", 4)
            },
            new LR2SongDBExtended.bmson_song
            {
                path = @"folder-d\bmson-alpha.bmson",
                folder = "folder-d",
                title = "BmsonAlpha",
                artist = "AlphaBmson",
                genre = "Genre0",
                mode_hint = "beat-5k",
                level = 3,
                md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                sha256 = "5555555555555555555555555555555555555555555555555555555555555555",
                MaintenanceInfo = CreateMaintenanceInfo(@"folder-d\bmson-alpha.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", 5)
            }
        ];
    }

    private static BMSFile CreateFile(
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
        var file = new TestableBmsFile();
        file.Apply(path, title, folder, artist, genre, level, mode, tag, hash, sha256);
        if (scoreSeed.HasValue)
        {
            int seed = scoreSeed.Value;
            file.bmsScore = CreateScore(
                file.hash,
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
                scoreDifficulty: 70.0 + seed);
        }
        if (maintenanceSeed.HasValue)
        {
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file.path, file.hash, maintenanceSeed.Value), suppressPropertyChanged: true);
        }
        if (warningSeed.HasValue)
        {
            switch (warningSeed.Value)
            {
                case 1:
                    file.SetWarning(ChartWarningKind.ZeroNoteMismatch, "zero note warning");
                    break;
                case 2:
                    file.SetWarning(ChartWarningKind.DuplicateChart, "duplicate warning");
                    break;
                default:
                    file.SetWarning(ChartWarningKind.ChartInfoParseFailure, "chart info warning");
                    break;
            }
        }
        return file;
    }

    private static VirtualNormalLibrarySortDescriptor[] CreateExpectedDefaultPrewarmDescriptors()
    {
        return [.. CreateExpectedDefaultPrewarmColumnNames()
            .SelectMany(column => new[]
            {
                new VirtualNormalLibrarySortDescriptor(column, ListSortDirection.Ascending, GetPrewarmPriority(column)),
                new VirtualNormalLibrarySortDescriptor(column, ListSortDirection.Descending, GetPrewarmPriority(column))
            })];
    }

    private static int GetPrewarmPriority(string columnName)
    {
        Assert.IsTrue(ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata));
        return metadata.PrewarmPriority;
    }

    private static string[] CreateExpectedDefaultPrewarmColumnNames()
    {
        return
        [
            nameof(LibraryChartRow.Title),
            nameof(LibraryChartRow.Folder),
            nameof(LibraryChartRow.path),
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.clear),
            nameof(LibraryChartRow.rateDouble),
            nameof(LibraryChartRow.minbp),
            nameof(LibraryChartRow.ChartJudgeSortKey),
            nameof(LibraryChartRow.ChartNotes),
            nameof(LibraryChartRow.ChartLongNotes),
            nameof(LibraryChartRow.ChartScratchNotes),
            nameof(LibraryChartRow.ChartMainBpmSortKey),
            nameof(LibraryChartRow.ChartMinBpmSortKey),
            nameof(LibraryChartRow.ChartMaxBpmSortKey),
            nameof(LibraryChartRow.ChartSoflanCount),
            nameof(LibraryChartRow.ChartTotalSortKey),
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey),
            nameof(LibraryChartRow.ChartDurationSortKey),
            nameof(LibraryChartRow.ChartDensitySortKey),
            nameof(LibraryChartRow.ChartPeakDensitySortKey),
            nameof(LibraryChartRow.ChartEndDensitySortKey),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
            nameof(LibraryChartRow.instl_dst),
            nameof(LibraryChartRow.InstallDestinationTitle),
            nameof(LibraryChartRow.InstallDestinationArtist),
            nameof(LibraryChartRow.RefTablesSymbols),
            nameof(LibraryChartRow.score),
            nameof(LibraryChartRow.maxcombo),
            nameof(LibraryChartRow.rankingString),
            nameof(LibraryChartRow.rankingLastupdate),
            nameof(LibraryChartRow.stddevVal),
            nameof(LibraryChartRow.scoreDifficulty),
            nameof(LibraryChartRow.ChartLevelSortKey),
            nameof(LibraryChartRow.ChartDifficultySortKey),
            nameof(LibraryChartRow.ChartFeatureSortKey)
        ];
    }

    private static string[] CreateExpectedVirtualSortColumnNames()
    {
        return
        [
            nameof(LibraryChartRow.Title),
            nameof(LibraryChartRow.path),
            nameof(LibraryChartRow.Folder),
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.WarningDigestText),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
            nameof(LibraryChartRow.instl_dst),
            nameof(LibraryChartRow.InstallDestinationTitle),
            nameof(LibraryChartRow.InstallDestinationArtist),
            nameof(LibraryChartRow.RefTablesSymbols),
            nameof(LibraryChartRow.WAVHealth),
            nameof(LibraryChartRow.BGAHealth),
            nameof(LibraryChartRow.MovieHealth),
            nameof(LibraryChartRow.encoding),
            nameof(LibraryChartRow.level),
            nameof(LibraryChartRow.clear),
            nameof(LibraryChartRow.rateDouble),
            nameof(LibraryChartRow.score),
            nameof(LibraryChartRow.maxcombo),
            nameof(LibraryChartRow.minbp),
            nameof(LibraryChartRow.rankingString),
            nameof(LibraryChartRow.rankingLastupdate),
            nameof(LibraryChartRow.stddevVal),
            nameof(LibraryChartRow.scoreDifficulty),
            nameof(LibraryChartRow.ChartLevelSortKey),
            nameof(LibraryChartRow.ChartDifficultySortKey),
            nameof(LibraryChartRow.ChartMainBpmSortKey),
            nameof(LibraryChartRow.ChartMaxBpmSortKey),
            nameof(LibraryChartRow.ChartMinBpmSortKey),
            nameof(LibraryChartRow.ChartDurationSortKey),
            nameof(LibraryChartRow.ChartJudgeSortKey),
            nameof(LibraryChartRow.ChartFeatureSortKey),
            nameof(LibraryChartRow.ChartNotes),
            nameof(LibraryChartRow.ChartLongNotes),
            nameof(LibraryChartRow.ChartScratchNotes),
            nameof(LibraryChartRow.ChartTotalSortKey),
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey),
            nameof(LibraryChartRow.ChartDensitySortKey),
            nameof(LibraryChartRow.ChartPeakDensitySortKey),
            nameof(LibraryChartRow.ChartEndDensitySortKey),
            nameof(LibraryChartRow.ChartSoflanCount)
        ];
    }

    private static void AssertRegistryDependency(IEnumerable<ChartListOrderColumnMetadata> metadata, MainViewDataDependency dependency, string columnName)
    {
        Assert.AreEqual(
            dependency,
            metadata.Single(column => column.NormalizedColumnName == columnName).Dependency,
            columnName);
    }

    private static void AssertRegistryPrewarmPriority(IEnumerable<ChartListOrderColumnMetadata> metadata, int priority, string columnName)
    {
        Assert.AreEqual(
            priority,
            metadata.Single(column => column.NormalizedColumnName == columnName).PrewarmPriority,
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

    private static ChartListSourceRow CreateOwnerBackedSourceRow(BMSFile file)
    {
        return ChartListSourceRow.FromChartFile(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
            ChartListSourceProjectionMode.OwnerBacked);
    }

    private static ChartListSourceRow CreateOwnerBackedSourceRow(LR2SongDBExtended.bmson_song song)
    {
        return ChartListSourceRow.FromChartFile(
            ChartFileProjection.FromBmsonSong(song, includeWarningSnapshot: false),
            ChartListSourceProjectionMode.OwnerBacked);
    }

    private static List<ChartListSourceRow> BuildOwnerBackedSourceRows(
        IEnumerable<BMSFile>? files,
        IEnumerable<LR2SongDBExtended.bmson_song>? bmsonSongs = null,
        Func<ChartListSourceRow, ResourceHealthWarningProjection>? resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay>? playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState>? chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info>? chartInfoProjectionProvider = null)
    {
        List<ChartFile> charts =
        [
            .. (files ?? [])
                .Where(file => file != null)
                .Select(file => ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false, includeScoreSnapshot: true)),
            .. (bmsonSongs ?? [])
                .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                .OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)
                .Select(song => ChartFileProjection.FromBmsonSong(song, includeWarningSnapshot: false)),
        ];
        return ChartListSourceRow.BuildStandardLibraryRows(
            charts,
            ChartListSourceProjectionMode.OwnerBacked,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider,
            chartInfoProjectionProvider);
    }

    private static void AssertBmsonSortKeyChange(Action<LR2SongDBExtended.bmson_song> mutate)
    {
        LR2SongDBExtended.bmson_song original = CreateBmsonSong();
        LR2SongDBExtended.bmson_song next = CreateBmsonSong();
        mutate(next);
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(original);

        Assert.IsTrue(
            MainWindowViewModel.HasBmsonLibrarySortKeyChangedForTest(row, next));
    }

    private static void AssertBmsonSortKeyNotChanged(Action<LR2SongDBExtended.bmson_song> mutate)
    {
        LR2SongDBExtended.bmson_song original = CreateBmsonSong();
        LR2SongDBExtended.bmson_song next = CreateBmsonSong();
        mutate(next);
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(original);

        Assert.IsFalse(
            MainWindowViewModel.HasBmsonLibrarySortKeyChangedForTest(row, next));
    }

    private static void AssertBmsonSourceIdentityChange(Action<LR2SongDBExtended.bmson_song> mutate)
    {
        LR2SongDBExtended.bmson_song original = CreateBmsonSong();
        LR2SongDBExtended.bmson_song next = CreateBmsonSong();
        mutate(next);
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(original);

        Assert.IsTrue(
            MainWindowViewModel.HasBmsonLibrarySourceIdentityChangedForTest(row, next));
    }

    private static void AssertBmsonSourceIdentityNotChanged(Action<LR2SongDBExtended.bmson_song> mutate)
    {
        LR2SongDBExtended.bmson_song original = CreateBmsonSong();
        LR2SongDBExtended.bmson_song next = CreateBmsonSong();
        mutate(next);
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(original);

        Assert.IsFalse(
            MainWindowViewModel.HasBmsonLibrarySourceIdentityChangedForTest(row, next));
    }

    private static void AssertBmsonSameReferenceSortKeyChange(Action<LR2SongDBExtended.bmson_song> mutate)
    {
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(CreateBmsonSong());

        Assert.IsTrue(
            MainWindowViewModel.HasBmsonLibrarySortKeyChangedForTest(
                row,
                song =>
                {
                    mutate(song);
                }));
    }

    private static void AssertBmsonSameReferenceSortKeyNotChanged(Action<LR2SongDBExtended.bmson_song> mutate)
    {
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(CreateBmsonSong());

        Assert.IsFalse(
            MainWindowViewModel.HasBmsonLibrarySortKeyChangedForTest(
                row,
                song =>
                {
                    mutate(song);
                }));
    }

    private static LR2SongDBExtended.bmson_song CreateBmsonSong()
    {
        return new LR2SongDBExtended.bmson_song
        {
            path = @"folder-b\bmson.bmson",
            folder = "folder-b",
            title = "BmsonTitle",
            subtitle = "Subtitle",
            artist = "BmsonArtist",
            genre = "BmsonGenre",
            mode_hint = "beat-7k",
            level = 6,
            md5 = "22222222222222222222222222222222",
            sha256 = "2222222222222222222222222222222222222222222222222222222222222222",
            MaintenanceInfo = CreateMaintenanceInfo(@"folder-b\bmson.bmson", "22222222222222222222222222222222", 3)
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

    private static LR2SongDBExtended.chart_info CreateChartInfo(string sha256, string md5, int level, int difficulty, double mainBpm, double total)
    {
        return new LR2SongDBExtended.chart_info
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

    private static Func<ChartFile, LR2SongDBExtended.chart_info> CreateChartInfoProvider(params LR2SongDBExtended.chart_info[] rows)
    {
        return chart => ResolveChartInfoByIdentity(chart, rows);
    }

    private static LR2SongDBExtended.chart_info ResolveChartInfoByIdentity(ChartFile chart, IEnumerable<LR2SongDBExtended.chart_info> rows)
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

    private static BMSFileMaintenanceInfo CreateMaintenanceInfo(string path, string hash, int seed)
    {
        return new BMSFileMaintenanceInfo
        {
            path = path,
            hash = hash,
            encoding = seed % 2 == 0 ? "utf-8" : "shift_jis",
            wav_files_defined = 100,
            wav_files_existing = 20 + seed * 10,
            bga_files_defined = 50,
            bga_files_existing = 10 + seed * 5,
            movie_files_defined = 20,
            movie_files_existing = seed
        };
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void Apply(
            string filePath,
            string fileTitle,
            string folderName,
            string artistName = "",
            string genreName = "",
            int? levelValue = null,
            int? modeValue = null,
            string tagText = "",
            string md5 = "0123456789abcdef0123456789abcdef",
            string sha256Text = "")
        {
            path = filePath;
            title = fileTitle;
            folder = folderName;
            artist = artistName;
            genre = genreName;
            level = levelValue;
            mode = modeValue;
            tag = tagText;
            hash = md5;
            sha256 = sha256Text;
        }
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

        public IntPtr ParentHandle { private get; set; }

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

        public void PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null)
        {
            LastPlayedPath = bmsFilePath;
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

        public void VolumeChanged()
        {
        }

        internal void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class ThrowingFolderBmsFile : BMSFile
    {
        public override string Folder
        {
            get => throw new InvalidOperationException("Folder should not be read while constructing the virtual view.");
        }

        internal void Apply(string filePath, string fileTitle)
        {
            path = filePath;
            title = fileTitle;
        }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose()
        {
            throw new InvalidOperationException("dispose failed");
        }
    }
}
