using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class RegularChartNormalLibraryRefreshTests
{
    /// <summary>通常/専用一覧の実score配送で副選択だけを移し、主添字が不変でも選択識別とShift基点を復元します。16/128件で選択二件以外を実体化しません。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public void ScoreReorder_ActualTablePreservesSecondarySelectionAndAnchor(int count, bool subset)
    {
        WithTemporarySongDb(path => TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path, count);
            Assert.IsTrue(fixture.Build(subset: subset));
            ChartListVirtualView rows = fixture.View;
            var table = new CustomTableView { ItemsSource = rows, SelectedIndex = 1 };
            try
            {
                // 元のA/U/BからA/Uを選択し、主行A=0、基点U=1に分けます。
                Assert.IsTrue(table.HandleKeyDown(Key.Up, ModifierKeys.Shift));
                ChartFile[] charts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.ToArray();
                AssertSelectedTokens(table, charts[0], charts[2]);
                int realized = rows.RealizedRowCount;
                fixture.Change(2, 40);
                DeliverSelectionChanges(fixture);
                Assert.AreEqual(0, table.SelectedIndex);
                Assert.AreEqual(2, rows.IndexOfSelectionIdentity(charts[2].Token));
                AssertSelectedTokens(table, charts[0], charts[2]);
                Assert.AreEqual(realized, rows.RealizedRowCount);
                // 復元された基点U=2から範囲を伸ばすので、次のShift下はB/Uになります。
                Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.Shift));
                AssertSelectedTokens(table, charts[1], charts[2]);
            }
            finally { table.ItemsSource = null; }
        }));
    }

    /// <summary>通常/専用一覧のscore絞込みから主行・基点・全選択が離脱しても、残選択の先頭または解除へ収束し、未選択行を代わりに採用しません。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void ScoreFilterRemoval_ActualTableRestoresRemainingSelection(bool subset, bool removeAnchor)
    {
        WithTemporarySongDb(path => TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            Assert.IsTrue(fixture.Build(filtered: true, subset: subset));
            var table = new CustomTableView { ItemsSource = fixture.View, SelectedIndex = 1 };
            try
            {
                table.HandleKeyDown(Key.Up, ModifierKeys.Shift);
                ChartFile[] charts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.ToArray();
                AssertSelectedTokens(table, charts[2], charts[1]);
                fixture.Change(removeAnchor ? 1 : 2, 1);
                DeliverSelectionChanges(fixture);
                ChartFile remaining = charts[removeAnchor ? 2 : 1];
                AssertSelectedTokens(table, remaining);
                Assert.AreEqual(0, table.SelectedIndex);
                table.HandleKeyDown(Key.Down, ModifierKeys.Shift);
                AssertSelectedTokens(table, remaining);
                int clearedNotifications = 0;
                table.SelectionChanged += (_, e) => { if (e.SelectedRows.Count == 0) { clearedNotifications++; } };
                fixture.Change(removeAnchor ? 2 : 1, 1);
                DeliverSelectionChanges(fixture);
                Assert.AreEqual(-1, table.SelectedIndex);
                Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count);
                Assert.AreEqual(1, clearedNotifications);
            }
            finally { table.ItemsSource = null; }
        }));
    }

    /// <summary>別表headerの実確定通知も通常/専用一覧の選択捕捉境界を通り、参照順で副選択だけが動いても元の譜面集合を返します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReferenceReorder_ActualTablePreservesSelectedIdentities(bool subset)
    {
        WithTemporarySongDb(path => TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            ChartFile[] charts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.ToArray();
            var first = new BMSTable { symbol = "A", name = "First" };
            var moving = new BMSTable { symbol = "M", name = "Moving" };
            var other = new BMSTable { symbol = "Z", name = "Other" };
            fixture.Library.PublishPlaylistReferenceTables(
                [ReferenceTable(first, charts[0]), ReferenceTable(moving, charts[2]), ReferenceTable(other, charts[1])], []);
            DeliverSelectionChanges(fixture);
            Assert.IsTrue(BuildReferenceView(fixture, subset, string.Empty));
            var table = new CustomTableView { ItemsSource = fixture.View, SelectedIndex = 1 };
            try
            {
                table.HandleKeyDown(Key.Up, ModifierKeys.Shift);
                AssertSelectedTokens(table, charts[0], charts[2]);
                fixture.Library.ApplyPlaylistReferenceEntryCommits([], [new(moving, "ZZ", "Moving", 0, 0)]);
                DeliverSelectionChanges(fixture);
                Assert.AreEqual(0, table.SelectedIndex);
                AssertSelectedTokens(table, charts[0], charts[2]);
                Assert.AreEqual(2, fixture.View.RealizedRowCount);
            }
            finally { table.ItemsSource = null; }
        }));
    }

    /// <summary>実catalogのpath確定で通常/重複subsetの副選択だけが末尾へ移っても、実表から旧二譜面を返し、主添字だけの復元では別譜面を選ぶ誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CatalogPathChange_ActualTablePreservesSelectedIdentities(bool subset)
    {
        WithTemporarySongDb(path => TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            ChartFile[] charts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.ToArray();
            var gateway = new BmsLibraryDbGateway(path);
            gateway.UpsertSongs(charts);
            Assert.IsTrue(BuildSelectionView(fixture, subset, nameof(LibraryChartRow.path), ChartListSourceProjectionMode.DuplicateCurrent));
            var table = new CustomTableView { ItemsSource = fixture.View, SelectedIndex = 1 };
            try
            {
                table.HandleKeyDown(Key.Up, ModifierKeys.Shift);
                var mutation = new CatalogMutationOwner(OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(fixture.Library), gateway);
                string newPath = Path.Combine(Path.GetDirectoryName(path) ?? throw new InvalidOperationException(), "9.bms");
                File.WriteAllText(newPath, "#TITLE Moved\n");
                Assert.IsTrue(mutation.ApplyCatalogMutation(new(chartPathChanges: [new LibraryChartPathChange
                { Chart = charts[1], OldPath = charts[1].Path, NewPath = newPath }])).Applied);
                DeliverSelectionChanges(fixture);
                Assert.AreEqual(0, table.SelectedIndex);
                Assert.AreEqual(2, fixture.View.IndexOfSelectionIdentity(charts[1].Token));
                AssertSelectedTokens(table, charts[0], charts[1]);
                Assert.AreEqual(2, fixture.View.RealizedRowCount);
            }
            finally { table.ItemsSource = null; }
        }));
    }

    /// <summary>実解析・詳細DB確定から通常/専用一覧のノーツ順を局所更新し、主行不動の副選択と未訪問行を保持します。背景16/128件で復元の全行実体化を検出します。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public void ChartInfoCommit_ActualTablePreservesSelectedIdentities(int count, bool subset)
    {
        WithTemporarySongDb(path => TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path, count);
            ChartFile[] charts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.Select(chart =>
            {
                File.WriteAllText(chart.Path, "#PLAYER 1\n#TITLE " + chart.Title + "\n#BPM 120\n#00111:01\n");
                ChartFile parsed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chart.Path));
                return chart with { Sha256 = parsed.Sha256 };
            }).ToArray();
            new BmsLibraryDbGateway(path).UpsertSongs(charts);
            fixture.Library.BmsCharts = charts;
            DeliverSelectionChanges(fixture);
            charts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.ToArray();
            Assert.IsTrue(BuildSelectionView(fixture, subset, nameof(LibraryChartRow.ChartNotes)));
            var table = new CustomTableView { ItemsSource = fixture.View, SelectedIndex = 1 };
            try
            {
                table.HandleKeyDown(Key.Up, ModifierKeys.Shift);
                AssertSelectedTokens(table, charts[0], charts[1]);
                ChartInfoInlineBuildResult parsed = fixture.Library.BuildAndPersistInlineChartInfoForInstalledCharts("chart_selection", [charts[1]]);
                Assert.AreEqual(1, parsed.SuccessCount);
                DeliverSelectionChanges(fixture);
                Assert.AreEqual(0, table.SelectedIndex);
                Assert.AreEqual(count - 1, fixture.View.IndexOfSelectionIdentity(charts[1].Token));
                AssertSelectedTokens(table, charts[0], charts[1]);
                Assert.AreEqual(2, fixture.View.RealizedRowCount);
                Assert.AreEqual(1, ((LibraryChartRow)fixture.View[count - 1]).ChartNotes);
            }
            finally { table.ItemsSource = null; }
        }));
    }

    /// <summary>同じ専用元集合のsort/filter再評価だけで選択識別を持ち越し、同区分でも別の群・packageや通常一覧へ選択を持ち越さない境界を確認します。</summary>
    [TestMethod]
    public void SelectionIdentityScope_DistinguishesSubsetContextsAndNormalLibrary()
    {
        WithTemporarySongDb(path =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            Assert.IsTrue(BuildSelectionView(fixture, true, nameof(LibraryChartRow.Title), context: "group A"));
            ChartListVirtualView first = fixture.View;
            Assert.IsTrue(BuildSelectionView(fixture, true, nameof(LibraryChartRow.stddevVal), context: "group A"));
            Assert.IsTrue(first.SharesSelectionIdentityScope(fixture.View));
            Assert.IsTrue(BuildSelectionView(fixture, true, nameof(LibraryChartRow.Title), context: "group B"));
            Assert.IsFalse(first.SharesSelectionIdentityScope(fixture.View));
            Assert.IsTrue(BuildSelectionView(fixture, false, nameof(LibraryChartRow.Title)));
            Assert.IsFalse(first.SharesSelectionIdentityScope(fixture.View));
        });
    }

    /// <summary>条件を生産側の実要求へ渡し、通常と専用の初回構築・依存登録を本番実装で行います。</summary>
    private static bool BuildSelectionView(RegularScoreDeliveryTestFixture fixture, bool subset, string sortColumn,
        ChartListSourceProjectionMode projection = ChartListSourceProjectionMode.OwnerBacked, object? context = null)
    {
        var columns = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
            MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings());
        if (subset)
        {
            return fixture.Owner.TryApplyVirtualChartSubset(new()
            {
                Library = fixture.Library,
                SourceCharts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows,
                SourceProjectionMode = projection,
                SubsetName = "selection",
                SelectionContext = context,
                TreeMode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = sortColumn,
                SortDirection = ListSortDirection.Ascending,
                ColumnSelection = columns,
                Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                Stopwatch = Stopwatch.StartNew()
            }).WasCommitted;
        }
        return fixture.Owner.TryApplyVirtualNormalLibrary(new()
        {
            Library = fixture.Library,
            ModeFilter = ChartModeFilter.All,
            SortColumnName = sortColumn,
            SortDirection = ListSortDirection.Ascending,
            ColumnSelection = columns,
            Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
            Stopwatch = Stopwatch.StartNew()
        }).WasCommitted;
    }

    /// <summary>同じ実schedulerのcallbackとCompletionを両方終端し、Dispatcher上の表への差分適用完了まで待ちます。</summary>
    private static void DeliverSelectionChanges(RegularScoreDeliveryTestFixture fixture)
    {
        Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
        if (!delivery.IsCompleted)
        {
            Task<ScheduledScoreOperation> next = fixture.Scheduler.NextAsync();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(next, "selection delivery callback");
            ScheduledScoreOperation operation = next.GetAwaiter().GetResult();
            operation.Run();
            operation.Complete();
        }
        TestUiDispatcherHost.AwaitTaskOnDispatcher(delivery, "selection delivery completion");
    }

    /// <summary>実表が後続操作へ返す選択行の譜面識別を、テスト所有の入力と比較します。</summary>
    private static void AssertSelectedTokens(CustomTableView table, params ChartFile[] expected)
        => CollectionAssert.AreEqual(expected.Select(chart => chart.Token).ToArray(),
            table.GetSelectedRowsSnapshot().Cast<LibraryChartRow>().Select(row => row.Chart.Token).ToArray());
}
