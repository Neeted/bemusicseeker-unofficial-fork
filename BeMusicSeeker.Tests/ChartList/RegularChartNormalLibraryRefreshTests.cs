using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>実libraryから通常一覧への変更通知、依存失効と停止時の収束を検証します。</summary>
/// <remarks>Windowを使わず実SQLite・package所有者からの通知接続を確認し、表示操作の接続はMainWindowのcompiled画面caseが分担します。</remarks>
[TestClass]
public sealed partial class RegularChartNormalLibraryRefreshTests
{
    public TestContext? TestContext { get; set; }
    /// <summary>背景16/128件を温めて一件だけ実保守し、配送終端までの投影・確保を対象数へ限定します。未訪問行と無関係行・選択を保持し、旧捕捉値は変更しません。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public void MaintenanceCommit_ChangesOnlyTargetProjectionAndKeepsUnvisitedRows(int count, bool dependOnHealth)
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());

        async Task RunAsync(string path)
        {
            string root = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
            ChartFile[] charts = Enumerable.Range(0, count).Select(index =>
            {
                string file = Path.Combine(root, index.ToString("D3") + ".bms");
                File.WriteAllText(file, "#PLAYER 1\r\n#TITLE Song " + index.ToString("D3") + "\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
                return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(file));
            }).ToArray();
            new BmsLibraryDbGateway(path).UpsertSongs(charts);
            var library = new TestBmsLibrary(path) { BmsCharts = charts };
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table));
            var request = new RegularVirtualNormalLibraryApplyRequest
            {
                Library = library,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = dependOnHealth ? nameof(LibraryChartRow.WAVHealth) : nameof(LibraryChartRow.Title),
                SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                Stopwatch = System.Diagnostics.Stopwatch.StartNew(),
                Reason = "maintenance_target_work"
            };
            var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<NormalLibraryRefreshAppliedEventArgs> handler = (_, args) =>
            {
                try
                {
                    MainViewRefreshAction action = owner.ApplyNotificationPresentation(library, args.NotificationBatch,
                        request.Mode, null, ChartListFilterSnapshot.Default, false);
                    if (action == MainViewRefreshAction.Refresh)
                    {
                        request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(request).WasCommitted);
                    }
                    applied.TrySetResult(true);
                }
                catch (Exception exception) { applied.TrySetException(exception); }
            };
            Task<MaintenanceWorkflowResult>? operation = null;
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                var initialClock = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(request).WasCommitted);
                initialClock.Stop();
                var rows = (ChartListVirtualView)table.Rows;
                var first = (LibraryChartRow)rows[0];
                var unrelated = (LibraryChartRow)rows[1];
                table.SelectedIndex = 1;
                CatalogChartCollectionSnapshot before = library.CaptureNormalLibraryChartSnapshot(false);
                ChartFile target = before.BmsRows[0];
                var work = new List<(string Kind, int Count)>();
                table.RowProjection.NormalInputWorkObserver = (kind, amount) => work.Add((kind, amount));
                owner.NormalLibraryRefreshApplied += handler;
                var updateClock = System.Diagnostics.Stopwatch.StartNew();
                operation = Task.Run(() => library.RescanResourceHealthCharts([target]));
                await Task.WhenAny(operation, applied.Task);
                await operation;
                await applied.Task;
                updateClock.Stop();
                TestContext?.WriteLine("normal_library count={0} health_sort={1} initial_ms={2:F3} warm_commit_delivery_ms={3:F3}", count,
                    dependOnHealth, initialClock.Elapsed.TotalMilliseconds, updateClock.Elapsed.TotalMilliseconds);
                Assert.IsTrue(work.Where(value => value.Kind is "projection_buffer" or "order_buffer").Sum(value => value.Count) <= 1,
                    "一件の差分で全件長の投影bufferを確保しません。");
                Assert.AreSame(rows, table.Rows);
                Assert.AreSame(first, rows[dependOnHealth ? count - 1 : 0]);
                Assert.AreSame(unrelated, rows[dependOnHealth ? 0 : 1]);
                Assert.AreEqual(dependOnHealth ? 0 : 1, table.SelectedIndex);
                Assert.AreEqual(2, rows.RealizedRowCount);
                Assert.AreEqual(0, first.WAVHealth);
                Assert.IsNull(target.WAVHealth);
                Assert.IsTrue(work.Where(value => value.Kind == "projection").Sum(value => value.Count) <= 1);
                Assert.IsTrue(work.Where(value => value.Kind == "source_visit").Sum(value => value.Count) <= 4,
                    "一件の差分で背景sourceへ訪問しません。");
                Assert.AreEqual(1, work.Where(value => value.Kind == "order_key").Sum(value => value.Count));
                Assert.AreEqual(0, work.Where(value => value.Kind == "hash_dependency_visit").Sum(value => value.Count));
                StringAssert.Contains(first.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
                table.RowProjection.NormalInputWorkObserver = null;
                applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
                library.SetChartResourceWarningsIgnored([first.Chart]);
                await applied.Task;
                Assert.IsTrue(first.Chart.ResourceHealthWarningsIgnored);
                Assert.IsTrue(library.TryGetCurrentResourceHealthWarningProjection(first.Chart).IsIgnored);
                StringAssert.Contains(first.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
                Assert.AreEqual(0, first.WAVHealth);
                applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
                library.SetChartResourceWarningsIgnored([first.Chart], unset: true);
                await applied.Task;
                Assert.IsFalse(first.Chart.ResourceHealthWarningsIgnored);
                Assert.IsFalse(library.TryGetCurrentResourceHealthWarningProjection(first.Chart).IsIgnored);
                StringAssert.Contains(first.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
                File.WriteAllBytes(Path.Combine(root, "missing.wav"), [0]);
                applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = Task.Run(() => library.RescanResourceHealthCharts([first.Chart]));
                await Task.WhenAny(operation, applied.Task);
                await operation;
                await applied.Task;
                Assert.AreEqual(100, first.WAVHealth);
                Assert.IsFalse(first.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, StringComparison.Ordinal));
                Assert.AreEqual(2, rows.RealizedRowCount);
                Assert.IsNull(unrelated.WAVHealth);
            }
            finally
            {
                owner.NormalLibraryRefreshApplied -= handler;
                table.RowProjection.NormalInputWorkObserver = null;
                await owner.StopAsync();
                library.RequestShutdown("maintenance_target_work_test");
                if (operation != null) { await operation; }
            }
        }
    }

    /// <summary>候補捕捉後の正規保守を配送前で止め、旧値の採用を待機させます。配送後は同じ候補へ対象差分だけを反映し、全構築をやり直しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CatalogCandidate_WaitsForUndeliveredCommitAndResumesOnlyAffectedRows(bool deliveryBeforeAdoption)
    {
        WithTemporarySongDb(path =>
        {
            string root = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
            string file = Path.Combine(root, "candidate.bms");
            File.WriteAllText(file, "#PLAYER 1\r\n#TITLE Z after あ\r\n", new System.Text.UTF8Encoding(false));
            ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(file)) with
            { Title = "A before", RawTitle = "A before" };
            string secondPath = Path.Combine(root, "background.bms");
            File.WriteAllText(secondPath, "#PLAYER 1\r\n#TITLE B background\r\n");
            ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondPath));
            new BmsLibraryDbGateway(path).UpsertSongs([first, second]);
            var library = new TestBmsLibrary(path) { BmsCharts = [first, second] };
            var table = new MainChartListViewModel();
            using var scheduler = new ScoreDeliveryUiScheduler();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table), action => action(),
                scheduler);
            var request = new RegularVirtualNormalLibraryApplyRequest
            {
                Library = library,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.Title),
                SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                Stopwatch = System.Diagnostics.Stopwatch.StartNew(),
                Reason = "undelivered_candidate"
            };
            bool changed = false;
            var work = new List<(string Kind, int Count)>();
            EventHandler captureThenCommit = (_, _) =>
            {
                if (changed) { return; }
                changed = true;
                ChartFile target = library.CaptureNormalLibraryChartSnapshot(false).BmsRows.Single(chart => chart.Path == file);
                library.SetBMSFilesEncoding([target], "utf-8");
                Assert.AreEqual("Z after あ", library.CaptureCatalogReadSnapshot([LibraryChartRef.FromChartFile(target)]).Resolve(LibraryChartRef.FromChartFile(target)).Title);
                // ここまでが候補の初回仕事です。配送を再開した後の全再構築を独立に観測します。
                work.Clear();
                if (deliveryBeforeAdoption)
                {
                    Task delivery = owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                    ScheduledScoreOperation operation = scheduler.NextAsync().GetAwaiter().GetResult();
                    operation.Run(); operation.Complete();
                    delivery.GetAwaiter().GetResult();
                }
            };
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                Task initial = owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                if (scheduler.TryTake(out ScheduledScoreOperation? pending)) { pending.Run(); pending.Complete(); }
                initial.GetAwaiter().GetResult();
                Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(request).WasCommitted);
                var originalRows = (ChartListVirtualView)table.Rows;
                var original = (LibraryChartRow)originalRows[0];
                CatalogChartCollectionSnapshot captured = originalRows.NormalInput.Snapshot;
                NormalLibrarySourceRows capturedInput = originalRows.NormalInput;
                var capturedOrder = ChartListOrder.CreateTitleAscending(capturedInput);
                NormalLibrarySortCacheKey capturedKey = owner.CreateVirtualOrderKey(owner.SourceGeneration, owner.SortKeyGeneration,
                    nameof(LibraryChartRow.Title), System.ComponentModel.ListSortDirection.Ascending, capturedInput.Count, new(0, 0, 0));
                table.RowProjection.NormalInputWorkObserver = (kind, amount) => work.Add((kind, amount));
                table.RowsReplacing += captureThenCommit;
                request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Assert.AreEqual(deliveryBeforeAdoption, owner.TryApplyVirtualNormalLibrary(request).WasCommitted);
                Assert.IsTrue(changed);
                if (!deliveryBeforeAdoption)
                {
                    Assert.AreSame(originalRows, table.Rows);
                    Assert.AreEqual("A before", original.Title);
                    Assert.AreEqual(1, scheduler.Count, "未配送中は採用待ちに留め、再試行を予約しません。");
                    Task delivery = owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                    ScheduledScoreOperation operation = scheduler.NextAsync().GetAwaiter().GetResult();
                    operation.Run(); operation.Complete();
                    delivery.GetAwaiter().GetResult();
                    ScheduledScoreOperation continuation = scheduler.NextAsync().GetAwaiter().GetResult();
                    continuation.Run(); continuation.Complete();
                }
                Assert.AreEqual(0, scheduler.Count);
                Assert.IsFalse(owner.TryPublishVirtualOrder(capturedKey, capturedOrder, new(0, 0, 0), capturedInput),
                    "世代が同じでも、背景構築の旧入力を新版のcacheへ再登録しません。");
                Assert.IsTrue(work.Where(value => value.Kind is "projection_buffer" or "order_buffer").Sum(value => value.Count) <= 1);
                Assert.AreEqual(0, ((ChartListVirtualView)table.Rows).RealizedRowCount);
                CollectionAssert.AreEqual(new[] { "B background", "Z after あ" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                Assert.AreEqual("A before", captured.BmsRows.Single(chart => chart.Path == file).Title);
                Assert.AreEqual(library.CatalogRevision, ((ChartListVirtualView)table.Rows).NormalInput.Snapshot.CatalogRevision);
            }
            finally
            {
                table.RowsReplacing -= captureThenCommit;
                table.RowProjection.NormalInputWorkObserver = null;
                Task stop = owner.StopAsync();
                scheduler.AbortOutstanding();
                while (scheduler.TryTake(out ScheduledScoreOperation? pending)) { pending.Run(); pending.Complete(); }
                stop.GetAwaiter().GetResult();
                library.RequestShutdown("undelivered_candidate_test");
            }
        });
    }

    /// <summary>実SQLiteの追加・同folder内/別folderへの移転・削除から正規commitと実配送を通し、形式範囲とkeywordの各表示件数・folder数・生存選択を独立値で確認します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void CatalogMembershipCommits_UpdateCountsAndDistinctFoldersLocally(bool includeBmson, bool filtered)
    {
        WithTemporarySongDb(path =>
        {
            string root = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
            ChartFile CreateChart(string folder, string name, string title, bool bmson = false)
            {
                string directory = Path.Combine(root, folder);
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, name + (bmson ? ".bmson" : ".bms"));
                File.WriteAllText(file, bmson
                    ? "{\"info\":{\"title\":\"" + title + "\",\"artist\":\"Artist\"},\"sound_channels\":[]}"
                    : "#PLAYER 1\r\n#TITLE " + title + "\r\n");
                return bmson ? ChartTestValues.ReadBmson(file)
                    : BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(file));
            }
            ChartFile first = CreateChart("Shared", "a", "Keep A");
            ChartFile background = CreateChart("Shared", "b", "Drop B");
            ChartFile bmson = CreateChart("Other", "z", "Keep Z", bmson: true);
            var gateway = new BmsLibraryDbGateway(path);
            gateway.UpsertSongs([first, background]);
            gateway.UpsertBmsonSongs([bmson]);
            var library = new TestBmsLibrary(path) { BmsCharts = [first, background], BmsonCharts = [bmson] };
            CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
            var mutations = new CatalogMutationOwner(catalog, gateway);
            var queue = new Queue<Action>();
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table), action => action(), new ActionQueueUiScheduler(queue.Enqueue));
            var filters = new ChartListFilterSnapshot(filtered ? "Keep" : string.Empty, ChartModeFilter.All);
            owner.NormalLibraryRefreshApplied += (_, args) => owner.ApplyNotificationPresentation(library, args.NotificationBatch,
                MainViewUpdateMode.FullScanAllChartsFilterSelected, null, filters, false);
            void Deliver()
            {
                Task delivery = owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                while (queue.Count > 0) { queue.Dequeue()(); }
                delivery.GetAwaiter().GetResult();
            }
            ChartFile Current(ChartFile value) => library.CaptureCatalogReadSnapshot([LibraryChartRef.FromChartFile(value)]).Resolve(LibraryChartRef.FromChartFile(value));
            ChartFile Move(ChartFile value, string folder, string name)
            {
                ChartFile current = Current(value);
                string directory = Path.Combine(root, folder);
                Directory.CreateDirectory(directory);
                string next = Path.Combine(directory, name + Path.GetExtension(current.Path));
                File.Move(current.Path, next);
                mutations.ApplyCatalogMutation(new(chartPathChanges: [new LibraryChartPathChange { Chart = current, OldPath = current.Path, NewPath = next }]));
                Deliver();
                return Current(value);
            }
            void Check(int expectedRows, int expectedFolders)
            {
                var rows = (ChartListVirtualView)table.Rows;
                Assert.AreEqual(expectedRows, rows.RowCount);
                Assert.AreEqual(expectedFolders, rows.DistinctFolderCount);
                string expectedSummary = "[" + expectedRows + BeMusicSeeker.Properties.Resources.Num_songs
                    + (expectedFolders > 1 ? " / " + expectedFolders + BeMusicSeeker.Properties.Resources.Num_folders : string.Empty) + "]";
                Assert.AreEqual(expectedSummary, table.SummaryText);
            }
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                Deliver();
                Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(new()
                {
                    Library = library,
                    IncludeBmsonRows = includeBmson,
                    KeywordFilter = filters.KeywordFilter,
                    ModeFilter = ChartModeFilter.All,
                    SortColumnName = nameof(LibraryChartRow.Title),
                    SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                    ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                        MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                    Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                    Stopwatch = System.Diagnostics.Stopwatch.StartNew(),
                    Reason = "catalog_membership_counts"
                }).WasCommitted);
                int initialCount = (filtered ? 1 : 2) + (includeBmson ? 1 : 0);
                Check(initialCount, 1 + (includeBmson ? 1 : 0));
                var originalRows = (ChartListVirtualView)table.Rows;
                ChartFile ownedFirst = library.CaptureNormalLibraryChartSnapshot().BmsRows.Single(chart => chart.Path == first.Path);
                var selected = (LibraryChartRow)originalRows[originalRows.IndexOfNormalToken(ownedFirst.Token)];
                table.SelectedIndex = originalRows.IndexOf(selected);
                ChartFile added = CreateChart("New", "c", "Keep C");
                gateway.UpsertSongs([added]);
                Assert.IsTrue(catalog.ApplyCommittedChartUpsert([added], out _));
                Deliver();
                added = library.CaptureNormalLibraryChartSnapshot().BmsRows.Single(chart => chart.Path == added.Path);
                Check(initialCount + 1, 2 + (includeBmson ? 1 : 0));
                added = Move(added, "New", "same-folder");
                Check(initialCount + 1, 2 + (includeBmson ? 1 : 0));
                added = Move(added, "Shared", "other-folder");
                Check(initialCount + 1, 1 + (includeBmson ? 1 : 0));
                ChartFile ownedBmson = library.CaptureNormalLibraryChartSnapshot().BmsonRows[0];
                ownedBmson = Move(ownedBmson, "Shared", "bmson-moved");
                Check(initialCount + 1, 1);
                Assert.AreSame(originalRows, table.Rows);
                Assert.AreSame(selected, table.Rows[table.SelectedIndex]);
                Assert.IsTrue(mutations.ApplyCatalogMutation(new(chartRemoveRequests: [OwnedChartRemoveRequest.FromChart(added)])).Applied);
                Deliver();
                Check(initialCount, 1);
                Assert.IsTrue(mutations.ApplyCatalogMutation(new(chartRemoveRequests: [OwnedChartRemoveRequest.FromChart(ownedFirst)])).Applied);
                Deliver();
                Check(initialCount - 1, !filtered || includeBmson ? 1 : 0);
                Assert.AreEqual(-1, table.SelectedIndex);
                Assert.IsTrue(mutations.ApplyCatalogMutation(new(chartRemoveRequests: [OwnedChartRemoveRequest.FromChart(ownedBmson)])).Applied);
                Deliver();
                Check(filtered ? 0 : 1, filtered ? 0 : 1);
            }
            finally
            {
                while (queue.Count > 0) { queue.Dequeue()(); }
                owner.StopAsync().GetAwaiter().GetResult();
                library.RequestShutdown("catalog_membership_counts_test");
            }
        });
    }

    /// <summary>実保守の文字コード再読解を同tokenの基本値・集合版・playlist局所差分と接続済み一覧の順序・絞込みへ反映し、再読解の無変更では版を増やさないことを確認します。</summary>
    [TestMethod]
    public void MaintenanceMetadataChange_ReachesAttachedViewAndWarmPlaylistDeltaOnce()
    {

        WithTemporarySongDb(songDbPath => RunAsync(songDbPath).GetAwaiter().GetResult());

        static async Task RunAsync(string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string firstPath = Path.Combine(root, "first.bms");
            string secondPath = Path.Combine(root, "second.bms");
            string backgroundPath = Path.Combine(root, "background.bms");
            File.WriteAllText(firstPath, "#PLAYER 1\r\n#TITLE Zあ\r\n#ARTIST 作者あ\r\n#GENRE genre\r\n", new System.Text.UTF8Encoding(false));
            File.WriteAllText(secondPath, "#PLAYER 1\r\n#TITLE Bあ\r\n#ARTIST 別作者あ\r\n#GENRE genre\r\n", new System.Text.UTF8Encoding(false));
            File.WriteAllText(backgroundPath, "#PLAYER 1\r\n#TITLE M before\r\n", System.Text.Encoding.ASCII);
            ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstPath)) with
            {
                Title = "A before",
                RawTitle = "A before",
                Artist = "old first",
                RawArtist = "old first"
            };
            ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondPath)) with
            {
                Title = "C before",
                RawTitle = "C before",
                Artist = "old second",
                RawArtist = "old second"
            };
            ChartFile background = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(backgroundPath));
            new BmsLibraryDbGateway(songDbPath).UpsertSongs([first, second, background]);
            var library = new TestBmsLibrary(songDbPath) { BmsCharts = [first, second, background] };
            OwnedChartCollectionState collection = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library).Collection;
            first = collection.ResolveCurrentChart(LibraryChartRef.FromPath(ChartFileKind.Bms, firstPath, null, null));
            second = collection.ResolveCurrentChart(LibraryChartRef.FromPath(ChartFileKind.Bms, secondPath, null, null));
            background = collection.ResolveCurrentChart(LibraryChartRef.FromPath(ChartFileKind.Bms, backgroundPath, null, null));
            var table = new MainChartListViewModel();
            using RegularChartListOwner receiver = CreateOwner(table, CreateWorkspaceForOwner(table));
            var request = new RegularVirtualNormalLibraryApplyRequest
            {
                Library = library,
                KeywordFilter = "before",
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.Title),
                SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                Stopwatch = System.Diagnostics.Stopwatch.StartNew(),
                Reason = "maintenance_metadata_connection"
            };
            var applied = new TaskCompletionSource<NormalLibraryRefreshNotificationBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<NormalLibraryRefreshAppliedEventArgs> handler = (_, args) =>
            {
                try
                {
                    MainViewRefreshAction action = receiver.ApplyNotificationPresentation(library, args.NotificationBatch,
                        MainViewUpdateMode.FullScanAllChartsFilterSelected, null, new ChartListFilterSnapshot(request.KeywordFilter, ChartModeFilter.All), false);
                    if (action == MainViewRefreshAction.Refresh)
                    {
                        request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        Assert.IsTrue(receiver.TryApplyVirtualNormalLibrary(request).WasCommitted);
                    }
                    applied.TrySetResult(args.NotificationBatch);
                }
                catch (Exception exception)
                {
                    applied.TrySetException(exception);
                }
            };
            Task<MaintenanceWorkflowResult>? operation = null;
            try
            {
                receiver.AttachNormalLibraryRefreshSource(library);
                Assert.IsTrue(receiver.TryApplyVirtualNormalLibrary(request).WasCommitted);
                CollectionAssert.AreEqual(new[] { "A before", "C before", "M before" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                receiver.NormalLibraryRefreshApplied += handler;
                var storeWork = new List<string>();
                library.PlaylistLibraryResolveIndexStoreWorkObserver = storeWork.Add;
                PlaylistLibraryResolveIndexSnapshot beforeIndex = library.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out _, out _);
                CatalogReadSnapshot beforeRead = library.CaptureCatalogReadSnapshot([LibraryChartRef.FromChartFile(first)]);
                storeWork.Clear();
                int version = library.CatalogRevision;
                operation = Task.Run(() => library.RescanResourceHealthCharts([first, second]));
                await Task.WhenAny(operation, applied.Task);
                MaintenanceWorkflowResult result = await operation;
                NormalLibraryRefreshNotificationBatch batch = await applied.Task;
                Assert.IsTrue(result.HasUpdates);
                Assert.AreEqual(2, result.ReloadedSongCount);
                Assert.AreEqual(version + 1, library.CatalogRevision);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.AreEqual(version + 1, batch.CatalogRevision);
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Token == first.Token));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Token == second.Token));
                ChartFile currentFirst = collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(first));
                ChartFile currentSecond = collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(second));
                Assert.AreSame(first.Token, currentFirst.Token);
                Assert.AreSame(second.Token, currentSecond.Token);
                Assert.AreEqual("Zあ", currentFirst.Title);
                Assert.AreEqual("作者あ", currentFirst.Artist);
                Assert.AreEqual("Bあ", currentSecond.Title);
                Assert.AreEqual("別作者あ", currentSecond.Artist);
                Assert.AreEqual("A before", first.Title);
                Assert.AreEqual("old second", second.Artist);
                Assert.AreSame(background, collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(background)));
                CollectionAssert.AreEqual(new[] { "M before" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                PlaylistLibraryResolveIndexSnapshot afterIndex = library.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out bool cacheHit, out _);
                Assert.IsTrue(cacheHit);
                Assert.AreEqual("A before", beforeRead.Resolve(beforeIndex.ResolveChartForPlaylistHash(first.Md5, first.Sha256)).Title);
                Assert.AreEqual("Zあ", library.CaptureCatalogReadSnapshot([afterIndex.ResolveChartForPlaylistHash(first.Md5, first.Sha256)]).Resolve(afterIndex.ResolveChartForPlaylistHash(first.Md5, first.Sha256)).Title);
                Assert.AreEqual("Bあ", library.CaptureCatalogReadSnapshot([afterIndex.ResolveChartForPlaylistHash(second.Md5, second.Sha256)]).Resolve(afterIndex.ResolveChartForPlaylistHash(second.Md5, second.Sha256)).Title);
                Assert.IsFalse(storeWork.Contains("playlist_resolve_delta_apply"));
                Assert.IsFalse(storeWork.Contains("playlist_resolve_source_entry_visited"));
                Assert.IsFalse(storeWork.Contains("playlist_resolve_source_enumeration"));
                Assert.IsFalse(storeWork.Contains("playlist_resolve_full_root_enumeration"));
                using (LR2SongDBExtended songDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    LR2SongDB.song firstRow = songDb.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?", firstPath).Single();
                    LR2SongDB.song secondRow = songDb.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?", secondPath).Single();
                    Assert.AreEqual("Zあ", firstRow.title);
                    Assert.AreEqual("作者あ", firstRow.artist);
                    Assert.AreEqual("Bあ", secondRow.title);
                    Assert.AreEqual("別作者あ", secondRow.artist);
                }
                request.KeywordFilter = "";
                request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsTrue(receiver.TryApplyVirtualNormalLibrary(request).WasCommitted);
                CollectionAssert.AreEqual(new[] { "Bあ", "M before", "Zあ" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = Task.Run(() => library.RescanResourceHealthCharts([currentFirst, currentSecond]));
                await Task.WhenAny(operation, applied.Task);
                MaintenanceWorkflowResult repeated = await operation;
                NormalLibraryRefreshNotificationBatch repeatedBatch = await applied.Task;
                Assert.AreEqual(2, repeated.ReloadedSongCount);
                Assert.AreEqual(version + 1, library.CatalogRevision);
                Assert.IsFalse(repeatedBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                CollectionAssert.AreEqual(new[] { "Bあ", "M before", "Zあ" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
            }
            finally
            {
                receiver.NormalLibraryRefreshApplied -= handler;
                await receiver.StopAsync();
                library.PlaylistLibraryResolveIndexStoreWorkObserver = null;
                library.RequestShutdown("maintenance_metadata_connection_test");
                if (operation != null)
                {
                    await operation;
                }
            }
        }
    }

    /// <summary>表のsort・keyword・mode変更、別package、reset、情報不足を組み合わせ、現在表示へ影響する変更だけ再構築して不要再構築と必要更新漏れを検出します。</summary>
    [DataTestMethod]
    // 通常タイトル・別packageは表示のみ。path・keyword・mode・reset・情報不足は再構築します。
    [DataRow("Title", "", false, false, false, false, false)]
    [DataRow("path", "", false, false, false, false, true)]
    [DataRow("Title", "before", false, false, false, false, true)]
    [DataRow("Title", "", true, false, false, false, true)]
    [DataRow("Title", "", false, true, false, false, false)]
    [DataRow("Title", "", false, false, true, false, true)]
    [DataRow("Title", "", false, false, false, true, true)]
    public void InstalledBatch_ResolvesCurrentViewDependenciesOnce(string sort, string keyword,
        bool modeChanged, bool unrelatedPackage, bool reset, bool informationMissing, bool rebuild)
    {
        ChartFile before = ChartTestValues.Empty(ChartFileKind.Bms) with
        {
            Token = new OwnedChartToken(),
            Path = @"C:\before\chart.bms",
            Title = "A",
            Mode = 7,
            Md5 = new string('a', 32)
        };
        ChartFile current = before with { Path = @"C:\after\chart.bms", Mode = modeChanged ? 14 : 7 };
        var entry = PackageChartEntry.FromChart(current);
        var package = ChartPackage.FromChartEntries([entry]);
        ChartPackage selected = unrelatedPackage ? ChartPackage.FromChartEntries([PackageChartEntry.FromChart(before with { Token = new OwnedChartToken() })]) : package;
        var batch = new NormalLibraryRefreshNotificationBatch(1, 1,
            LibraryChartRefreshEffects.SourceChanged | LibraryChartRefreshEffects.WarningPresentationChanged
                | LibraryChartRefreshEffects.MaintenancePresentationChanged | LibraryChartRefreshEffects.InstallDestinationOverlayChanged,
            [], reset, changedCharts: [current],
            installedChartChanges: informationMissing ? [] : [new(entry, [package], before, current)]);
        MainViewRefreshAction action = MainViewRefreshDecisionService.BuildNotificationBatch(batch,
            MainViewUpdateMode.NewlyInstalledFolderSelected, selected, false, keyword,
            modeChanged ? ChartModeFilter._7KEYS : ChartModeFilter.All, sort, false);
        Assert.AreEqual(rebuild ? MainViewRefreshAction.Refresh : MainViewRefreshAction.RefreshDisplay, action);
    }

    /// <summary>導入済み項目の表示だけに影響する複数effectをまとめても行参照を保持して表示更新を一回だけ出し、重複更新と行再置換を検出します。</summary>
    [TestMethod]
    public void InstalledBatch_AllPresentationEffectsIssueOneDisplayRequestAndKeepRows()
    {
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(songDbPath);
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table));
            ChartFile before = ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken(), Path = @"C:\before.bmson", Title = "A" };
            var entry = PackageChartEntry.FromChart(before);
            var package = ChartPackage.FromChartEntries([entry]);
            ChartFile current = before with { Path = @"C:\after.bmson" };
            entry.ApplyCurrentChart(current);
            IListRowsForNotification(table, entry);
            System.Collections.IList rows = table.Rows;
            int replacing = 0;
            int display = 0;
            table.RowsReplacing += (_, _) => replacing++;
            table.DisplayRefreshRequested += (_, _) => display++;
            var batch = new NormalLibraryRefreshNotificationBatch(1, 1,
                LibraryChartRefreshEffects.SourceChanged | LibraryChartRefreshEffects.WarningPresentationChanged
                    | LibraryChartRefreshEffects.MaintenancePresentationChanged | LibraryChartRefreshEffects.InstallDestinationOverlayChanged,
                [], false, changedCharts: [current], changedDetailMd5s: ["detail"],
                installedChartChanges: [new(entry, [package], before, current)]);
            Assert.AreEqual(MainViewRefreshAction.RefreshDisplay,
                owner.ApplyNotificationPresentation(library, batch, MainViewUpdateMode.NewlyInstalledFolderSelected,
                    package, ChartListFilterSnapshot.Default, false));
            Assert.AreSame(rows, table.Rows);
            Assert.AreEqual(0, replacing);
            Assert.AreEqual(1, display);
        });
    }

    /// <summary>実package所有者が確定したタイトル変更の通知から一覧を再整列し、元項目と旧snapshotを保持して、旧タイトル順の残留や項目差替えを検出します。</summary>
    [TestMethod]
    public void InstalledTitleChangeThroughOwnerFactsRebuildsFinalTitleOrder()
    {
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(songDbPath);
            var table = new MainChartListViewModel();
            using RegularChartListOwner receiver = CreateOwner(table, CreateWorkspaceForOwner(table));
            var producer = new PackageLifecycleOwner(new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), _ => { },
                packages => new System.Collections.ObjectModel.ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            ChartFile before = ChartTestValues.Empty(ChartFileKind.Bms) with
            {
                Token = new OwnedChartToken(),
                Path = @"C:\Charts\a.bms",
                Title = "A",
                RawTitle = "A"
            };
            var first = PackageChartEntry.FromChart(before);
            var second = PackageChartEntry.FromChart(before with { Token = new OwnedChartToken(), Path = @"C:\Charts\m.bms", Title = "M", RawTitle = "M" });
            var package = ChartPackage.FromChartEntries([first, second]);
            producer.ReplaceInstalledPackages([package]);
            var request = new RegularVirtualChartSubsetApplyRequest
            {
                Library = library,
                SourceEntries = [first, second],
                TreeMode = MainViewUpdateMode.NewlyInstalledFolderSelected,
                SubsetName = "installed_title_connection",
                KeywordFilter = "",
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.Title),
                SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    MainViewUpdateMode.NewlyInstalledFolderSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.NewlyInstalledFolderSelected,
                Stopwatch = System.Diagnostics.Stopwatch.StartNew()
            };
            try
            {
                Assert.IsTrue(receiver.TryApplyVirtualChartSubset(request).WasCommitted);
                LibraryChartRow[] initialRows = table.Rows.Cast<LibraryChartRow>().ToArray();
                Assert.AreEqual("A", initialRows[0].Title);
                var changes = new List<InstalledChartCurrentChange>();
                ChartFile current = before with { Title = "Z", RawTitle = "Z" };
                producer.PrepareCommittedChartApplication([current], changes)();
                var publisher = new NormalLibraryRefreshPublisher();
                publisher.Publish(new NormalLibraryRefreshPublishRequest
                {
                    Effects = LibraryChartRefreshEffects.SourceChanged,
                    ChangedCharts = [current],
                    InstalledChartChanges = changes
                });
                NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(0);
                Assert.AreEqual(MainViewRefreshAction.Refresh, receiver.ApplyNotificationPresentation(library, batch,
                    MainViewUpdateMode.NewlyInstalledFolderSelected, package, ChartListFilterSnapshot.Default, false));
                request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsTrue(receiver.TryApplyVirtualChartSubset(request).WasCommitted);
                LibraryChartRow[] finalRows = table.Rows.Cast<LibraryChartRow>().ToArray();
                Assert.AreEqual("M", finalRows[0].Title);
                Assert.AreEqual("Z", finalRows[1].Title);
                Assert.AreSame(first, finalRows[1].PackageEntry);
                Assert.AreEqual("A", before.Title);
            }
            finally
            {
                producer.ClearInstalledPackages();
            }
        });
    }

    private static void IListRowsForNotification(MainChartListViewModel table, PackageChartEntry entry)
    {
        table.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = new System.Collections.Generic.List<LibraryChartRow> { LibraryChartRow.FromPackageChartEntry(entry) },
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD),
            SelectionPolicy = MainChartListSelectionPolicy.Reset,
            Summary = MainChartListSummaryUpdate.NormalCounts(1, 1),
            Stopwatch = System.Diagnostics.Stopwatch.StartNew()
        });
    }

    /// <summary>実libraryの通常score setterがsnapshot変更キーを生産し、接続済み通常一覧の実lookup表示だけを更新して無関係行へ通知しません。</summary>
    /// <remarks>既存Attachと部分一覧のfixtureを使い、投影値の条件網羅はChartListVirtualViewTestsとsort engineへ分担します。</remarks>
    [TestMethod]
    public void AttachedScoreSnapshotSource_RefreshesOnlyMatchingRowFromRealScoreSetter()
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string scorePath = Path.Combine(root, "score.db");
            const string affectedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string unrelatedHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            using (var db = new LR2ScoreDBExtended(scorePath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2ScoreDB.player>();
                    db.CreateTable<LR2ScoreDB.score>();
                    db.Insert(new LR2ScoreDB.player { id = "row-score", irid = 123 });
                    db.Insert(new LR2ScoreDB.score { hash = affectedHash, perfect = 321, great = 45 });
                    db.Insert(new LR2ScoreDB.score { hash = unrelatedHash, perfect = 100, great = 20 });
                });
            }
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2RootPath = root };
            var library = new TestBmsLibrary(songDbPath, null, scorePath, null, () => options,
                TestBmsFactory.MissingEverythingBridge);
            library.InitializeScoresOnly(null);
            library.BmsCharts =
            [
                CreateTestableBmsFile(Path.Combine(root, "affected.bms")) with { Title = "Affected" },
                CreateTestableBmsFile(Path.Combine(root, "unrelated.bms")) with { Title = "Unrelated", Md5 = unrelatedHash }
            ];
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table));
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(new RegularVirtualNormalLibraryApplyRequest
                {
                    Library = library,
                    KeywordFilter = string.Empty,
                    ModeFilter = ChartModeFilter.All,
                    SortColumnName = nameof(LibraryChartRow.Title),
                    SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                    ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                        MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                    Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                    Stopwatch = System.Diagnostics.Stopwatch.StartNew(),
                    Reason = "real_score_snapshot_connection"
                }).WasCommitted);
                LibraryChartRow[] rows = table.Rows.Cast<LibraryChartRow>().ToArray();
                Assert.AreEqual(2, rows.Length);
                LibraryChartRow affected = rows.Single(row => row.Title == "Affected");
                LibraryChartRow unrelated = rows.Single(row => row.Title == "Unrelated");
                int affectedNotifications = 0;
                int unrelatedNotifications = 0;
                affected.PropertyChanged += (_, _) => affectedNotifications++;
                unrelated.PropertyChanged += (_, _) => unrelatedNotifications++;
                BMSScore score = library.GetBMSScores().Single(value => value.hash == affectedHash);
                score.ranking = 7;
                Assert.AreEqual(7, affected.ranking);
                Assert.AreEqual(7, library.ResolveChartScoreSnapshot(ChartFileKind.Bms, affected.Chart.Path, affectedHash, string.Empty).Ranking);
                Assert.IsTrue(affectedNotifications > 0);
                Assert.AreEqual(0, unrelatedNotifications);
                Assert.AreEqual(220, unrelated.score);
            }
            finally { library.RequestShutdown("row-score-fixture-cleanup"); }
        });
    }

    /// <summary>通知済みlibraryへ接続すると未適用変更へ一回追従して派生cacheを失効させ、同じ版の二重適用を検出します。</summary>
    [TestMethod]
    public void AttachNormalLibraryRefreshSource_CatchesUpOnceAndSuppressesDuplicate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\catch-up.bms")];
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            RegularMaterializedChartListApplyResult materialized = owner.TryApplyMaterialized(
                CreateMaterializedApplyRequest(
                [
                    LibraryChartRow.FromChartFile(CreateSourceRow("Old", "old.bms").Chart)
                ]));
            Assert.IsTrue(materialized.WasCommitted);
            Assert.IsTrue(owner.HasFolderRows);
            var applied = new List<NormalLibraryRefreshAppliedEventArgs>();
            owner.NormalLibraryRefreshApplied += (_, args) => applied.Add(args);
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);

                Assert.AreEqual(1, applied.Count);
                Assert.IsTrue(applied[0].NotificationBatch.ResetsPriorNotifications);
                Assert.IsTrue(applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.IsTrue(owner.SourceGeneration > 0);
                Assert.AreEqual(
                    applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged),
                    owner.WarningGeneration > 0);
                Assert.AreEqual(
                    applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged)
                        && !applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged),
                    owner.InstallDestinationGeneration > 0);
                Assert.IsFalse(owner.HasFolderRows);
                Assert.AreEqual(1, applied.Count);
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    /// <summary>接続後の実保存行置換が一覧のreset通知へ一回届き、購読漏れと二重適用を検出します。</summary>
    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_AppliesPublishedStorageReplacement()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            using var applied = new ManualResetEventSlim();
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.ResetsPriorNotifications)
                {
                    Interlocked.Increment(ref appliedCount);
                    applied.Set();
                }
            };
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\published.bms")];

                applied.Wait();
                Assert.AreEqual(1, Volatile.Read(ref appliedCount));
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    /// <summary>通知callbackを保持して停止workerを開始しても、解放後は集合変更とStopAsyncの実Taskがどちらも終端し、通知が一回に収束することを確認します。</summary>
    /// <remarks>stopStartedはStopAsync呼出し前の合図であり、callback解放まで停止完了が待つ順序は検査しません。未実行通知の停止回収はStopAsync_QueuedNormalLibraryRefreshBecomesNoOpが分担します。</remarks>
    [TestMethod]
    public void StopAsync_DrainsInFlightNormalLibraryRefreshApplication()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            using var applyEntered = new ManualResetEventSlim();
            using var releaseApply = new ManualResetEventSlim();
            using var stopStarted = new ManualResetEventSlim();
            using var stopCompleted = new ManualResetEventSlim();
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (!args.NotificationBatch.ResetsPriorNotifications)
                {
                    return;
                }
                Interlocked.Increment(ref appliedCount);
                applyEntered.Set();
                releaseApply.Wait();
            };

            Task mutationTask = StartLongRunning(() =>
            {
                library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\in-flight.bms")];
            });
            Task? stopTask = null;
            try
            {
                applyEntered.Wait();
                stopTask = StartLongRunningAsync(async delegate
                {
                    stopStarted.Set();
                    await owner.StopAsync();
                    stopCompleted.Set();
                });
                stopStarted.Wait();
                Assert.IsFalse(stopCompleted.IsSet);
                releaseApply.Set();
                Task.WaitAll(mutationTask, stopTask);
                Assert.AreEqual(1, Volatile.Read(ref appliedCount));
            }
            finally
            {
                releaseApply.Set();
                try
                {
                    mutationTask.GetAwaiter().GetResult();
                    stopTask?.GetAwaiter().GetResult();
                }
                finally
                {
                    owner.Dispose();
                }
            }
        });
    }


    /// <summary>接続時のcatch-upを注入execution laneに予約し、実行前は世代を変えず実行後に一回適用することで、呼出laneでの先行適用を検出します。</summary>
    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_MarshalsApplyToExecutionLane()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\queued.bms")];
            var pendingActions = new Queue<Action>();
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(pendingActions.Enqueue));
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, _) => appliedCount++;
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);

                Assert.AreEqual(1, pendingActions.Count);
                Assert.AreEqual(0, appliedCount);
                Assert.AreEqual(0L, owner.SourceGeneration);

                pendingActions.Dequeue()();

                Assert.AreEqual(1, appliedCount);
                Assert.IsTrue(owner.SourceGeneration > 0);
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    /// <summary>catalog writer保持中でも生産側がUI完了を待たず予約して戻り、guard解放後の一回の配送と捕捉が終端することを確認します。</summary>
    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_QueuesUiDeliveryWhileCatalogWriterHeld()
    {
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(songDbPath);
            var queue = new Queue<Action>();
            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner(),
                action => action(), new ActionQueueUiScheduler(queue.Enqueue));
            int applied = 0;
            owner.AttachNormalLibraryRefreshSource(library);
            // writerはreceiptを登録し、既存の公開口は登録済み通知の配送だけを起動する。
            OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library).ReplaceCharts(
                [CreateTestableBmsFile("C:\\Charts\\writer-held.bms")], []);
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.ResetsPriorNotifications)
                {
                    library.CaptureNormalLibraryChartSnapshot();
                    applied++;
                }
            };
            Task? producer = null;
            try
            {
                using (GetCatalogStorageRowsWriteGate(library).GetWriterGuard())
                {
                    producer = StartLongRunning(() => PublishNormalLibraryRefreshResetNotification(library,
                        notifiesBmsFiles: true, notifiesBmsonSongs: false));
                    producer.GetAwaiter().GetResult();
                    Assert.AreEqual(0, applied);
                    Assert.AreEqual(1, queue.Count);
                }
                queue.Dequeue()();
                Assert.AreEqual(1, applied);
                Assert.AreEqual(0, queue.Count);
            }
            finally
            {
                while (queue.Count > 0) { queue.Dequeue()(); }
                if (producer != null) { producer.GetAwaiter().GetResult(); }
                owner.StopAsync().GetAwaiter().GetResult();
                library.RequestShutdown("catalog_writer_delivery_test");
            }
        });
    }


    /// <summary>未実行通知を持つ停止は予約処理の収束を待ち、実行された旧通知は世代も公開回数も変えず、停止後の状態更新を検出します。</summary>
    [TestMethod]
    public void StopAsync_QueuedNormalLibraryRefreshBecomesNoOp()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\queued-stop.bms")];
            var pendingActions = new Queue<Action>();
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(pendingActions.Enqueue));
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, _) => appliedCount++;

            owner.AttachNormalLibraryRefreshSource(library);
            Assert.AreEqual(1, pendingActions.Count);

            Task stopTask = owner.StopAsync();
            Assert.IsFalse(stopTask.IsCompleted);
            pendingActions.Dequeue()();
            stopTask.GetAwaiter().GetResult();

            Assert.AreEqual(0, appliedCount);
            Assert.AreEqual(0L, owner.SourceGeneration);
        });
    }


    /// <summary>受付済みでもexecution laneが取消した通知を停止が回収し、開始されない適用を無期限に待つ誤りを検出します。</summary>
    [TestMethod]
    public void StopAsync_AcceptedButAbortedNormalLibraryRefreshDoesNotHang()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [CreateTestableBmsFile("C:\\Charts\\aborted-refresh.bms")]
            };
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new AbortingUiScheduler());

            owner.AttachNormalLibraryRefreshSource(library);

            owner.StopAsync().Wait();
        });
    }


    /// <summary>通知subscriberの例外後にも次の実保存行置換を一回適用し、失敗した版で受付を閉じたままにする誤りを検出します。</summary>
    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_ApplyFailureDoesNotBlockLaterVersion()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            EventHandler<NormalLibraryRefreshAppliedEventArgs> failingHandler =
                (_, _) => throw new InvalidOperationException("injected refresh apply failure");
            owner.NormalLibraryRefreshApplied += failingHandler;

            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\failed-refresh.bms")];
            owner.NormalLibraryRefreshApplied -= failingHandler;

            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.ResetsPriorNotifications)
                {
                    appliedCount++;
                }
            };
            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\recovered-refresh.bms")];

            Assert.AreEqual(1, appliedCount);
        });
    }
}
