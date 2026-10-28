using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Tests.Helpers;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>実譜面・SQLite・保守確定・単一配送から実 playlist source/view までを検証します。候補条件の網羅は model 系列へ分担します。</summary>
[TestClass]
public sealed class PlaylistCatalogViewChangeTests
{
    public TestContext? TestContext { get; set; }

    /// <summary>確定差分の適用後も実配送Taskが未終端ならdraft/Pを保持し、配送失敗時も正本値の表示と編集解除を終えて元失敗を返します。次の明示編集で再受付できます。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DetailEdit_WaitsForRequiredDeliveryAndCleansUpOnFailure(bool failPresentation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool gate = false;
        var failure = new InvalidOperationException("required entry presentation failed");
        await using var fixture = new Fixture(16, entryDeliveryScheduler: async action =>
        {
            action();
            if (!gate) { return; }
            entered.TrySetResult();
            await release.Task;
            if (failPresentation) { throw failure; }
        });
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var row = (PlaylistDetailRow)rows[0];
        var context = new MainChartListCellEditContext(row, nameof(PlaylistDetailRow.Level), ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(context);
        row.Level = "12.5";
        gate = true;
        Task edit = fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "12.5", true));
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, edit, "entry-required-delivery");
            Assert.IsFalse(edit.IsCompleted);
            Assert.IsTrue(fixture.Workspace.DetailViewState.Source.IsPlaylistCellEditing);
            Assert.AreEqual(12.5, row.Entry.level);
            Assert.AreEqual("12.5", row.Level, "必要配送の実終端まではdraftを閉じません。");
            Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out IDisposable rejected));
            rejected?.Dispose();
        }
        finally { gate = false; release.TrySetResult(); }
        if (failPresentation) { Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => edit)); }
        else { await edit; }
        Assert.AreEqual("12.5", row.Level);
        Assert.IsFalse(fixture.Workspace.DetailViewState.Source.IsPlaylistCellEditing);
        await fixture.Workspace.WaitForDetailBuildIdleAsync();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out IDisposable next)); next.Dispose();
        fixture.Workspace.BeginDetailEdit(context); row.Level = "12.5";
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "12.5", true));
        Assert.AreEqual("12.5", row.Level, "同値の明示保存へ以前の表示失敗を持ち越しません。");
        fixture.Workspace.BeginDetailEdit(context); row.Level = "13";
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "13", true));
        Assert.AreEqual("13", row.Level);
    }

    /// <summary>譜面情報の新SHA参照捕捉中に別版を確定して旧entry投影を保留し、その間の明示編集を局所確定します。後着参照で確定memoを旧値へ戻さず、譜面情報と参照値も回収します。</summary>
    [TestMethod]
    public async Task DetailEdit_DelayedReferenceProjectionKeepsCommittedEntryValues()
    {
        await using var fixture = new Fixture(16, prepareTable: (_, entries) => entries[0].sha256 = null);
        ChartFile chart = fixture.Library.BmsCharts[0];
        fixture.Library.BmsCharts = fixture.Library.BmsCharts.Skip(1).ToList();
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[15];
        Assert.AreEqual("entry 0", target.Entry.title);
        Assert.IsNull(target.ChartNotes);
        bool changedReference = false;
        fixture.PauseDelivery = true;
        fixture.Workspace.DetailWorkObserver = kind =>
        {
            if (kind != "playlist_source_projection" || changedReference) { return; }
            changedReference = true;
            using LibraryFileMutationLease lease = fixture.Playlist.AcquirePlaylistMutationLease("reference_during_detail_projection");
            using LibraryFileMutationCapability capability = lease.CreateMutationCapability();
            fixture.Playlist.CommitPlaylistHeaderEdits(new Dictionary<BMSTable, PlaylistHeaderEdit>
            { [target.Entry.parent] = new() { Fields = PlaylistHeaderFields.Symbol, Symbol = "AFTER" } }, capability);
        };
        fixture.Library.BuildAndPersistInlineChartInfoForInstalledCharts("pending_reference_detail", [chart]);
        while (!changedReference && fixture.DeliverOne()) { }
        Assert.IsTrue(changedReference);
        Assert.IsNull(target.ChartNotes, "新SHA参照の捕捉版へ未到達の投影はまだ公開しません。");
        var context = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.memo), ChartOperationSourceScope.PlaylistMissing, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(context); target.memo = "saved after reference capture";
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "saved after reference capture", true));
        Assert.AreEqual("saved after reference capture", target.memo);
        Assert.IsFalse(fixture.Workspace.DetailViewState.Source.IsPlaylistCellEditing);
        Assert.IsNull(target.ChartNotes);
        fixture.Deliver();
        Assert.AreEqual("saved after reference capture", target.memo);
        Assert.AreEqual(1, target.ChartNotes);
        Assert.AreEqual("AFTER", target.RefTablesSymbols);
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
    }

    /// <summary>別の明示sortを採用準備で保持しても、現在行への編集は必要な局所反映・cleanupで終端します。後発候補を解放した後も確定memoを保ち、候補採用待ちへの完了引継ぎを検出します。</summary>
    [TestMethod]
    public async Task DetailEdit_IndependentSortDoesNotDelayCellCompletion()
    {
        await using var fixture = new Fixture(16);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var row = (PlaylistDetailRow)rows[0];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler block = (_, _) =>
        { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        fixture.MainTable.RowsReplacing += block;
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.Title), ListSortDirection.Descending);
        Task sort = fixture.Refresh();
        Task? edit = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, sort, "independent-sort-presentation");
            var context = new MainChartListCellEditContext(row, nameof(PlaylistDetailRow.memo), ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            fixture.Workspace.BeginDetailEdit(context); row.memo = "saved independently";
            edit = fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "saved independently", true));
            await edit;
            Assert.IsFalse(sort.IsCompleted);
            Assert.AreEqual("saved independently", row.memo);
            Assert.IsFalse(fixture.Workspace.DetailViewState.Source.IsPlaylistCellEditing);
            Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out IDisposable next)); next.Dispose();
        }
        finally
        {
            release.TrySetResult();
            if (edit != null) { await edit; }
            await sort;
            fixture.MainTable.RowsReplacing -= block;
        }
        PlaylistDetailSourceRow current = fixture.Workspace.DetailViewState.Source.Rows.Single(value => ReferenceEquals(value.Entry, row.Entry));
        Assert.AreEqual("saved independently", current.memo);
    }

    /// <summary>保存後のentry配送を保留して別表を実表示し、旧編集の配送・cleanup後も新表示の行と値を保持します。旧対象への表示適用を新表示へ読み替える誤りを検出します。</summary>
    [TestMethod]
    public async Task DetailEdit_ReplacedDisplayKeepsNewTableRows()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        await using var fixture = new Fixture(3, entryDeliveryScheduler: async action =>
        {
            // 旧entry配送だけを保留し、同じUI schedulerを使う別表の採用は通します。
            if (hold) { hold = false; entered.TrySetResult(); await release.Task; }
            action();
        });
        var other = new BMSTable
        {
            playlist_id = 4190,
            name = "Other",
            is_external_sync = false,
            entries = [new BMSTableEntry { md5 = new string('e', 32), title = "Other row", memo = "new display" }]
        };
        fixture.Playlist.BMSTables.Add(other); fixture.Playlist.CommitBMSTableWithEntriesToDB(other);
        await fixture.Refresh();
        PlaylistDetailRow old = fixture.Workspace.DetailViewState.View.Rows[0] as PlaylistDetailRow ?? throw new AssertFailedException();
        var context = new MainChartListCellEditContext(old, nameof(PlaylistDetailRow.memo), ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(context); old.memo = "old target saved"; hold = true;
        Task edit = fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "old target saved", true));
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, edit, "old-view-entry-delivery");
            fixture.Workspace.RequestDetailSelection(other);
            await fixture.Refresh();
            IList rows = fixture.Workspace.DetailViewState.View.Rows;
            PlaylistDetailRow current = rows[0] as PlaylistDetailRow ?? throw new AssertFailedException();
            Assert.AreSame(other.entries[0], current.Entry);
            hold = false; release.TrySetResult(); await edit;
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
            Assert.AreSame(current, rows[0]);
            Assert.AreEqual("new display", current.memo);
            Assert.AreEqual("old target saved", old.Entry.memo);
            Assert.IsFalse(fixture.Workspace.DetailViewState.Source.IsPlaylistCellEditing);
        }
        finally { hold = false; release.TrySetResult(); await edit; }
    }

    /// <summary>別表Bへの実workspace追加・property保存・削除と参照選択を同drainでAへ反映します。背景E/Hだけを増やし、必須出力以外の参照・投影・順序仕事が増えないことを確認します。</summary>
    [TestMethod]
    public async Task OtherTableEntryAndHeaderCommits_UpdateReferenceSearchAndSortLocally()
    {
        int[] small = await MeasureOtherTableReferenceFlow(16);
        int[] large = await MeasureOtherTableReferenceFlow(128);
        CollectionAssert.AreEqual(small, large, "変更数・表示依存が同じ実add/deleteではE/Hを増やしてもSQL・参照・投影の仕事量を増やしません。");
    }

    private async Task<int[]> MeasureOtherTableReferenceFlow(int background)
    {
        using var sql = new SqliteStatementObservation();
        await using var fixture = new Fixture(background, openEntryWriteConnection: sql.OpenSongDb);
        var reference = new BMSTable
        {
            playlist_id = 4189,
            name = "Reference before",
            symbol = "REF",
            is_external_sync = false,
            Folder_order = ["background", "added"],
            entries = [.. Enumerable.Range(0, background).Select(index => new BMSTableEntry
            { md5 = (index + 500).ToString("x32"), title = "unrelated " + index, folder = "background" })]
        };
        fixture.Playlist.BMSTables.Add(reference);
        fixture.Playlist.CommitBMSTableWithEntriesToDB(reference);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        var retained = (PlaylistDetailRow)rows[1];
        fixture.MainTable.SelectedIndex = 1;
        var modelWork = new List<string>(); var viewWork = new List<string>();
        fixture.Library.PlaylistReferenceWorkObserver = modelWork.Add;
        fixture.Workspace.DetailWorkObserver = viewWork.Add;
        int sqlStart = sql.Statements.Count;
        await fixture.Workspace.AddRowsToFolderAsync([target], reference, PlaylistFolderNode.CreateFolder("added"));
        fixture.Deliver();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        // 正規登録済みのprimary表A（symbol空）も同じMD5 bucketに属し、空要素を含む既存joinを保つ。
        Assert.AreEqual(" REF", target.RefTablesSymbols);
        Assert.IsTrue(target.RefTablesNames.Contains("Reference before", StringComparison.Ordinal));
        Assert.AreSame(retained, rows[1]); Assert.AreEqual(1, fixture.MainTable.SelectedIndex);
        Assert.AreEqual(0, modelWork.Count(kind => kind is "reference_entry_captured" or "reference_entry_indexed"));
        Assert.AreEqual(1, modelWork.Count(kind => kind == "reference_bucket_update"));
        Assert.AreEqual(1, viewWork.Count(kind => kind == "playlist_reference_projection"));
        Assert.AreEqual(0, viewWork.Count(kind => kind is "playlist_dependency_index_entry" or "playlist_order_buffer"));
        Assert.AreEqual(2, rows.RealizedRowCount);
        SqliteStatementObservation.SqliteObservedStatement[] addSql = sql.Statements.Skip(sqlStart).Where(statement => statement.Sql.Contains("playlist_entry", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.IsTrue(addSql.Any(statement => statement.Sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(0, addSql.Sum(statement => statement.FullScanSteps));
        var workCounts = new List<int> { addSql.Length, addSql.Sum(statement => statement.RowCount), addSql.Sum(statement => statement.VmSteps),
            modelWork.Count, viewWork.Count(kind => kind is "playlist_reference_projection" or "playlist_order_write" or "playlist_detail_materialized"), rows.RealizedRowCount };
        Assert.IsTrue(viewWork.Count(kind => kind == "playlist_order_access") <= 4 * Math.Ceiling(Math.Log2(background)) + 4);

        var service = new PlaylistPropertySaveService(() => fixture.Playlist, () => fixture.Library, () => null!,
            () => new CustomFolderOutputSettingsSnapshot());
        service.PlaylistPropertySummaryDataRefreshRequested += (_, _) => { };
        var draft = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.memo),
            ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(draft); target.memo = "unsaved reference draft";
        using (PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(reference)
            ?? throw new AssertFailedException("Property session was not created."))
        {
            session.Values.Name = "Reference after"; session.Values.Symbol = "NEW";
            PlaylistPropertySaveCommit commit = await service.TrySaveAsync(session, session.Values)
                ?? throw new AssertFailedException("Property save was not accepted.");
            Assert.IsNotNull(commit); Assert.IsNull(commit.Exception);
        }
        fixture.Deliver();
        Assert.AreEqual(" NEW", target.RefTablesSymbols);
        Assert.IsTrue(target.RefTablesNames.Contains("Reference after", StringComparison.Ordinal));
        Assert.AreEqual("unsaved reference draft", target.memo);
        Assert.AreEqual("entry memo", target.Entry.memo);
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(draft, "unsaved reference draft", commit: false));
        fixture.Workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("NEW", ChartModeFilter.All));
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.RefTablesSymbols), ListSortDirection.Ascending);
        await fixture.Refresh();
        Assert.AreEqual(1, fixture.Workspace.DetailViewState.View.Rows.Count);
        fixture.Library.SynchronizeReferenceBMSTables([target.Entry.parent]); fixture.Deliver();
        Assert.AreEqual(0, fixture.Workspace.DetailViewState.View.Rows.Count);
        fixture.Library.SynchronizeReferenceBMSTables([target.Entry.parent, reference]); fixture.Deliver();
        Assert.AreEqual(1, fixture.Workspace.DetailViewState.View.Rows.Count);
        BMSTableEntry added = reference.entries.Single(entry => entry.folder == "added");
        modelWork.Clear(); viewWork.Clear(); sqlStart = sql.Statements.Count;
        await fixture.Workspace.DeleteSelectedEntriesAsync([new PlaylistDetailSourceRow(added, target.Chart).CreateViewRow()]);
        fixture.Deliver();
        Assert.AreEqual(0, fixture.Workspace.DetailViewState.View.Rows.Count);
        Assert.AreEqual("[0" + Resources.Num_songs + "]", fixture.MainTable.SummaryText);
        Assert.AreEqual(background, target.Entry.parent.entries.Count);
        Assert.AreEqual("entry memo", target.Entry.memo);
        SqliteStatementObservation.SqliteObservedStatement[] deleteSql = sql.Statements.Skip(sqlStart).Where(statement => statement.Sql.Contains("playlist_entry", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.IsTrue(deleteSql.Any(statement => statement.Sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(0, deleteSql.Sum(statement => statement.FullScanSteps));
        workCounts.AddRange([deleteSql.Length, deleteSql.Sum(statement => statement.RowCount), deleteSql.Sum(statement => statement.VmSteps), modelWork.Count,
            viewWork.Count(kind => kind is "playlist_reference_projection" or "playlist_order_write" or "playlist_detail_materialized")]);
        sql.ThrowIfCallbackFailed();
        return [.. workCounts];
    }

    /// <summary>初回参照捕捉後・依存索引構築前の更新を実投影gateで配送し、採用時に同じ差分を反映します。採用前中止では候補を公開しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReferenceCommit_DuringInitialProjectionIsBufferedBeforeDependencyRegistration(bool cancel)
    {
        await using var fixture = new Fixture(16);
        BMSTable table = fixture.Playlist.BMSTables[0];
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int visits = 0;
        fixture.Workspace.DetailWorkObserver = kind =>
        {
            if (kind != "playlist_source_projection" || Interlocked.Increment(ref visits) != 1) { return; }
            captured.TrySetResult(); release.Task.GetAwaiter().GetResult();
        };
        Task refresh = fixture.Refresh();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(captured.Task, refresh, "first playlist projection");
            using LibraryFileMutationLease lease = fixture.Playlist.AcquirePlaylistMutationLease("reference_during_projection");
            using LibraryFileMutationCapability capability = lease.CreateMutationCapability();
            fixture.Playlist.CommitPlaylistHeaderEdits(new Dictionary<BMSTable, PlaylistHeaderEdit>
            { [table] = new() { Fields = PlaylistHeaderFields.Name | PlaylistHeaderFields.Symbol, Name = "Updated during build", Symbol = "DURING" } }, capability);
            fixture.Deliver();
            if (cancel) { fixture.Workspace.CancelDetailBuilds(); }
            release.TrySetResult(); await refresh;
            if (cancel)
            {
                Assert.AreEqual(0, fixture.Workspace.DetailViewState.Source.Rows.Count);
                Assert.AreEqual(0, fixture.Workspace.DetailViewState.View.Rows.Count);
                return;
            }
            Assert.AreEqual(16, fixture.Workspace.DetailViewState.Source.Rows.Count);
            Assert.IsTrue(fixture.Workspace.DetailViewState.Source.Rows.All(row => row.RefTablesSymbols == "DURING"));
            if (fixture.Workspace.DetailViewState.View.Rows[0] is not PlaylistDetailRow adopted)
            { throw new AssertFailedException("Playlist detail row was not adopted."); }
            Assert.AreEqual("DURING", adopted.RefTablesSymbols);
        }
        finally { release.TrySetResult(); await refresh; fixture.Workspace.DetailWorkObserver = null; }
    }

    /// <summary>worker準備後の未配送header確定をUI採用前に見つけ、古い候補を公開せず実drain後に参照値と適用版を揃えます。</summary>
    [TestMethod]
    public async Task ReferenceCommit_AfterWorkerPreparationWaitsForDeliveryBeforeUiAdoption()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int hold = 1;
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        await using var fixture = new Fixture(16, regularDelivery: true, entryDeliveryScheduler: async action =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            await dispatcher.InvokeAsync(action).Task;
        });
        await fixture.DrainLibraryChanges();
        Task refresh = fixture.Refresh();
        ScheduledScoreOperation? delivery = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, refresh, "reference-prepared-ui-candidate");
            BMSTable table = fixture.Playlist.BMSTables.Single();
            using (LibraryFileMutationLease lease = fixture.Playlist.AcquirePlaylistMutationLease("reference_after_worker_preparation"))
            using (LibraryFileMutationCapability capability = lease.CreateMutationCapability())
            {
                fixture.Playlist.CommitPlaylistHeaderEdits(new Dictionary<BMSTable, PlaylistHeaderEdit>
                { [table] = new() { Fields = PlaylistHeaderFields.Symbol, Symbol = "AFTER" } }, capability);
            }
            long required = fixture.Library.PlaylistReferenceRevision;
            release.TrySetResult();
            await TestUiDispatcherHost.AwaitNotificationAsync(fixture.DeliveryRequested.Task, refresh, "reference-prepared-candidate-delivery");
            delivery = await fixture.DeliveryScheduler.NextAsync();
            Assert.IsFalse(refresh.IsCompleted);
            Assert.AreEqual(0, fixture.MainTable.Rows.Count, "未配送の新参照版を持つ旧候補は採用しません。");
            delivery.Run(); delivery.Complete();
            await refresh;
            Assert.AreEqual(16, fixture.MainTable.Rows.Count);
            Assert.IsTrue(fixture.MainTable.Rows.Cast<PlaylistDetailRow>().All(row => row.RefTablesSymbols == "AFTER"));
            Assert.AreEqual(required, fixture.Workspace.DetailViewState.Source.Dependencies?.ReferenceInput?.Revision);
        }
        finally
        {
            release.TrySetResult();
            delivery?.Abort();
            while (fixture.DeliveryScheduler.TryTake(out ScheduledScoreOperation? queued)) { queued.Abort(); }
            fixture.Workspace.CancelDetailBuilds();
            await refresh;
        }
    }

    /// <summary>同条件要求でも未配送参照を待ち、配送callbackと実Completionを区別します。要求取消は共有drainを取り消しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReferenceCommit_SameViewWaitsForActualDeliveryAndCancellationKeepsSharedDrain(bool cancel)
    {
        await using var fixture = new Fixture(16, regularDelivery: true);
        await fixture.DrainLibraryChanges(); await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        BMSTable table = target.Entry.parent;
        using (LibraryFileMutationLease lease = fixture.Playlist.AcquirePlaylistMutationLease("reference_same_view"))
        using (LibraryFileMutationCapability capability = lease.CreateMutationCapability())
        {
            fixture.Playlist.CommitPlaylistHeaderEdits(new Dictionary<BMSTable, PlaylistHeaderEdit>
            { [table] = new() { Fields = PlaylistHeaderFields.Symbol, Symbol = "PENDING" } }, capability);
        }
        Task shared = fixture.GetLibraryDelivery();
        Task refresh = fixture.Refresh();
        ScheduledScoreOperation? operation = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(fixture.DeliveryRequested.Task, refresh, "reference delivery request");
            operation = await fixture.DeliveryScheduler.NextAsync();
            Assert.IsFalse(refresh.IsCompleted);
            operation.Run();
            Assert.AreEqual("PENDING", target.RefTablesSymbols);
            Assert.IsFalse(refresh.IsCompleted, "callbackだけでは生成元UI操作の終端ではありません。");
            if (cancel) { fixture.Workspace.CancelDetailBuilds(); await refresh; }
            Assert.IsFalse(shared.IsCompleted);
            operation.Complete(); await shared; await refresh;
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        }
        finally
        {
            operation?.Abort();
            while (fixture.DeliveryScheduler.TryTake(out ScheduledScoreOperation? queued)) { queued.Abort(); }
            fixture.Workspace.CancelDetailBuilds(); await refresh;
            try { await shared; } catch (TaskCanceledException) { }
        }
    }

    /// <summary>別表の実DB読込み後、参照入力の捕捉をgateで遅延させます。開いているAのentryを変えずにhydrationの公開だけでREFと検索を更新します。</summary>
    [TestMethod]
    public async Task OtherTableHydration_UpdatesExistingDetailAfterDelayedReferenceCapture()
    {
        await using var fixture = new Fixture(16);
        var reference = new BMSTable { playlist_id = 4189, name = "Late reference", symbol = "LATE", entries = [] };
        fixture.Playlist.BMSTables.Add(reference); fixture.Playlist.CommitBMSTableWithEntriesToDB(reference);
        BMSTableEntry entry = fixture.Playlist.BMSTables[0].entries[0];
        using (var db = new LR2SongDBExtended(fixture.SongDb))
        { db.Execute("INSERT INTO playlist_entry(playlist_id,md5,title,folder,is_removed) VALUES(?,?,?,?,?);", reference.playlist_id, entry.md5, "hydrated", "late", 0); }
        reference.MarkEntriesNotLoaded();
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        Assert.AreEqual(string.Empty, target.RefTablesSymbols);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Library.PlaylistReferenceWorkObserver = kind =>
        { if (kind == "reference_entry_captured") { captured.TrySetResult(); release.Task.GetAwaiter().GetResult(); } };
        var hydration = Task.Run(() => fixture.Playlist.EnsurePlaylistEntriesLoaded(reference, "delayed_reference_hydration"));
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(captured.Task, hydration, "hydrated reference capture");
            Assert.AreEqual(string.Empty, target.RefTablesSymbols);
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
            release.TrySetResult(); await hydration; fixture.Deliver();
            // primary表Aの空symbolと、hydrationしたBのsymbolを正本表順で結合する。
            Assert.AreEqual(" LATE", target.RefTablesSymbols);
            Assert.IsTrue(GridKeywordSearchQuery.Parse("LATE").MatchesPlaylistDetail(fixture.Workspace.DetailViewState.Source.Rows[0]));
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        }
        finally { release.TrySetResult(); await hydration; fixture.Library.PlaylistReferenceWorkObserver = null; }
    }

    /// <summary>新hashをRで一回捕捉して元entryを保留し、既存r→R、新依存挿入、後続R→Sの順で公開します。Rの値をSへ付け替えて後続変更を失う実装を検出します。</summary>
    [TestMethod]
    public async Task NewEntryReferenceCapture_InsertsDependencyAtExactBoundaryBeforeFollowingCommit()
    {
        await using var fixture = new Fixture(16);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        BMSTable table = fixture.Playlist.BMSTables[0];
        const string md5 = "ffffffffffffffffffffffffffffffff";
        fixture.PauseDelivery = true;
        var work = new List<string>(); fixture.Library.PlaylistReferenceWorkObserver = work.Add;
        Assert.IsTrue(fixture.Playlist.AddPlaylistEntriesToFolderBMSTable(
            [new BMSTableEntry { md5 = md5, title = "new reference dependency" }], table, string.Empty));
        Assert.AreEqual(16, rows.Count, "新keyの捕捉Rへ既存入力が届く前は元receiptを公開しません。");
        using (LibraryFileMutationLease lease = fixture.Playlist.AcquirePlaylistMutationLease("following_reference_commit"))
        using (LibraryFileMutationCapability capability = lease.CreateMutationCapability())
        {
            fixture.Playlist.CommitPlaylistHeaderEdits(new Dictionary<BMSTable, PlaylistHeaderEdit>
            { [table] = new() { Fields = PlaylistHeaderFields.Symbol, Symbol = "AFTER" } }, capability);
        }
        Assert.AreEqual(1, work.Count(kind => kind == "reference_lookup"), "新依存一keyだけを一回捕捉します。");
        fixture.Deliver();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(17, rows.Count);
        PlaylistDetailSourceRow added = fixture.Workspace.DetailViewState.Source.Rows.Single(row => row.hash == md5);
        Assert.AreEqual("AFTER", added.RefTablesSymbols);
        Assert.AreEqual("AFTER", rows.Cast<PlaylistDetailRow>().Single(row => row.hash == md5).RefTablesSymbols);
        Assert.AreEqual(1, work.Count(kind => kind == "reference_lookup"));
        Assert.AreEqual("[17" + Resources.Num_songs + "]", fixture.MainTable.SummaryText);
    }

    /// <summary>背景16/128件のwarm一件保守で未変更行・Rows・選択・未訪問行を保持し、旧入力は変えず対象値と専門警告を配送します。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    public async Task MaintenanceCommit_UpdatesOnlyDependentPlaylistInputs(int count, bool bmson)
    {
        await using var fixture = new Fixture(count, bmson);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var first = (PlaylistDetailRow)rows[0];
        var unrelated = (PlaylistDetailRow)rows[1];
        PlaylistDetailSourceRow old = fixture.Workspace.DetailViewState.Source.Rows[0];
        fixture.MainTable.SelectedIndex = 1;
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        MaintenanceWorkflowResult result = fixture.Library.RescanResourceHealthCharts([old.OwnedCurrent]);
        watch.Stop();
        Assert.IsTrue(result.HasUpdates);
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(first, rows[0]);
        Assert.AreSame(unrelated, rows[1]);
        Assert.AreEqual(1, fixture.MainTable.SelectedIndex);
        Assert.AreEqual(2, rows.RealizedRowCount);
        Assert.IsNull(old.WAVHealth);
        Assert.AreEqual(0, first.WAVHealth);
        Assert.IsFalse(first.HasHighlightedWarning, "資源欠損は通常一覧と同じく行全体を強調しません。");
        StringAssert.Contains(first.WarningTooltipText, string.Format(BeMusicSeeker.Properties.Resources.Warning_WavFilesNotFound, 0, 0, 1));
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.WarningDigestText));
        Assert.AreEqual(1, work.Count(value => value == "playlist_source_projection"));
        Assert.AreEqual(0, work.Count(value => value == "playlist_dependency_index_entry"),
            "初回構築の逆引きを採用し、warm更新で背景entryを再列挙しません。");
        Assert.AreEqual(1, work.Count(value => value == "playlist_dependency_index_write"));
        Assert.AreEqual(1, fixture.Workspace.DetailViewState.Source.Rows.Count(row => row.WAVHealth == 0));
        Assert.IsNull(((PlaylistDetailRow)rows[count - 1]).WAVHealth);
        TestContext?.WriteLine("playlist count={0} bmson={1} warm_commit_delivery_ms={2:F3}", count, bmson, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>入力捕捉後の実保守を配送前で止め、旧入力へ新版を付けず、配送後に同じ候補へ局所差分だけを反映します。</summary>
    /// <remarks>Windowを作らず共有Dispatcherへ採用を配送し、RowsReplacingのUI所属と、現役sourceへの合法なUI配送を候補のworker追随から分けます。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BuildingCandidate_WaitsForCommitDeliveryAndResumesWithoutRebuild(bool deliverBeforeAdoption)
    {
        System.Windows.Threading.Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        await using var fixture = new Fixture(16, entryDeliveryScheduler: action => dispatcher.InvokeAsync(action).Task);
        await fixture.Refresh();
        var oldRows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var oldFirst = (PlaylistDetailRow)oldRows[0];
        fixture.PauseDelivery = true;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new List<string>();
        bool changed = false;
        EventHandler callback = (_, _) =>
        {
            Assert.IsTrue(dispatcher.CheckAccess(), "採用前callbackは共有UI dispatcher上で実行します。");
            if (changed) { return; }
            changed = true;
            ChartFile target = fixture.Workspace.DetailViewState.Source.Rows[0].OwnedCurrent;
            fixture.WriteFirst("Z changed");
            // 資源保守は同一encodingのメタデータ再読解を要求しません。題名を変える正規入口を使います。
            fixture.Library.SetBMSFilesEncoding([target], "utf-8");
            Assert.AreEqual("Z changed", fixture.Library.CapturePlaylistCatalogReadSnapshot(
                [new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, target.Md5)], default)
                .Resolve(new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, target.Md5)).Title,
                "正規保守の確定値と、未配送候補への反映を分けて検査します。");
            work.Clear();
            if (deliverBeforeAdoption) { fixture.Deliver(); }
            reached.TrySetResult();
        };
        fixture.MainTable.RowsReplacing += callback;
        fixture.Workspace.DetailWorkObserver = work.Add;
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.Title), ListSortDirection.Descending);
        Task? refresh = null;
        try
        {
            refresh = fixture.Refresh();
            await Task.WhenAny(reached.Task, refresh);
            if (!reached.Task.IsCompleted) { await refresh; Assert.Fail("実候補の採用境界へ到達しませんでした。"); }
            if (!deliverBeforeAdoption)
            {
                Assert.IsFalse(refresh.IsCompleted);
                Assert.AreSame(oldRows, fixture.Workspace.DetailViewState.View.Rows);
                Assert.AreEqual("A target", oldFirst.Title);
                TestUiDispatcherHost.Invoke(fixture.Deliver);
            }
            await refresh;
            if (fixture.Workspace.DetailViewState.View.Rows[0] is not PlaylistDetailRow adopted)
            { throw new AssertFailedException("Playlist detail row was not adopted."); }
            Assert.AreEqual("Z changed", adopted.Title);
            Assert.IsTrue(work.Count(value => value == "playlist_source_projection") <= 2,
                "既存表示と構築候補の依存一行ずつだけを再投影します。");
        }
        finally
        {
            fixture.MainTable.RowsReplacing -= callback;
            TestUiDispatcherHost.Invoke(fixture.Deliver);
            if (refresh != null) { await refresh; }
        }
    }

    /// <summary>文字列更新で依存filterとsortだけを再評価し、非表示のentryも検索へ入れます。表固有level・memo・DBは保持します。</summary>
    [TestMethod]
    public async Task MetadataCommit_ReevaluatesHiddenDependenciesAndKeepsEntryValues()
    {
        await using var fixture = new Fixture(3);
        fixture.Workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("changed", ChartModeFilter.All));
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        Assert.AreEqual(0, rows.Count);
        PlaylistDetailSourceRow target = fixture.Workspace.DetailViewState.Source.Rows[0];
        fixture.WriteFirst("Z changed");
        fixture.Library.SetBMSFilesEncoding([target.OwnedCurrent], "utf-8");
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(1, rows.Count);
        var row = (PlaylistDetailRow)rows[0];
        Assert.AreEqual("Z changed", row.Title);
        Assert.AreEqual("fixture artist", row.Artist);
        Assert.AreEqual("2.5", row.Level);
        Assert.AreEqual("entry memo", row.memo);
        Assert.AreSame(target.OwnedCurrent.Token, row.Chart.Token);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual("entry memo", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE title = 'entry 0';"));
        Assert.AreEqual(2.5, db.ExecuteScalar<double>("SELECT level FROM playlist_entry WHERE title = 'entry 0';"));
    }

    /// <summary>TITLE正規表現の実表示後にMEMOをDB確定しても集合を変えず、実題名確定では同じ要求式から離脱・再加入します。MEMOだけ一致する非表示行の混入と局所更新時の式変質を検出します。</summary>
    [DataTestMethod]
    [DataRow(@"title:re:^\d$", "1", "A")]
    [DataRow(@"title:re:^\D$", "A", "1")]
    public async Task TitleRegex_EntryMemoCommitsPreserveMembershipAndMetadataChangesReevaluate(
        string expression, string matchingTitle, string otherTitle)
    {
        await using var fixture = new Fixture(2, prepareTable: (_, entries) => entries[1].memo = otherTitle);
        fixture.WriteFirst(matchingTitle);
        fixture.Library.SetBMSFilesEncoding([fixture.Library.BmsCharts[0]], "utf-8");
        fixture.Workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot(expression, ChartModeFilter.All));
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        Assert.AreEqual(1, rows.Count);
        var target = (PlaylistDetailRow)rows[0];
        Assert.AreEqual(matchingTitle, target.Title);
        if (fixture.Workspace.DetailViewState.View.CurrentIdentity is not PlaylistRequestIdentity initialIdentity)
        { throw new AssertFailedException("初回表示の検索要求が採用されていません。"); }
        PlaylistPresentationIdentity presentation = initialIdentity.PresentationIdentity;
        var context = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.memo),
            ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(context);
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "changed memo", commit: true));
        BMSTableEntry hidden = fixture.Playlist.BMSTables.Single().entries[1];
        fixture.Playlist.CommitBMSTableEntry(hidden, nameof(PlaylistDetailRow.memo), matchingTitle, null);
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(1, rows.Count);
        Assert.AreSame(target, rows[0]);
        Assert.AreEqual("changed memo", target.memo);
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly())
        {
            Assert.AreEqual("changed memo", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE title = 'entry 0';"));
            Assert.AreEqual(matchingTitle, db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE title = 'entry 1';"));
        }
        fixture.WriteFirst(otherTitle);
        fixture.Library.SetBMSFilesEncoding([fixture.Library.BmsCharts[0]], "utf-8");
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(0, rows.Count);
        fixture.WriteFirst(matchingTitle);
        fixture.Library.SetBMSFilesEncoding([fixture.Library.BmsCharts[0]], "utf-8");
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(1, rows.Count);
        Assert.AreSame(target.Entry, ((PlaylistDetailRow)rows[0]).Entry);
        Assert.AreEqual(matchingTitle, ((PlaylistDetailRow)rows[0]).Title);
        Assert.AreEqual(presentation, fixture.Workspace.DetailViewState.View.CurrentIdentity?.PresentationIdentity);
    }

    /// <summary>背景件数・実体化済み件数を増やしても、一件の題名移動は順序木の探索経路だけを訪問します。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public async Task MetadataSortMove_PreservesRealizedEntriesWithLogarithmicOrderWork(int count)
    {
        await using var fixture = new Fixture(count);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        PlaylistDetailRow[] realized = rows.Cast<PlaylistDetailRow>().ToArray();
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        int publications = 0;
        realized[0].PropertyChanged += (_, _) =>
        {
            Assert.IsFalse(System.Threading.Monitor.IsEntered(fixture.Workspace.DetailViewState.SyncRoot));
            Assert.IsFalse(System.Threading.Monitor.IsEntered(fixture.Workspace.DetailBuildState.SyncRoot));
            Assert.AreEqual("Z changed", realized[0].Title);
            publications++;
        };
        rows.CollectionChanged += (_, _) =>
        {
            Assert.IsFalse(System.Threading.Monitor.IsEntered(fixture.Workspace.DetailViewState.SyncRoot));
            Assert.IsFalse(System.Threading.Monitor.IsEntered(fixture.Workspace.DetailBuildState.SyncRoot));
            Assert.AreSame(realized[0], rows[count - 1]);
        };
        fixture.WriteFirst("Z changed");
        fixture.Library.SetBMSFilesEncoding([fixture.Workspace.DetailViewState.Source.Rows[0].OwnedCurrent], "utf-8");
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(realized[0], rows[count - 1]);
        Assert.AreEqual(count, rows.RealizedRowCount);
        Assert.IsTrue(publications > 0);
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
        Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 6 * Math.Ceiling(Math.Log2(count)) + 6);
        Assert.IsTrue(work.Count(kind => kind == "playlist_order_write") <= 3);
        Assert.IsTrue(work.Count(kind => kind == "playlist_notification_buffer") <= 3);
        for (int index = 1; index < count; index++) { Assert.AreSame(realized[index], rows[index - 1]); }
    }

    /// <summary>実score ownerのA/B変更で対象行を更新し、memo draftと旧順序を保ってCompleteDetailEditのfinally後だけB/U/Aへ再配置します。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public async Task ScoreChanges_PatchDependentEntriesAndDeferPositionUntilDraftEnds(int count)
    {
        await using var fixture = new Fixture(count, regularDelivery: true, oracleScores: true);
        await fixture.DrainLibraryChanges();
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.stddevVal), ListSortDirection.Ascending);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        var other = (PlaylistDetailRow)rows[1];
        var context = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.memo),
            ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(context);
        target.memo = "unsaved draft";
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        long revision = fixture.Library.CatalogRevision;
        fixture.ChangeScore(0, 40);
        fixture.ChangeScore(1, 5);
        await fixture.DrainLibraryChanges();
        Assert.AreEqual(revision, fixture.Library.CatalogRevision);
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(target, rows[0]);
        Assert.AreEqual(40d, target.stddevVal);
        Assert.AreEqual("unsaved draft", target.memo);
        Assert.AreEqual("entry memo", target.Entry.memo);
        Assert.AreEqual(2, work.Count(kind => kind == "playlist_source_projection"));
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_dependency_index_entry"));
        Assert.AreEqual(2, rows.RealizedRowCount);
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "unsaved draft", commit: false));
        Assert.AreSame(target, rows[2]);
        Assert.AreSame(other, rows[1]);
        Assert.AreEqual("entry 1", ((PlaylistDetailRow)rows[0]).Entry.title);
        Assert.AreEqual(5, ((PlaylistDetailRow)rows[0]).score);
        Assert.AreEqual("entry memo", target.memo);
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly())
        { Assert.AreEqual("entry memo", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE title = 'entry 0';")); }
        Assert.AreEqual(40d, target.stddevVal);
        Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 12 * Math.Ceiling(Math.Log2(count)) + 12);
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
    }

    /// <summary>正常なscore再読込みで記録が消えた所持/未所持行は全成績を消去し、draftと位置を編集終端まで保持します。旧Chart.Scoreの再利用を検出します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ScoreReload_MissingRecordClearsScoresAndDefersOrderUntilDraftEnds(bool owned)
    {
        await using var fixture = new Fixture(16, regularDelivery: true, oracleScores: true);
        if (!owned) { fixture.Library.BmsCharts = fixture.Library.BmsCharts.Where(chart => chart.Md5 != fixture.OracleScores[1].hash).ToList(); }
        BMSScore previous = fixture.OracleScores[1];
        previous.clear = ClearType.HARD; previous.rank = RankType.AA; previous.rate = 15;
        previous.minbp = 7; previous.maxcombo = 24;
        previous.ranking = 2; previous.rankingNum = 10;
        previous.rankingLastupdate = new DateTime(2026, 1, 2);
        previous.scoreDifficulty = 12.5;
        await fixture.DrainLibraryChanges();
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.score), ListSortDirection.Descending);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        var other = (PlaylistDetailRow)rows[1];
        Assert.AreEqual("entry 1", target.Entry.title);
        Assert.AreEqual(owned, target.IsOwned);
        Assert.AreEqual(ClearType.HARD, target.clear);
        Assert.AreEqual(RankType.AA, target.rank);
        Assert.AreEqual(30, target.score); Assert.AreEqual(15d, target.rate); Assert.AreEqual(.15, target.rateDouble);
        Assert.AreEqual(100, target.totalnotes); Assert.AreEqual(7, target.minbp); Assert.AreEqual(24, target.maxcombo);
        Assert.AreEqual(" 2/10", target.rankingString); Assert.AreEqual(previous.rankingLastupdate, target.rankingLastupdate);
        Assert.AreEqual(30d, target.stddevVal); Assert.AreEqual(12.5, target.scoreDifficulty);
        var context = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.memo),
            owned ? ChartOperationSourceScope.PlaylistOwned : ChartOperationSourceScope.PlaylistMissing, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(context);
        target.memo = "unsaved draft";
        var work = new List<string>(); fixture.Workspace.DetailWorkObserver = work.Add;
        long revision = fixture.Library.CatalogRevision;
        fixture.RemoveScoreAndReload(1);
        await fixture.DrainLibraryChanges();
        Assert.IsTrue(fixture.Library.ScoreSnapshotReady);
        Assert.AreEqual(revision, fixture.Library.CatalogRevision);
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(target, rows[0]); Assert.AreSame(other, rows[1]);
        Assert.AreEqual("unsaved draft", target.memo); Assert.AreEqual("entry memo", target.Entry.memo);
        Assert.AreEqual(owned ? ClearType.NO_PLAY : ClearType.NO_SONG, target.clear);
        Assert.AreEqual(RankType.INVALID, target.rank);
        Assert.IsNull(target.score); Assert.IsNull(target.rate); Assert.IsNull(target.rateDouble);
        Assert.IsNull(target.totalnotes); Assert.IsNull(target.minbp); Assert.IsNull(target.maxcombo);
        Assert.AreEqual(string.Empty, target.rankingString); Assert.IsNull(target.rankingLastupdate);
        Assert.IsNull(target.stddevVal); Assert.IsNull(target.scoreDifficulty);
        Assert.IsNull(target.Chart.Score.Ranking); Assert.IsNull(target.Chart.Score.RankingNum);
        Assert.IsNull(target.Chart.Score.Score);
        Assert.AreEqual(0, work.Count(kind => kind is "playlist_dependency_index_entry" or "playlist_order_buffer"));
        Assert.AreEqual(2, rows.RealizedRowCount);
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "unsaved draft", commit: false));
        Assert.AreSame(other, rows[0]); Assert.AreSame(target, rows[rows.Count - 1]);
        Assert.AreEqual("entry memo", target.memo);
        Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
    }

    /// <summary>実DBの記録消去をscore-only通知から適用し、所持/未所持の未プレイ検索加入と旧score検索離脱を確認します。非表示sourceの旧成績保持も検出します。</summary>
    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task ScoreReload_MissingRecordUpdatesFilterMembership(bool owned, bool includeMissing)
    {
        await using var fixture = new Fixture(3, regularDelivery: true, oracleScores: true);
        if (!owned) { fixture.Library.BmsCharts = fixture.Library.BmsCharts.Where(chart => chart.Md5 != fixture.OracleScores[1].hash).ToList(); }
        fixture.OracleScores[1].clear = ClearType.HARD;
        await fixture.DrainLibraryChanges();
        fixture.Workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot(
            includeMissing ? owned ? "clear:NP" : "clear:nosong" : "score:>=20", ChartModeFilter.All));
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        Assert.AreEqual(!includeMissing, rows.Cast<PlaylistDetailRow>().Any(row => row.Entry.title == "entry 1"));
        int beforeCount = rows.Count;
        fixture.RemoveScoreAndReload(1);
        await fixture.DrainLibraryChanges();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(includeMissing, rows.Cast<PlaylistDetailRow>().Any(row => row.Entry.title == "entry 1"));
        Assert.AreEqual(beforeCount + (includeMissing ? 1 : -1), rows.Count);
    }

    /// <summary>非所持entryの詳細独立通知を実解析・DB確定から配送し、全source再構築なしでノーツ数一件だけを反映します。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public async Task ChartInfoChange_PatchesOnlyUnownedDependentEntry(int count)
    {
        await using var fixture = new Fixture(count);
        ChartFile file = fixture.Library.BmsCharts[0];
        fixture.Library.BmsCharts = fixture.Library.BmsCharts.Skip(1).ToList();
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        // fixtureのTitle順はB background群の後に未所持entry 0が一件だけ続きます。
        var target = (PlaylistDetailRow)rows[count - 1];
        Assert.AreEqual("entry 0", target.Entry.title);
        Assert.IsFalse(target.IsOwned);
        Assert.AreEqual(1, fixture.Workspace.DetailViewState.Source.Rows.Count(row =>
            StringComparer.OrdinalIgnoreCase.Equals(row.hash, file.Md5)
            || StringComparer.OrdinalIgnoreCase.Equals(row.sha256, file.Sha256)),
            "背景entryは異なるhashで、今回の詳細キーに依存するsourceは未所持の一行だけです。");
        var other = (PlaylistDetailRow)rows[0];
        Assert.IsNull(target.ChartNotes);
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        long revision = fixture.Library.CatalogRevision;
        // 成功時の詳細indexとparse-failure解除の公開を、実UIの予約→drainと同じ一batchで受け取ります。
        fixture.BatchNotificationsOnDelivery = true;
        ChartInfoInlineBuildResult built = fixture.Library.BuildAndPersistInlineChartInfoForInstalledCharts("playlist_detail_test", [file]);
        Assert.AreEqual(1, built.SuccessCount);
        Assert.AreEqual(0, built.ParseFailedCount);
        fixture.Deliver();
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly())
        { Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT notes FROM chart_info WHERE sha256 = ?;", file.Sha256)); }
        Assert.AreEqual(revision, fixture.Library.CatalogRevision);
        Assert.AreEqual(1, target.ChartNotes);
        Assert.AreEqual("2.5", target.Level);
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(other, rows[0]);
        Assert.AreEqual(1, work.Count(kind => kind == "playlist_source_projection"));
        Assert.AreEqual(1, work.Count(kind => kind == "playlist_dependency_index_write"));
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_dependency_index_entry"));
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
        Assert.AreEqual(2, rows.RealizedRowCount, "対象と既訪問の背景一行だけを保持し、未訪問行を実体化しません。");
    }

    /// <summary>workerが登録した詳細投影のUI採用を保留し、通常配送が先行消費した後の再開で再適用・追加通知・背景実体化を行いません。</summary>
    [TestMethod]
    public async Task ChartInfoPatch_NormalDeliveryConsumesRegisteredProjectionBeforeUiApply()
    {
        bool hold = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(16, entryDeliveryScheduler: async action =>
        {
            if (hold) { entered.TrySetResult(); await release.Task; }
            action();
        });
        ChartFile file = fixture.Library.BmsCharts[0];
        fixture.Library.BmsCharts = fixture.Library.BmsCharts.Skip(1).ToList();
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.ChartNotes), ListSortDirection.Ascending);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        BMSTable table = fixture.Playlist.BMSTables[0];
        int oldPosition = rows.IndexOfSelectionIdentity(table.entries[0]);
        var target = (PlaylistDetailRow)rows[oldPosition];
        var background = (PlaylistDetailRow)rows[rows.IndexOfSelectionIdentity(table.entries[1])];
        Assert.IsNull(target.ChartNotes);
        fixture.PauseDelivery = true;
        fixture.BatchNotificationsOnDelivery = true;
        ChartInfoInlineBuildResult built = fixture.Library.BuildAndPersistInlineChartInfoForInstalledCharts("detail-patch-before-ui-apply", [file]);
        Assert.AreEqual(1, built.SuccessCount);
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly())
        { Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT notes FROM chart_info WHERE sha256 = ?;", file.Sha256)); }
        PlaylistRequestIdentity before = fixture.Workspace.DetailViewState.View.CurrentIdentity ?? throw new AssertFailedException();
        var request = new PlaylistBuildRequest
        {
            RequestVersion = fixture.Workspace.DetailBuildState.RequestVersion,
            Identity = PlaylistRequestFactory.CreateIdentity(before.Table, before.SelectionScope, before.FolderName, before.FilterType,
                before.KeywordFilter, before.ModeFilter, fixture.Workspace.CapturePlaylistDetailSortParameters(), fixture.Library.CatalogRevision,
                before.Table.PlaylistEntriesRevision, fixture.Library.GetScoreSnapshotForDiagnostics().Version, fixture.Library.ChartInfoIndexVersion, true)
        };
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        int notifications = 0;
        rows.CollectionChanged += (_, _) => notifications++;
        hold = true;
        Task<bool> patch = Task.Run(() => fixture.Workspace.TryPatchDetailSourceChartInfo(request, CancellationToken.None, out _, out _, out _, out _));
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, patch, "detail-patch-ui-apply-pending");
            Assert.IsNull(target.ChartNotes, "UI採用前は登録した新情報を表示へ先取りしません。");
            Assert.AreSame(target, rows[oldPosition]);
            fixture.Deliver();
            Assert.AreEqual(1, target.ChartNotes);
            Assert.AreSame(target, rows[15]);
            int published = notifications, realized = rows.RealizedRowCount, workCount = work.Count;
            release.TrySetResult();
            Assert.IsTrue(await patch);
            Assert.AreEqual(published, notifications);
            Assert.AreEqual(realized, rows.RealizedRowCount);
            Assert.AreEqual(workCount, work.Count, "先行consumerの反映版を採用し、順序・投影を再処理しません。");
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
            Assert.AreSame(background, rows[0]);
            Assert.AreEqual(2, rows.RealizedRowCount);
        }
        finally { hold = false; release.TrySetResult(); await patch; }
    }

    /// <summary>未配送catalogを必要とするentry追加を保留したまま別表を開いても、その後の配送で旧表entryが混入しません。</summary>
    [TestMethod]
    public async Task DeferredEntryCommit_DoesNotLeakIntoLaterTable()
    {
        await using var fixture = new Fixture(3);
        await fixture.Refresh();
        BMSTable oldTable = fixture.Workspace.DetailViewState.Source.CurrentTable;
        fixture.PauseDelivery = true;
        fixture.WriteFirst("Z changed");
        fixture.Library.SetBMSFilesEncoding([fixture.Library.BmsCharts[0]], "utf-8");
        Assert.IsTrue(fixture.Playlist.AddPlaylistEntriesToFolderBMSTable(
            [new BMSTableEntry { md5 = new string('f', 32), title = "old table added" }], oldTable, string.Empty));
        var otherTable = new BMSTable
        {
            playlist_id = 4190,
            name = "other",
            entries =
            [new BMSTableEntry { md5 = new string('e', 32), title = "other entry", folder = string.Empty }]
        };
        fixture.Playlist.BMSTables.Add(otherTable);
        fixture.Playlist.CommitBMSTableWithEntriesToDB(otherTable);
        fixture.Workspace.RequestDetailSelection(otherTable);
        // 別表の初回捕捉はすでにcatalog新版です。旧表保留はこの入力へ移しません。
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var row = (PlaylistDetailRow)rows[0];
        fixture.Deliver();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(1, rows.Count);
        Assert.AreSame(row, rows[0]);
        Assert.AreSame(otherTable.entries[0], row.Entry);
        Assert.AreEqual(1, fixture.Workspace.DetailViewState.Source.Rows.Count);
    }

    /// <summary>別表の実全保存で、現在開いている表のRows・入力・実体化を作り直しません。局所entry操作の接続は別ケースが担います。</summary>
    [TestMethod]
    public async Task UnrelatedTableFullSave_PreservesCurrentPresentation()
    {
        await using var fixture = new Fixture(3);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var row = (PlaylistDetailRow)rows[0];
        var other = new BMSTable
        {
            playlist_id = 4191,
            name = "other",
            entries =
            [new BMSTableEntry { md5 = new string('e', 32), title = "unrelated", folder = string.Empty }]
        };
        fixture.Playlist.BMSTables.Add(other);
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        fixture.Playlist.CommitBMSTableWithEntriesToDB(other);
        await fixture.Workspace.WaitForDetailBuildIdleAsync();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(row, rows[0]);
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_source_projection"));
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
    }

    /// <summary>同一receiptの後半が未配送catalogを要するとき、前半のfolder変更も表示へ先行適用しません。</summary>
    [TestMethod]
    public async Task EntryReceipt_WaitsAtomicallyForAllRequiredLookups()
    {
        await using var fixture = new Fixture(3);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var first = (PlaylistDetailRow)rows[0];
        BMSTable table = first.Entry.parent;
        string originalFolder = first.Folder;
        fixture.PauseDelivery = true;
        fixture.WriteFirst("Z changed");
        fixture.Library.SetBMSFilesEncoding([first.Chart], "utf-8");
        Assert.IsTrue(fixture.Playlist.AddPlaylistEntriesToFolderBMSTable(
            [first.Entry, new BMSTableEntry { md5 = new string('f', 32), title = "new member" }], table, "moved"));
        Assert.AreEqual("moved", first.Entry.folder);
        Assert.AreEqual(originalFolder, first.Folder);
        Assert.AreEqual(3, rows.Count);
        fixture.Deliver();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual("moved", first.Folder);
        Assert.AreEqual(4, rows.Count);
        Assert.AreEqual(4, fixture.Workspace.DetailViewState.Source.Rows.Count);
    }

    /// <summary>少数削除のsource位置更新は末尾一件だけで、表示の明示sortと同値EntryOrderを維持します。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public async Task EntryRemoval_ChangesOnlyAffectedSourcePositions(int count)
    {
        await using var fixture = new Fixture(count);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var first = (PlaylistDetailRow)rows[0];
        var next = (PlaylistDetailRow)rows[1];
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        Assert.IsTrue(fixture.Playlist.RemoveEntriesBMSTable([first.Entry], first.Entry.parent));
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(next, rows[0]);
        Assert.AreEqual(count - 1, rows.Count);
        Assert.AreEqual("[" + (count - 1) + Resources.Num_songs + "]", fixture.MainTable.SummaryText);
        Assert.AreEqual(1, work.Count(kind => kind == "playlist_dependency_position_write"));
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
        Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 4 * Math.Ceiling(Math.Log2(count)) + 4);
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.Title), ListSortDirection.Ascending);
        await fixture.Refresh();
        if (fixture.Workspace.DetailViewState.View.Rows[0] is not PlaylistDetailRow adopted)
        { throw new AssertFailedException("Playlist detail row was not adopted."); }
        Assert.AreSame(next.Entry, adopted.Entry);
    }

    /// <summary>大文字小文字と数値表記が既存比較器で同値ならEntryOrderを保ち、実キー変更時だけ対象参照を移動します。</summary>
    [DataTestMethod]
    [DataRow(nameof(PlaylistDetailRow.Title))]
    [DataRow(nameof(PlaylistDetailRow.Folder))]
    public void LocalSort_UsesSemanticEqualityAndConfirmedEntryOrder(string column)
    {
        var table = new BMSTable();
        var input = Enumerable.Range(0, 3).Select(index => new PlaylistDetailSourceRow(
            new BMSTableEntry { md5 = index.ToString("x32"), title = "same", folder = "2", parent = table },
            null, entryOrder: index)).ToList();
        PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.OrdinaryRoot, null,
            PlaylistDetailFilter.PlaylistFilter, string.Empty, ChartModeFilter.All,
            new ChartListSortParameters { ColumnsName = column, Direction = ListSortDirection.Ascending }, 0, 0, 0, 0, true);
        var rows = new PlaylistDetailVirtualView(input, identity: identity);
        var first = (PlaylistDetailRow)rows[0];
        var middle = (PlaylistDetailRow)rows[1];
        var last = (PlaylistDetailRow)rows[2];
        PlaylistEntryValue equal = column == nameof(PlaylistDetailRow.Title)
            ? input[1].EntryValue with { Title = "SAME" } : input[1].EntryValue with { Folder = "02" };
        var equalRow = new PlaylistDetailSourceRow(input[1].Entry, null, entryValue: equal, entryOrder: 1);
        rows.ApplyChange(input[1], equalRow, identity, false);
        rows.PublishChanges();
        Assert.AreSame(first, rows[0]); Assert.AreSame(middle, rows[1]); Assert.AreSame(last, rows[2]);
        PlaylistEntryValue changed = column == nameof(PlaylistDetailRow.Title) ? equal with { Title = "a" } : equal with { Folder = "1" };
        var changedRow = new PlaylistDetailSourceRow(input[1].Entry, null, entryValue: changed, entryOrder: 1);
        rows.ApplyChange(equalRow, changedRow, identity, false);
        rows.PublishChanges();
        Assert.AreSame(middle, rows[0]); Assert.AreSame(first, rows[1]); Assert.AreSame(last, rows[2]);
    }

    /// <summary>正規セル保存のLevel確定差分は同じRowsで並びを変え、memo確定差分は依存keyword所属だけを外します。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public async Task EntryEdits_UpdateLevelOrderAndMemoFilterLocally(int count)
    {
        await using var fixture = new Fixture(count);
        fixture.Workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("memo", ChartModeFilter.All));
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.Level), ListSortDirection.Ascending);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        var next = (PlaylistDetailRow)rows[1];
        int selectionDepth = 0, orderNotifications = 0;
        rows.SelectionUpdateStarting += (_, _) => { Assert.AreEqual(0, selectionDepth); selectionDepth++; };
        rows.SelectionUpdateCompleted += (_, _) => { Assert.AreEqual(1, selectionDepth); selectionDepth--; };
        rows.CollectionChanged += (_, _) => { Assert.AreEqual(1, selectionDepth, "順序通知は選択scope内へ含めます。"); orderNotifications++; };
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        var level = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.Level),
            ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(level);
        target.Level = "4";
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(level, "4", commit: true));
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(target, rows[count - 1]);
        Assert.AreSame(next, rows[0]);
        Assert.AreEqual("4", target.Level);
        var memo = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.memo),
            ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(memo);
        target.memo = "removed from query";
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(memo, "removed from query", commit: true));
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(count - 1, rows.Count);
        Assert.AreSame(next, rows[0]);
        Assert.AreEqual(-1, rows.IndexOf(target));
        Assert.AreEqual(0, selectionDepth); Assert.AreEqual(2, orderNotifications);
        Assert.AreEqual(1, rows.RealizedRowCount, "削除したtarget以外の背景を実体化しません。");
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_detail_materialized"));
        Assert.AreEqual(2, work.Count(kind => kind == "playlist_source_projection"));
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_dependency_index_entry"));
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
        Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 10 * Math.Ceiling(Math.Log2(count)) + 10);
    }

    /// <summary>実改名操作の公開後にroot表示を再構築せず、選択folder自身の改名時だけ新選択を評価します。明示folder順はtree、詳細Folder列は自然順です。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public async Task WorkspaceRenameFolder_UsesReceiptAndRefreshesOnlyRemappedSelection(int count, bool selectedFolder)
    {
        await using var fixture = new Fixture(count, firstFolder: "source");
        BMSTable table = fixture.Playlist.BMSTables.Single();
        if (selectedFolder) { fixture.Workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("source")); }
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.Folder), ListSortDirection.Ascending);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[selectedFolder ? 0 : count - 1];
        PlaylistDetailRow? background = selectedFolder ? null : (PlaylistDetailRow)rows[0];
        int reloads = 0;
        Task reload = Task.CompletedTask;
        fixture.Workspace.PlaylistDetailReloadRefreshRequested += (_, _) => { reloads++; reload = fixture.Refresh(); };
        var work = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        var published = new List<(string[] Nodes, int Saved)>();
        table.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(BMSTable.FolderNodes)) { return; }
            using LR2SongDBExtended database = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
            published.Add((table.FolderNodes.Where(node => !node.IsSpecial).Select(node => node.FolderName).ToArray(),
                database.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE folder='a';")));
        };
        await fixture.Workspace.RenameFolderAsync(table, PlaylistFolderNode.CreateFolder("source"), "a");
        await reload;
        Assert.IsTrue(published.Count > 0, "改名の確定構成通知が操作終端までに届きます。");
        foreach ((string[] Nodes, int Saved) value in published) { CollectionAssert.AreEqual(new[] { "background", "a" }, value.Nodes); Assert.AreEqual(1, value.Saved); }
        CollectionAssert.AreEqual(new[] { "background", "a" }, table.folder_list.ToArray());
        Assert.AreEqual("a", table.entries[0].folder);
        Assert.AreEqual(1, work.Count(kind => kind == "playlist_source_projection"));
        if (selectedFolder)
        {
            Assert.AreEqual(1, reloads);
            Assert.AreEqual("a", fixture.Workspace.CapturePlaylistDetailSelection().FolderName);
            Assert.AreEqual(1, fixture.Workspace.DetailViewState.View.Rows.Count);
            if (fixture.Workspace.DetailViewState.View.Rows[0] is not PlaylistDetailRow adopted)
            { throw new AssertFailedException("Renamed folder was not presented."); }
            Assert.AreSame(target.Entry, adopted.Entry);
            Assert.AreEqual("a", adopted.Folder);
            Assert.AreEqual(1, work.Count(kind => kind == "playlist_dependency_index_entry"));
            Assert.AreEqual(1, work.Count(kind => kind == "playlist_order_buffer"));
        }
        else
        {
            Assert.AreEqual(0, reloads);
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
            Assert.AreSame(target, rows[0]);
            Assert.AreSame(background, rows[1]);
            Assert.AreEqual(2, rows.RealizedRowCount);
            Assert.AreEqual("a", target.Folder);
            Assert.AreEqual(0, work.Count(kind => kind == "playlist_dependency_index_entry"));
            Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
            Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 6 * Math.Ceiling(Math.Log2(count)) + 6);
        }
    }

    /// <summary>実delete/dropを局所反映し、同表移動後の新entryでMEMO保存しても参照だけに一致するfilterと表示を保持します。16→128の背景では移動・保存を別区間で観測します。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public async Task WorkspaceDeleteOrDrop_DoesNotRequestDuplicateDetailRefresh(int count, bool drop)
    {
        const string referenceSymbol = "MOVE_REF";
        await using var fixture = new Fixture(count, firstFolder: "source", prepareTable: drop ? (prepared, entries) =>
        {
            prepared.symbol = referenceSymbol;
            // source/targetの双方に別譜面を残し、空folder用dummyを作らず同表移動を検証する。
            entries[1].folder = "source";
        }
        : null);
        if (drop) { fixture.Workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot(referenceSymbol, ChartModeFilter.All)); }
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var first = (PlaylistDetailRow)rows[0];
        var next = (PlaylistDetailRow)rows[1];
        BMSTable table = first.Entry.parent;
        int reloads = 0;
        fixture.Workspace.PlaylistDetailReloadRefreshRequested += (_, _) => reloads++;
        var work = new List<string>();
        var modelWork = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        fixture.Library.PlaylistReferenceWorkObserver = modelWork.Add;
        if (drop)
        {
            Assert.AreEqual(count, rows.Count);
            Assert.AreEqual(referenceSymbol, first.RefTablesSymbols);
            Assert.AreEqual(table.name, first.RefTablesNames);
            await fixture.Workspace.AddRowsToFolderAsync([first], table, PlaylistFolderNode.CreateFolder("background"));
            fixture.Deliver();
            Assert.AreEqual(count, rows.Count);
            if (rows[0] is not PlaylistDetailRow adopted) { throw new AssertFailedException("Dropped entry was not presented."); }
            Assert.AreEqual("background", adopted.Entry.folder);
            Assert.AreEqual(first.Entry.md5, adopted.Entry.md5);
            Assert.AreNotSame(first.Entry, adopted.Entry);
            Assert.IsFalse(table.ContainsEntry(first.Entry));
            Assert.IsTrue(table.ContainsEntry(adopted.Entry));
            Assert.AreSame(next, rows[1]);
            Assert.AreEqual(referenceSymbol, adopted.RefTablesSymbols);
            Assert.AreEqual(table.name, adopted.RefTablesNames);
            using (var db = new LR2SongDBExtended(fixture.SongDb))
            { Assert.AreEqual("background", db.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE title = 'entry 0';")); }
            Assert.AreEqual(1, work.Count(kind => kind == "playlist_source_projection"));
            Assert.AreEqual(1, work.Count(kind => kind == "playlist_detail_materialized"));
            Assert.AreEqual(2, rows.RealizedRowCount);
            Assert.AreEqual(0, modelWork.Count(kind => kind is "reference_lookup" or "reference_entry_captured" or "reference_entry_indexed"));
            Assert.AreEqual(0, reloads);
            Assert.AreEqual(0, work.Count(kind => kind is "playlist_dependency_index_entry" or "playlist_order_buffer"));
            Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 10 * Math.Ceiling(Math.Log2(count)) + 10);

            work.Clear(); modelWork.Clear();
            var memo = new MainChartListCellEditContext(adopted, nameof(PlaylistDetailRow.memo),
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            fixture.Workspace.BeginDetailEdit(memo);
            adopted.memo = "saved after folder move";
            await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(memo, "saved after folder move", commit: true));
            fixture.Deliver();
            // filter語は参照symbolだけに存在する。後続の実保存でも欠落して表示から外れてはならない。
            Assert.AreEqual(referenceSymbol, adopted.RefTablesSymbols);
            Assert.AreEqual(table.name, adopted.RefTablesNames);
            Assert.AreEqual(count, rows.Count);
            Assert.AreSame(adopted, rows[0]);
            Assert.AreSame(next, rows[1]);
            Assert.AreEqual("source", next.Folder);
            Assert.AreEqual("entry memo", next.memo);
            Assert.AreEqual("saved after folder move", adopted.Entry.memo);
            using (var db = new LR2SongDBExtended(fixture.SongDb))
            {
                Assert.AreEqual("background", db.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE title = 'entry 0';"));
                Assert.AreEqual("saved after folder move", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE title = 'entry 0';"));
                Assert.AreEqual(count, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id = ?;", table.playlist_id));
            }
            Assert.AreEqual(1, work.Count(kind => kind == "playlist_source_projection"));
            Assert.AreEqual(0, work.Count(kind => kind == "playlist_detail_materialized"));
            Assert.AreEqual(2, rows.RealizedRowCount);
            Assert.AreEqual(0, modelWork.Count(kind => kind is "reference_lookup" or "reference_entry_captured" or "reference_entry_indexed"));
        }
        else
        {
            object survivor = rows.GetSelectionIdentity(2);
            int starts = 0, completions = 0, changes = 0;
            rows.SelectionUpdateStarting += (_, _) => starts++;
            rows.SelectionUpdateCompleted += (_, _) =>
            {
                completions++;
                if (rows.IndexOfSelectionIdentity(first.Entry) < 0)
                {
                    Assert.AreEqual(-1, rows.IndexOfSelectionIdentity(next.Entry));
                    Assert.AreEqual(0, rows.IndexOfSelectionIdentity(survivor));
                }
            };
            rows.CollectionChanged += (_, _) => { changes++; Assert.AreEqual(starts - 1, completions); };
            await fixture.Workspace.DeleteSelectedEntriesAsync([first, next]);
            Assert.AreEqual(count - 2, rows.Count);
            Assert.AreSame(survivor, rows.GetSelectionIdentity(0));
            Assert.AreEqual(starts, completions);
            Assert.AreEqual(2, changes);
            Assert.AreEqual(0, work.Count(kind => kind == "playlist_detail_materialized"));
            Assert.AreEqual(0, rows.RealizedRowCount);
            Assert.IsFalse(table.ContainsEntry(first.Entry));
            Assert.IsFalse(table.ContainsEntry(next.Entry));
        }
        Assert.AreEqual(0, reloads);
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_dependency_index_entry"));
        Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
        Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 10 * Math.Ceiling(Math.Log2(count)) + 10);
    }

    /// <summary>実workspace削除の2→1ではMD5/SHA入力を保持し、最後の依存が消えた終端だけ解放します。別表のowner値と捕捉済み旧入力は残ります。</summary>
    [TestMethod]
    public async Task EntryDeletion_ReleasesOnlyLastReferenceDependencyAndPreservesOldInput()
    {
        await using var fixture = new Fixture(3, prepareTable: (prepared, entries) =>
        {
            prepared.symbol = "DETAIL";
            entries[1].md5 = entries[0].md5;
            entries[1].sha256 = entries[0].sha256;
            // 正規取込みは同folder内の同hashを重複排除するため、二つの実依存を別folderへ置く。
            entries[1].folder = "duplicate";
        });
        BMSTable table = fixture.Playlist.BMSTables[0];
        string md5 = table.entries[0].md5;
        string sha256 = table.entries[0].sha256;
        var owner = new BMSTable
        {
            playlist_id = 4189,
            name = "Retained reference",
            symbol = "KEEP",
            is_external_sync = false,
            entries = [new BMSTableEntry { md5 = md5, sha256 = sha256, title = "md5 owner" },
                new BMSTableEntry { sha256 = sha256, title = "sha owner" }]
        };
        fixture.Playlist.BMSTables.Add(owner);
        fixture.Playlist.CommitBMSTableWithEntriesToDB(owner);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var first = (PlaylistDetailRow)rows[0];
        var second = (PlaylistDetailRow)rows[1];
        var retained = (PlaylistDetailRow)rows[2];
        List<PlaylistDetailSourceRow> source = fixture.Workspace.DetailViewState.Source.Rows;
        PlaylistDetailDependencies dependencies = fixture.Workspace.DetailViewState.Source.Dependencies
            ?? throw new AssertFailedException("Detail dependencies were not adopted.");
        ChartListReferenceInput oldInput = dependencies.ReferenceInput
            ?? throw new AssertFailedException("Reference input was not adopted.");
        PlaylistReferenceDisplay oldMd5 = oldInput.Resolve(md5, null);
        PlaylistReferenceDisplay oldSha = oldInput.Resolve(null, sha256);
        Assert.IsTrue(oldMd5.Names.Contains(owner.name, StringComparison.Ordinal));
        Assert.AreEqual("KEEP", oldSha.Symbols);

        await fixture.Workspace.DeleteSelectedEntriesAsync([first]); fixture.Deliver();
        Assert.AreEqual(2, rows.Count);
        Assert.AreSame(second, rows[0]);
        Assert.AreSame(retained, rows[1]);
        Assert.AreEqual(oldMd5.Symbols, dependencies.ReferenceInput.Resolve(md5, null).Symbols);
        Assert.AreEqual(oldMd5.Names, dependencies.ReferenceInput.Resolve(md5, null).Names);
        Assert.AreEqual(oldSha.Symbols, dependencies.ReferenceInput.Resolve(null, sha256).Symbols);
        Assert.AreEqual(oldSha.Names, dependencies.ReferenceInput.Resolve(null, sha256).Names);

        await fixture.Workspace.DeleteSelectedEntriesAsync([second]); fixture.Deliver();
        Assert.AreEqual(1, rows.Count);
        Assert.AreSame(retained, rows[0]);
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(source, fixture.Workspace.DetailViewState.Source.Rows);
        Assert.AreSame(dependencies, fixture.Workspace.DetailViewState.Source.Dependencies);
        Assert.AreSame(source, dependencies.Rows);
        Assert.AreEqual(string.Empty, dependencies.ReferenceInput.Resolve(md5, null).Symbols);
        Assert.AreEqual(string.Empty, dependencies.ReferenceInput.Resolve(md5, null).Names);
        Assert.AreEqual(string.Empty, dependencies.ReferenceInput.Resolve(null, sha256).Symbols);
        Assert.AreEqual(string.Empty, dependencies.ReferenceInput.Resolve(null, sha256).Names);
        PlaylistReferenceReadSnapshot snapshot = fixture.Library.CapturePlaylistReferenceReadSnapshot(
            [new(PlaylistEntryLookupKeyKind.Md5, md5), new(PlaylistEntryLookupKeyKind.Sha256, sha256)]);
        Assert.AreEqual("KEEP", snapshot.Values[new(PlaylistEntryLookupKeyKind.Md5, md5)].Symbols);
        Assert.AreEqual("KEEP", snapshot.Values[new(PlaylistEntryLookupKeyKind.Sha256, sha256)].Symbols);
        Assert.AreSame(oldMd5, oldInput.Resolve(md5, null));
        Assert.AreSame(oldSha, oldInput.Resolve(null, sha256));
        Assert.IsTrue(oldInput.Resolve(md5, null).Names.Contains(owner.name, StringComparison.Ordinal));
    }

    /// <summary>初回捕捉でbucketが不在でも有効な入力です。後続の実MEMO保存は追加lookupせず、空表示と採用中sourceを保持します。</summary>
    [TestMethod]
    public async Task MemoSave_WithCapturedAbsentReferenceBucketDoesNotRecapture()
    {
        await using var fixture = new Fixture(3);
        fixture.Library.SynchronizeReferenceBMSTables([]);
        fixture.Deliver();
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        PlaylistDetailDependencies dependencies = fixture.Workspace.DetailViewState.Source.Dependencies
            ?? throw new AssertFailedException("Detail dependencies were not adopted.");
        ChartListReferenceInput input = dependencies.ReferenceInput
            ?? throw new AssertFailedException("Reference input was not adopted.");
        Assert.IsTrue(input.IsCaptured);
        PlaylistReferenceReadSnapshot absent = fixture.Library.CapturePlaylistReferenceReadSnapshot(
            [new(PlaylistEntryLookupKeyKind.Md5, target.hash), new(PlaylistEntryLookupKeyKind.Sha256, target.sha256)]);
        Assert.AreEqual(0, absent.Values.Count);
        var modelWork = new List<string>();
        fixture.Library.PlaylistReferenceWorkObserver = modelWork.Add;
        var memo = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.memo),
            ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(memo);
        target.memo = "saved with no reference";
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(memo, "saved with no reference", commit: true));
        fixture.Deliver();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(dependencies, fixture.Workspace.DetailViewState.Source.Dependencies);
        Assert.AreSame(target, rows[0]);
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual(string.Empty, target.RefTablesSymbols);
        Assert.AreEqual(string.Empty, target.RefTablesNames);
        Assert.AreEqual("saved with no reference", target.Entry.memo);
        using (var db = new LR2SongDBExtended(fixture.SongDb))
        { Assert.AreEqual("saved with no reference", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE title = 'entry 0';")); }
        Assert.AreEqual(0, modelWork.Count(kind => kind == "reference_lookup"));
    }

    /// <summary>A/Bの別キー確定後、片方のpure wakeを止めても単一drainが全rangeを覆い、同条件要求は混合入力をreuseしません。旧同キー逆順保証もここへ移管します。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public async Task ScoreRanges_DelayedDistinctWakeAndSameConditionRequestCoverAllKeys(int count, bool reverseWake)
    {
        await using var fixture = new Fixture(count, regularDelivery: true, oracleScores: true);
        await fixture.DrainLibraryChanges();
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.stddevVal), ListSortDirection.Ascending);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var a = (PlaylistDetailRow)rows[0];
        var u = (PlaylistDetailRow)rows[1];
        var b = (PlaylistDetailRow)rows[2];
        fixture.MainTable.SelectedIndex = 1;
        long catalog = fixture.Library.CatalogRevision;
        var work = new List<string>();
        var scoreWork = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add;
        fixture.Library.ScoreSnapshotWorkObserver = scoreWork.Add;
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int producerThread = 0;
        PropertyChangedEventHandler gate = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.ScoreSnapshotVersion)
                && Environment.CurrentManagedThreadId == Volatile.Read(ref producerThread))
            { reached.Set(); release.Wait(); }
        };
        // ScoreSnapshotVersionのpure通知は確定とqueue登録後、通常wakeの前です。
        fixture.Library.PropertyChanged += gate;
        Task? producer = null;
        Task? refresh = null;
        try
        {
            if (!reverseWake) { fixture.ChangeScore(0, 40); }
            producer = Task.Run(() =>
            {
                Volatile.Write(ref producerThread, Environment.CurrentManagedThreadId);
                fixture.ChangeScore(reverseWake ? 0 : 1, reverseWake ? 40 : 5);
            });
            reached.Wait();
            if (reverseWake) { fixture.ChangeScore(1, 5); }
            Assert.AreEqual(10d, a.stddevVal);
            Assert.AreEqual(30d, b.stddevVal);
            refresh = fixture.Refresh();
            await fixture.DeliveryRequested.Task;
            Assert.IsFalse(refresh.IsCompleted, "配送終端前に同条件要求を最新版として完了しません。");
            await fixture.DrainLibraryChanges();
            await refresh;
            release.Set();
            await producer;
            await fixture.DrainLibraryChanges();
            CollectionAssert.AreEqual(new[] { b, u, a }, rows.Cast<PlaylistDetailRow>().Take(3).ToArray());
            Assert.AreEqual(40, a.score); Assert.AreEqual(5, b.score); Assert.AreEqual(20, u.score);
            Assert.AreEqual(40d, a.stddevVal); Assert.AreEqual(5d, b.stddevVal); Assert.AreEqual(20d, u.stddevVal);
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
            Assert.AreEqual(1, fixture.MainTable.SelectedIndex);
            Assert.AreEqual(3, rows.RealizedRowCount);
            Assert.AreEqual(catalog, fixture.Library.CatalogRevision);
            Assert.AreEqual(fixture.Library.GetScoreSnapshotForDiagnostics().Version,
                fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
            Assert.AreEqual(2, work.Count(kind => kind == "playlist_source_projection"));
            Assert.AreEqual(2, work.Count(kind => kind == "playlist_dependency_index_write"));
            Assert.AreEqual(0, work.Count(kind => kind is "playlist_dependency_index_entry" or "playlist_order_buffer"));
            Assert.AreEqual(0, work.Count(kind => kind == "playlist_detail_materialized"));
            Assert.AreEqual(2, scoreWork.Count(kind => kind == "score_projection_lookup"));
            Assert.AreEqual(2, scoreWork.Count(kind => kind == "score_subscription_lookup"));
            Assert.AreEqual(2, scoreWork.Count(kind => kind == "score_projection_value_copied"));
            Assert.AreEqual(0, scoreWork.Count(kind => kind is "score_lookup_source_enumerated" or "score_lookup_reference_copied" or "score_subscription_key_enumerated"));
            Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 12 * Math.Ceiling(Math.Log2(count)) + 12);
            Assert.AreEqual(work.Count(kind => kind == "playlist_order_access"), work.Count(kind => kind == "playlist_order_comparison"));
            Assert.IsTrue(work.Count(kind => kind == "playlist_notification_buffer") <= 6);
        }
        finally
        {
            fixture.Library.PropertyChanged -= gate;
            release.Set();
            if (producer != null) { await producer; }
            fixture.Workspace.CancelDetailBuilds();
            if (refresh != null) { await refresh; }
        }
    }

    /// <summary>実folderのfilter前全entryを同じbaselineで構築し、非表示Aの加入・Bの離脱も一件ずつ更新します。Uと未訪問背景は維持します。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public async Task ScoreRanges_UpdateHiddenInputsBeforeFilteringAndCoverActualFolder(int count, bool folder)
    {
        await using var fixture = new Fixture(count, firstFolder: folder ? "source" : null, regularDelivery: true, oracleScores: true);
        await fixture.DrainLibraryChanges();
        if (folder) { fixture.Workspace.RequestDetailSelection(fixture.Playlist.BMSTables.Single(), PlaylistFolderNode.CreateFolder("source")); }
        fixture.Workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("score:>=20", ChartModeFilter.All));
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.stddevVal), ListSortDirection.Ascending);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        Assert.AreEqual(folder ? 3 : count, fixture.Workspace.DetailViewState.Source.Rows.Count);
        Assert.AreEqual(2, rows.Count);
        var u = (PlaylistDetailRow)rows[0];
        var work = new List<string>(); fixture.Workspace.DetailWorkObserver = work.Add;
        fixture.ChangeScore(0, 40); fixture.ChangeScore(1, 5);
        await fixture.DrainLibraryChanges();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(u, rows[0]);
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("entry 2", ((PlaylistDetailRow)rows[0]).Entry.title);
        Assert.AreEqual("entry 0", ((PlaylistDetailRow)rows[1]).Entry.title);
        Assert.AreEqual(40, ((PlaylistDetailRow)rows[1]).score);
        Assert.AreEqual(2, work.Count(kind => kind == "playlist_source_projection"));
        Assert.AreEqual(0, work.Count(kind => kind is "playlist_dependency_index_entry" or "playlist_order_buffer"));
        Assert.AreEqual(fixture.Library.GetScoreSnapshotForDiagnostics().Version,
            fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
    }

    /// <summary>初回A投影後/B投影前の実更新をworkerでrange適用し、rootではUI採用待ち中の別key差分も最初のRows公開へ含めます。</summary>
    /// <remarks>folderは初回投影中の差分、rootは準備後のC差分も保証します。Windowなしの共有Dispatcherで、候補投影・scoreコピーのworker所属とRowsReplacing・Rows公開のUI所属を分担します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScoreRanges_CommittedDuringInitialProjectionAreAppliedBeforeAdoption(bool folder)
    {
        System.Windows.Threading.Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        var adoptionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adoptionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdAdoption = folder ? 0 : 1;
        await using var fixture = new Fixture(16, firstFolder: folder ? "source" : null, regularDelivery: true, oracleScores: true,
            entryDeliveryScheduler: async action =>
            {
                if (Interlocked.Exchange(ref holdAdoption, 0) == 1)
                {
                    adoptionEntered.TrySetResult();
                    await adoptionRelease.Task;
                }
                await dispatcher.InvokeAsync(action).Task;
            });
        await fixture.DrainLibraryChanges();
        if (folder) { fixture.Workspace.RequestDetailSelection(fixture.Playlist.BMSTables.Single(), PlaylistFolderNode.CreateFolder("source")); }
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.stddevVal), ListSortDirection.Ascending);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int projected = 0, uiProjection = 0, uiScoreCopies = 0, publishedRows = 0;
        fixture.Workspace.DetailWorkObserver = kind =>
        {
            // 初回候補だけが存在する区間なので、これらの投影は現役sourceの合法なUI配送ではありません。
            if (kind is "playlist_source_projection" or "playlist_detail_dependency_projection" && dispatcher.CheckAccess())
            { Interlocked.Increment(ref uiProjection); }
            if (kind == "playlist_source_projection" && Interlocked.Increment(ref projected) == 2)
            { reached.TrySetResult(); release.Wait(); }
        };
        fixture.Library.ScoreSnapshotWorkObserver = kind =>
        {
            if (kind == "score_projection_value_copied" && dispatcher.CheckAccess()) { Interlocked.Increment(ref uiScoreCopies); }
        };
        EventHandler replacing = (_, _) => Assert.IsTrue(dispatcher.CheckAccess(), "RowsReplacingはUI採用actionに含めます。");
        PropertyChangedEventHandler published = (_, args) =>
        {
            if (args.PropertyName != nameof(MainChartListViewModel.Rows)) { return; }
            Assert.IsTrue(dispatcher.CheckAccess());
            publishedRows++;
            CollectionAssert.AreEqual(folder ? new[] { "entry 1", "entry 2", "entry 0" } : new[] { "entry 2", "entry 1", "entry 0" },
                fixture.MainTable.Rows.Cast<PlaylistDetailRow>().Take(3).Select(row => row.Entry.title).ToArray(),
                "最初のRows公開から、UI採用待ち中に届いた差分を含む最終順序を公開します。");
            Assert.AreEqual(fixture.Library.GetScoreSnapshotForDiagnostics().Version,
                fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
        };
        fixture.MainTable.RowsReplacing += replacing;
        fixture.MainTable.PropertyChanged += published;
        Task refresh = fixture.Refresh();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, refresh, "initial-score-projection");
            fixture.ChangeScore(0, 40); fixture.ChangeScore(1, 5);
            await fixture.DrainLibraryChanges();
            release.Set();
            if (!folder)
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(adoptionEntered.Task, refresh, "prepared-candidate-ui-adoption");
                Assert.AreEqual(0, publishedRows);
                fixture.ChangeScore(2, 1);
                await fixture.DrainLibraryChanges();
                adoptionRelease.TrySetResult();
            }
            await refresh;
            var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
            CollectionAssert.AreEqual(folder ? new[] { "entry 1", "entry 2", "entry 0" } : new[] { "entry 2", "entry 1", "entry 0" },
                rows.Cast<PlaylistDetailRow>().Take(3).Select(row => row.Entry.title).ToArray());
            Assert.AreEqual(folder ? 5 : 1, ((PlaylistDetailRow)rows[0]).score);
            Assert.AreEqual(40, ((PlaylistDetailRow)rows[2]).score);
            Assert.AreEqual(1, publishedRows);
            Assert.AreEqual(0, uiProjection, "初回候補と依存差分の投影をworkerに保ちます。");
            Assert.AreEqual(0, uiScoreCopies, "候補追随のscoreコピーをUIへ移しません。");
            Assert.AreEqual(fixture.Library.GetScoreSnapshotForDiagnostics().Version,
                fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
        }
        finally
        {
            release.Set(); adoptionRelease.TrySetResult();
            fixture.Workspace.CancelDetailBuilds();
            try { await refresh; }
            finally
            {
                fixture.MainTable.RowsReplacing -= replacing;
                fixture.MainTable.PropertyChanged -= published;
                fixture.Workspace.DetailWorkObserver = null;
                fixture.Library.ScoreSnapshotWorkObserver = null;
            }
        }
    }

    /// <summary>実drainのfanout失敗、正常配送だがworkspace未反映、port欠落を要求失敗へ収束させ、score新版のreuseと永久待機を防ぎます。</summary>
    [DataTestMethod]
    [DataRow("apply_failure")]
    [DataRow("uncovered")]
    [DataRow("no_port")]
    [DataRow("reject")]
    [DataRow("abort")]
    public async Task ScoreRanges_DeliveryTerminalFailureDoesNotAdoptUncoveredSource(string failure)
    {
        await using var fixture = new Fixture(16, regularDelivery: true, oracleScores: true, connectDelivery: failure != "no_port");
        await fixture.DrainLibraryChanges(); await fixture.Refresh();
        IList rows = fixture.Workspace.DetailViewState.View.Rows;
        int before = fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion;
        fixture.FailWorkspaceDelivery = failure == "apply_failure";
        fixture.SkipWorkspaceDelivery = failure == "uncovered";
        fixture.DeliveryScheduler.Reject = failure == "reject";
        fixture.ChangeScore(0, 40);
        Task refresh = fixture.Refresh();
        if (failure != "no_port")
        {
            await fixture.DeliveryRequested.Task;
            if (failure == "abort")
            {
                ScheduledScoreOperation operation = await fixture.DeliveryScheduler.NextAsync();
                operation.Abort();
            }
            else if (failure == "reject")
            {
                // 拒否時は受理operationが存在しません。portが返した同じTaskの失敗を観測し、再配送を要求しません。
                Task delivery = fixture.RequestedLibraryDelivery
                    ?? throw new AssertFailedException("配送portの実終端Taskが捕捉されていません。");
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await delivery);
                Assert.AreEqual(0, fixture.DeliveryScheduler.Count);
            }
            else
            {
                try { await fixture.DrainLibraryChanges(); }
                catch (InvalidOperationException) when (failure == "apply_failure") { }
            }
        }
        if (failure == "abort")
        { await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await refresh); }
        else { await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await refresh); }
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreEqual(before, fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
        Assert.AreEqual(failure == "no_port" ? 0 : 1, fixture.DeliveryRequests, "同じ必要位置で配送要求を再試行しません。");
        await fixture.Workspace.WaitForDetailBuildIdleAsync();
    }

    /// <summary>Δ=1のwarm変更はAだけを投影し、Uの参照・選択・背景未実体化を保持します。lookup列挙/複写と全source索引・順序bufferは作りません。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public async Task ScoreRange_OneKeyKeepsBackgroundWorkIndependent(int count)
    {
        await using var fixture = new Fixture(count, regularDelivery: true, oracleScores: true);
        await fixture.DrainLibraryChanges();
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.stddevVal), ListSortDirection.Ascending);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var a = (PlaylistDetailRow)rows[0]; var u = (PlaylistDetailRow)rows[1];
        var background = (PlaylistDetailRow)rows[count - 1];
        fixture.MainTable.SelectedIndex = count - 1;
        var work = new List<string>(); var model = new List<string>();
        fixture.Workspace.DetailWorkObserver = work.Add; fixture.Library.ScoreSnapshotWorkObserver = model.Add;
        fixture.ChangeScore(0, 40); await fixture.DrainLibraryChanges();
        Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
        Assert.AreSame(u, rows[0]); Assert.AreSame(a, rows[2]); Assert.AreEqual(40, a.score);
        Assert.AreEqual(count - 1, fixture.MainTable.SelectedIndex);
        Assert.AreSame(background, rows[count - 1]);
        Assert.AreEqual(3, rows.RealizedRowCount);
        Assert.AreEqual(1, work.Count(kind => kind == "playlist_source_projection"));
        Assert.AreEqual(1, work.Count(kind => kind == "playlist_dependency_index_write"));
        Assert.AreEqual(1, model.Count(kind => kind == "score_projection_lookup"));
        Assert.AreEqual(1, model.Count(kind => kind == "score_subscription_lookup"));
        Assert.AreEqual(1, model.Count(kind => kind == "score_projection_value_copied"));
        Assert.AreEqual(0, model.Count(kind => kind is "score_lookup_source_enumerated" or "score_lookup_reference_copied" or "score_subscription_key_enumerated"));
        Assert.AreEqual(0, work.Count(kind => kind is "playlist_dependency_index_entry" or "playlist_order_buffer" or "playlist_detail_materialized"));
        Assert.IsTrue(work.Count(kind => kind == "playlist_order_access") <= 6 * Math.Ceiling(Math.Log2(count)) + 6);
        Assert.AreEqual(work.Count(kind => kind == "playlist_order_access"), work.Count(kind => kind == "playlist_order_comparison"));
    }

    /// <summary>callback実行と実Completionを分け、要求取消/接続交換は他consumerの共有drainを取消さず、旧結果を再採用しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScoreRange_CancelOrReplaceRequestDoesNotCancelSharedDelivery(bool replace)
    {
        await using var fixture = new Fixture(16, regularDelivery: true, oracleScores: true);
        await fixture.DrainLibraryChanges(); await fixture.Refresh();
        IList rows = fixture.Workspace.DetailViewState.View.Rows;
        if (rows[0] is not PlaylistDetailRow old)
        { throw new AssertFailedException("Playlist detail row was not materialized."); }
        fixture.ChangeScore(0, 40);
        Task shared = fixture.GetLibraryDelivery();
        Task refresh = fixture.Refresh();
        ScheduledScoreOperation? operation = null;
        try
        {
            await fixture.DeliveryRequested.Task;
            operation = await fixture.DeliveryScheduler.NextAsync();
            if (replace) { fixture.Workspace.SetDetailDataSource(new FakePlaylistDetailDataSource()); }
            else
            {
                operation.Run();
                Assert.IsFalse(refresh.IsCompleted, "fanout後も実UI操作Completionまで要求を完了しません。");
                fixture.Workspace.CancelDetailBuilds();
            }
            await refresh;
            Assert.IsFalse(shared.IsCompleted, "要求側の取消は共有drainを取り消しません。");
            if (replace) { operation.Run(); }
            operation.Complete();
            await shared;
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
            Assert.AreEqual(replace ? 10d : 40d, old.stddevVal);
        }
        finally
        {
            if (operation?.IsCompleted == false) { operation.Abort(); }
            while (fixture.DeliveryScheduler.TryTake(out ScheduledScoreOperation? queued)) { queued.Abort(); }
            fixture.Workspace.CancelDetailBuilds();
            await refresh;
            try { await shared; } catch (TaskCanceledException) { }
        }
    }

    /// <summary>A配送のcallback後/実Completion前にBが別確定した場合だけ、新しい必要位置へ次の単一drainを要求できます。</summary>
    [TestMethod]
    public async Task ScoreRanges_NewCommitAfterFanoutWaitsForNewRegisteredRange()
    {
        await using var fixture = new Fixture(16, regularDelivery: true, oracleScores: true);
        await fixture.DrainLibraryChanges();
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.stddevVal), ListSortDirection.Ascending);
        await fixture.Refresh();
        IList rows = fixture.Workspace.DetailViewState.View.Rows;
        var secondRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.LibraryDeliveryRequested += count => { if (count == 2) { secondRequest.TrySetResult(); } };
        fixture.ChangeScore(0, 40);
        Task refresh = fixture.Refresh();
        ScheduledScoreOperation? first = null;
        ScheduledScoreOperation? second = null;
        try
        {
            await fixture.DeliveryRequested.Task;
            first = await fixture.DeliveryScheduler.NextAsync();
            first.Run();
            fixture.ChangeScore(1, 5);
            first.Complete();
            await secondRequest.Task;
            second = await fixture.DeliveryScheduler.NextAsync();
            Assert.IsFalse(refresh.IsCompleted);
            Assert.IsTrue(fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion < fixture.Library.GetScoreSnapshotForDiagnostics().Version);
            Assert.AreEqual(30, fixture.Workspace.DetailViewState.Source.Rows.Single(row => row.Entry.title == "entry 1").score,
                "Bのrange配送前はB旧値のままで、そのsourceへB確定位置を付けません。");
            second.Run(); second.Complete();
            await refresh;
            Assert.AreSame(rows, fixture.Workspace.DetailViewState.View.Rows);
            CollectionAssert.AreEqual(new[] { "entry 1", "entry 2", "entry 0" }, rows.Cast<PlaylistDetailRow>().Take(3).Select(row => row.Entry.title).ToArray());
            Assert.AreEqual(2, fixture.DeliveryRequests);
            Assert.AreEqual(fixture.Library.GetScoreSnapshotForDiagnostics().Version,
                fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
        }
        finally
        {
            first?.Abort(); second?.Abort();
            while (fixture.DeliveryScheduler.TryTake(out ScheduledScoreOperation? queued)) { queued.Abort(); }
            fixture.Workspace.CancelDetailBuilds();
            await refresh;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistCatalogViewChangeTests), Guid.NewGuid().ToString("N"));
        private readonly Queue<Action> deliveries = new();
        private readonly PropertyChangedEventHandler? notification;
        private readonly RegularChartListOwner? regularOwner;
        internal ScoreDeliveryUiScheduler DeliveryScheduler { get; } = new();
        internal BMSScore[] OracleScores { get; } = [];
        internal TaskCompletionSource DeliveryRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? RequestedLibraryDelivery { get; private set; }
        internal int DeliveryRequests { get; private set; }
        internal event Action<int>? LibraryDeliveryRequested;
        internal bool SkipWorkspaceDelivery { get; set; }
        internal bool FailWorkspaceDelivery { get; set; }
        private int handled;
        private readonly bool bmson;
        internal string SongDb { get; }
        internal TestBmsLibrary Library { get; }
        internal TestBmsPlaylist Playlist { get; }
        internal PlaylistWorkspaceViewModel Workspace { get; }
        internal MainChartListViewModel MainTable { get; }
        internal bool PauseDelivery { get; set; }
        internal bool BatchNotificationsOnDelivery { get; set; }
        private bool batchNotificationQueued;

        /// <summary>所属・hash索引を確立するentries取込み前に表と未登録entryを準備し、初回捕捉も同じ正規保存値から開始します。</summary>
        internal Fixture(int count, bool bmson = false, string? firstFolder = null,
            bool regularDelivery = false, bool oracleScores = false, bool connectDelivery = true,
            Func<string, LR2SongDBExtended>? openEntryWriteConnection = null, Action<BMSTable, List<BMSTableEntry>>? prepareTable = null,
            Func<Action, Task>? entryDeliveryScheduler = null)
        {
            this.bmson = bmson;
            Directory.CreateDirectory(directory);
            SongDb = Path.Combine(directory, "song.db");
            using (var db = new LR2SongDBExtended(SongDb)) { }
            PlaylistPersistenceRepository.EnsureSchema(SongDb);
            var charts = new List<ChartFile>();
            for (int index = 0; index < count; index++)
            {
                string path = Path.Combine(directory, index + (bmson ? ".bmson" : ".bms"));
                Write(path, index == 0 ? "A target" : "B background " + index.ToString("D3"));
                ChartFileSnapshot input = ChartFileContentReader.ReadSnapshot(path);
                charts.Add(bmson ? BmsonChartFileParser.ParseSnapshot(input) : BmsChartFileParser.ParseSnapshot(input));
            }
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
            string? scorePath = oracleScores ? Path.Combine(directory, "score.db") : null;
            if (scorePath != null)
            {
                using var db = new SQLite.SQLiteConnection(scorePath);
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                for (int index = 0; index < count; index++)
                { db.Insert(new LR2ScoreDB.score { hash = charts[index].Md5, great = index switch { 0 => 10, 1 => 30, 2 => 20, _ => 1 }, totalnotes = 100 }); }
            }
            Library = new TestBmsLibrary(SongDb, null, scorePath, startupRequiredFileScanReason: null, settings: settings);
            Library.BmsCharts = bmson ? [] : charts;
            Library.BmsonCharts = bmson ? charts : [];
            new BmsLibraryDbGateway(SongDb).UpsertSongs(charts);
            if (oracleScores) { Library.InitializeScoresOnly(null); }
            if (oracleScores)
            {
                var byHash = Library.GetBMSScores().ToDictionary(score => score.hash);
                OracleScores = charts.Select(chart => byHash[chart.Md5]).ToArray();
                for (int index = 0; index < count; index++)
                { OracleScores[index].stddevVal = index switch { 0 => 10, 1 => 30, 2 => 20, _ => 100 + index }; }
            }
            Playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(Library), SongDb,
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
                playlistUrlCompletionOptionsProvider: () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
                beatorajaBmtOptionsProvider: () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings),
                openEntryWriteConnection: openEntryWriteConnection);
            var table = new BMSTable
            {
                playlist_id = 4188,
                name = "catalog view",
                is_external_sync = false,
                Folder_order = firstFolder == null ? [] : ["background", firstFolder]
            };
            var entries = charts.Select((chart, index) => new BMSTableEntry
            {
                md5 = chart.Md5,
                sha256 = chart.Sha256,
                title = "entry " + index,
                artist = "entry artist",
                memo = "entry memo",
                level = index == 0 ? 2.5 : 3,
                folder = firstFolder == null ? string.Empty : index < (oracleScores ? 3 : 1) ? firstFolder : "background"
            }).ToList();
            prepareTable?.Invoke(table, entries);
            table.entries = entries;
            Playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            Playlist.CommitBMSTableWithEntriesToDB(table);
            Workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _, playlistStoreProvider: () => Playlist,
                playlistLibraryProvider: () => Library,
                dispatchPresentation: action => action(), restoreUiApplyScheduler: entryDeliveryScheduler);
            Workspace.SetDetailDataSource(new PlaylistDetailDataSource(Library, Playlist, new MainChartRowProjectionOwner()));
            Workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            MainTable = (MainChartListViewModel)(typeof(PlaylistWorkspaceViewModel).GetField("detailMainChartList",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Workspace) ?? throw new InvalidOperationException());
            Workspace.RequestDetailSelection(table);
            Workspace.InitializePlaylistDetailFilter(ChartListFilterSnapshot.Default);
            Workspace.InitializePlaylistDetailSort(new ChartListSortParameters { ColumnsName = nameof(PlaylistDetailRow.Title), Direction = ListSortDirection.Ascending });
            if (regularDelivery)
            {
                RegularChartListOwner owner = RegularChartListOwnerTestSupport.CreateOwner(MainTable, Workspace, action => action(),
                    DeliveryScheduler);
                regularOwner = owner;
                owner.NormalLibraryRefreshApplied += (_, args) =>
                {
                    if (FailWorkspaceDelivery) { throw new InvalidOperationException("fixture fanout failure"); }
                    if (!SkipWorkspaceDelivery) { Workspace.ApplyLibraryChanges(Library, args.NotificationBatch); }
                };
                if (connectDelivery)
                {
                    Workspace.SetLibraryChangeDelivery((library, cancellationToken) =>
                    {
                        Task delivery = owner.DeliverPendingLibraryChangesAsync(library, cancellationToken);
                        RequestedLibraryDelivery = delivery;
                        DeliveryRequests++;
                        LibraryDeliveryRequested?.Invoke(DeliveryRequests);
                        DeliveryRequested.TrySetResult();
                        return delivery;
                    });
                }
                owner.AttachNormalLibraryRefreshSource(Library);
                return;
            }
            handled = Library.NormalLibraryRefreshNotificationVersion;
            notification = (_, args) =>
            {
                if (args.PropertyName != "NormalLibraryRefreshNotificationVersion") { return; }
                if (BatchNotificationsOnDelivery)
                {
                    if (!batchNotificationQueued)
                    {
                        batchNotificationQueued = true;
                        deliveries.Enqueue(() =>
                        {
                            batchNotificationQueued = false;
                            NormalLibraryRefreshNotificationBatch pending = Library.GetNormalLibraryRefreshNotificationsAfter(handled);
                            handled = pending.LatestVersion;
                            Workspace.ApplyLibraryChanges(Library, pending);
                        });
                    }
                    return;
                }
                NormalLibraryRefreshNotificationBatch batch = Library.GetNormalLibraryRefreshNotificationsAfter(handled);
                handled = batch.LatestVersion;
                Action deliver = () => Workspace.ApplyLibraryChanges(Library, batch);
                if (PauseDelivery) { deliveries.Enqueue(deliver); } else { deliver(); }
            };
            Library.PropertyChanged += notification;
        }

        internal Task Refresh() => Workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected, false, default);

        internal void ChangeScore(int index, int value)
        {
            OracleScores[index].great = value;
            OracleScores[index].stddevVal = value;
        }

        /// <summary>有効な実DBから対象の記録だけを消し、本番のscore-only再読込みで「記録なし」を公開します。</summary>
        internal void RemoveScoreAndReload(int index)
        {
            using (var db = new SQLite.SQLiteConnection(Path.Combine(directory, "score.db")))
            { db.Execute("DELETE FROM score WHERE hash = ?", OracleScores[index].hash); }
            Library.InitializeScoresOnly(null);
        }

        internal Task GetLibraryDelivery() => regularOwner?.DeliverPendingLibraryChangesAsync(Library, CancellationToken.None)
            ?? throw new InvalidOperationException("Real delivery owner was not attached.");

        internal async Task DrainLibraryChanges()
        {
            if (regularOwner == null) { throw new InvalidOperationException("Real delivery owner was not attached."); }
            Task completion = regularOwner.DeliverPendingLibraryChangesAsync(Library, CancellationToken.None);
            if (!completion.IsCompleted)
            {
                ScheduledScoreOperation next = await DeliveryScheduler.NextAsync();
                next.Run(); next.Complete();
            }
            await completion;
        }

        internal void WriteFirst(string title) => Write(Path.Combine(directory, "0" + (bmson ? ".bmson" : ".bms")), title);

        private void Write(string path, string title)
        {
            string content = bmson
                ? "{\"version\":\"1.0.0\",\"info\":{\"title\":\"" + title + "\",\"artist\":\"fixture artist\",\"init_bpm\":120,\"mode_hint\":\"beat-7k\",\"resolution\":240},\"sound_channels\":[{\"name\":\"missing.wav\",\"notes\":[{\"x\":1,\"y\":240,\"l\":0}]}]}"
                : "#PLAYER 1\r\n#BPM 120\r\n#TITLE " + title + "\r\n#ARTIST fixture artist\r\n#WAV01 missing.wav\r\n#00111:01\r\n";
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        internal void Deliver() { while (deliveries.Count > 0) { deliveries.Dequeue()(); } }
        internal bool DeliverOne()
        {
            if (deliveries.Count == 0) { return false; }
            deliveries.Dequeue()(); return true;
        }
        public async ValueTask DisposeAsync()
        {
            Workspace.DetailWorkObserver = null;
            Library.PlaylistReferenceWorkObserver = null;
            if (notification != null) { Library.PropertyChanged -= notification; }
            Deliver();
            Workspace.CancelDetailBuilds();
            await Workspace.WaitForDetailBuildIdleAsync();
            if (regularOwner != null)
            {
                SkipWorkspaceDelivery = true; FailWorkspaceDelivery = false;
                Task stopped = regularOwner.StopAsync();
                DeliveryScheduler.AbortOutstanding();
                while (DeliveryScheduler.TryTake(out ScheduledScoreOperation? operation)) { operation.Run(); operation.Complete(); }
                await stopped;
                regularOwner.Dispose();
            }
            DeliveryScheduler.Dispose();
            Workspace.Dispose();
            Playlist.RequestShutdown("playlist_catalog_view_test");
            Library.RequestShutdown("playlist_catalog_view_test");
            Directory.Delete(directory, recursive: true);
        }
    }
}
