using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class RegularChartNormalLibraryRefreshTests
{
    /// <summary>背景16/128件の通常/専用元集合で別表headerを実確定し、参照検索の非表示加入・参照順序・選択を局所更新します。旧入力と未訪問行、同条件の次要求も確認します。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public void ReferenceHeaderCommit_UpdatesOnlyDependentInputAndPreservesSelection(int count, bool subset)
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());
        async Task RunAsync(string path)
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path, count);
            ChartFile[] charts = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.ToArray();
            BMSTable affected = new() { symbol = "A", name = "Hidden" };
            BMSTable other = new() { symbol = "M", name = "Ready" };
            fixture.Library.PublishPlaylistReferenceTables(
                [ReferenceTable(affected, charts[0]), ReferenceTable(other, charts[2])], []);
            await DeliverReferencesAsync(fixture);
            Assert.IsTrue(BuildReferenceView(fixture, subset, "playlist:Ready"));
            ChartListVirtualView rows = fixture.View;
            Assert.AreEqual(1, rows.Count);
            var selected = (LibraryChartRow)rows[0];
            Assert.AreEqual("U", selected.Title);
            fixture.Table.SelectedIndex = 0;
            NormalLibrarySourceRows? oldSource = rows.NormalInput;
            ChartListSourceRow? oldA = null;
            if (oldSource != null) { Assert.IsTrue(oldSource.TryGetRow(charts[0].Token, out oldA)); }
            int notifications = 0;
            selected.PropertyChanged += (_, _) => notifications++;
            var work = new Dictionary<string, int>();
            fixture.Table.RowProjection.NormalInputWorkObserver = (kind, amount) => work[kind] = work.GetValueOrDefault(kind) + amount;
            fixture.Library.PlaylistReferenceWorkObserver = kind => work[kind] = work.GetValueOrDefault(kind) + 1;
            var clock = Stopwatch.StartNew();
            fixture.Library.ApplyPlaylistReferenceEntryCommits([], [new(affected, "A", "Ready A", 0, 0)]);
            Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
            ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
            operation.Run();
            Assert.IsFalse(delivery.IsCompleted, "fanout callbackだけで実UI Completionを代用しません。");
            Assert.AreSame(rows, fixture.Table.Rows);
            Assert.AreEqual(2, rows.Count);
            Assert.AreSame(selected, rows[1]);
            Assert.AreEqual(1, fixture.Table.SelectedIndex);
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(1, rows.RealizedRowCount, "加入した未訪問Aを実体化しません。");
            Assert.AreEqual(1, work.GetValueOrDefault("projection"));
            Assert.IsTrue(work.GetValueOrDefault("source_visit") <= 3);
            Assert.IsTrue(work.GetValueOrDefault("order_key") <= 1);
            Assert.AreEqual(0, work.GetValueOrDefault("order_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("projection_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("hash_dependency_visit"));
            Assert.AreEqual(0, work.GetValueOrDefault("reference_entry_captured"));
            Assert.AreEqual(0, work.GetValueOrDefault("reference_lookup"));
            operation.Complete();
            await delivery;
            TestContext?.WriteLine("reference warm count=" + count + " subset=" + subset + " totalMs=" + clock.ElapsedMilliseconds
                + " work=" + string.Join(",", work.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + pair.Value)));
            Assert.AreEqual(fixture.Library.CapturePlaylistReferenceReadSnapshot([]).Revision, rows.ReferencePosition);
            var added = (LibraryChartRow)rows[0];
            Assert.AreEqual("A", added.Title);
            Assert.AreEqual("Ready A", added.RefTablesNames);
            if (oldA != null) { Assert.AreEqual("Hidden", oldA.RefTablesNames); }
            fixture.Table.RowProjection.NormalInputWorkObserver = null;
            fixture.Library.PlaylistReferenceWorkObserver = null;
            Assert.IsTrue(BuildReferenceView(fixture, subset, "playlist:Ready"));
            CollectionAssert.AreEqual(new[] { "A", "U" }, fixture.Table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
            fixture.Library.RemovePlaylistReferenceTables([affected]);
            await DeliverReferencesAsync(fixture);
            Assert.AreEqual(1, fixture.Table.Rows.Count);
            LibraryChartRow remaining = fixture.Table.Rows[0] as LibraryChartRow
                ?? throw new AssertFailedException("参照削除後の表示行がありません。");
            Assert.AreEqual("U", remaining.Title);
        }
    }

    /// <summary>初回参照capture後・source公開前に実header差分を配送し、依存登録前から受けたrangeで同じ構築候補の検索と順序を採用します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReferenceCommit_DuringInitialCaptureIsAppliedBeforeAdoption(bool subset)
    {
        WithTemporarySongDb(path =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            ChartFile a = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows[0];
            BMSTable table = new() { symbol = "OLD", name = "Hidden" };
            fixture.Library.PublishPlaylistReferenceTables([ReferenceTable(table, a)], []);
            DeliverReferencesAsync(fixture).GetAwaiter().GetResult();
            bool captured = false;
            fixture.Table.RowProjection.NormalInputWorkObserver = (kind, _) =>
            {
                if (kind != "reference_capture" || captured) { return; }
                captured = true;
                fixture.Library.ApplyPlaylistReferenceEntryCommits([], [new(table, "NEW", "Ready", 0, 0)]);
                DeliverReferencesAsync(fixture).GetAwaiter().GetResult();
            };
            Assert.IsTrue(BuildReferenceView(fixture, subset, "playlist:Ready"));
            Assert.IsTrue(captured);
            Assert.AreEqual(1, fixture.View.Count);
            var row = (LibraryChartRow)fixture.View[0];
            Assert.AreEqual("A", row.Title);
            Assert.AreEqual("NEW", row.RefTablesSymbols);
            Assert.AreEqual("Ready", row.RefTablesNames);
            Assert.AreEqual(fixture.Library.CapturePlaylistReferenceReadSnapshot([]).Revision, fixture.View.ReferencePosition);
        });
    }

    /// <summary>初回MD5が空表示で存在してもSHAを同時捕捉し、MD5 bucketの実削除だけでSHA参照による非表示加入へ収束します。再捕捉で欠落を埋めません。</summary>
    [TestMethod]
    public void ReferenceCapture_RetainsShaDependencyWhileEmptyMd5BucketExists()
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());
        async Task RunAsync(string path)
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            ChartFile a = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows[0] with { Sha256 = new string('f', 64) };
            fixture.Library.BmsCharts = [a];
            a = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.Single();
            BMSTable empty = new() { symbol = string.Empty, name = string.Empty };
            BMSTable fallback = new() { symbol = "SHA", name = "Fallback" };
            fixture.Library.PublishPlaylistReferenceTables([ReferenceTable(empty, a),
                new(fallback, fallback.symbol, fallback.name, [new BMSTableEntry { sha256 = a.Sha256 }])], []);
            await DeliverReferencesAsync(fixture);
            Assert.IsTrue(BuildReferenceView(fixture, false, "playlist:Fallback"));
            ChartListVirtualView rows = fixture.View;
            NormalLibrarySourceRows before = rows.NormalInput;
            Assert.AreEqual(0, rows.Count);
            Assert.IsTrue(before.TryGetRow(a.Token, out ChartListSourceRow old));
            Assert.AreEqual(string.Empty, old.RefTablesSymbols);
            int lookups = 0;
            fixture.Library.PlaylistReferenceWorkObserver = kind => { if (kind == "reference_lookup") { lookups++; } };
            fixture.Library.RemovePlaylistReferenceTables([empty]);
            await DeliverReferencesAsync(fixture);
            Assert.AreSame(rows, fixture.Table.Rows);
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(0, rows.RealizedRowCount);
            Assert.AreEqual(0, lookups);
            var row = (LibraryChartRow)rows[0];
            Assert.AreEqual("SHA", row.RefTablesSymbols);
            Assert.AreEqual("Fallback", row.RefTablesNames);
            Assert.AreEqual(string.Empty, old.RefTablesSymbols);
        }
    }

    private static PlaylistReferenceTableSnapshot ReferenceTable(BMSTable table, params ChartFile[] charts)
        => new(table, table.symbol, table.name, charts.Select(chart => new BMSTableEntry(chart)).ToArray());

    private static async Task DeliverReferencesAsync(RegularScoreDeliveryTestFixture fixture)
    {
        Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
        if (delivery.IsCompleted) { await delivery; return; }
        ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
        operation.Run();
        operation.Complete();
        await delivery;
    }

    private static bool BuildReferenceView(RegularScoreDeliveryTestFixture fixture, bool subset, string keyword)
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
}
