using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.PlaylistWorkspaceFixtureFactory;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;

/// <summary>外部表取込みの実受付・登録・通知とsettingsへのcatalog配送を確認します。</summary>
/// <remarks>通信中の受付と実DB・BMT・configの一括反映はplaylistとworkspaceの実接続が対象です。個別JSON・BMT内容は下位parser/exportケース、実Window配送はMainWindowケースへ分担します。</remarks>
[TestClass]
public sealed class PlaylistWorkspaceExternalSourceTests
{
    /// <summary>既存表の実tableURL取込みをDB・本番サマリー・configまで通し、入力順の先頭配置と残りの相対順、BMT非書換えを保証します。</summary>
    [TestMethod]
    public async Task BeatorajaTableUrlImport_ExistingTablesKeepInputFrontOrderAndRemainingRelativeOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        PlaylistWorkspaceViewModel? workspace = null;
        bool started = false;
        TaskCompletionSource<BeatorajaTableUrlImportSummary> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string db = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(db);
            string root = Path.Combine(directory, "beatoraja");
            string output = Path.Combine(root, "table");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
            string config = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(config, "{\"tablepath\":\"table\",\"tableURL\":[]}");
            var options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true };
            BMSTable[] tables = Enumerable.Range(1, 3).Select(id => new BMSTable
            {
                playlist_id = id,
                name = "Table " + id,
                bmt_sort = id,
                is_external_sync = true,
                Page_url = new Uri("https://example.com/table" + id),
                last_update = new DateTime(2024, 1, 1),
                Folder_order = ["Folder"],
                entries = [new BMSTableEntry { title = "Song " + id, md5 = new string((char)('a' + id), 32), folder = "Folder" }]
            }).ToArray();
            playlist = new TestBmsPlaylist(db, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => options, () => new CustomFolderOutputSettingsSnapshot(),
                BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(db, CustomFolderOutputPhysicalSurface.Empty))
            { BMSTables = new ObservableCollection<BMSTable>(tables) };
            playlist.CommitBMSTablesWithEntriesToDB(tables);
            await playlist.BmtOutput.ExportAllAsync("import-initial");
            string[] files = Directory.GetFiles(output);
            Dictionary<string, byte[]> bytes = files.ToDictionary(path => path, File.ReadAllBytes);
            Dictionary<string, DateTime> times = files.ToDictionary(path => path, File.GetLastWriteTimeUtc);
            var library = new TestBmsLibrary(db);
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables));
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.IsPlaylistSummaryMode = true;
            workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.BmtSort), System.ComponentModel.ListSortDirection.Ascending);
            workspace.ApplyPlaylistSummaryDataRefresh(deferred: false, rebuildAsync: false);
            workspace.BeatorajaTableUrlImportConfirmationRequested += (_, request) => request.Confirmed = true;
            workspace.BeatorajaTableUrlImportNotificationRequested += (_, request) => completed.TrySetException(new InvalidOperationException(request.Message));
            workspace.BeatorajaTableUrlImportSummaryReady += (_, request) => completed.TrySetResult(request.Summary);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            // 二表の入力順と、次の一表取込みで残る二表の相対順を同じ三表で確認する。
            foreach (int[] input in new[] { new[] { 3, 1 }, new[] { 2 } })
            {
                completed = new TaskCompletionSource<BeatorajaTableUrlImportSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
                var document = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
                document["tableURL"] = new Newtonsoft.Json.Linq.JArray(input.Select(id => "https://example.com/table" + id));
                File.WriteAllText(config, document.ToString());
                started = true;
                workspace.StartBeatorajaTableUrlImport(root);
                await TestUiDispatcherHost.AwaitNotificationAsync(completed.Task, playlist.WaitForPlaylistMutationIdleAsync(), "existing BMT URL import summary");
                BeatorajaTableUrlImportSummary summary = await completed.Task;
                await playlist.WaitForPlaylistMutationIdleAsync();
                Assert.AreEqual(0, summary.FailedCount);
                Assert.AreEqual(input.Length, summary.Outcomes.Count);
                Assert.IsTrue(summary.Outcomes.All(outcome => outcome.Kind == BeatorajaTableUrlImportOutcomeKind.Existing));
                int[] expected = input.Length == 2 ? [3, 1, 2] : [2, 3, 1];
                using (LR2SongDBExtended verify = new BmsLibraryDbGateway(db).OpenSongDbReadOnly())
                {
                    CollectionAssert.AreEqual(expected.Select(id => (int?)id).ToArray(), verify.Table<BMSTable>().OrderBy(table => table.bmt_sort).Select(table => table.playlist_id).ToArray());
                }
                CollectionAssert.AreEqual(expected.Select(id => (int?)id).ToArray(), workspace.PlaylistSummaryView.Select(row => row.PlaylistId).ToArray());
                CollectionAssert.AreEqual(expected.Select(id => "https://example.com/table" + id).ToArray(), BeatorajaConfigService.ReadTableUrls(root).ToArray());
                foreach (string file in files)
                {
                    CollectionAssert.AreEqual(bytes[file], File.ReadAllBytes(file));
                    Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
                }
            }
        }
        finally
        {
            try
            {
                if (started && playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }
            }
            finally
            {
                try
                {
                    if (workspace != null)
                    {
                        workspace.StopPlaylistSummaryDataBuild();
                        await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                    }
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
        }
    }

    /// <summary>URIなし・空行混在の入力は有効URIなしと不正行だけを返し、trim後の不正行を失わず取込みを開始しません。</summary>
    [TestMethod]
    public void SubmitExternalPlaylistUriText_AllInvalidReturnsValidationFactsWithoutEnqueueing()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);

        ExternalPlaylistUriSubmissionResult result = workspace.SubmitExternalPlaylistUriText("  not-a-uri  \r\n\t");

        Assert.IsFalse(result.HasValidUris);
        Assert.IsTrue(result.HasInvalidLines);
        CollectionAssert.AreEqual(new[] { "not-a-uri" }, result.InvalidLines.ToList());
    }

    /// <summary>二つの有効URIと不正行を一要求にまとめ、実HTTP本文待ち中は追加受付を拒否し、入力順の取込み結果と進捗終了を返します。</summary>
    [TestMethod]
    public async Task SubmitExternalPlaylistUriText_OneRequestHoldsAdmissionDuringCommunicationAndCompletesAllInputs()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        SingleRequestHttpServer? server = null;
        TestBmsPlaylist? acceptedPlaylist = null;
        var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool submitted = false;
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string firstDataPath = Path.Combine(tempDirectory, "first-data.json");
            string secondHeaderPath = Path.Combine(tempDirectory, "second.json");
            string secondDataPath = Path.Combine(tempDirectory, "second-data.json");
            File.WriteAllText(firstDataPath, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"First song\",\"artist\":\"Artist\",\"level\":\"1\"}]");
            File.WriteAllText(secondHeaderPath, "{\"name\":\"SecondImport\",\"symbol\":\"S\",\"output_dir\":\"SecondImport\",\"data_url\":\"./second-data.json\"}");
            File.WriteAllText(secondDataPath, "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Second song\",\"artist\":\"Artist\",\"level\":\"2\"}]");

            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot())
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            acceptedPlaylist = playlist;
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, request) =>
                summaryReady.TrySetResult(request.Summary);

            server = new SingleRequestHttpServer(BmsPlaylistTestSupport.CreateUtf8BomBytes(
                "{\"name\":\"FirstImport\",\"symbol\":\"F\",\"output_dir\":\"FirstImport\",\"data_url\":\"" + new Uri(firstDataPath).AbsoluteUri + "\"}"), holdBody: true);
            string firstUri = server.Address.AbsoluteUri;
            string secondUri = new Uri(secondHeaderPath).AbsoluteUri;
            ExternalPlaylistUriSubmissionResult submission = workspace.SubmitExternalPlaylistUriText(
                firstUri + "\r\nnot-a-uri\r\n \r\n" + secondUri);

            submitted = true;
            await Task.WhenAny(server.HeadersSent.Task, summaryReady.Task);
            if (!server.HeadersSent.Task.IsCompleted) { await summaryReady.Task; Assert.Fail("URI取込みの実通信へ到達しませんでした。"); }
            Assert.ThrowsException<InvalidOperationException>(() => playlist.AcquirePlaylistMutationLease("uri-import-communication-busy"));
            Assert.IsFalse(workspace.TryEnqueueBuiltInExternalPlaylistImport(secondUri), "通信中の追加要求を予約しません。");
            server.ReleaseBody.TrySetResult();
            Assert.IsTrue(submission.HasValidUris);
            Assert.AreEqual(2, submission.ValidUriCount);
            CollectionAssert.AreEqual(new[] { "not-a-uri" }, submission.InvalidLines.ToList());
            ExternalPlaylistImportQueueSummary summary = await summaryReady.Task.ConfigureAwait(false);

            PlaylistSyncProgressSnapshot[] notifications = progress.ToArray();
            Assert.IsTrue(notifications.Length > 0);
            Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "external_playlist_import"));
            Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CurrentUri?.AbsoluteUri == firstUri));
            Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CurrentUri?.AbsoluteUri == secondUri));
            Assert.IsFalse(notifications[^1].IsActive);

            Assert.AreEqual(2, summary.ImportedCount);
            Assert.AreEqual(0, summary.FailedCount);
            CollectionAssert.AreEqual(
                new[] { firstUri, secondUri },
                summary.Outcomes.Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(
                new[] { "FirstImport", "SecondImport" },
                playlist.BMSTables.Select(table => table.name).ToArray());
        }
        finally
        {
            server?.ReleaseBody.TrySetResult();
            try
            {
                if (submitted)
                {
                    await summaryReady.Task;
                    await (acceptedPlaylist ?? throw new InvalidOperationException("Accepted playlist was not captured.")).WaitForPlaylistMutationIdleAsync();
                }
            }
            finally
            {
                try { if (server != null) { await server.DisposeAsync(); } }
                finally { if (Directory.Exists(tempDirectory)) { Directory.Delete(tempDirectory, recursive: true); } }
            }
        }
    }

    /// <summary>
    /// 保証：混合4対象のURI・Kindを元入力順で照合し、実参照通知の失敗時は同じ例外のWarningを登録結果の直後へ置く。
    /// 登録確定・IDとentry所属、既存表の非取得・内容、残る二表・管理台帳・config他キー・管理外BMTを両行で保つ。
    /// 通信中Busyと要求元付き進捗を観測し、実受付Taskとconsumerを合流してからHTTP・tempを解放する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartBeatorajaTableUrlImport_NewTablePublishesOwnedProgressAndSuccessfulSummary(bool failReferenceNotification)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var auxiliaryWork = new ConcurrentQueue<Task>();
        var summaryReady = new TaskCompletionSource<BeatorajaTableUrlImportSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        SingleRequestHttpServer? server = null;
        SingleRequestHttpServer? secondServer = null;
        SingleRequestHttpServer? unmanagedServer = null;
        SingleRequestHttpServer? existingServer = null;
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? acceptedPlaylist = null;
        TestBmsLibrary? acceptedLibrary = null;
        Task? importCompletion = null;
        bool started = false;
        Exception? primaryFailure = null;
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            File.WriteAllBytes(Path.Combine(root, "data.json"), BmsPlaylistTestSupport.CreateUtf8BomBytes(
                "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song B\",\"artist\":\"Artist B\",\"level\":\"2\"}]"));
            File.WriteAllBytes(Path.Combine(root, "second-data.json"), BmsPlaylistTestSupport.CreateUtf8BomBytes(
                "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song A\",\"artist\":\"Artist A\",\"level\":\"10\"}]"));
            server = new SingleRequestHttpServer(BmsPlaylistTestSupport.CreateUtf8BomBytes(
                "{\"name\":\"New B\",\"symbol\":\"B\",\"compat_prefix\":\"\",\"output_dir\":\"NewB\",\"data_url\":\"" + new Uri(Path.Combine(root, "data.json")).AbsoluteUri + "\"}"), holdBody: true);
            secondServer = new SingleRequestHttpServer(BmsPlaylistTestSupport.CreateUtf8BomBytes(
                "{\"name\":\"New A\",\"symbol\":\"A\",\"compat_prefix\":\"\",\"output_dir\":\"NewA\",\"data_url\":\"" + new Uri(Path.Combine(root, "second-data.json")).AbsoluteUri + "\"}"));
            unmanagedServer = new SingleRequestHttpServer(BmsPlaylistTestSupport.CreateUtf8BomBytes("not a difficulty table"));
            existingServer = new SingleRequestHttpServer(BmsPlaylistTestSupport.CreateUtf8BomBytes("existing table must not be fetched"));
            string inputUri = server.Address.AbsoluteUri;
            string secondUri = secondServer.Address.AbsoluteUri;
            string unmanagedUri = unmanagedServer.Address.AbsoluteUri;
            string existingUri = existingServer.Address.AbsoluteUri;
            string beatorajaRoot = Path.Combine(root, "beatoraja");
            Directory.CreateDirectory(beatorajaRoot);
            File.WriteAllBytes(Path.Combine(beatorajaRoot, "beatoraja.jar"), []);
            string output = Path.Combine(beatorajaRoot, "table");
            Directory.CreateDirectory(output);
            string config = Path.Combine(beatorajaRoot, "config_sys.json");
            File.WriteAllText(config, "{\"tablepath\":\"table\",\"other\":{\"volume\":37},\"tableURL\":[]}");
            string unmanagedBmt = Path.Combine(output, "unmanaged.bmt");
            byte[] unmanagedBytes = [5, 6, 8, 1];
            File.WriteAllBytes(unmanagedBmt, unmanagedBytes);
            BMSTable[] existing = new[] { "Existing E", "Remaining C", "Remaining D" }.Select((name, index) => new BMSTable
            {
                playlist_id = index + 1,
                name = name,
                bmt_sort = index,
                is_external_sync = true,
                Page_url = new Uri(index == 0 ? existingUri : "https://example.com/remaining" + index),
                Output_dir = "Existing" + index,
                Folder_order = ["old"],
                entries = [new BMSTableEntry { md5 = new string((char)('c' + index), 32), title = name + " song", folder = "old" }]
            }).ToArray();
            var bmtOptions = new BeatorajaBmtOptionsSnapshot
            {
                EnableBeatorajaBmtOutput = true,
                BeatorajaRootPath = beatorajaRoot,
                RegisterBeatorajaBmtUrls = true
            };
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => bmtOptions,
                () => new CustomFolderOutputSettingsSnapshot(),
                BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(existing)
            };
            acceptedPlaylist = playlist;
            playlist.CommitBMSTablesWithEntriesToDB(existing);
            await playlist.BmtOutput.ExportAllAsync("new-table-import-fixture");
            var configDocument = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
            configDocument["tableURL"] = new Newtonsoft.Json.Linq.JArray(inputUri, existingUri, unmanagedUri, secondUri);
            File.WriteAllText(config, configDocument.ToString());
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                auxiliaryWork.Enqueue(Task.Run(work));
                return true;
            };
            var library = new TestBmsLibrary(songDbPath);
            acceptedLibrary = library;
            workspace = CreateDetailWorkspace(out _,
                playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library,
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables));
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.IsPlaylistSummaryMode = true;
            workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.BmtSort), System.ComponentModel.ListSortDirection.Ascending);
            workspace.ApplyPlaylistSummaryDataRefresh(deferred: false, rebuildAsync: false);
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            workspace.BeatorajaTableUrlImportConfirmationRequested += (_, request) => request.Confirmed = true;
            workspace.BeatorajaTableUrlImportNotificationRequested += (_, request) =>
                summaryReady.TrySetException(new InvalidOperationException(request.Message));
            workspace.BeatorajaTableUrlImportSummaryReady += (_, request) => summaryReady.TrySetResult(request.Summary);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            var referenceNotificationFailure = new InvalidOperationException("固定入力：beatoraja共有参照通知の失敗");
            int referenceNotificationCount = 0;
            // 実一括参照追加後の通知先でだけ失敗させ、生産側が保持する元causeと対象ごとのWarningを観測する。
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) =>
            {
                referenceNotificationCount++;
                if (failReferenceNotification) { throw referenceNotificationFailure; }
            };

            started = true;
            workspace.StartBeatorajaTableUrlImport(beatorajaRoot);
            // Startが同期確保した受付の終端を本文gate中に捕捉し、通知欠落も同じ実Taskとの競争で検出する。
            importCompletion = playlist.WaitForPlaylistMutationIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(server.HeadersSent.Task, importCompletion, "new table import HTTP headers");
            Assert.ThrowsException<InvalidOperationException>(() => playlist.AcquirePlaylistMutationLease("beatoraja-import-communication-busy"));
            Assert.IsFalse(importCompletion.IsCompleted);
            server.ReleaseBody.TrySetResult();
            await TestUiDispatcherHost.AwaitNotificationAsync(summaryReady.Task, importCompletion, "new table import summary");
            BeatorajaTableUrlImportSummary summary = await summaryReady.Task;
            await importCompletion;
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            while (auxiliaryWork.TryDequeue(out Task? work)) { await work; }
            string diagnostics = string.Join(Environment.NewLine, summary.Outcomes.Select(outcome =>
                $"取込み結果: {outcome.Kind}, URI={outcome.Uri}, 表名={outcome.TableName}, 例外={outcome.Exception}"));
            Assert.AreEqual(2, summary.ImportedCount, diagnostics);
            Assert.AreEqual(1, summary.ExistingCount, diagnostics);
            Assert.AreEqual(1, summary.FailedCount, diagnostics);
            Assert.AreEqual(0, summary.RestoredFromBmtCount, diagnostics);
            Assert.AreEqual(failReferenceNotification ? 2 : 0, summary.WarningCount, diagnostics);
            Assert.AreEqual(failReferenceNotification ? 6 : 4, summary.Outcomes.Count, diagnostics);
            CollectionAssert.AreEqual(failReferenceNotification
                ? new[] { inputUri, inputUri, existingUri, unmanagedUri, secondUri, secondUri }
                : new[] { inputUri, existingUri, unmanagedUri, secondUri }, summary.Outcomes.Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(failReferenceNotification
                ? new[] { BeatorajaTableUrlImportOutcomeKind.Imported, BeatorajaTableUrlImportOutcomeKind.Warning,
                    BeatorajaTableUrlImportOutcomeKind.Existing, BeatorajaTableUrlImportOutcomeKind.Failed,
                    BeatorajaTableUrlImportOutcomeKind.Imported, BeatorajaTableUrlImportOutcomeKind.Warning }
                : new[] { BeatorajaTableUrlImportOutcomeKind.Imported, BeatorajaTableUrlImportOutcomeKind.Existing,
                    BeatorajaTableUrlImportOutcomeKind.Failed, BeatorajaTableUrlImportOutcomeKind.Imported },
                summary.Outcomes.Select(outcome => outcome.Kind).ToArray(), diagnostics);
            Assert.IsTrue(referenceNotificationCount > 0, "実参照通知へ到達する必要があります。");
            if (failReferenceNotification)
            {
                foreach ((int mainIndex, int warningIndex, string uri, string name) in new[]
                {
                    (0, 1, inputUri, "New B"), (4, 5, secondUri, "New A")
                })
                {
                    BeatorajaTableUrlImportOutcome main = summary.Outcomes[mainIndex];
                    BeatorajaTableUrlImportOutcome warning = summary.Outcomes[warningIndex];
                    Assert.AreEqual(uri, main.Uri.AbsoluteUri);
                    Assert.AreEqual(name, main.TableName);
                    Assert.AreEqual(main.Uri, warning.Uri);
                    Assert.AreEqual(main.TableName, warning.TableName);
                    Assert.AreSame(referenceNotificationFailure, warning.Exception);
                }
            }
            Assert.IsFalse(existingServer.HeadersSent.Task.IsCompleted, "既存一致のURLを再取得しません。");
            BeatorajaTableUrlImportOutcome imported = summary.Outcomes.First();
            Assert.AreEqual(BeatorajaTableUrlImportOutcomeKind.Imported, imported.Kind, diagnostics);
            Assert.AreEqual(inputUri, imported.Uri.AbsoluteUri);
            Assert.AreEqual("New B", imported.TableName);
            string[] expectedOrder = ["New B", "Existing E", "New A", "Remaining C", "Remaining D"];
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTable[] headers = verify.Table<BMSTable>().OrderBy(table => table.bmt_sort).ToArray();
                CollectionAssert.AreEqual(expectedOrder, headers.Select(table => table.name).ToArray());
                BMSTable newB = headers.Single(table => table.name == "New B");
                BMSTable newA = headers.Single(table => table.name == "New A");
                Assert.IsTrue(newB.playlist_id > 0 && newA.playlist_id > 0);
                Assert.AreNotEqual(newB.playlist_id, newA.playlist_id);
                foreach ((BMSTable table, string hash, string title, string folder) in new[]
                {
                    (newB, new string('a', 32), "Song B", "2"), (newA, new string('b', 32), "Song A", "10")
                })
                {
                    BMSTableEntry entry = verify.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id);
                    Assert.AreEqual(hash, entry.md5);
                    Assert.AreEqual(title, entry.title);
                    Assert.AreEqual(folder, entry.folder);
                }
                Assert.AreEqual(5, verify.Table<BMSTableEntry>().Count());
                foreach ((string name, int id, char hashDigit, string uri, string outputDirectory) in new[]
                {
                    ("Existing E", 1, 'c', existingUri, "Existing0"),
                    ("Remaining C", 2, 'd', "https://example.com/remaining1", "Existing1"),
                    ("Remaining D", 3, 'e', "https://example.com/remaining2", "Existing2")
                })
                {
                    BMSTable header = headers.Single(table => table.name == name);
                    Assert.AreEqual(id, header.playlist_id);
                    Assert.AreEqual(uri, header.Page_url.AbsoluteUri);
                    Assert.AreEqual(outputDirectory, header.Output_dir);
                    CollectionAssert.AreEqual(new[] { "old" }, header.Folder_order);
                    BMSTableEntry entry = verify.Table<BMSTableEntry>().Single(row => row.playlist_id == id);
                    Assert.AreEqual(new string(hashDigit, 32), entry.md5);
                    Assert.AreEqual(name + " song", entry.title);
                    Assert.AreEqual("old", entry.folder);
                }
                List<BmtTableExportService.ManagedTableUrlEntry> managed = BmtTableExportService.ReadManagedTableUrls(output);
                CollectionAssert.AreEquivalent(headers.Select(table => table.playlist_id.ToString()).ToArray(),
                    managed.Select(item => item.PlaylistIdentity).ToArray());
                Assert.IsFalse(managed.Any(item => item.Url == unmanagedUri));
            }
            CollectionAssert.AreEqual(expectedOrder, workspace.PlaylistSummaryView.Select(row => row.TableRef.name).ToArray());
            CollectionAssert.AreEqual(new[] { unmanagedUri, inputUri, existingUri, secondUri, "https://example.com/remaining1", "https://example.com/remaining2" }, BeatorajaConfigService.ReadTableUrls(beatorajaRoot).ToArray());
            Assert.AreEqual(37, (int?)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config))["other"]?["volume"]);
            CollectionAssert.AreEqual(unmanagedBytes, File.ReadAllBytes(unmanagedBmt));
            PlaylistSyncProgressSnapshot[] notifications = progress.ToArray();
            Assert.IsTrue(notifications.Length > 0);
            Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "beatoraja_table_url_import"));
            Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CurrentUri?.AbsoluteUri == inputUri));
            Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CompletedTableCount > 0));
            Assert.IsFalse(notifications[^1].IsActive);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            server?.ReleaseBody.TrySetResult();
            secondServer?.ReleaseBody.TrySetResult();
            unmanagedServer?.ReleaseBody.TrySetResult();
            existingServer?.ReleaseBody.TrySetResult();
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (importCompletion != null) { await CleanupAsync(() => importCompletion); }
            if (started && acceptedPlaylist != null)
            {
                await CleanupAsync(acceptedPlaylist.WaitForPlaylistMutationIdleAsync);
                if (!summaryReady.Task.IsCompleted)
                {
                    await CleanupAsync(() => TestUiDispatcherHost.AwaitNotificationAsync(summaryReady.Task,
                        acceptedPlaylist.WaitForPlaylistMutationIdleAsync(), "new table import cleanup summary"));
                }
            }
            if (workspace != null) { await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync); }
            while (auxiliaryWork.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { workspace?.RefreshPlaylistTreeTables(null); return Task.CompletedTask; });
            while (auxiliaryWork.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { acceptedPlaylist?.RequestShutdown("normal-beatoraja-import-fixture"); return Task.CompletedTask; });
            await CleanupAsync(() => { acceptedLibrary?.RequestShutdown("normal-beatoraja-import-fixture"); return Task.CompletedTask; });
            if (server != null) { await CleanupAsync(async () => await server.DisposeAsync()); }
            if (secondServer != null) { await CleanupAsync(async () => await secondServer.DisposeAsync()); }
            if (unmanagedServer != null) { await CleanupAsync(async () => await unmanagedServer.DisposeAsync()); }
            if (existingServer != null) { await CleanupAsync(async () => await existingServer.DisposeAsync()); }
            await CleanupAsync(() => { Directory.Delete(root, recursive: true); return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("beatoraja import cleanup", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    /// <summary>playlist未接続ではcollectionとbuilt-in取込みをenqueueせず、取込み要求を初期化後の予約へ持ち越しません。</summary>
    [TestMethod]
    public void ExternalPlaylistSourceRequestsRejectUnavailableStoreBeforeQueueing()
    {
        PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);

        Assert.IsFalse(unavailableWorkspace.TryEnqueueExternalPlaylistCollectionImport(
            new BMSTableSimple { url = new Uri("https://example.test/collection.json") }));
        Assert.IsFalse(unavailableWorkspace.TryEnqueueBuiltInExternalPlaylistImport("https://example.test/built-in.json"));
    }

    /// <summary>playlist未接続は新規表・URI・collection・built-inを無効にし、接続後は四入口を有効にして操作受付と表示可否を揃えます。</summary>
    [TestMethod]
    public void PlaylistRootContextMenuAvailability_UsesWorkspaceOwnedStateSnapshot()
    {
        PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);
        PlaylistRootContextMenuAvailability unavailable =
            unavailableWorkspace.CapturePlaylistRootContextMenuAvailability();

        Assert.IsFalse(unavailable.CanCreatePlaylist);
        Assert.IsFalse(unavailable.CanLoadPlaylistUri);
        Assert.IsFalse(unavailable.CanLoadPlaylistCollection);
        Assert.IsFalse(unavailable.CanLoadBuiltInTables);

        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            PlaylistWorkspaceViewModel initializedWorkspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            PlaylistRootContextMenuAvailability initialized =
                initializedWorkspace.CapturePlaylistRootContextMenuAvailability();

            Assert.IsTrue(initialized.CanCreatePlaylist);
            Assert.IsTrue(initialized.CanLoadPlaylistUri);
            Assert.IsTrue(initialized.CanLoadPlaylistCollection);
            Assert.IsTrue(initialized.CanLoadBuiltInTables);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    /// <summary>実workspaceのsettings portが返した表snapshotは元表の名前変更後も旧名を保ち、開いた設定に可変表を共有しません。</summary>
    [TestMethod]
    public void SettingsWorkspacePort_ExposesCanonicalImmutablePlaylistSnapshot()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var table = new BMSTable
            {
                playlist_id = 1,
                name = "Initial",
                symbol = "I"
            };
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            ISettingsDialogWorkspacePort settingsPort = workspace;

            Assert.IsTrue(settingsPort.HasPlaylistTables);
            PlaylistTablePresentationSnapshot snapshot = settingsPort.CapturePlaylistPresentationSnapshots().Single();
            table.name = "Changed";

            Assert.AreEqual("Initial", snapshot.Name);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    /// <summary>表示だけの変更はcatalog通知を出さず、catalog更新はqueue経由で届け、設定非表示中の更新を再表示時に一回だけ反映します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SettingsWorkspacePort_CatalogVersionDefersSettingsFanoutUntilDialogIsVisible()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
            var catalogNotificationQueue = new Queue<Action>();
            workspace.ConfigureCatalogNotificationQueue(catalogNotificationQueue.Enqueue);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            ISettingsDialogWorkspacePort settingsPort = workspace;
            var settingNotifications = new List<string>();
            dialog.PropertyChanged += (_, eventArgs) =>
                settingNotifications.Add(eventArgs.PropertyName!);
            dialog.SetPresentationActive(active: true);
            settingNotifications.Clear();
            int catalogEventCount = 0;
            settingsPort.PlaylistCatalogChanged += (_, _) => catalogEventCount++;
            long presentationVersion = settingsPort.PlaylistCatalogVersion;

            workspace.PlaylistSummaryText = "summary";
            workspace.PublishColumnPresentation(
                workspace.CommitColumnPresentationWithoutNotification(
                    Visibility.Collapsed,
                    new PlaylistSummaryColumnSettings()));
            workspace.IsPlaylistDetailViewActive = true;
            workspace.SetPlaylistSummaryMode(enabled: true);

            Assert.AreEqual(0, catalogEventCount);
            Assert.AreEqual(presentationVersion, settingsPort.PlaylistCatalogVersion);
            CollectionAssert.DoesNotContain(
                settingNotifications,
                nameof(SettingsDialogViewModel.LR2ConfigBMSDirectories));
            CollectionAssert.DoesNotContain(
                settingNotifications,
                nameof(SettingsDialogViewModel.AvailableBMSDirectories));

            long previousVersion = settingsPort.PlaylistCatalogVersion;
            dialog.SetPresentationActive(active: false);
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>(
                [
                    new BMSTable
                    {
                        playlist_id = 1,
                        name = "Catalog",
                        symbol = "C",
                    }
                ])
            };
            workspace.RefreshPlaylistTreeTables(playlist);

            Assert.IsTrue(settingsPort.PlaylistCatalogVersion > previousVersion);
            Assert.AreEqual(1, catalogNotificationQueue.Count);
            catalogNotificationQueue.Dequeue()();
            Assert.AreEqual(1, catalogEventCount);
            CollectionAssert.DoesNotContain(
                settingNotifications,
                nameof(SettingsDialogViewModel.LR2ConfigBMSDirectories));
            CollectionAssert.DoesNotContain(
                settingNotifications,
                nameof(SettingsDialogViewModel.AvailableBMSDirectories));

            dialog.SetPresentationActive(active: true);

            Assert.AreEqual(
                1,
                settingNotifications.Count(name =>
                    name == nameof(SettingsDialogViewModel.LR2ConfigBMSDirectories)));
            Assert.AreEqual(
                1,
                settingNotifications.Count(name =>
                    name == nameof(SettingsDialogViewModel.AvailableBMSDirectories)));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    /// <summary>連続catalog更新は同期通知せず一つのqueueへ集約し、配送時には最新versionだけを一回公開します。</summary>
    [TestMethod]
    public void SettingsWorkspacePort_CatalogCallbackIsQueuedAndCoalescesLatestVersion()
    {
        var presentationQueue = new Queue<Action>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            dispatchPresentation: presentationQueue.Enqueue);
        ISettingsDialogWorkspacePort settingsPort = workspace;
        int eventCount = 0;
        long publishedVersion = 0L;
        settingsPort.PlaylistCatalogChanged += (_, eventArgs) =>
        {
            eventCount++;
            publishedVersion = eventArgs.Version;
        };

        workspace.RefreshPlaylistTreeTables(null);
        workspace.RefreshPlaylistTreeTables(null);

        Assert.AreEqual(0, eventCount);
        Assert.AreEqual(1, presentationQueue.Count);
        long latestVersion = settingsPort.PlaylistCatalogVersion;

        presentationQueue.Dequeue()();

        Assert.AreEqual(1, eventCount);
        Assert.AreEqual(latestVersion, publishedVersion);
    }

    /// <summary>実DBで名前・prefix・出力基点を保存する各更新はcatalog versionを進め、queue終端で最新typed通知を各一回配送します。</summary>
    [TestMethod]
    public async Task SettingsWorkspacePort_CatalogMutationPublishesTypedVersion()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTableEntry entry = new TestablePlaylistEntry(
                "abababababababababababababababab",
                "Catalog entry")
            {
                playlist_id = 7820,
                folder = "Folder"
            };
            BMSTable table = new()
            {
                playlist_id = 7820,
                name = "Catalog target",
                symbol = "CAT",
                compat_prefix = string.Empty,
                Page_url = new Uri("https://example.test/catalog"),
                Header_url = new Uri("https://example.test/catalog.json"),
                Data_url = new Uri("https://example.test/catalog-data.json"),
                Output_dir = "CatalogTarget",
                is_root_folder = true,
                custom_folder_output_base_name = "OldBase",
                entries = [entry],
                Folder_order = ["Folder"]
            };
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }

            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot())
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(songDbPath);
            var propertySaveService = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            var catalogNotificationQueue = new Queue<Action>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                propertySaveService: propertySaveService,
                catalogNotificationQueue: catalogNotificationQueue.Enqueue);
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => { };
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            ISettingsDialogWorkspacePort settingsPort = workspace;
            var publishedEvents = new List<PlaylistCatalogChangedEventArgs>();
            settingsPort.PlaylistCatalogChanged += (_, eventArgs) => publishedEvents.Add(eventArgs);

            long CompletePublishedMutation(long previousVersion, int previousEventCount)
            {
                Assert.IsTrue(
                    settingsPort.PlaylistCatalogVersion > previousVersion,
                    "The catalog version did not advance for the mutation.");
                Assert.IsTrue(
                    catalogNotificationQueue.Count > 0,
                    "The mutation did not queue a typed catalog notification.");
                while (catalogNotificationQueue.Count > 0)
                {
                    catalogNotificationQueue.Dequeue()();
                }
                Assert.AreEqual(previousEventCount + 1, publishedEvents.Count);
                PlaylistCatalogChangedEventArgs latestEvent = publishedEvents[^1];
                Assert.AreEqual(settingsPort.PlaylistCatalogVersion, latestEvent.Version);
                Assert.IsTrue(latestEvent.Version > previousVersion);
                return latestEvent.Version;
            }

            long previousVersion = settingsPort.PlaylistCatalogVersion;
            int previousEventCount = publishedEvents.Count;
            workspace.RefreshPlaylistTreeTables(playlist);
            Assert.AreSame(playlist.BMSTables, workspace.PlaylistTreeTables);
            CompletePublishedMutation(previousVersion, previousEventCount);

            previousVersion = settingsPort.PlaylistCatalogVersion;
            previousEventCount = publishedEvents.Count;
            PlaylistSummaryPropertyEditCompletion nameEdit =
                await workspace.CompleteSummaryPropertyEditAsync(
                    new PlaylistSummaryRow { TableRef = table },
                    nameof(PlaylistSummaryRow.Name),
                    "Catalog renamed",
                    commit: true);
            Assert.IsTrue(nameEdit.IsApplied);
            Assert.AreEqual("Catalog renamed", table.name);
            CompletePublishedMutation(previousVersion, previousEventCount);

            previousVersion = settingsPort.PlaylistCatalogVersion;
            previousEventCount = publishedEvents.Count;
            PlaylistSummaryPropertyEditCompletion entriesEdit =
                await workspace.CompleteSummaryPropertyEditAsync(
                    new PlaylistSummaryRow { TableRef = table },
                    nameof(PlaylistSummaryRow.CompatPrefix),
                    "★",
                    commit: true);
            Assert.IsTrue(entriesEdit.IsApplied);
            Assert.AreEqual("★Folder", table.entries.Single().folder);
            CompletePublishedMutation(previousVersion, previousEventCount);

            previousVersion = settingsPort.PlaylistCatalogVersion;
            previousEventCount = publishedEvents.Count;
            await workspace.ApplyPlaylistSummaryOutputBase(
                [new PlaylistSummaryRow { TableRef = table }],
                outputBaseName: string.Empty);
            Assert.IsTrue(string.IsNullOrWhiteSpace(table.custom_folder_output_base_name));
            CompletePublishedMutation(previousVersion, previousEventCount);

            for (int index = 1; index < publishedEvents.Count; index++)
            {
                Assert.IsTrue(publishedEvents[index - 1].Version < publishedEvents[index].Version);
            }
            Assert.AreEqual(
                settingsPort.PlaylistCatalogVersion,
                publishedEvents[^1].Version);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>catalog購読先の例外をその配送で公開した後も、次の更新を再queueして通知でき、失敗で通知待ち状態を固着させません。</summary>
    [TestMethod]
    public void SettingsWorkspacePort_CatalogCallbackFailureDoesNotBlockNextVersion()
    {
        var presentationQueue = new Queue<Action>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            dispatchPresentation: presentationQueue.Enqueue);
        ISettingsDialogWorkspacePort settingsPort = workspace;
        bool fail = true;
        int successfulCount = 0;
        settingsPort.PlaylistCatalogChanged += (_, _) =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("subscriber failed");
            }
            successfulCount++;
        };

        workspace.RefreshPlaylistTreeTables(null);
        Assert.ThrowsException<InvalidOperationException>(() => presentationQueue.Dequeue()());

        workspace.RefreshPlaylistTreeTables(null);
        Assert.AreEqual(1, presentationQueue.Count);
        presentationQueue.Dequeue()();

        Assert.AreEqual(1, successfulCount);
    }

    /// <summary>tree表示を延期していても接続済みcollectionへの表追加はcatalog versionと通知を進め、設定用更新を表示延期へ巻き込みません。</summary>
    [TestMethod]
    public void SettingsWorkspacePort_DeferredCollectionMutationStillPublishesCatalogVersion()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                presentationRefreshDeferredProvider: request =>
                    request.Kind == PlaylistPresentationRefreshKind.Tree);
            ISettingsDialogWorkspacePort settingsPort = workspace;
            int eventCount = 0;
            settingsPort.PlaylistCatalogChanged += (_, _) => eventCount++;
            workspace.RefreshPlaylistTreeTables(playlist);
            long attachedVersion = settingsPort.PlaylistCatalogVersion;

            playlist.BMSTables.Add(new BMSTable
            {
                playlist_id = 2,
                name = "Deferred",
                symbol = "D",
            });

            Assert.AreEqual(2, eventCount);
            Assert.IsTrue(settingsPort.PlaylistCatalogVersion > attachedVersion);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }


    /// <summary>settings portからURL補完の非同期scheduleへ要求理由を渡し、別のBMT出力Taskも完了でき、BMTをURL補完queueへ混入させません。</summary>
    [TestMethod]
    public async Task SettingsWorkspacePort_ForwardsUrlCacheSchedulingAndBmtCompletion()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var scheduled = new List<string>();
            playlist.StartupBackgroundTaskScheduler = (kind, reason, dependency, work) =>
            {
                scheduled.Add(kind + ":" + reason);
                return true;
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () =>
                {
                    return playlist;
                });
            ISettingsDialogWorkspacePort settingsPort = workspace;

            settingsPort.SchedulePlaylistUrlCompletionRefresh("settings-test");
            await settingsPort.ExportBeatorajaBmtAsync("settings-test", null, null);

            CollectionAssert.AreEqual(
                new[] { "playlist_url_completion:settings-test" },
                scheduled);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    /// <summary>playlist providerがnullの状態でもURL補完要求を例外なく受け、BMT出力要求のTaskが正常に完了することを確認します。</summary>
    [TestMethod]
    public async Task SettingsWorkspacePort_BackgroundPublishRequestsAreNoOpWhenStoreDetached()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistStoreProvider: () => null!);
        ISettingsDialogWorkspacePort settingsPort = workspace;

        settingsPort.SchedulePlaylistUrlCompletionRefresh("detached-test");
        await settingsPort.ExportBeatorajaBmtAsync("detached-test", null, null);
    }

    /// <summary>collection URLなしは未受付、壊れたbuilt-in URIは元UriFormatExceptionとし、二つの既存入力契約を同じ空成功へ置換しません。</summary>
    [TestMethod]
    public void ExternalPlaylistSourceRequestsPreserveNullSourceAndMalformedUriContracts()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);

            Assert.IsFalse(workspace.TryEnqueueExternalPlaylistCollectionImport(new BMSTableSimple()));
            Assert.ThrowsException<UriFormatException>(
                () => workspace.TryEnqueueBuiltInExternalPlaylistImport("http://["));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    /// <summary>collection取込みの実終端後にbuilt-inを次の明示要求で受け、各一表の結果通知・URI順・実表登録を保持します。</summary>
    [TestMethod]
    public async Task ExternalPlaylistSourceRequests_CatalogAndBuiltInImportsUseSeparateExplicitRequests()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        BMSPlaylist? pendingPlaylist = null;
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string firstHeaderPath = Path.Combine(tempDirectory, "catalog.json");
            string firstDataPath = Path.Combine(tempDirectory, "catalog-data.json");
            string secondHeaderPath = Path.Combine(tempDirectory, "walkure.json");
            string secondDataPath = Path.Combine(tempDirectory, "walkure-data.json");
            File.WriteAllText(firstHeaderPath, "{\"name\":\"CatalogImport\",\"symbol\":\"C\",\"output_dir\":\"CatalogImport\",\"data_url\":\"./catalog-data.json\"}");
            File.WriteAllText(firstDataPath, "[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"title\":\"Catalog song\",\"artist\":\"Artist\",\"level\":\"3\"}]");
            File.WriteAllText(secondHeaderPath, "{\"name\":\"WalkureImport\",\"symbol\":\"W\",\"output_dir\":\"WalkureImport\",\"data_url\":\"./walkure-data.json\"}");
            File.WriteAllText(secondDataPath, "[{\"md5\":\"dddddddddddddddddddddddddddddddd\",\"title\":\"Walkure song\",\"artist\":\"Artist\",\"level\":\"4\"}]");

            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            pendingPlaylist = playlist;
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, request) =>
                summaryReady.TrySetResult(request.Summary);

            bool catalogAccepted = workspace.TryEnqueueExternalPlaylistCollectionImport(
                new BMSTableSimple { url = new Uri(firstHeaderPath) });
            Assert.IsTrue(catalogAccepted);
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsTrue(summaryReady.Task.IsCompleted, "受理済み取込みの終端までに結果通知を完了します。");
            ExternalPlaylistImportQueueSummary first = await summaryReady.Task;
            Assert.AreEqual(1, first.ImportedCount);
            summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(workspace.TryEnqueueBuiltInExternalPlaylistImport(new Uri(secondHeaderPath).AbsoluteUri));
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsTrue(summaryReady.Task.IsCompleted, "次の明示要求も実終端までに結果を通知します。");
            ExternalPlaylistImportQueueSummary second = await summaryReady.Task;
            Assert.AreEqual(1, second.ImportedCount);
            CollectionAssert.AreEqual(
                new[] { new Uri(firstHeaderPath).AbsoluteUri, new Uri(secondHeaderPath).AbsoluteUri },
                first.Outcomes.Concat(second.Outcomes).Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(
                new[] { "CatalogImport", "WalkureImport" },
                playlist.BMSTables.Select(table => table.name).ToArray());
        }
        finally
        {
            if (pendingPlaylist != null) { await pendingPlaylist.WaitForPlaylistMutationIdleAsync(); }
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }


    /// <summary>catalog取得gate中は非同期Taskを保留し、取得完了後にtag分類と表名を公開して未完了の結果を先に表示しません。</summary>
    [TestMethod]
    public async Task ExternalTableListCatalog_SlowFetchDoesNotBlockCallerAndPublishesAfterCompletion()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var fetchGate = new TaskCompletionSource<IReadOnlyList<BMSTableSimple>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var table = new BMSTableSimple
        {
            tag1 = "Deferred",
            name = "Loaded",
            url = new Uri("https://example.test/table")
        };

        Task loadTask = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/catalog"),
            (_, _) => fetchGate.Task);

        try
        {
            Assert.IsFalse(loadTask.IsCompleted);
            fetchGate.TrySetResult([table]);
            await loadTask.ConfigureAwait(false);

            Assert.IsNotNull(workspace.BMSExternalTableListExt);
            Assert.AreEqual("Deferred", workspace.BMSExternalTableListExt.Children[0].name);
            Assert.AreEqual("Loaded", workspace.BMSExternalTableListExt.Children[0].Children[0].name);
        }
        finally
        {
            fetchGate.TrySetResult([table]);
            await loadTask.ConfigureAwait(false);
        }
    }

    /// <summary>終了取消を実fetch tokenへ伝えて取得Taskを終了し、旧catalogを公開して終了後の表示を更新しません。</summary>
    [TestMethod]
    public async Task ExternalTableListCatalog_ShutdownCancelsAndDoesNotPublishStaleCatalog()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var cancellationObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BMSTableSimple[] staleCatalog = new[]
        {
            new BMSTableSimple
            {
                tag1 = "Stale",
                name = "Must not publish",
                url = new Uri("https://example.test/stale")
            }
        };

        Task loadTask = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/catalog"),
            async (_, cancellationToken) =>
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () => cancellationObserved.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return staleCatalog;
            });

        try
        {
            Assert.IsFalse(loadTask.IsCompleted);
            workspace.CancelExternalTableCollectionLoadForShutdown();
            // 取消の伝播を通知で確認する。workerの割当て時間は取消契約に含めない。
            await TestUiDispatcherHost.AwaitNotificationAsync(cancellationObserved.Task, loadTask, "external catalog shutdown cancellation");
            await loadTask.ConfigureAwait(false);

            Assert.IsNull(workspace.BMSExternalTableListExt);
        }
        finally
        {
            workspace.CancelExternalTableCollectionLoadForShutdown();
            await loadTask.ConfigureAwait(false);
        }
    }

    /// <summary>後発catalogを先に適用してから前発の遅い応答を返しても最新分類・表名を保ち、古い応答で上書きしません。</summary>
    [TestMethod]
    public async Task ExternalTableListCatalog_NewRequestKeepsLatestCatalogAfterLateCompletion()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var firstGate = new TaskCompletionSource<IReadOnlyList<BMSTableSimple>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource<IReadOnlyList<BMSTableSimple>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task firstLoad = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/first"),
            (_, _) => firstGate.Task);
        Task secondLoad = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/second"),
            (_, _) => secondGate.Task);

        try
        {
            secondGate.TrySetResult(
            [
                new BMSTableSimple
                {
                    tag1 = "Current",
                    name = "Second",
                    url = new Uri("https://example.test/current")
                }
            ]);
            await secondLoad.ConfigureAwait(false);
            firstGate.TrySetResult(
            [
                new BMSTableSimple
                {
                    tag1 = "Stale",
                    name = "First",
                    url = new Uri("https://example.test/stale")
                }
            ]);
            await firstLoad.ConfigureAwait(false);

            Assert.AreEqual("Current", workspace.BMSExternalTableListExt.Children[0].name);
            Assert.AreEqual("Second", workspace.BMSExternalTableListExt.Children[0].Children[0].name);
            Assert.IsNotNull(workspace.BMSExternalTableListExt);
        }
        finally
        {
            firstGate.TrySetResult([]);
            secondGate.TrySetResult([]);
            await Task.WhenAll(firstLoad, secondLoad).ConfigureAwait(false);
        }
    }

    /// <summary>入力の第一階層順を保ち、第二階層を名前順へ分類してURL付きleafと子なしdirect項目を区別し、menu階層を壊しません。</summary>
    [TestMethod]
    public void ExternalTableListCatalog_PreservesHierarchyOrderAndLeafMenuShape()
    {
        var firstLeaf = new BMSTableSimple
        {
            tag1 = "First",
            name = "direct",
            url = new Uri("https://example.test/direct")
        };
        var secondLeaf = new BMSTableSimple
        {
            tag1 = "Second",
            name = "second",
            url = new Uri("https://example.test/second")
        };
        var nestedZ = new BMSTableSimple
        {
            tag1 = "First",
            tag2 = "Zeta",
            name = "z-name",
            url = new Uri("https://example.test/z")
        };
        var nestedA = new BMSTableSimple
        {
            tag1 = "First",
            tag2 = "Alpha",
            name = "a-name",
            url = new Uri("https://example.test/a")
        };

        BMSTableSimpleCategorized catalog = PlaylistWorkspaceViewModel.BuildExternalTableListCatalog(
            [firstLeaf, nestedZ, nestedA, secondLeaf]);

        Assert.AreEqual(2, catalog.Children.Count);
        Assert.AreEqual("First", catalog.Children[0].name);
        Assert.AreEqual("Second", catalog.Children[1].name);
        Assert.AreEqual(3, catalog.Children[0].Children.Count);
        Assert.AreEqual("direct", catalog.Children[0].Children[0].name);
        Assert.IsNotNull(catalog.Children[0].Children[1].Children);
        Assert.AreEqual("Alpha", catalog.Children[0].Children[1].name);
        Assert.AreEqual("Zeta", catalog.Children[0].Children[2].name);
        Assert.IsNotNull(catalog.Children[0].Children[1].Children[0].url);
        Assert.IsNotNull(catalog.Children[0].Children[2].Children[0].url);
        Assert.IsNull(catalog.Children[0].Children[0].Children);
    }

}
