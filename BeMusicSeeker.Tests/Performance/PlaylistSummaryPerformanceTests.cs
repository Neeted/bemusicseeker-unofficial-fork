using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

/// <summary>summaryの実Dispatcher区間と所有処理全体を、機能検証とは別に同一合成入力で明示測定します。</summary>
[TestClass, TestCategory("Net10Performance"), TestCategory("playlist_summary"), DoNotParallelize]
public sealed class PlaylistSummaryPerformanceTests
{
    // MSTestが実行前に注入する結果出力先です。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>500表58万entry・21万所持譜面の初回summary、warm sort/filter、単独編集の結果と実UI/全体時間を5反復記録します。時間に独自合否閾値を設けません。</summary>
    [TestMethod]
    public async Task SummaryPublication_MeasureDispatcherAndOwnedTerminal()
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture();
        fixture.Library.BmsCharts = [.. Enumerable.Range(1, 210_000).Select(index => ChartTestValues.Empty() with
        {
            Token = new OwnedChartToken(), Md5 = index.ToString("x32", CultureInfo.InvariantCulture),
            Sha256 = index.ToString("x64", CultureInfo.InvariantCulture), Path = $@"C:\SummaryCorpus\{index:D6}\chart.bms"
        })];
        // 所持索引の初回費用をsummary countのcold/warmと混同しません。全反復の事前条件を揃えます。
        fixture.Library.GetOwnedChartHashIndexSnapshot();
        var entries = Enumerable.Range(1, 1160).Select(index => new BMSTableEntry
        { md5 = index.ToString("x32", CultureInfo.InvariantCulture), title = "entry " + index }).ToList();
        fixture.Table.entries = entries;
        fixture.Table.name = "Table 0000";
        fixture.Playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { fixture.Table }.Concat(
            Enumerable.Range(1, 499).Select(index => new BMSTable
            { playlist_id = 8000 + index, name = $"Table {index:D4}", entries = entries.Select(entry => new BMSTableEntry { md5 = entry.md5, title = entry.title }).ToList(), is_bmt_output = false })));
        fixture.Playlist.CommitBMSTableWithEntriesToDB(fixture.Table);
        var samples = new Dictionary<string, List<double>>();
        for (int repetition = 0; repetition < 5; repetition++)
        {
            fixture.Table.name = "Table 0000";
            fixture.Table.symbol = "PF";
            var scheduled = new List<Task>();
            double uiMs = 0, queueMs = 0;
            int uiDepth = 0;
            var logs = new List<string>();
            Task Schedule(Action action)
            {
                var queued = Stopwatch.StartNew();
                void Apply()
                {
                    bool outer = uiDepth++ == 0;
                    if (outer) { queueMs += queued.Elapsed.TotalMilliseconds; }
                    var watch = Stopwatch.StartNew();
                    try { action(); }
                    finally { uiDepth--; if (outer) { uiMs += watch.Elapsed.TotalMilliseconds; } }
                }
                Task task;
                if (TestUiDispatcherHost.Dispatcher.CheckAccess()) { Apply(); task = Task.CompletedTask; }
                else { task = TestUiDispatcherHost.Dispatcher.InvokeAsync(Apply).Task; }
                lock (scheduled) { scheduled.Add(task); }
                return task;
            }
            using PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _,
                playlistStoreProvider: () => fixture.Playlist, playlistLibraryProvider: () => fixture.Library,
                propertySaveService: fixture.Service, presentationRefreshDeferredProvider: _ => true,
                restoreUiApplyScheduler: Schedule, restoreUiThreadCheck: () => TestUiDispatcherHost.Dispatcher.CheckAccess(),
                dispatchPresentation: action => { _ = Schedule(action); }, detailRetentionLog: message => logs.Add(message));
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            TestUiDispatcherHost.Invoke(() => workspace.RequestPlaylistSummaryMode(true));
            async Task Measure(string route, Func<Task> run, Action check)
            {
                uiMs = queueMs = 0;
                lock (scheduled) { scheduled.Clear(); }
                int logStart = logs.Count;
                var watch = Stopwatch.StartNew();
                await run();
                Task[] pending;
                lock (scheduled) { pending = [.. scheduled]; }
                await Task.WhenAll(pending);
                watch.Stop();
                TestUiDispatcherHost.Invoke(check);
                string[] work = logs.Skip(logStart).ToArray();
                int builds = work.Count(message => message.StartsWith("playlist_summary_build ", StringComparison.Ordinal));
                int presentations = work.Count(message => message.StartsWith("playlist_summary_present ", StringComparison.Ordinal));
                Assert.AreEqual(route == "cold" ? 1 : 0, builds, "明示presentationや編集でentry集計を再実行しません。");
                Assert.AreEqual(route == "edit" ? 0 : 1, presentations, "必要な全体評価は各要求で一回、編集は局所反映だけです。");
                foreach (string editWork in work.Where(message => message.StartsWith("playlist_summary_edit ", StringComparison.Ordinal)))
                { TestContext.WriteLine(editWork); }
                if (!samples.TryGetValue(route, out List<double>? values)) { samples[route] = values = []; }
                values.Add(watch.Elapsed.TotalMilliseconds);
                TestContext.WriteLine($"route={route} repetition={repetition + 1} tables=500 entries=580000 owned=210000 uiMs={uiMs:F3} uiQueueMs={queueMs:F3} terminalMs={watch.Elapsed.TotalMilliseconds:F3} builds={builds} presentations={presentations}");
            }
            try
            {
                await Measure("cold", async () =>
                {
                    await Task.Run(() => workspace.RebuildPlaylistSummaryView());
                    await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                }, () =>
                {
                    CollectionAssert.AreEqual(Enumerable.Range(0, 500).Select(index => $"Table {index:D4}").ToArray(), workspace.PlaylistSummaryView.Select(row => row.Name).ToArray());
                    Assert.AreEqual(580_000, workspace.PlaylistSummaryView.Sum(row => row.TotalCharts));
                });
                await Measure("sort", () =>
                {
                    return Schedule(() =>
                    {
                        workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.Name), ListSortDirection.Descending);
                        workspace.RefreshPlaylistSummaryPresentation();
                    });
                }, () => CollectionAssert.AreEqual(Enumerable.Range(0, 500).Reverse().Select(index => $"Table {index:D4}").ToArray(), workspace.PlaylistSummaryView.Select(row => row.Name).ToArray()));
                await Measure("filter_narrow", () => Schedule(() =>
                {
                    workspace.PlaylistSummaryKeywordFilter = "name:\"Table 0000\"";
                    workspace.RefreshPlaylistSummaryPresentation();
                }), () => Assert.AreEqual("Table 0000", workspace.PlaylistSummaryView.Single().Name));
                await Measure("filter", () =>
                {
                    return Schedule(() => { workspace.PlaylistSummaryKeywordFilter = "name:Table"; workspace.RefreshPlaylistSummaryPresentation(); });
                }, () => CollectionAssert.AreEqual(Enumerable.Range(0, 500).Reverse().Select(index => $"Table {index:D4}").ToArray(), workspace.PlaylistSummaryView.Select(row => row.Name).ToArray()));
                await Measure("edit", async () =>
                {
                    Task<PlaylistSummaryPropertyEditCompletion>? edit = null;
                    await Schedule(() => edit = workspace.CompleteSummaryPropertyEditAsync(
                        workspace.PlaylistSummaryView.Single(row => ReferenceEquals(row.TableRef, fixture.Table)), nameof(PlaylistSummaryRow.Symbol), "  Saved  ", true));
                    Assert.IsTrue((await (edit ?? throw new AssertFailedException("編集が開始されませんでした。"))).IsApplied);
                }, () => Assert.AreEqual("Saved", workspace.PlaylistSummaryView.Single(row => ReferenceEquals(row.TableRef, fixture.Table)).Symbol));
                foreach (string message in logs.Where(message => message.StartsWith("playlist_summary_build ", StringComparison.Ordinal)))
                { TestContext.WriteLine(message); }
            }
            finally { workspace.StopPlaylistSummaryDataBuild(); await workspace.WaitForPlaylistSummaryDataBuildIdleAsync(); }
        }
        foreach (KeyValuePair<string, List<double>> sample in samples)
        {
            double[] ordered = [.. sample.Value.Order()];
            TestContext.WriteLine($"route={sample.Key} samplesMs={string.Join(",", sample.Value.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} medianMs={ordered[2]:F3} minMs={ordered[0]:F3} maxMs={ordered[^1]:F3}");
        }
    }
}
