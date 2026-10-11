using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsPlaylistExternalReloadTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>
    /// 単表入口と二表入口で、正本置換後の実BMT失敗が確定参照を隠さず、
    /// 二表の取得失敗とも別に返ることを確認します。解除後の変更なしと次操作まで実DBで観測します。
    /// 置換前の拒否・中断は既存UIスケジューラ検証へ分担します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_OutputFailureReturnsConfirmedReferencesAndSeparateTargetFailures(bool singleTarget)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        FileStream? blocker = null;
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string header = Path.Combine(directory, "header.json");
            string data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"Reload\",\"symbol\":\"R\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            string bmt = Path.Combine(directory, "bmt");
            Directory.CreateDirectory(bmt);
            string manifest = Path.Combine(bmt, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
            playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt },
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable original = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            original.playlist_id = 7501;
            original.is_bmt_output = true;
            original.Output_dir = "Reload";
            original.entries[0].memo = "local-memo";
            var bad = new BMSTable { playlist_id = 7502, name = "Bad", Page_url = new Uri(Path.Combine(directory, "missing.json")), entries = [], is_bmt_output = false };
            playlist.BMSTables = new ObservableCollection<BMSTable>(singleTarget ? [original] : [original, bad]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"After\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
            blocker = new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = singleTarget
                ? await playlist.ExternalSyncOwner.ReloadAndApplySingleTableAsync(original, new Uri(header), "reload-output-failure")
                : await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original, bad]);
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult confirmed = result.Single(target => ReferenceEquals(target.SourceTable, original));
            Assert.IsNotNull(result.OperationException);
            Assert.IsNotNull(confirmed.UpdateReceipt);
            Assert.IsTrue(confirmed.StatePersisted);
            Assert.IsNull(confirmed.Exception);
            Assert.AreNotSame(original, confirmed.ResultTable);
            Assert.AreSame(confirmed.ResultTable, playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id));
            Assert.AreEqual("local-memo", confirmed.ResultTable.entries.Single(entry => entry.md5 == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").memo);
            if (!singleTarget)
            {
                PlaylistExternalSyncOwner.PlaylistReloadTargetResult failed = result.Single(target => ReferenceEquals(target.SourceTable, bad));
                Assert.IsNotNull(failed.Exception);
                Assert.IsNull(failed.UpdateReceipt);
                Assert.AreSame(bad, failed.ResultTable);
            }
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(singleTarget ? 1 : 2, db.Table<BMSTable>().Count());
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == original.playlist_id));
            }
            blocker.Dispose();
            blocker = null;
            DateTime updated = confirmed.ResultTable.last_update;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([confirmed.ResultTable]);
            unchanged.ThrowIfFailed();
            Assert.IsFalse(unchanged.Single().StatePersisted);
            Assert.AreSame(confirmed.ResultTable, unchanged.Single().ResultTable);
            Assert.AreEqual(updated, unchanged.Single().ResultTable.last_update);
            playlist.RemoveBMSTable(unchanged.Single().ResultTable);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly();
            Assert.AreEqual(singleTarget ? 0 : 1, verify.Table<BMSTable>().Count());
        }
        finally
        {
            blocker?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 手動単表と任意複数表の実再取得で、実BMT失敗と確定参照採用後の詳細通知失敗を重ね、両原因・DB・P/Taskと進捗終端・次要求を保ちます。
    /// 通常取得失敗と出力単独失敗は既存ケースへ分担し、表示だけをgateで保持して実保存・BMTは置換しません。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceResync_PostAdoptionPresentationFailureRetainsOutputFailureAndConfirmedState(bool optionalBatch)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaylistWorkspaceViewModel? workspace = null;
        Task? operation = null;
        FileStream? blocker = null;
        bool failPresentation = true;
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string bmt = Path.Combine(directory, "bmt");
            Directory.CreateDirectory(bmt);
            string manifest = Path.Combine(bmt, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"Presentation failure\",\"symbol\":\"P\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            var library = new TestBmsLibrary(songPath, settings: testSettings);
            var playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt },
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            table.playlist_id = 7511;
            table.is_bmt_output = true;
            Assert.IsTrue(table.EnableExternalSync());
            var other = new BMSTable
            {
                playlist_id = 7512,
                name = "Other target",
                Page_url = new Uri(header),
                Header_url = new Uri(header),
                Data_url = new Uri(data),
                entries = []
            };
            Assert.IsTrue(other.EnableExternalSync());
            playlist.BMSTables = new ObservableCollection<BMSTable>(optionalBatch ? [table, other] : [table]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            workspace = CreatePlaylistWorkspace(playlist, library);
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("1"));
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            var presentationFailure = new InvalidOperationException("post-adoption detail presentation failed");
            PlaylistWorkspaceViewModel currentWorkspace = workspace;
            workspace.PlaylistDetailReloadRefreshRequested += (_, _) =>
            {
                if (!failPresentation) { return; }
                Assert.AreNotSame(table, currentWorkspace.CapturePlaylistDetailSelection().Table);
                Assert.AreSame(playlist.BMSTables.Single(candidate => candidate.playlist_id == table.playlist_id), currentWorkspace.CapturePlaylistDetailSelection().Table);
                entered.TrySetResult();
                release.Wait();
                throw presentationFailure;
            };
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"level\":\"2\"}]");
            blocker = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            operation = Task.Run(async () =>
            {
                if (optionalBatch) { await currentWorkspace.RunExternalPlaylistSyncAsync("post-adoption-failure", false, true, 21); }
                else { await currentWorkspace.ResyncPlaylistTableAsync(table); }
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "resync-post-adoption-presentation");
            Assert.IsFalse(operation.IsCompleted);
            bool enteredP = playlist.TryEnterPlaylistMutation(out IDisposable admission);
            admission?.Dispose();
            Assert.IsFalse(enteredP);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == table.playlist_id));
            }
            release.Set();
            Exception? failure = null;
            try { await operation; } catch (Exception caught) { failure = caught; }
            Assert.IsNotNull(failure);
            IReadOnlyList<Exception> failures = new AggregateException(failure).Flatten().InnerExceptions;
            Assert.IsTrue(failures.Any(cause => cause is PlaylistMutationPostCommitException), "元BMT失敗を後段通知失敗が隠しません。");
            Assert.IsTrue(failures.Any(cause => ReferenceEquals(cause, presentationFailure)));
            Assert.IsFalse(progress.Last().IsActive);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            failPresentation = false;
            blocker.Dispose();
            blocker = null;
            BMSTable confirmed = playlist.BMSTables.Single(candidate => candidate.playlist_id == table.playlist_id);
            if (optionalBatch) { await workspace.RunExternalPlaylistSyncAsync("after-presentation-failure", false, true, 22); }
            else { await workspace.ResyncPlaylistTableAsync(confirmed); }
            Assert.AreSame(confirmed, workspace.CapturePlaylistDetailSelection().Table);
        }
        finally
        {
            release.Set();
            if (operation != null) { try { await operation; } catch (Exception) { } }
            blocker?.Dispose();
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Aの実JSON・DB・正本置換を確定した後、Bの実HTTP取得を操作tokenで取り消します。
    /// schedulerの完了だけを保持して全開始Taskの終端までPと進捗が続くこと、解放後にAのreceiptと全体取消原因が届きBのDBを変更しないことを確認します。
    /// true行は実workspaceのreceipt購読でA採用後のsort通知失敗も重ね、全体取消原因と二次失敗を共に保持します。通常対象失敗、置換前の拒否・中断、出力失敗は既存ケースへ分担します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_PartialCancellationRetainsConfirmedReferenceUntilAllTasksEnd(bool failAdoptionNotification)
    {
        (string directory, TestBmsPlaylist playlist, BMSTable original, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending);
        using var cancellation = new CancellationTokenSource();
        SingleRequestHttpServer? server = null;
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? operation = null;
        Exception? primaryFailure = null;
        PlaylistWorkspaceViewModel? workspace = null;
        var sortFailure = new InvalidOperationException("adopted reference sort failed");
        var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
        var cancelledTargetEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string songPath = Path.Combine(directory, "song.db");
            server = new SingleRequestHttpServer(CreateUtf8BomBytes(
                "{\"name\":\"Cancelled\",\"symbol\":\"C\",\"data_url\":\"" + new Uri(Path.Combine(directory, "score.json")).AbsoluteUri + "\"}"), holdBody: true);
            var cancelledTable = new BMSTable
            {
                playlist_id = 9052,
                name = "Cancelled original",
                Page_url = server.Address,
                entries = [new BMSTableEntry { md5 = "cccccccccccccccccccccccccccccccc", title = "Unchanged", memo = "keep-cancelled" }]
            };
            original.entries[0].memo = "keep-confirmed";
            playlist.BMSTables = new ObservableCollection<BMSTable>([original, cancelledTable]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            if (failAdoptionNotification)
            {
                var library = new TestBmsLibrary(songPath, settings: testSettings);
                workspace = CreatePlaylistWorkspace(playlist, library);
                workspace.RequestDetailSelection(original, PlaylistFolderNode.CreateFolder("1"));
                PlaylistWorkspaceViewModel currentWorkspace = workspace;
                workspace.PlaylistReferenceSortInvalidationRequested += (_, _) =>
                {
                    Assert.AreSame(playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id), currentWorkspace.CapturePlaylistDetailSelection().Table);
                    throw sortFailure;
                };
            }
            Uri cancelledUri = server.Address;
            operation = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original, cancelledTable],
                progressCallback: snapshot =>
                {
                    progress.Enqueue(snapshot);
                    if (snapshot.CompletedTableCount > 0 && snapshot.CurrentUri == cancelledUri)
                    {
                        cancelledTargetEnded.TrySetResult();
                    }
                }, cancellationToken: cancellation.Token, reason: "partial-cancellation", publishReferenceReceipts: failAdoptionNotification);
            Assert.AreSame(server.HeadersSent.Task, await Task.WhenAny(server.HeadersSent.Task, operation));
            await server.HeadersSent.Task;
            Assert.AreSame(scheduler.Scheduled, await Task.WhenAny(scheduler.Scheduled, operation));
            await scheduler.Scheduled;
            await scheduler.ApplyPendingWithoutCompletionAsync();
            BMSTable confirmed = playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id);
            Assert.AreNotSame(original, confirmed);
            Assert.AreEqual(2, confirmed.entries.Count);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == original.playlist_id));
            }
            cancellation.Cancel();
            Assert.AreSame(cancelledTargetEnded.Task, await Task.WhenAny(cancelledTargetEnded.Task, operation));
            await cancelledTargetEnded.Task;
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(playlist.IsPlaylistUpdating);
            Assert.IsTrue(progress.Last().IsActive);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _));
            scheduler.CompletePending();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = await operation;
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult successful = result.Single(target => ReferenceEquals(target.SourceTable, original));
            Assert.AreSame(confirmed, successful.ResultTable);
            Assert.IsTrue(successful.Updated);
            Assert.IsTrue(successful.StatePersisted);
            if (!failAdoptionNotification) { Assert.IsNull(successful.Exception); }
            else
            {
                Assert.IsNotNull(result.Exception);
                Assert.IsTrue(new AggregateException(result.Exception).Flatten().InnerExceptions.Any(failure => ReferenceEquals(failure, sortFailure)));
                Assert.AreSame(confirmed, workspace?.CapturePlaylistDetailSelection().Table);
            }
            Assert.IsNotNull(successful.UpdateReceipt);
            Assert.AreSame(confirmed, successful.UpdateReceipt.NewTable);
            Assert.AreEqual("keep-confirmed", confirmed.entries.Single(entry => entry.md5 == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").memo);
            Assert.IsNotNull(result.OperationException);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            // 実HTTP本文はlinked tokenを使うため、元取消例外のtoken同一性ではなく操作の取消しと原因を確認する。
            Assert.IsTrue(new AggregateException(result.OperationException).Flatten().InnerExceptions
                .Any(failure => failure is OperationCanceledException));
            Assert.AreSame(cancelledTable, playlist.BMSTables.Single(table => table.playlist_id == cancelledTable.playlist_id));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("Cancelled original", db.Table<BMSTable>().Single(table => table.playlist_id == cancelledTable.playlist_id).name);
                LR2SongDBExtended.playlist_entry unchanged = db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == cancelledTable.playlist_id);
                Assert.AreEqual("Unchanged", unchanged.title);
                Assert.AreEqual("keep-cancelled", unchanged.memo);
                Assert.AreEqual("cccccccccccccccccccccccccccccccc", unchanged.md5);
            }
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            Assert.IsFalse(progress.Last().IsActive);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult next = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([confirmed]);
            next.ThrowIfFailed();
            Assert.AreSame(confirmed, next.Single().ResultTable);
            Assert.IsFalse(next.Single().Updated);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            cancellation.Cancel();
            server?.ReleaseBody.TrySetResult();
            scheduler.CompletePending();
            if (operation != null)
            {
                try { await operation; }
                catch when (primaryFailure != null) { }
            }
            if (server != null) { await server.DisposeAsync(); }
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>実コレクションの代入後に購読者が失敗しても、新参照と元例外を返しDB確定と次操作を保ちます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_CollectionNotificationFailureRetainsActuallyAdoptedReference()
    {
        (string directory, TestBmsPlaylist playlist, BMSTable original, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending);
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? operation = null;
        var expected = new InvalidOperationException("replacement subscription failed");
        System.Collections.Specialized.NotifyCollectionChangedEventHandler fail = (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace) { throw expected; }
        };
        playlist.BMSTables.CollectionChanged += fail;
        try
        {
            operation = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original]);
            await TestUiDispatcherHost.AwaitNotificationAsync(scheduler.Scheduled, operation, "reload.collection-notification");
            scheduler.ReleasePending();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = await operation;
            Assert.AreSame(expected, result.Single().Exception);
            Assert.IsNotNull(result.Single().UpdateReceipt);
            Assert.AreNotSame(original, result.Single().ResultTable);
            Assert.AreSame(result.Single().ResultTable, playlist.BMSTables.Single());
            BMSTable persisted = new PlaylistPersistenceRepository(Path.Combine(directory, "song.db")).LoadPlaylistHeaders().Single();
            Assert.AreEqual(result.Single().ResultTable.data_sha256, persisted.data_sha256);
            playlist.BMSTables.CollectionChanged -= fail;
            playlist.RemoveBMSTable(result.Single().ResultTable);
            Assert.AreEqual(0, new PlaylistPersistenceRepository(Path.Combine(directory, "song.db")).LoadPlaylistHeaders().Count);
        }
        finally
        {
            scheduler.ReleasePending();
            if (operation != null) { await operation; }
            playlist.BMSTables.CollectionChanged -= fail;
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>三推薦の実登録・再取得で利用者状態、日時、不要保存抑止、通知とLR2/BMT出力を保持し、保存行の所属とmemoは新主体の本番hydrationで確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LocalRecommendations_KeepUserStateAndAvoidUnchangedWritesThenReachLr2AndBmt()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null, reader = null;
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            var repository = new PlaylistPersistenceRepository(songPath);
            int reads = 0;
            string selectedInput = "mixed";
            CustomFolderOutputSettingsSnapshot settings = new() { ShowRecommUpdatedMsg = true };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songPath, CustomFolderOutputPhysicalSurface.Empty);
            playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => settings,
                synchronization, recommendationScoreReader: _ =>
                {
                    reads++;
                    return Task.FromResult(ReadWalkureInput(selectedInput));
                }, settings: testSettings);
            using PlaylistOperationNotificationOwner.OperationNotificationSession session = playlist.OperationNotificationOwner.BeginSession();
            Uri[] uris = [new("bmseeker:table.recommended"), new("bmseeker:table.recommended?base=failed"), new("bmseeker:table.recommended?failed=noplay")];
            List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> loaded = await playlist.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(uris.Select(uri => new BMSTable { Page_url = uri }), false);
            Assert.AreEqual(1, reads);
            await playlist.ExternalSyncOwner.RegistrateExternalTablesAsync(loaded.Select(item => item.ExternalTable), false, "local-recommendations");
            Assert.AreEqual(3, playlist.BMSTables.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty, "取得・新規登録だけでは実力変化を通知しません。");
            BMSTable original = playlist.BMSTables.Single(table => table.Page_url == uris[0]);
            original.name = "My recommendations";
            original.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            original.symbol = "CUSTOM";
            original.compat_prefix = "KEEP ";
            original.Output_dir = "LocalRecommendations";
            original.custom_folder_output_base_name = "CustomName";
            original.bmt_sort = 42;
            original.is_bmt_output = false;
            original.entries[0].memo = "keep-user-memo";
            playlist.CommitBMSTableWithEntriesToDB(original);
            int? id = original.playlist_id;
            DateTime updatedAt = original.last_update;
            string hash = original.data_sha256;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.AreEqual(2, reads);
            Assert.IsTrue(unchanged.All(item => item.Succeeded && !item.Updated));
            Assert.IsTrue(unchanged.Single(item => item.ResultTable.playlist_id == id).StatePersisted);
            Assert.IsTrue(unchanged.Where(item => item.ResultTable.playlist_id != id).All(item => !item.StatePersisted));
            string mixedName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                ReadWalkureCase("mixed")["rating"]!.Value<double>("playerStarRating").ToString("F2", CultureInfo.InvariantCulture));
            BMSTable renamed = playlist.BMSTables.Single(table => table.playlist_id == id);
            Assert.AreEqual(mixedName, renamed.name);
            Assert.AreEqual(mixedName, renamed.org_name);
            Assert.AreEqual(0, synchronization.Operations.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);
            BMSTable persisted = repository.LoadPlaylistHeaders().Single(table => table.playlist_id == id);
            Assert.AreEqual(updatedAt, persisted.last_update);
            Assert.AreEqual(hash, persisted.data_sha256);
            Assert.AreEqual(mixedName, persisted.name);
            Assert.AreEqual(mixedName, persisted.org_name);
            Assert.AreEqual("CUSTOM", persisted.symbol);
            Assert.AreEqual("KEEP ", persisted.compat_prefix);
            Assert.AreEqual("LocalRecommendations", persisted.Output_dir);
            Assert.AreEqual("CustomName", persisted.custom_folder_output_base_name);
            Assert.AreEqual(42, persisted.bmt_sort);
            Assert.IsFalse(persisted.is_bmt_output);
            reader = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings);
            await reader.ReloadTablesAsync(exportBeatorajaBmt: false);
            BMSTable hydrated = reader.BMSTables.Single(table => table.playlist_id == id);
            reader.EnsurePlaylistEntriesLoaded(hydrated, "recommendation-state-hydration-test");
            Assert.IsTrue(hydrated.entries.Any(entry => entry.memo == "keep-user-memo"));
            Assert.IsTrue(hydrated.entries.All(entry => ReferenceEquals(hydrated, entry.parent) && entry.playlist_id == id));
            Assert.IsTrue(renamed.entries.All(entry => ReferenceEquals(renamed, entry.parent) && entry.playlist_id == id));

            // 数値を変えず org_name だけ旧表記にした場合もヘッダーへ保存する。
            renamed.org_name = "旧方針 ★3.87";
            playlist.CommitBMSTableWithEntriesToDB(renamed);
            unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(unchanged.Single(item => item.ResultTable.playlist_id == id).StatePersisted);
            persisted = repository.LoadPlaylistHeaders().Single(table => table.playlist_id == id);
            Assert.AreEqual(mixedName, persisted.org_name);
            Assert.AreEqual(mixedName, playlist.BMSTables.Single(table => table.playlist_id == id).org_name);
            Assert.AreEqual(updatedAt, persisted.last_update);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);

            unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(unchanged.All(item => item.Succeeded && !item.Updated && !item.StatePersisted));
            Assert.AreEqual(0, synchronization.Operations.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);

            selectedInput = "standard";
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult changed = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.AreEqual(5, reads);
            Assert.IsTrue(changed.All(item => item.Succeeded && item.Updated && item.StatePersisted));
            Assert.AreEqual(3, session.TakeReceipt().Notifications.Count);
            BMSTable updated = playlist.BMSTables.Single(table => table.playlist_id == id);
            string standardName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                ReadWalkureCase("standard")["rating"]!.Value<double>("playerStarRating").ToString("F2", CultureInfo.InvariantCulture));
            Assert.AreEqual(standardName, updated.name);
            Assert.AreEqual(standardName, updated.org_name);
            Assert.AreEqual("CUSTOM", updated.symbol);
            Assert.AreEqual("KEEP ", updated.compat_prefix);
            Assert.AreEqual("LocalRecommendations", updated.Output_dir);
            Assert.AreEqual(42, updated.bmt_sort);
            Assert.IsFalse(updated.is_bmt_output);
            Assert.IsTrue(repository.LoadPersistedPlaylistEntries(id, false).Any(entry => entry.memo == "keep-user-memo"));

            // 通知設定を無効にした、成功反映の次回操作でも実力更新通知を出しません。
            settings = new CustomFolderOutputSettingsSnapshot();
            selectedInput = "mixed";
            changed = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(changed.All(item => item.Succeeded && item.Updated));
            Assert.IsTrue(session.TakeReceipt().IsEmpty);
            updated = playlist.BMSTables.Single(table => table.playlist_id == id);
            settings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = Path.Combine(directory, "Output"),
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([updated], "local-recommendations-output");
            string lr2Path = Path.Combine(settings.LR2CustomFolderOutputBaseDir, updated.Output_dir, "0000.lr2folder");
            Assert.IsTrue(File.Exists(lr2Path));
            StringAssert.Contains(ReadShiftJisText(lr2Path), "#TITLE");
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.IsTrue(verify.Table<LR2SongDB.folder>().Any(row => row.path == lr2Path));
            }
            string bmtDirectory = Path.Combine(directory, "Bmt");
            string bmtName = BmtTableExportService.ExportTableData(bmtDirectory, BmtTableExportService.BuildTableData(updated), id.ToString());
            Assert.IsTrue(File.Exists(Path.Combine(bmtDirectory, bmtName)));
            // 既存 BMT 契約はタイトルを要求し、外部表のフォルダへタグを付けます。
            // 同じ譜面の複数目標も有効行ごとに、実際の圧縮出力から照合します。
            string[] expectedBmtRows = updated.entries.Where(entry => !entry.is_removed && !string.IsNullOrWhiteSpace(entry.title))
                .Select(entry => entry.md5 + ":CUSTOM" + entry.folder).ToArray();
            JObject actualBmt = BeatorajaBmtTableImportService.ReadCachedTableData(Path.Combine(bmtDirectory, bmtName));
            Assert.AreEqual(updated.Page_url.OriginalString, actualBmt.Value<string>("url"));
            Assert.AreEqual(mixedName, actualBmt.Value<string>("name"));
            Assert.AreEqual("CUSTOM", actualBmt.Value<string>("tag"));
            CollectionAssert.AreEqual(updated.folder_list.Select(folder => "CUSTOM" + folder).ToArray(),
                ((JArray)(actualBmt["folder"] ?? throw new FormatException())).Select(folder => folder.Value<string>("name")).ToArray());
            CollectionAssert.AreEquivalent(expectedBmtRows,
                ((JArray)(actualBmt["folder"] ?? throw new FormatException())).SelectMany(folder =>
                    ((JArray)(folder["songs"] ?? throw new FormatException())).Select(song => song.Value<string>("md5") + ":" + folder.Value<string>("name"))).ToArray());
        }
        finally
        {
            reader?.RequestShutdown("local-recommendations-test-complete");
            playlist?.RequestShutdown("local-recommendations-test-complete");
            Directory.Delete(directory, true);
        }
    }

    [DataTestMethod]
    [DataRow("旧方針 ★3.87", "standard", true, false, 1)]
    [DataRow("旧方針 ★6.6", "standard", true, false, 0)]
    [DataRow("旧方針 ★6.60", "mixed", true, false, 1)]
    [DataRow("手動名のみ", "standard", true, false, 0)]
    [DataRow("旧方針 ★3.87", "standard", false, false, 0)]
    [DataRow("旧方針 ★3.87", "standard", true, true, 0)]
    [TestCategory("Playlist")]
    public async Task LocalRecommendations_HashInitializationAndSaveFailureKeepSkillNotificationBoundary(
        string oldOrgName, string inputName, bool showNotification, bool failSave, int notificationCount)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        const string triggerName = "recommendation_test_write_failure";
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            var repository = new PlaylistPersistenceRepository(songPath);
            playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = showNotification },
                recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput(inputName)), settings: testSettings);
            BMSTable original = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri("bmseeker:table.recommended"));
            original.playlist_id = 9071;
            original.name = "Manual name ★999";
            original.org_name = oldOrgName;
            original.header_sha256 = null;
            original.data_sha256 = null;
            original.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            playlist.BMSTables = new ObservableCollection<BMSTable>([original]);
            playlist.CommitBMSTableWithEntriesToDB(original);
            BMSTable before = repository.LoadPlaylistHeaders().Single();
            using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
            if (failSave)
            {
                using var writeFailure = new LR2SongDBExtended(songPath);
                writeFailure.Execute("CREATE TRIGGER " + triggerName + " BEFORE INSERT ON playlist WHEN NEW.playlist_id = 9071 "
                    + "BEGIN SELECT RAISE(ABORT, 'recommendation DB write failure'); END;");
            }
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results;
            try
            {
                results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original]);
            }
            finally
            {
                if (failSave)
                {
                    using var cleanup = new LR2SongDBExtended(songPath);
                    cleanup.Execute("DROP TRIGGER " + triggerName + ";");
                }
            }
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = results.Single();
            Assert.IsFalse(result.Updated);
            Assert.AreEqual(!failSave, result.Succeeded);
            Assert.AreEqual(!failSave, result.StatePersisted);
            Assert.AreEqual(original.last_update, result.ResultTable.last_update);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            BMSTable persisted = repository.LoadPlaylistHeaders().Single();
            Assert.AreEqual(original.last_update, persisted.last_update);
            PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = notifications.TakeReceipt();
            Assert.AreEqual(notificationCount, receipt.Notifications.Count);
            if (failSave)
            {
                Assert.AreSame(original, playlist.BMSTables.Single());
                Assert.AreEqual(before.name, persisted.name);
                Assert.AreEqual(before.org_name, persisted.org_name);
                Assert.AreEqual(before.header_sha256, persisted.header_sha256);
                Assert.AreEqual(before.data_sha256, persisted.data_sha256);
                // 障害を除いた別操作を受理でき、元の操作の再試行は行わない。
                Assert.IsTrue((await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original])).Single().Succeeded);
            }
            else
            {
                double expectedSkill = ReadWalkureCase(inputName)["rating"]!.Value<double>("playerStarRating");
                string expectedName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                    expectedSkill.ToString("F2", CultureInfo.InvariantCulture));
                Assert.AreEqual(expectedName, persisted.name);
                Assert.AreEqual(expectedName, persisted.org_name);
                Assert.AreEqual(expectedName, playlist.BMSTables.Single().name);
                Assert.AreEqual(expectedName, playlist.BMSTables.Single().org_name);
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));
                Assert.AreEqual(original.entries.Count, repository.LoadPersistedPlaylistEntries(original.playlist_id, false).Count());
                if (notificationCount == 1)
                {
                    double oldSkill = inputName == "mixed" ? 6.60 : 3.87;
                    Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage,
                        expectedSkill.ToString("F2"), (expectedSkill - oldSkill).ToString(" (+#0.00); (-#0.00);")),
                        receipt.Notifications.Single().Message);
                }
            }
        }
        finally
        {
            playlist?.RequestShutdown("recommendation-hash-test-complete");
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2FolderSync_InvokesMutationGuardBeforeOpeningDatabase()
    {
        bool previousOperationModeLr2Db = testSettings.OperationModeLR2DB;
        string previousOutputBaseDir = testSettings.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "Output");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            synchronization.Failure = new InvalidOperationException(Resources.Warn_LibraryOperationBusy);
            var table = new BMSTable
            {
                playlist_id = 9001,
                name = "SyncMutationGuard",
                symbol = "SMG",
                Output_dir = "SyncMutationGuard",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder")],
                Folder_order = ["Folder"]
            };
            BMSPlaylist playlist = CreatePlaylist(songDbPath, synchronization, settings: testSettings);
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_mutation_guard"));

            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, exception.Message);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationModeLr2Db;
            testSettings.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2FolderSync_RethrowsCapabilityFailure()
    {
        bool previousOperationModeLr2Db = testSettings.OperationModeLR2DB;
        string previousOutputBaseDir = testSettings.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "Output");
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            var originalException = new InvalidOperationException("forced LR2 folder sync failure");
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            synchronization.Failure = originalException;
            var table = new BMSTable
            {
                playlist_id = 9002,
                name = "SyncCapabilityFailure",
                symbol = "SCF",
                Output_dir = "SyncCapabilityFailure",
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder")],
                Folder_order = ["Folder"]
            };
            BMSPlaylist playlist = CreatePlaylist(songDbPath, synchronization, settings: testSettings);
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_capability_failure"));

            Assert.AreSame(originalException, exception);
            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationModeLr2Db;
            testSettings.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_PassesOldAndNewEntrySnapshotsToCallback()
    {
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"SnapshotTable\",\r\n\"symbol\":\"S\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Snapshot Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = null!;
            var receiptFailure = new InvalidOperationException("receipt consumer failure");
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
                throw receiptFailure;
            };

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult operation = await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: _ => throw new InvalidOperationException("sync result callback failure"),
                publishReferenceReceipts: true);
            Assert.AreSame(receiptFailure, operation.Exception);
            Assert.AreSame(receipt.NewTable, operation.Single().ResultTable);

            Assert.IsNotNull(receipt);
            Assert.AreSame(table, receipt!.OldTable);
            Assert.IsNotNull(receipt.NewTable);
            Assert.IsNotNull(receipt.OldEntriesSnapshot);
            Assert.IsNotNull(receipt.NewEntriesSnapshot);
            Assert.AreEqual(1, receipt.OldEntriesSnapshot.Count);
            Assert.AreEqual(1, receipt.NewEntriesSnapshot.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", receipt.OldEntriesSnapshot[0].md5);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", receipt.NewEntriesSnapshot[0].md5);
            Assert.AreEqual(new string('b', 64), receipt.NewEntriesSnapshot[0].sha256);
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>未設定hashを実再取得・保存してもUpdatedと日時は進めず、手動名、新旧表の行所属と参照変更receiptを保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_HashInitializationPersistsWithoutUpdatedFlag()
    {
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"HashInitTable\",\r\n\"symbol\":\"H\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Hash Init Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.name = "Manual generic name";
            table.playlist_id = 900001;
            table.header_sha256 = null;
            table.data_sha256 = null;
            var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
            table.last_update = existingLastUpdate;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries)
                    {
                        db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                });
            }
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Hash Init Song\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Repaired Song\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = null!;
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
            };

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                reason: "test_hash_initialization",
                publishReferenceReceipts: true);

            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Updated);
            Assert.IsTrue(results[0].StatePersisted);
            Assert.IsNotNull(receipt);
            Assert.IsFalse(receipt!.Updated);
            Assert.IsTrue(receipt.ReferenceEntriesChanged);
            Assert.AreEqual(existingLastUpdate, results[0].ResultTable.last_update);
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.data_sha256));
            Assert.AreEqual(2, results[0].ResultTable.entries.Count(entry => !entry.is_removed));
            Assert.IsTrue(table.entries.All(entry => ReferenceEquals(table, entry.parent) && entry.playlist_id == table.playlist_id));
            Assert.IsTrue(results[0].ResultTable.entries.All(entry => ReferenceEquals(results[0].ResultTable, entry.parent) && entry.playlist_id == table.playlist_id));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == 900001);
            Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));
            Assert.AreEqual(existingLastUpdate, persisted.last_update);
            Assert.AreEqual("Manual generic name", persisted.name);
            Assert.AreEqual("Manual generic name", results[0].ResultTable.name);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 900001 AND is_removed = 0;"));
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実外部再取得でfolder投影を保存・LR2出力へ更新し、新旧表の行所属と採番IDを保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReoutputsLr2FolderProjectionAfterPersistedEntryChange()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalFolderProjection\",\r\n\"symbol\":\"E\",\r\n\"tag\":\"LEVEL \",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Projection Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings,
                synchronization, settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.playlist_id = 900002;
            table.Output_dir = "ExternalReloadProjection";
            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            playlist.ReOutputCustomFolder(table);

            string outputPath = Path.Combine(outputBaseDir, table.Output_dir, "0000.lr2folder");
            StringAssert.Contains(ReadShiftJisText(outputPath), "#TITLE LEVEL 1");
            using (LR2SongDBExtended verifyInitial = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("LEVEL 1", verifyInitial.Table<LR2SongDB.folder>().Single(row => row.path == outputPath).title);
            }
            synchronization.Operations.Clear();
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Projection Song\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                reason: "test_external_reload_custom_folder_projection");

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.IsTrue(results[0].StatePersisted);
            BMSTable reloadedTable = results[0].ResultTable;
            Assert.AreSame(reloadedTable, playlist.BMSTables.Single());
            Assert.IsTrue(table.entries.All(entry => ReferenceEquals(table, entry.parent) && entry.playlist_id == table.playlist_id));
            Assert.IsTrue(reloadedTable.entries.All(entry => ReferenceEquals(reloadedTable, entry.parent) && entry.playlist_id == table.playlist_id));
            Assert.AreEqual("LEVEL 2", reloadedTable.entries.Single(entry => !entry.is_removed).folder);
            string reloadedText = ReadShiftJisText(outputPath);
            StringAssert.Contains(reloadedText, "#TITLE LEVEL 2");
            Assert.IsFalse(reloadedText.Contains("#TITLE LEVEL 1", StringComparison.Ordinal));
            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
            using (LR2SongDBExtended verifyReloaded = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(
                    "LEVEL 2",
                    verifyReloaded.ExecuteScalar<string>(
                        "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                        900002,
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
                LR2SongDB.folder folderRow = verifyReloaded.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
                Assert.AreEqual("LEVEL 2", folderRow.title);
                StringAssert.Contains(folderRow.command, "playlist_entry");
            }

            synchronization.Operations.Clear();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult unchangedResults = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [reloadedTable],
                reason: "test_external_reload_custom_folder_projection_no_change");

            Assert.AreEqual(1, unchangedResults.Count);
            Assert.IsTrue(unchangedResults[0].Succeeded);
            Assert.IsFalse(unchangedResults[0].Updated);
            Assert.IsFalse(unchangedResults[0].StatePersisted);
            Assert.AreEqual(0, synchronization.Operations.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LocalPlaylistMutations_RejectBusyLeaseBeforeMutationAndRetryToConvergence()
    {
        foreach (LocalPlaylistMutationCase mutationCase in CreateLocalPlaylistMutationCases())
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            IDisposable? incumbent = null;
            try
            {
                string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
                string songDbPath = CreateTempSongDbPath(tempDirectory);
                PlaylistPersistenceRepository.EnsureSchema(songDbPath);
                CustomFolderOutputSettingsSnapshot outputSettings = CreateLocalMutationOutputSettings(tempDirectory, outputBaseDir, operationModeLr2Db: true);
                TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                    songDbPath,
                    CustomFolderOutputPhysicalSurface.Empty);
                var admission = new ChartFileOperationSynchronizer();
                var playlist = new TestBmsPlaylist(
                    songDbPath,
                    null,
                    null,
                    null,
                    () => new PlaylistUrlCompletionOptionsSnapshot(),
                    () => new BeatorajaBmtOptionsSnapshot(),
                    () => outputSettings,
                    synchronization,
                    mutationAdmission: admission, settings: testSettings);
                BMSTable table = CreateLocalMutationTable();
                playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
                playlist.CommitBMSTableWithEntriesToDB(table);
                playlist.ReOutputCustomFolder(table);
                synchronization.Operations.Clear();

                PlaylistModelSnapshot modelBefore = CapturePlaylistModel(table);
                byte[] databaseBefore = File.ReadAllBytes(songDbPath);
                Dictionary<string, byte[]> filesBefore = CaptureFileSurface(outputBaseDir);
                Assert.IsTrue(admission.TryEnter(out incumbent));

                Task<Exception?> busyCall = Task.Run(() => CaptureMutationFailure(mutationCase, playlist, table));
                Exception? busyException = await busyCall;
                Assert.IsNotNull(busyException, mutationCase.Name + " must reject while the lease is busy.");

                AssertPlaylistModelUnchanged(modelBefore, CapturePlaylistModel(table));
                CollectionAssert.AreEqual(databaseBefore, File.ReadAllBytes(songDbPath));
                AssertFileSurfaceUnchanged(filesBefore, CaptureFileSurface(outputBaseDir));
                Assert.AreEqual(0, synchronization.Operations.Count, mutationCase.Name + " must not reach LR2 output while admission is rejected.");

                incumbent.Dispose();
                incumbent = null;
                object retryResult = mutationCase.Invoke(playlist, table, true);
                Assert.IsTrue(mutationCase.WasApplied(retryResult), mutationCase.Name + " retry should apply the requested mutation.");

                CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
                AssertPersistedEntriesMatchModel(table, songDbPath);
                Assert.IsTrue(CaptureFileSurface(outputBaseDir).Count > 0, mutationCase.Name + " retry should leave an app-managed custom-folder surface.");
            }
            finally
            {
                incumbent?.Dispose();
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LocalPlaylistMutations_DoNotConsultLeaseForNoCommitOrNonLr2()
    {
        foreach (LocalPlaylistMutationCase mutationCase in CreateLocalPlaylistMutationCases())
        {
            foreach ((bool CommitFlag, bool OperationModeLr2Db) in new[]
            {
                (false, true),
                (true, false)
            })
            {
                string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);
                try
                {
                    string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
                    string songDbPath = CreateTempSongDbPath(tempDirectory);
                    PlaylistPersistenceRepository.EnsureSchema(songDbPath);
                    CustomFolderOutputSettingsSnapshot outputSettings = CreateLocalMutationOutputSettings(
                        tempDirectory,
                        outputBaseDir,
                        OperationModeLr2Db);
                    TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                        songDbPath,
                        CustomFolderOutputPhysicalSurface.Empty);
                    var admission = new ChartFileOperationSynchronizer();
                    var playlist = new TestBmsPlaylist(
                        songDbPath,
                        null,
                        null,
                        null,
                        () => new PlaylistUrlCompletionOptionsSnapshot(),
                        () => new BeatorajaBmtOptionsSnapshot(),
                        () => outputSettings,
                        synchronization,
                        mutationAdmission: admission, settings: testSettings);
                    BMSTable table = CreateLocalMutationTable();
                    playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
                    playlist.CommitBMSTableWithEntriesToDB(table);
                    PlaylistModelSnapshot modelBefore = CapturePlaylistModel(table);
                    byte[] databaseBefore = File.ReadAllBytes(songDbPath);
                    Dictionary<string, byte[]> filesBefore = CaptureFileSurface(outputBaseDir);

                    object result = mutationCase.Invoke(playlist, table, CommitFlag);
                    Assert.IsTrue(mutationCase.WasApplied(result), mutationCase.Name + " negative control should apply the in-memory mutation.");

                    Assert.IsFalse(
                        string.Equals(
                            string.Join("\u001f", modelBefore.EntryStates),
                            string.Join("\u001f", CapturePlaylistModel(table).EntryStates),
                            StringComparison.Ordinal),
                        mutationCase.Name + " negative control should change the model.");
                    AssertFileSurfaceUnchanged(filesBefore, CaptureFileSurface(outputBaseDir));
                    Assert.AreEqual(0, synchronization.Operations.Count);
                    if (!CommitFlag)
                    {
                        CollectionAssert.AreEqual(databaseBefore, File.ReadAllBytes(songDbPath));
                    }
                    else
                    {
                        AssertPersistedEntriesMatchModel(table, songDbPath);
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, recursive: true);
                    }
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReportsPlaylistSyncProgressSnapshots()
    {
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ProgressTable\",\r\n\"symbol\":\"P\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Progress Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });

            List<PlaylistSyncProgressSnapshot> snapshots = [];

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult updated = await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: null,
                progressCallback: snapshots.Add);

            Assert.IsNotNull(updated);
            Assert.IsTrue(snapshots.Count >= 3);
            Assert.IsTrue(snapshots.Any(s => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 0));
            Assert.IsTrue(snapshots.Any(s => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 1 && string.Equals(s.CurrentTableName, "ProgressTable", StringComparison.Ordinal)));
            PlaylistSyncProgressSnapshot playlistSyncProgressSnapshot = snapshots.Last();
            Assert.IsFalse(playlistSyncProgressSnapshot.IsActive);
            Assert.AreEqual(1, playlistSyncProgressSnapshot.TotalTableCount);
            Assert.AreEqual(1, playlistSyncProgressSnapshot.CompletedTableCount);
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReloadsOnlyExternalSyncTargets()
    {
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string externalHeaderPath = Path.Combine(tempDirectory, "external-header.json");
            string externalScorePath = Path.Combine(tempDirectory, "external-score.json");
            string manualHeaderPath = Path.Combine(tempDirectory, "manual-header.json");
            File.WriteAllBytes(externalHeaderPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalTarget\",\r\n\"symbol\":\"E\",\r\n\"data_url\":\"./external-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(externalScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(manualHeaderPath, CreateUtf8BomBytes("{ invalid json"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable externalTable = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(externalHeaderPath));
            externalTable.EnableExternalSync();
            var manualTable = new BMSTable
            {
                name = "ManualTarget",
                symbol = "M",
                Header_url = new Uri(manualHeaderPath)
            };
            manualTable.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { externalTable, manualTable });
            List<PlaylistSyncAttemptResult> syncResults = [];

            await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: syncResults.Add);

            Assert.AreEqual(1, syncResults.Count);
            Assert.AreSame(externalTable, syncResults[0].SourceTable);
            Assert.IsTrue(syncResults[0].Succeeded);
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReloadsExplicitTargetRegardlessOfExternalSyncFlag()
    {
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ManualTarget\",\r\n\"symbol\":\"M\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9001;
            table.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([table], reason: "test_explicit_reload");

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.IsFalse(results[0].ResultTable.is_external_sync);
            using (results[0].ResultTable.ReaderWriterLock.GetReaderGuard())
            {
                Assert.AreEqual(2, results[0].ResultTable.entries.Count(entry => !entry.is_removed));
            }
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadAndApplySingleTable_UsesExplicitUriOverride()
    {
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        string firstDirectory = Path.Combine(tempDirectory, "first");
        string secondDirectory = Path.Combine(tempDirectory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        try
        {
            string firstHeaderPath = Path.Combine(firstDirectory, "header.json");
            string firstScorePath = Path.Combine(firstDirectory, "score.json");
            string secondHeaderPath = Path.Combine(secondDirectory, "header.json");
            string secondScorePath = Path.Combine(secondDirectory, "score.json");
            const string headerJson = "{\r\n\"name\":\"ExplicitUriTarget\",\r\n\"symbol\":\"E\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}";
            File.WriteAllBytes(firstHeaderPath, CreateUtf8BomBytes(headerJson));
            File.WriteAllBytes(secondHeaderPath, CreateUtf8BomBytes(headerJson));
            File.WriteAllBytes(firstScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(secondScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(firstHeaderPath));
            table.playlist_id = 9002;
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult operation = await playlist.ExternalSyncOwner.ReloadAndApplySingleTableAsync(
                table,
                new Uri(secondHeaderPath),
                "test_explicit_uri");

            operation.ThrowIfFailed();
            BMSTable result = operation.Single().ResultTable;
            using (result.ReaderWriterLock.GetReaderGuard())
            {
                Assert.AreEqual(2, result.entries.Count(entry => !entry.is_removed));
            }
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_AwaitsUiReplacementCompletionBeforeApplying(bool recommendation)
    {
        (string tempDirectory, TestBmsPlaylist playlist, BMSTable table, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending, recommendation);
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? reloadTask = null;
        Task? competingRestore = null;
        using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
        try
        {
            reloadTask =
                playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [table],
                    reason: "test_ui_replacement_completion");

            Task firstTerminal = await Task.WhenAny(scheduler.Scheduled, reloadTask);
            Assert.AreSame(scheduler.Scheduled, firstTerminal);
            await scheduler.Scheduled;
            Assert.IsFalse(reloadTask.IsCompleted);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Assert.AreEqual(0, scheduler.ExecutionCount);
            Assert.IsTrue(notifications.TakeReceipt().IsEmpty);
            // U2-R2: 個別 reload の実 pending publication がある間、復元は DB 更新前に拒否する。
            competingRestore = playlist.RestorePlaylistDumpAsync(
                PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(77, "Rejected restore", "X"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                competingRestore);
            using (var db = new LR2SongDBExtended(Path.Combine(tempDirectory, "song.db")))
            {
                Assert.AreEqual(table.playlist_id, db.Table<BMSTable>().Single().playlist_id);
            }

            string[] persistedEntries;
            using (LR2SongDBExtended before = new BmsLibraryDbGateway(Path.Combine(tempDirectory, "song.db")).OpenSongDbReadOnly())
            {
                persistedEntries = before.Table<LR2SongDBExtended.playlist_entry>().Select(entry => entry.md5 + ":" + entry.title).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            }
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Task.Run(() => playlist.RemoveBMSTable(table)));
            using (LR2SongDBExtended afterRejection = new BmsLibraryDbGateway(Path.Combine(tempDirectory, "song.db")).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, afterRejection.Table<BMSTable>().Count());
                CollectionAssert.AreEqual(persistedEntries,
                    afterRejection.Table<LR2SongDBExtended.playlist_entry>().Select(entry => entry.md5 + ":" + entry.title).OrderBy(value => value, StringComparer.Ordinal).ToArray());
            }
            scheduler.ReleasePending();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await reloadTask;

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.AreEqual(1, scheduler.ExecutionCount);
            Assert.AreSame(results[0].ResultTable, playlist.BMSTables.Single());
            Assert.AreNotSame(table, results[0].ResultTable);
            Assert.AreEqual(recommendation ? 1 : 0, notifications.TakeReceipt().Notifications.Count);
            if (recommendation)
            {
                Assert.IsFalse(results[0].Updated);
                Assert.AreEqual(table.last_update, results[0].ResultTable.last_update);
            }
            playlist.RemoveBMSTable(results[0].ResultTable);
            using (LR2SongDBExtended after = new BmsLibraryDbGateway(Path.Combine(tempDirectory, "song.db")).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0, after.Table<BMSTable>().Count());
                Assert.AreEqual(0, after.Table<LR2SongDBExtended.playlist_entry>().Count());
            }
        }
        finally
        {
            if (reloadTask != null && !reloadTask.IsCompleted)
            {
                try
                {
                    scheduler.ReleasePending();
                    await reloadTask;
                }
                catch
                {
                }
            }
            if (competingRestore != null)
            {
                try { await competingRestore; }
                catch (InvalidOperationException) { }
            }
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    // U2-R4/R2: 既存 operation scheduler で UI rejection / cancel / fault を区別し、DB 保持と予約解放を確認する。
    [DataTestMethod]
    [DataRow("Rejected")]
    [DataRow("Aborted")]
    [DataRow("Faulted")]
    [TestCategory("Playlist")]
    public async Task PlaylistRestore_UiTerminalFailureKeepsDatabaseAndReleasesReservation(string outcomeName)
    {
        UiScheduleOutcome outcome = Enum.Parse<UiScheduleOutcome>(outcomeName);
        (string directory, TestBmsPlaylist playlist, BMSTable oldTable, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(outcome);
        bool previousUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        bool previousLr2Mode = testSettings.OperationModeLR2DB;
        testSettings.OperationModeLR2DB = false;
        playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
        try
        {
            Exception? failure = null;
            try
            {
                await playlist.RestorePlaylistDumpAsync(
                    PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(77, "Committed despite UI failure", "R"));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Assert.IsNotNull(failure);
            Assert.AreSame(oldTable, playlist.BMSTables.Single());
            Assert.AreEqual(0, scheduler.ExecutionCount);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(Path.Combine(directory, "song.db")).OpenSongDbReadOnly())
            {
                Assert.AreEqual(77, verify.Table<BMSTable>().Single().playlist_id);
            }
            // 独立した次の復元を受理する。失敗操作自身の repost / retry ではない。
            await playlist.RestorePlaylistDumpAsync(
                PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(88, "Next independent restore", "N"));
            Assert.AreEqual(88, playlist.BMSTables.Single().playlist_id);
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousUrlCompletion;
            testSettings.OperationModeLR2DB = previousLr2Mode;
            Directory.Delete(directory, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsRejectedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Rejected,
            typeof(InvalidOperationException),
            "rejected", recommendation);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsAbortedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Aborted,
            typeof(OperationCanceledException),
            "aborted", recommendation);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsFaultedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Faulted,
            typeof(InvalidOperationException),
            "faulted", recommendation);

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_CommunicationKeepsPAndRejectsRemovalWithoutReplay()
    {
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        SingleRequestHttpServer? server = null;
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? reloadTask = null;
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"RemovedTarget\",\r\n\"symbol\":\"R\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));
            table.playlist_id = 9021;
            table.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries ?? [])
                {
                    setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            server = new SingleRequestHttpServer(CreateUtf8BomBytes(
                "{\"name\":\"RemovedTarget\",\"symbol\":\"R\",\"data_url\":\"" + new Uri(scoreJsonPath).AbsoluteUri + "\",\"level_order\":[1]}"), holdBody: true);
            table.Page_url = server.Address;
            int syncResultCount = 0;
            reloadTask = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                _ => syncResultCount++,
                reason: "test_removed_target",
                requireCurrentTargetForApply: true);
            await Task.WhenAny(server.HeadersSent.Task, reloadTask);
            if (!server.HeadersSent.Task.IsCompleted) { await reloadTask; Assert.Fail("実通信へ到達しませんでした。"); }
            Assert.IsFalse(reloadTask.IsCompleted);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _));
            Assert.ThrowsException<InvalidOperationException>(() => playlist.RemoveBMSTable(table));
            using (LR2SongDBExtended before = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1L, before.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist WHERE playlist_id = 9021;"));
                Assert.AreEqual(1L, before.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 9021;"));
            }
            server.ReleaseBody.TrySetResult();

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await reloadTask;

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.AreEqual(2, results[0].ResultTable.entries.Count);
            Assert.AreEqual(1, playlist.BMSTables.Count, "拒否した削除を後で自動実行しません。");
            playlist.RemoveBMSTable(results[0].ResultTable);
            Assert.AreEqual(1, syncResultCount);
            Assert.AreEqual(0, playlist.BMSTables.Count);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist WHERE playlist_id = 9021;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 9021;"));
        }
        finally
        {
            server?.ReleaseBody.TrySetResult();
            if (reloadTask != null && !reloadTask.IsCompleted)
            {
                try
                {
                    await reloadTask;
                }
                catch
                {
                }
            }
            if (server != null) { await server.DisposeAsync(); }
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private async Task AssertUiReplacementFailureAsync(
        UiScheduleOutcome outcome,
        Type expectedInnerExceptionType,
        string reason, bool recommendation)
    {
        (string tempDirectory, TestBmsPlaylist playlist, BMSTable table, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(outcome, recommendation);
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
        try
        {
            Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult> reloadTask =
                playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [table],
                    reason: "test_ui_replacement_" + reason);

            Task firstTerminal = await Task.WhenAny(scheduler.Scheduled, reloadTask);
            Assert.AreSame(scheduler.Scheduled, firstTerminal);
            await scheduler.Scheduled;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await reloadTask;

            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Succeeded);
            Assert.AreSame(table, results[0].ResultTable);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Exception failure = results[0].Exception
                ?? throw new AssertFailedException("The UI replacement failure was not reported.");
            Assert.IsInstanceOfType(failure, typeof(PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException));
            Assert.IsNotNull(failure.InnerException);
            Assert.IsInstanceOfType(failure.InnerException, expectedInnerExceptionType);
            StringAssert.Contains(failure.InnerException!.Message, reason);
            Assert.AreEqual(0, scheduler.ExecutionCount);
            Assert.IsTrue(notifications.TakeReceipt().IsEmpty);
            BMSTable persisted = new PlaylistPersistenceRepository(Path.Combine(tempDirectory, "song.db"))
                .LoadPlaylistHeaders().Single();
            Assert.AreEqual(table.name, persisted.name);
            Assert.AreEqual(table.org_name, persisted.org_name);
            Assert.AreEqual(table.header_sha256, persisted.header_sha256);
            Assert.AreEqual(table.data_sha256, persisted.data_sha256);
            Assert.AreEqual(table.last_update, persisted.last_update);
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private async Task<(string TempDirectory, TestBmsPlaylist Playlist, BMSTable Table, ControlledUiScheduler Scheduler)> CreateUiReplacementReloadFixtureAsync(
        UiScheduleOutcome outcome, bool recommendation = false)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(
                headerJsonPath,
                CreateUtf8BomBytes(
                    "{\r\n\"name\":\"AsyncReplacementTable\",\r\n\"symbol\":\"A\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(
                scoreJsonPath,
                CreateUtf8BomBytes(
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var scheduler = new ControlledUiScheduler(TestUiDispatcherHost.Dispatcher);
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = true },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty),
                recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput("standard")), uiScheduler: scheduler, settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(
                recommendation ? new Uri("bmseeker:table.recommended") : new Uri(headerJsonPath));
            if (recommendation)
            {
                table.name = "Manual recommendation ★999";
                table.org_name = "旧方針 ★3.87";
                table.header_sha256 = null;
                table.data_sha256 = null;
                table.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            }
            table.playlist_id = 9051;
            table.DisableExternalSync();
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries ?? [])
                    {
                        setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                });
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            File.WriteAllBytes(
                scoreJsonPath,
                CreateUtf8BomBytes(
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            scheduler.Arm(outcome);
            return (tempDirectory, playlist, table, scheduler);
        }
        catch
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            throw;
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceManualResync_UsesActiveTableAndPublishesLifecycle()
    {
        bool previousEnablePlaylistUrlCompletion = testSettings.EnablePlaylistUrlCompletion;
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "workspace-header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "workspace-score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"WorkspaceTarget\",\r\n\"symbol\":\"W\",\r\n\"data_url\":\"./workspace-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9011;
            table.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries ?? [])
                {
                    setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            var lifecycleLogs = new List<string>();
            var failureLogs = new List<(Exception Exception, string Message)>();
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
                () => playlist,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => library,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                message => lifecycleLogs.Add(message),
                (exception, message) => failureLogs.Add((exception, message)), (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
            workspace.ConfigureCatalogNotificationQueue(action => action());
            PlaylistWorkspaceTestPorts.AttachImmediatePlaylistPresentationRouter(workspace);
            workspace.RefreshPlaylistTreeTables(playlist);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            int referenceSortInvalidationCount = 0;
            long summaryDataGenerationBeforeResync = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder(string.Empty));
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => referenceSortInvalidationCount++;
            Assert.IsTrue(workspace.ContainsActivePlaylistTable(table));
            Assert.IsTrue(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = table }]));

            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            BMSTable firstSelectionTarget = await workspace.ResyncPlaylistTableAsync(table);

            Assert.AreEqual(
                1,
                lifecycleLogs.Count(log => log.StartsWith(
                    "playlist_reload_operation started operationKind=single reason=manual_resync tableCount=1",
                    StringComparison.Ordinal)));
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            progress.Clear();
            Assert.AreEqual(1, referenceSortInvalidationCount);
            Assert.IsTrue(lifecycleLogs.Any(log => log.StartsWith(
                "playlist_reload_operation completed operationKind=single reason=manual_resync tableCount=1 processedCount=1",
                StringComparison.Ordinal)));
            Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > summaryDataGenerationBeforeResync);

            BMSTable reloadedTable = playlist.BMSTables.Single();
            Assert.AreNotSame(table, reloadedTable);
            Assert.AreSame(reloadedTable, firstSelectionTarget);
            PlaylistDetailSelection reloadedSelection = workspace.CapturePlaylistDetailSelection()
                ?? throw new AssertFailedException("Replacement detail selection was not retained.");
            Assert.AreSame(reloadedTable, reloadedSelection.Table);
            Assert.IsFalse(workspace.ContainsActivePlaylistTable(table));
            Assert.IsFalse(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = table }]));
            Assert.IsTrue(workspace.ContainsActivePlaylistTable(reloadedTable));
            Assert.IsTrue(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = reloadedTable }]));

            reloadedTable.header_sha256 = null;
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"WorkspaceTarget\",\r\n\"symbol\":\"W\",\r\n\"tag\":\"header-refresh\",\r\n\"data_url\":\"./workspace-score.json\",\r\n\"level_order\":[1]\r\n}"));
            BMSTable secondSelectionTarget = await workspace.ResyncPlaylistTableAsync(reloadedTable);
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            progress.Clear();
            Assert.AreEqual(2, referenceSortInvalidationCount);
            BMSTable headerRefreshedTable = playlist.BMSTables.Single();
            Assert.AreNotSame(reloadedTable, headerRefreshedTable);
            Assert.AreSame(headerRefreshedTable, secondSelectionTarget);
            Assert.AreSame(headerRefreshedTable, workspace.CapturePlaylistDetailSelection().Table);
            Assert.IsFalse(workspace.ContainsActivePlaylistTable(reloadedTable));
            Assert.IsTrue(workspace.ContainsActivePlaylistTable(headerRefreshedTable));

            Assert.AreEqual(
                2,
                lifecycleLogs.Count(log => log.StartsWith(
                    "playlist_reload_operation started operationKind=single reason=manual_resync tableCount=1",
                    StringComparison.Ordinal)));

            headerRefreshedTable.header_sha256 = null;
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{"));
            Uri failureUri = headerRefreshedTable.Page_url ?? headerRefreshedTable.Header_url;
            BMSTable failureSelectionTarget = await workspace.ResyncPlaylistTableAsync(headerRefreshedTable);
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            Assert.AreEqual(1, failureLogs.Count);
            Assert.AreEqual(2, referenceSortInvalidationCount);
            Assert.AreSame(headerRefreshedTable, workspace.CapturePlaylistDetailSelection().Table);
            Assert.AreSame(headerRefreshedTable, failureSelectionTarget);
            Assert.IsNotNull(failureLogs[0].Exception);
            Assert.AreEqual(
                "playlist_manual_resync_failed table=WorkspaceTarget uri=" + failureUri,
                failureLogs[0].Message);
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void AssertManualResyncProgress(ConcurrentQueue<PlaylistSyncProgressSnapshot> progress, Uri targetUri)
    {
        PlaylistSyncProgressSnapshot[] notifications = progress.ToArray();
        Assert.IsTrue(notifications.Length > 0);
        Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "playlist_manual_reload"));
        Assert.IsTrue(notifications.All(snapshot => snapshot.OperationId == 0));
        Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive
            && snapshot.TotalTableCount == 1 && snapshot.CurrentUri == targetUri));
        Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CompletedTableCount == 1));
        Assert.IsFalse(notifications[^1].IsActive);
    }

    private static IReadOnlyList<LocalPlaylistMutationCase> CreateLocalPlaylistMutationCases()
    {
        return
        [
            new LocalPlaylistMutationCase(
                "rename-folder",
                (playlist, table, commitFlag) => playlist.RenameFolderBMSTable(table, "Folder A", "Folder B", commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "remove-folder",
                (playlist, table, commitFlag) => playlist.RemoveFolderBMSTable(table, "Folder A", commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "create-folder",
                (playlist, table, commitFlag) => playlist.CreateNewFolderBMSTable(table, "Folder B", commitFlag),
                result => result is string folderName && !string.IsNullOrWhiteSpace(folderName)),
            new LocalPlaylistMutationCase(
                "add-entries-to-folder",
                (playlist, table, commitFlag) => playlist.AddPlaylistEntriesToFolderBMSTable(
                    [table.entries.Single(entry => entry.md5 == LocalMutationSecondHash)],
                    table,
                    "Folder B",
                    commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "remove-entries",
                (playlist, table, commitFlag) => playlist.RemoveEntriesBMSTable(
                    [table.entries.Single(entry => entry.md5 == LocalMutationSecondHash)],
                    table,
                    commitFlag),
                result => result is true)
        ];
    }

    private static CustomFolderOutputSettingsSnapshot CreateLocalMutationOutputSettings(
        string tempDirectory,
        string outputBaseDirectory,
        bool operationModeLr2Db)
    {
        return new CustomFolderOutputSettingsSnapshot
        {
            OperationModeLR2DB = operationModeLr2Db,
            LR2RootPath = tempDirectory,
            LR2CustomFolderOutputBaseDir = outputBaseDirectory,
            LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootCustomFolder"),
            LR2CustomFolderAdditionalOutputBaseDirs = "[]"
        };
    }

    private static BMSTable CreateLocalMutationTable()
    {
        BMSTableEntry firstEntry = CreateEntry(LocalMutationFirstHash, "Folder A");
        firstEntry.folder = "Folder A";
        firstEntry.level = 1;
        firstEntry.title = "Local admission first";
        BMSTableEntry secondEntry = CreateEntry(LocalMutationSecondHash, "Folder A");
        secondEntry.folder = "Folder A";
        secondEntry.level = 2;
        secondEntry.title = "Local admission second";
        return new BMSTable
        {
            playlist_id = 990001,
            name = "LocalAdmissionTable",
            symbol = "LAT",
            Output_dir = "LocalAdmissionOutput",
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
            entries = [firstEntry, secondEntry],
            Folder_order = ["Folder A"]
        };
    }

    private static Exception? CaptureMutationFailure(
        LocalPlaylistMutationCase mutationCase,
        TestBmsPlaylist playlist,
        BMSTable table)
    {
        try
        {
            mutationCase.Invoke(playlist, table, true);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static PlaylistModelSnapshot CapturePlaylistModel(BMSTable table)
    {
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return new PlaylistModelSnapshot(
                table.last_update,
                [.. (table.Folder_order ?? [])],
                [.. (table.entries ?? []).Select(CreateEntryState)]);
        }
    }

    private static void AssertPlaylistModelUnchanged(
        PlaylistModelSnapshot expected,
        PlaylistModelSnapshot actual)
    {
        Assert.AreEqual(expected.LastUpdate, actual.LastUpdate);
        CollectionAssert.AreEqual(expected.FolderOrder.ToArray(), actual.FolderOrder.ToArray());
        CollectionAssert.AreEqual(expected.EntryStates.ToArray(), actual.EntryStates.ToArray());
    }

    private static void AssertPersistedEntriesMatchModel(BMSTable table, string songDbPath)
    {
        string[] expected;
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            expected = [.. (table.entries ?? []).Select(CreateEntryState).OrderBy(state => state, StringComparer.Ordinal)];
        }

        using var db = new LR2SongDBExtended(songDbPath);
        string[] actual =
        [
            .. db.Table<LR2SongDBExtended.playlist_entry>()
                .Where(entry => entry.playlist_id == table.playlist_id)
                .Select(CreatePersistedEntryState)
                .OrderBy(state => state, StringComparer.Ordinal)
        ];
        CollectionAssert.AreEqual(expected, actual);
    }

    private static string CreateEntryState(BMSTableEntry entry)
    {
        return string.Join(
            "\u001f",
            entry?.md5 ?? string.Empty,
            entry?.sha256 ?? string.Empty,
            entry?.folder ?? string.Empty,
            entry?.is_removed == true ? "1" : "0");
    }

    private static string CreatePersistedEntryState(LR2SongDBExtended.playlist_entry entry)
    {
        return string.Join(
            "\u001f",
            entry?.md5 ?? string.Empty,
            entry?.sha256 ?? string.Empty,
            entry?.folder ?? string.Empty,
            entry?.is_removed == true ? "1" : "0");
    }

    private static Dictionary<string, byte[]> CaptureFileSurface(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(rootDirectory, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertFileSurfaceUnchanged(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (string path in expected.Keys)
        {
            CollectionAssert.AreEqual(expected[path], actual[path], path);
        }
    }

    private const string LocalMutationFirstHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string LocalMutationSecondHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class LocalPlaylistMutationCase
    {
        internal LocalPlaylistMutationCase(
            string name,
            Func<TestBmsPlaylist, BMSTable, bool, object> invoke,
            Func<object, bool> wasApplied)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
            WasApplied = wasApplied ?? throw new ArgumentNullException(nameof(wasApplied));
        }

        internal string Name { get; }

        internal Func<TestBmsPlaylist, BMSTable, bool, object> Invoke { get; }

        internal Func<object, bool> WasApplied { get; }
    }

    private sealed class PlaylistModelSnapshot
    {
        internal PlaylistModelSnapshot(
            DateTime lastUpdate,
            IReadOnlyList<string> folderOrder,
            IReadOnlyList<string> entryStates)
        {
            LastUpdate = lastUpdate;
            FolderOrder = folderOrder ?? [];
            EntryStates = entryStates ?? [];
        }

        internal DateTime LastUpdate { get; }

        internal IReadOnlyList<string> FolderOrder { get; }

        internal IReadOnlyList<string> EntryStates { get; }
    }

    private enum UiScheduleOutcome
    {
        AcceptedPending,
        Rejected,
        Aborted,
        Faulted
    }

    private sealed class ControlledUiScheduler : IUiScheduler
    {
        private readonly TestUiScheduler inner;

        private readonly object synchronization = new();

        private readonly TaskCompletionSource<IUiScheduledOperation> scheduled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private UiScheduleOutcome? armedOutcome;

        private PendingUiScheduledOperation? pendingOperation;

        internal ControlledUiScheduler(Dispatcher dispatcher)
        {
            inner = new TestUiScheduler(() => dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));
        }

        internal Task<IUiScheduledOperation> Scheduled => scheduled.Task;

        internal int ExecutionCount
        {
            get
            {
                lock (synchronization)
                {
                    return pendingOperation?.ExecutionCount ?? 0;
                }
            }
        }

        internal void Arm(UiScheduleOutcome outcome)
        {
            lock (synchronization)
            {
                if (armedOutcome.HasValue)
                {
                    throw new InvalidOperationException("The controlled UI scheduler is already armed.");
                }
                armedOutcome = outcome;
            }
        }

        internal void ReleasePending()
        {
            PendingUiScheduledOperation operation;
            lock (synchronization)
            {
                operation = pendingOperation
                    ?? throw new InvalidOperationException("No pending UI operation is available.");
            }

            IUiScheduledOperation dispatch = inner.Schedule(operation.Run);
            if (dispatch == null || !dispatch.IsAccepted)
            {
                operation.Fail(new InvalidOperationException(
                    "The controlled UI scheduler could not release the pending operation."));
            }
        }

        /// <summary>実置換actionをUI上で完了し、scheduler完了Taskだけは解放せず保持します。</summary>
        internal Task ApplyPendingWithoutCompletionAsync()
        {
            PendingUiScheduledOperation operation;
            lock (synchronization)
            {
                operation = pendingOperation
                    ?? throw new InvalidOperationException("No pending UI operation is available.");
            }
            return inner.InvokeAsync(operation.RunWithoutCompletion);
        }

        /// <summary>保持したscheduler完了Taskを解放します。未到達のcleanupでは何もしません。</summary>
        internal void CompletePending()
        {
            lock (synchronization) { pendingOperation?.Complete(); }
        }

        public bool IsAvailable => inner.IsAvailable;

        public bool CanExecuteInline => inner.CanExecuteInline;

        public bool CheckAccess() => inner.CheckAccess();

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            UiScheduleOutcome? outcome;
            lock (synchronization)
            {
                outcome = armedOutcome;
                armedOutcome = null;
            }
            if (!outcome.HasValue)
            {
                return inner.Schedule(action, priority);
            }

            IUiScheduledOperation operation = outcome.Value switch
            {
                UiScheduleOutcome.AcceptedPending => CreatePendingOperation(action),
                UiScheduleOutcome.Rejected => new CompletedUiScheduledOperation(
                    accepted: false,
                    aborted: true,
                    completion: Task.CompletedTask,
                    rejectionReason: "controlled scheduler rejection"),
                UiScheduleOutcome.Aborted => new CompletedUiScheduledOperation(
                    accepted: true,
                    aborted: true,
                    completion: Task.FromCanceled(new CancellationToken(canceled: true)),
                    rejectionReason: "controlled scheduler abort"),
                UiScheduleOutcome.Faulted => new CompletedUiScheduledOperation(
                    accepted: true,
                    aborted: false,
                    completion: Task.FromException(new InvalidOperationException("controlled scheduler faulted")),
                    rejectionReason: null),
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            };
            scheduled.TrySetResult(operation);
            return operation;
        }

        private IUiScheduledOperation CreatePendingOperation(Action action)
        {
            var operation = new PendingUiScheduledOperation(action);
            lock (synchronization)
            {
                pendingOperation = operation;
            }
            return operation;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.Invoke(action, priority);

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.Invoke(action, priority);

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.InvokeAsync(action, priority);

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.InvokeAsync(action, priority);
    }

    private sealed class PendingUiScheduledOperation : IUiScheduledOperation
    {
        private readonly Action action;

        private readonly TaskCompletionSource<object?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int executionCount;

        private int aborted;

        internal PendingUiScheduledOperation(Action action)
        {
            this.action = action ?? throw new ArgumentNullException(nameof(action));
        }

        internal int ExecutionCount => Volatile.Read(ref executionCount);

        public bool IsAccepted => true;

        public bool IsCompleted => completion.Task.IsCompleted;

        public bool IsAborted => Volatile.Read(ref aborted) != 0;

        public string? RejectionReason => null;

        public Task Completion => completion.Task;

        public void Abort()
        {
            if (Interlocked.Exchange(ref aborted, 1) == 0)
            {
                completion.TrySetCanceled();
            }
        }

        internal void Run() => Run(complete: true);

        /// <summary>実actionの実行だけを完了し、完了Taskの解放を呼出元へ残します。</summary>
        internal void RunWithoutCompletion() => Run(complete: false);

        /// <summary>実action後に保持された完了Taskを解放します。</summary>
        internal void Complete() => completion.TrySetResult(null);

        private void Run(bool complete)
        {
            if (IsAborted || Interlocked.Increment(ref executionCount) != 1)
            {
                return;
            }
            try
            {
                action();
                if (complete) { Complete(); }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        internal void Fail(Exception exception)
        {
            completion.TrySetException(exception ?? throw new ArgumentNullException(nameof(exception)));
        }
    }

    private sealed class CompletedUiScheduledOperation : IUiScheduledOperation
    {
        private readonly bool accepted;

        private readonly bool aborted;

        internal CompletedUiScheduledOperation(
            bool accepted,
            bool aborted,
            Task completion,
            string? rejectionReason)
        {
            this.accepted = accepted;
            this.aborted = aborted;
            Completion = completion ?? throw new ArgumentNullException(nameof(completion));
            RejectionReason = rejectionReason;
        }

        public bool IsAccepted => accepted;

        public bool IsCompleted => Completion.IsCompleted;

        public bool IsAborted => aborted;

        public string? RejectionReason { get; }

        public Task Completion { get; }

        public void Abort()
        {
        }
    }

}
