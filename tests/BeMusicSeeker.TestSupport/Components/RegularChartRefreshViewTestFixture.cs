using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>分割した同機能caseが実際に共用する入力・観測のhelperだけをまとめます。</summary>
internal static class RegularChartRefreshViewTestFixture
{

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static PlaylistReferenceTableSnapshot ReferenceTable(BMSTable table, params ChartFile[] charts)
        => new(table, table.symbol, table.name, charts.Select(chart => new BMSTableEntry(chart)).ToArray());

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static bool BuildReferenceView(RegularScoreDeliveryTestFixture fixture, bool subset, string keyword)
    {
        var columns = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
            MainViewUpdateMode.FullScanAllChartsFilterSelected, Visibility.Collapsed, new PlaylistSummaryColumnSettings());
        if (subset)
        {
            return fixture.Owner.TryApplyVirtualChartSubset(new()
            {
                Library = fixture.Library,
                SourceCharts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows,
                SourceProjectionMode = ChartListSourceProjectionMode.OwnerBacked,
                SubsetName = "reference_delivery",
                TreeMode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                KeywordFilter = keyword,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.RefTablesSymbols),
                SortDirection = ListSortDirection.Ascending,
                ColumnSelection = columns,
                Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                Stopwatch = Stopwatch.StartNew()
            }).WasCommitted;
        }
        return fixture.Owner.TryApplyVirtualNormalLibrary(new()
        {
            Library = fixture.Library,
            KeywordFilter = keyword,
            ModeFilter = ChartModeFilter.All,
            SortColumnName = nameof(LibraryChartRow.RefTablesSymbols),
            SortDirection = ListSortDirection.Ascending,
            ColumnSelection = columns,
            Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
            Stopwatch = Stopwatch.StartNew()
        }).WasCommitted;
    }

    /// <summary>条件を生産側の実要求へ渡し、通常と専用の初回構築・依存登録を本番実装で行います。</summary>
    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static bool BuildSelectionView(RegularScoreDeliveryTestFixture fixture, bool subset, string sortColumn,
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

    /// <summary>初回全評価の入力境界を通常/専用subsetで共有して検査し、実配送と候補継続の別UI操作をfinallyまで回収します。</summary>
    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static async Task VerifyScoreCandidateAsync(string path, bool filtered, bool subset)
    {
        using var fixture = new RegularScoreDeliveryTestFixture(path);
        int lookups = 0;
        bool changed = false;
        fixture.Library.ScoreSnapshotWorkObserver = kind =>
        {
            if (kind == "score_projection_lookup" && Interlocked.Increment(ref lookups) == 2)
            {
                changed = true;
                fixture.Change(0, 40);
                fixture.Change(1, 5);
            }
        };
        Assert.IsFalse(fixture.Build(filtered, subset));
        Assert.IsTrue(changed);
        fixture.Library.ScoreSnapshotWorkObserver = null;
        Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
        ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
        operation.Run();
        Assert.IsFalse(delivery.IsCompleted);
        operation.Complete();
        await delivery;
        ScheduledScoreOperation continuation = await fixture.Scheduler.NextAsync();
        continuation.Run(); continuation.Complete();
        CollectionAssert.AreEqual(filtered ? new[] { "U", "A" } : new[] { "B", "U", "A" },
            fixture.Table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
        Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, fixture.View.ScorePosition);
        if (subset)
        {
            ChartListVirtualView rows = fixture.View;
            var unrelated = (LibraryChartRow)rows[filtered ? 0 : 1];
            fixture.Table.SelectedIndex = filtered ? 0 : 1;
            var work = new ConcurrentDictionary<string, int>();
            fixture.Library.ScoreSnapshotWorkObserver = kind => work.AddOrUpdate(kind, 1, (_, old) => old + 1);
            fixture.Table.RowProjection.NormalInputWorkObserver = (kind, amount) => work.AddOrUpdate(kind, amount, (_, old) => old + amount);
            fixture.Change(1, 30);
            Task warm = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
            ScheduledScoreOperation patch = await fixture.Scheduler.NextAsync();
            patch.Run(); patch.Complete();
            await warm;
            Assert.AreSame(rows, fixture.Table.Rows);
            Assert.AreSame(unrelated, rows[0]);
            Assert.AreEqual(0, fixture.Table.SelectedIndex);
            Assert.AreEqual(0, work.GetValueOrDefault("order_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("projection_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("hash_dependency_visit"));
            Assert.IsTrue(work.GetValueOrDefault("order_key") <= 1);
            Assert.IsTrue(work.GetValueOrDefault("score_projection_lookup") <= 2);
            CollectionAssert.AreEqual(new[] { "U", "B", "A" }, rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
            Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, rows.ScorePosition);
        }
    }

}
