using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

using static BeMusicSeeker.Tests.PlaylistWorkspaceFixtureFactory;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;

/// <summary>外部表取込みの実受付・登録・通知とsettingsへのcatalog配送を確認します。</summary>
/// <remarks>通信中の受付と実DB・BMT・configの一括反映はplaylistとworkspaceの実接続が対象です。個別JSON・BMT内容は下位parser/exportケース、実Window配送はMainWindowケースへ分担します。</remarks>
[TestClass]
public sealed class PlaylistWorkspaceExternalSourceTests
{
    /// <summary>404からUnicode実名の実BMTを復元し、同hashの非数値二所属・数値一所属とnullable levelを通常登録・DB読戻し・再出力まで保持します。同URLの再取込みは取得不能でも既存一致として同一IDと保存内容を保ちます。</summary>
    /// <remarks>条件網羅はCreateTableの条件表へ分担し、ここでは実workspace受付から保存・再生成への接続を一例で確認します。</remarks>
    [TestMethod]
    public async Task StartBeatorajaTableUrlImport_CachedNonnumericMembershipsSurvivePersistenceAndReexport()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SingleRequestHttpServer? server = null;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Task importCompletion = Task.CompletedTask;
        var auxiliaryWork = new ConcurrentQueue<Task>();
        var summaryReady = new TaskCompletionSource<BeatorajaTableUrlImportSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? primaryFailure = null;
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            server = new SingleRequestHttpServer([], responseStatusCode: HttpStatusCode.NotFound);
            string inputUrl = server.Address.AbsoluteUri;
            string beatorajaRoot = Path.Combine(root, "beatoraja");
            string output = Path.Combine(beatorajaRoot, "table");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(beatorajaRoot, "beatoraja.jar"), []);
            File.WriteAllText(Path.Combine(beatorajaRoot, BeatorajaConfigService.ConfigFileName), new JObject
            {
                ["tablepath"] = "table",
                ["tableURL"] = new JArray(inputUrl)
            }.ToString());
            string hash = new('a', 32);
            (string Hash, string Folder, double? Level)[] expected =
                [(hash, "★???", null), (hash, "★お気に入り", null), (hash, "★2", 2d)];
            string bmtPath = BeatorajaBmtTableImportService.GetCachedTablePath(output, inputUrl);
            WriteBmtJson(bmtPath, new JObject
            {
                ["url"] = "https://example.test/old-cache.json",
                ["name"] = "한글",
                ["tag"] = "★",
                ["folder"] = new JArray(expected.Select(item => new JObject
                {
                    ["name"] = item.Folder,
                    ["songs"] = new JArray(new JObject { ["title"] = "Same song", ["md5"] = hash })
                }))
            });
            var options = new BeatorajaBmtOptionsSnapshot
            {
                EnableBeatorajaBmtOutput = true,
                BeatorajaRootPath = beatorajaRoot,
                RegisterBeatorajaBmtUrls = true
            };
            playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => options, () => new CustomFolderOutputSettingsSnapshot(),
                BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            { BMSTables = new ObservableCollection<BMSTable>() };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                auxiliaryWork.Enqueue(Task.Run(work));
                return true;
            };
            library = new TestBmsLibrary(songDbPath);
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library,
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables));
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.IsPlaylistSummaryMode = true;
            workspace.BeatorajaTableUrlImportConfirmationRequested += (_, request) => request.Confirmed = true;
            workspace.BeatorajaTableUrlImportNotificationRequested += (_, request) =>
                summaryReady.TrySetException(new InvalidOperationException(request.Message));
            workspace.BeatorajaTableUrlImportSummaryReady += (_, request) => summaryReady.TrySetResult(request.Summary);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            importCompletion = workspace.StartBeatorajaTableUrlImport(beatorajaRoot);
            await TestUiDispatcherHost.AwaitNotificationAsync(summaryReady.Task, importCompletion, "cached table import summary");
            BeatorajaTableUrlImportSummary summary = await summaryReady.Task;
            await importCompletion;
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            while (auxiliaryWork.TryDequeue(out Task? work)) { await work; }
            Assert.IsTrue(server.HeadersSent.Task.IsCompletedSuccessfully, server.Diagnostics);
            string diagnostics = string.Join(Environment.NewLine, summary.Outcomes.Select(outcome => $"{outcome.Kind}: {outcome.Exception}"));
            Assert.AreEqual(1, summary.RestoredFromBmtCount, diagnostics);
            Assert.AreEqual(0, summary.ImportedCount, diagnostics);
            Assert.AreEqual(0, summary.ExistingCount, diagnostics);
            Assert.AreEqual(0, summary.FailedCount, diagnostics);
            Assert.AreEqual(0, summary.WarningCount, diagnostics);
            Assert.AreEqual(1, summary.Outcomes.Count, diagnostics);
            Assert.AreEqual(BeatorajaTableUrlImportOutcomeKind.RestoredFromBmt, summary.Outcomes.Single().Kind);
            Assert.AreEqual(inputUrl, summary.Outcomes.Single().Uri.AbsoluteUri);
            Assert.IsTrue(workspace.PlaylistSummaryView.All(row => row.HasFailureStatus));
            Assert.IsTrue(workspace.PlaylistSummaryView.Any(row => row.Status == BeMusicSeeker.Properties.Resources.Playlist_sync_status_404));

            BMSTable restored = playlist.BMSTables.Single();
            Assert.IsTrue(restored.playlist_id > 0);
            AssertTableHeader(restored);
            CollectionAssert.AreEquivalent(expected, restored.entries.Select(entry => (entry.md5, entry.folder, entry.level)).ToArray());
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTable header = verify.Table<BMSTable>().Single();
                Assert.AreEqual(restored.playlist_id, header.playlist_id);
                AssertTableHeader(header);
                BMSTableEntry[] saved = verify.Table<BMSTableEntry>().ToArray();
                Assert.IsTrue(saved.All(entry => entry.playlist_id == header.playlist_id));
                CollectionAssert.AreEquivalent(expected, saved.Select(entry => (entry.md5, entry.folder, entry.level)).ToArray());
            }
            AssertExportedMemberships();

            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);
            BMSTable reloaded = playlist.BMSTables.Single();
            Assert.AreEqual(restored.playlist_id, reloaded.playlist_id);
            AssertTableHeader(reloaded);
            CollectionAssert.AreEquivalent(expected, reloaded.entries.Select(entry => (entry.md5, entry.folder, entry.level)).ToArray());
            // 既存ファイルの読取りだけでは成功しないよう、DB読戻し後に実ファイルを再生成する。
            File.Delete(bmtPath);
            await playlist.BmtOutput.ExportAllAsync("cached-memberships-reexport", forceProjection: true);
            AssertExportedMemberships();

            // 取得元を閉じた後も、専用入口は再取得・cache復元ではなく既存一致として扱う。
            await server.DisposeAsync();
            server = null;
            summaryReady = new TaskCompletionSource<BeatorajaTableUrlImportSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            importCompletion = workspace.StartBeatorajaTableUrlImport(beatorajaRoot);
            await TestUiDispatcherHost.AwaitNotificationAsync(summaryReady.Task, importCompletion, "cached table repeated import summary");
            BeatorajaTableUrlImportSummary repeated = await summaryReady.Task;
            await importCompletion;
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            while (auxiliaryWork.TryDequeue(out Task? work)) { await work; }
            Assert.AreEqual(1, repeated.ExistingCount);
            Assert.AreEqual(BeatorajaTableUrlImportOutcomeKind.Existing, repeated.Outcomes.Single().Kind);
            Assert.AreEqual(inputUrl, repeated.Outcomes.Single().Uri.AbsoluteUri);
            Assert.AreEqual(restored.playlist_id, playlist.BMSTables.Single().playlist_id);
            AssertTableHeader(playlist.BMSTables.Single());
            CollectionAssert.AreEquivalent(expected, playlist.BMSTables.Single().entries.Select(entry => (entry.md5, entry.folder, entry.level)).ToArray());
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTable header = verify.Table<BMSTable>().Single();
                Assert.AreEqual(restored.playlist_id, header.playlist_id);
                AssertTableHeader(header);
                BMSTableEntry[] saved = verify.Table<BMSTableEntry>().ToArray();
                Assert.IsTrue(saved.All(entry => entry.playlist_id == header.playlist_id));
                CollectionAssert.AreEquivalent(expected, saved.Select(entry => (entry.md5, entry.folder, entry.level)).ToArray());
            }
            AssertExportedMemberships();

            void AssertTableHeader(BMSTable table)
            {
                Assert.AreEqual("한글", table.name);
                Assert.AreEqual("★", table.tag);
                Assert.AreEqual(inputUrl, table.Page_url.AbsoluteUri);
                Assert.IsTrue(string.IsNullOrWhiteSpace(table.Output_dir), "実名由来の空出力名をスタンドアロンでは受理します。");
            }

            void AssertExportedMemberships()
            {
                JObject exported = ReadBmtJson(bmtPath);
                Assert.AreEqual("한글", exported.Value<string>("name"));
                Assert.AreEqual("★", exported.Value<string>("tag"));
                Assert.AreEqual(inputUrl, exported.Value<string>("url"), "元cacheが残っただけでは再出力成功としません。");
                JArray folders = exported["folder"] as JArray ?? throw new AssertFailedException("BMT folder配列がありません。");
                CollectionAssert.AreEquivalent(expected.Select(item => item.Folder).ToArray(),
                    folders.Select(folder => (string?)folder["name"]).ToArray());
                CollectionAssert.AreEquivalent(expected.Select(item => (item.Hash, item.Folder)).ToArray(),
                    folders.SelectMany(folder => (folder["songs"] as JArray ?? throw new AssertFailedException("BMT songs配列がありません。")).Select(song =>
                        ((string?)song["md5"], (string?)folder["name"]))).ToArray());
                BmtTableExportService.ManagedTableUrlEntry managed = BmtTableExportService.ReadManagedTableUrls(output).Single();
                Assert.AreEqual(restored.playlist_id.ToString(), managed.PlaylistIdentity);
                Assert.AreEqual(inputUrl, managed.Url);
                CollectionAssert.AreEqual(new[] { inputUrl }, BeatorajaConfigService.ReadTableUrls(beatorajaRoot).ToArray());
            }
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            await CleanupAsync(() => importCompletion);
            if (workspace != null) { await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync); }
            while (auxiliaryWork.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { workspace?.RefreshPlaylistTreeTables(null); workspace?.Dispose(); return Task.CompletedTask; });
            while (auxiliaryWork.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { playlist?.RequestShutdown("cached-memberships-fixture"); return Task.CompletedTask; });
            await CleanupAsync(() => { library?.RequestShutdown("cached-memberships-fixture"); return Task.CompletedTask; });
            if (server != null) { await CleanupAsync(async () => await server.DisposeAsync()); }
            await CleanupAsync(() => { Directory.Delete(root, recursive: true); return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("cached table import cleanup", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    private static void WriteBmtJson(string path, JObject json)
    {
        using FileStream file = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        writer.Write(json.ToString());
    }

    private static JObject ReadBmtJson(string path)
    {
        using FileStream file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return JObject.Parse(reader.ReadToEnd());
    }

    /// <summary>既存表の実tableURL取込みをDB・本番サマリー・configまで通し、入力順の先頭配置と残りの相対順、BMT非書換えを保証します。</summary>
    [TestMethod]
    public async Task BeatorajaTableUrlImport_ExistingTablesKeepInputFrontOrderAndRemainingRelativeOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Task operation = Task.CompletedTask;
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
                operation = workspace.StartBeatorajaTableUrlImport(root);
                await TestUiDispatcherHost.AwaitNotificationAsync(completed.Task, operation, "existing BMT URL import summary");
                BeatorajaTableUrlImportSummary summary = await completed.Task;
                await operation;
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
                await operation;
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

    /// <summary>Unicode実名・ASCII tag・明示出力名なしのHTTPとローカルJSONを一要求で登録し、実BMTを生成します。通信・必要出力中の追加拒否、通信中の表ロック解放、入力順と進捗終端を維持します。</summary>
    [TestMethod]
    public async Task TryStartPlaylistImport_HoldsAdmissionThroughCommunicationAndRequiredOutput()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        SingleRequestHttpServer? server = null;
        TestBmsPlaylist? acceptedPlaylist = null;
        TestBmsLibrary? acceptedLibrary = null;
        PlaylistWorkspaceViewModel? workspace = null;
        var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
        void OnProgress(object? sender, PlaylistSyncProgressChangedEventArgs request) => progress.Enqueue(request.Snapshot);
        void OnSummary(object? sender, ExternalPlaylistImportQueueSummaryReadyEventArgs request) => summaryReady.TrySetResult(request.Summary);
        var outputEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? importCompletion = null;
        bool holdOutput = false;
        Exception? primaryFailure = null;
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
            File.WriteAllText(secondHeaderPath, "{\"name\":\"한국어\",\"symbol\":\"S\",\"tag\":\"SECOND\",\"data_url\":\"./second-data.json\"}");
            File.WriteAllText(secondDataPath, "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Second song\",\"artist\":\"Artist\",\"level\":\"2\"}]");
            string outputDirectory = Path.Combine(tempDirectory, "table");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(tempDirectory, "beatoraja.jar"), string.Empty);

            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () =>
                {
                    if (holdOutput)
                    {
                        outputEntered.TrySetResult();
                        outputRelease.Task.GetAwaiter().GetResult();
                    }
                    return new BeatorajaBmtOptionsSnapshot
                    {
                        EnableBeatorajaBmtOutput = true,
                        BeatorajaBmtTablePath = outputDirectory,
                        BeatorajaRootPath = tempDirectory
                    };
                },
                () => new CustomFolderOutputSettingsSnapshot())
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            acceptedPlaylist = playlist;
            var library = new TestBmsLibrary(songDbPath);
            acceptedLibrary = library;
            workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            workspace.PlaylistSyncProgressChanged += OnProgress;
            workspace.ExternalPlaylistImportQueueSummaryReady += OnSummary;

            server = new SingleRequestHttpServer(BmsPlaylistTestSupport.CreateUtf8BomBytes(
                "{\"name\":\"한글\",\"symbol\":\"F\",\"tag\":\"FIRST\",\"data_url\":\"" + new Uri(firstDataPath).AbsoluteUri + "\"}"), holdBody: true);
            string firstUri = server.Address.AbsoluteUri;
            string secondUri = new Uri(secondHeaderPath).AbsoluteUri;
            holdOutput = true;
            PlaylistImportStartResult submission = workspace.TryStartPlaylistImport([new Uri(firstUri), new Uri(secondUri)]);
            Assert.AreEqual(PlaylistImportStartStatus.Started, submission.Status);
            Assert.AreEqual(2, submission.StartedCount);
            importCompletion = playlist.WaitForPlaylistMutationIdleAsync();
            await Task.WhenAny(server.HeadersSent.Task, importCompletion);
            Assert.IsTrue(server.HeadersSent.Task.IsCompleted, "URI取込みの実通信へ到達しませんでした。");
            Assert.ThrowsException<InvalidOperationException>(() => playlist.AcquirePlaylistMutationLease("uri-import-communication-busy"));
            Assert.AreEqual(PlaylistImportStartStatus.Busy,
                workspace.TryStartPlaylistImport([new Uri(secondUri)]).Status, "通信中の追加要求を予約しません。");
            await Task.Run(() =>
            {
                playlist.AcquireWriterLockBMSTables();
                playlist.FreeWriterLockBMSTables();
            });
            server.ReleaseBody.TrySetResult();
            await Task.WhenAny(outputEntered.Task, importCompletion);
            string outputDiagnostics = summaryReady.Task.IsCompletedSuccessfully
                ? string.Join(Environment.NewLine, (await summaryReady.Task).FailedOutcomes.Select(outcome => outcome.Exception))
                : string.Empty;
            Assert.IsTrue(outputEntered.Task.IsCompleted, "登録後の必要出力まで到達します。" + outputDiagnostics);
            Assert.IsFalse(importCompletion.IsCompleted);
            Assert.AreEqual(PlaylistImportStartStatus.Busy,
                workspace.TryStartPlaylistImport([new Uri(secondUri)]).Status, "必要出力中も同じ受付を保持します。");
            outputRelease.TrySetResult();
            await importCompletion;
            Assert.IsTrue(summaryReady.Task.IsCompleted, "実処理の終端までに結果を通知します。");
            ExternalPlaylistImportQueueSummary summary = await summaryReady.Task.ConfigureAwait(false);

            PlaylistSyncProgressSnapshot[] notifications = progress.ToArray();
            Assert.IsTrue(notifications.Length > 0);
            Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "external_playlist_import"));
            Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CurrentUri?.AbsoluteUri == firstUri));
            Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CurrentUri?.AbsoluteUri == secondUri));
            Assert.IsFalse(notifications[^1].IsActive);

            Assert.AreEqual(2, summary.ImportedCount);
            Assert.AreEqual(0, summary.SkippedDuplicateUrlCount);
            Assert.AreEqual(0, summary.FailedCount);
            Assert.AreEqual(2, Directory.GetFiles(outputDirectory, "*.bmt").Length);
            CollectionAssert.AreEqual(
                new[] { firstUri, secondUri },
                summary.Outcomes.Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(
                new[] { "한글", "한국어" },
                playlist.BMSTables.Select(table => table.name).ToArray());
            CollectionAssert.AreEqual(new[] { "FIRST", "SECOND" }, playlist.BMSTables.Select(table => table.tag).ToArray());
            Assert.IsTrue(playlist.BMSTables.All(table => string.IsNullOrWhiteSpace(table.Output_dir)));
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            server?.ReleaseBody.TrySetResult();
            outputRelease.TrySetResult();
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (importCompletion != null) { await CleanupAsync(() => importCompletion); }
            if (acceptedPlaylist != null) { await CleanupAsync(acceptedPlaylist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null)
            {
                workspace.PlaylistSyncProgressChanged -= OnProgress;
                workspace.ExternalPlaylistImportQueueSummaryReady -= OnSummary;
                await CleanupAsync(workspace.WaitForDetailBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistReloadCleanupIdleAsync);
                await CleanupAsync(() => { workspace.RefreshPlaylistTreeTables(null); workspace.Dispose(); return Task.CompletedTask; });
            }
            await CleanupAsync(() => { acceptedPlaylist?.RequestShutdown("unicode-mixed-import-test"); return Task.CompletedTask; });
            await CleanupAsync(() => { acceptedLibrary?.RequestShutdown("unicode-mixed-import-test"); return Task.CompletedTask; });
            if (server != null) { await CleanupAsync(async () => await server.DisposeAsync()); }
            await CleanupAsync(() => { Directory.Delete(tempDirectory, recursive: true); return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("Unicode混合取込みテストの後片付け", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    /// <summary>
    /// 保証：混合4対象のURI・Kindを元入力順で照合し、登録済みの参照表示をモデルから確認する。
    /// 登録確定・IDとentry所属、既存表の非取得・内容、残る二表・管理台帳・config他キー・管理外BMTを保つ。
    /// スタンドアロンで出力名が空になるUnicode実名を変更せず保存し、ASCII tag・曲集合・元URLを実gzipとURL登録まで反映する。
    /// 通信中Busyと要求元付き進捗を観測し、実受付Taskとconsumerを合流してからHTTP・tempを解放する。
    /// </summary>
    [TestMethod]
    public async Task StartBeatorajaTableUrlImport_NewTablePublishesOwnedProgressAndSuccessfulSummary()
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
                "{\"name\":\"한글\",\"symbol\":\"st\",\"tag\":\"st\",\"compat_prefix\":\"\",\"data_url\":\"" + new Uri(Path.Combine(root, "data.json")).AbsoluteUri + "\"}"), holdBody: true);
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
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                auxiliaryWork.Enqueue(Task.Run(work));
                return true;
            };
            playlist.CommitBMSTablesWithEntriesToDB(existing);
            await playlist.BmtOutput.ExportAllAsync("new-table-import-fixture");
            var configDocument = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
            configDocument["tableURL"] = new Newtonsoft.Json.Linq.JArray(inputUri, existingUri, unmanagedUri, secondUri);
            File.WriteAllText(config, configDocument.ToString());
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


            started = true;
            importCompletion = workspace.StartBeatorajaTableUrlImport(beatorajaRoot);
            // 実Startが返す取込みTaskで、通信から出力・通知・受付解放までを回収します。
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
            Assert.AreEqual(0, summary.WarningCount, diagnostics);
            Assert.AreEqual(4, summary.Outcomes.Count, diagnostics);
            CollectionAssert.AreEqual(new[] { inputUri, existingUri, unmanagedUri, secondUri },
                summary.Outcomes.Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(new[] { BeatorajaTableUrlImportOutcomeKind.Imported, BeatorajaTableUrlImportOutcomeKind.Existing,
                BeatorajaTableUrlImportOutcomeKind.Failed, BeatorajaTableUrlImportOutcomeKind.Imported },
                summary.Outcomes.Select(outcome => outcome.Kind).ToArray(), diagnostics);
            Assert.IsFalse(existingServer.HeadersSent.Task.IsCompleted, "既存一致のURLを再取得しません。");
            BeatorajaTableUrlImportOutcome imported = summary.Outcomes.First();
            Assert.AreEqual(BeatorajaTableUrlImportOutcomeKind.Imported, imported.Kind, diagnostics);
            Assert.AreEqual(inputUri, imported.Uri.AbsoluteUri);
            Assert.AreEqual("한글", imported.TableName);
            string[] expectedOrder = ["한글", "Existing E", "New A", "Remaining C", "Remaining D"];
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTable[] headers = verify.Table<BMSTable>().OrderBy(table => table.bmt_sort).ToArray();
                CollectionAssert.AreEqual(expectedOrder, headers.Select(table => table.name).ToArray());
                BMSTable newB = headers.Single(table => table.name == "한글");
                BMSTable newA = headers.Single(table => table.name == "New A");
                Assert.AreEqual("st", newB.tag);
                Assert.AreEqual(inputUri, newB.Header_url.AbsoluteUri);
                Assert.IsTrue(string.IsNullOrWhiteSpace(newB.Output_dir), "明示出力名なしのUnicode実名をそのまま保存します。");
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
                    CollectionAssert.AreEqual(new[] { "old" }, header.Folder_order.ToArray());
                    BMSTableEntry entry = verify.Table<BMSTableEntry>().Single(row => row.playlist_id == id);
                    Assert.AreEqual(new string(hashDigit, 32), entry.md5);
                    Assert.AreEqual(name + " song", entry.title);
                    Assert.AreEqual("old", entry.folder);
                }
                List<BmtTableExportService.ManagedTableUrlEntry> managed = BmtTableExportService.ReadManagedTableUrls(output);
                CollectionAssert.AreEquivalent(headers.Select(table => table.playlist_id.ToString()).ToArray(),
                    managed.Select(item => item.PlaylistIdentity).ToArray());
                Assert.AreEqual(inputUri, managed.Single(item => item.PlaylistIdentity == newB.playlist_id.ToString()).Url);
                Assert.IsFalse(managed.Any(item => item.Url == unmanagedUri));
            }
            JObject unicodeBmt = ReadBmtJson(BeatorajaBmtTableImportService.GetCachedTablePath(output, inputUri));
            Assert.AreEqual("한글", unicodeBmt.Value<string>("name"));
            Assert.AreEqual("st", unicodeBmt.Value<string>("tag"));
            Assert.AreEqual(inputUri, unicodeBmt.Value<string>("url"));
            JArray unicodeFolders = unicodeBmt["folder"] as JArray ?? throw new AssertFailedException("BMT folder配列がありません。");
            CollectionAssert.AreEqual(new[] { "st2" }, unicodeFolders.Select(folder => (string?)folder["name"]).ToArray());
            CollectionAssert.AreEquivalent(new[] { (new string('a', 32), "Song B") },
                unicodeFolders.SelectMany(folder => (folder["songs"] as JArray ?? throw new AssertFailedException("BMT songs配列がありません。"))
                    .Select(song => ((string?)song["md5"], (string?)song["title"]))).ToArray());
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
            if (workspace != null)
            {
                await CleanupAsync(workspace.WaitForDetailBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistReloadCleanupIdleAsync);
            }
            while (auxiliaryWork.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { workspace?.RefreshPlaylistTreeTables(null); workspace?.Dispose(); return Task.CompletedTask; });
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

    /// <summary>Table URL取込みは実storeのLR2モードでUnicode実名由来の空出力名だけを失敗にし、後続の正常表を保存・出力します。結果の元URL順と実終端後の受付を保ちます。</summary>
    /// <remarks>Workspace設定がスタンドアロンでも登録先storeのモードを使う接続を確認します。登録本体のモード×単表・一括の条件表は既存登録ownerテストへ分担します。</remarks>
    [TestMethod]
    public async Task StartBeatorajaTableUrlImport_Lr2ModeRejectsOnlyEmptyOutputNameAndImportsValidTable()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Task operation = Task.CompletedTask;
        var auxiliaryWork = new ConcurrentQueue<Task>();
        var summaryReady = new TaskCompletionSource<BeatorajaTableUrlImportSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? primaryFailure = null;
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string unicodeHeader = Path.Combine(root, "unicode.json");
            string validHeader = Path.Combine(root, "valid.json");
            File.WriteAllText(unicodeHeader, "{\"name\":\"한글\",\"symbol\":\"st\",\"tag\":\"st\",\"data_url\":\"./unicode-data.json\"}");
            File.WriteAllText(Path.Combine(root, "unicode-data.json"), "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Rejected song\",\"level\":\"1\"}]");
            File.WriteAllText(validHeader, "{\"name\":\"ValidImport\",\"symbol\":\"V\",\"tag\":\"VALID\",\"compat_prefix\":\"\",\"data_url\":\"./valid-data.json\"}");
            File.WriteAllText(Path.Combine(root, "valid-data.json"), "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Valid song\",\"level\":\"2\"}]");
            string unicodeUrl = new Uri(unicodeHeader).AbsoluteUri;
            string validUrl = new Uri(validHeader).AbsoluteUri;
            string beatorajaRoot = Path.Combine(root, "beatoraja");
            Directory.CreateDirectory(beatorajaRoot);
            File.WriteAllBytes(Path.Combine(beatorajaRoot, "beatoraja.jar"), []);
            File.WriteAllText(Path.Combine(beatorajaRoot, BeatorajaConfigService.ConfigFileName), new JObject
            {
                ["tablepath"] = "table",
                ["tableURL"] = new JArray(unicodeUrl, validUrl)
            }.ToString());
            string outputBase = Path.Combine(root, "Output");
            var outputSettings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBase,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            var configOwner = new Lr2ConfigOwner(BmsPlaylistTestSupport.CreateLr2Config(Path.Combine(root, "LR2"), outputBase));
            playlist = new TestBmsPlaylist(songDbPath, configOwner, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => outputSettings,
                BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                auxiliaryWork.Enqueue(Task.Run(work));
                return true;
            };
            library = new TestBmsLibrary(songDbPath);
            // 既存factoryのWorkspaceはスタンドアロン設定で、実storeだけがLR2連携を使う。
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library,
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables));
            workspace.BeatorajaTableUrlImportConfirmationRequested += (_, request) => request.Confirmed = true;
            workspace.BeatorajaTableUrlImportNotificationRequested += (_, request) =>
                summaryReady.TrySetException(new InvalidOperationException(request.Message));
            workspace.BeatorajaTableUrlImportSummaryReady += (_, request) => summaryReady.TrySetResult(request.Summary);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            operation = workspace.StartBeatorajaTableUrlImport(beatorajaRoot);
            await TestUiDispatcherHost.AwaitNotificationAsync(summaryReady.Task, operation, "LR2 Table URL import summary");
            BeatorajaTableUrlImportSummary summary = await summaryReady.Task;
            await operation;
            while (auxiliaryWork.TryDequeue(out Task? work)) { await work; }
            string diagnostics = string.Join(Environment.NewLine, summary.Outcomes.Select(outcome => $"{outcome.Kind}: {outcome.Exception}"));
            Assert.AreEqual(1, summary.FailedCount, diagnostics);
            Assert.AreEqual(1, summary.ImportedCount, diagnostics);
            Assert.AreEqual(0, summary.WarningCount, diagnostics);
            CollectionAssert.AreEqual(new[] { unicodeUrl, validUrl }, summary.Outcomes.Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(new[] { BeatorajaTableUrlImportOutcomeKind.Failed, BeatorajaTableUrlImportOutcomeKind.Imported },
                summary.Outcomes.Select(outcome => outcome.Kind).ToArray(), diagnostics);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Error_OutputDirNameEmpty, summary.Outcomes[0].Exception.Message);
            Assert.AreEqual("ValidImport", playlist.BMSTables.Single().name);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTable saved = verify.Table<BMSTable>().Single();
                Assert.AreEqual("ValidImport", saved.name);
                Assert.AreEqual("VALID", saved.tag);
                Assert.AreEqual(validUrl, saved.Header_url.AbsoluteUri);
                BMSTableEntry entry = verify.Table<BMSTableEntry>().Single();
                Assert.AreEqual(saved.playlist_id, entry.playlist_id);
                Assert.AreEqual(new string('b', 32), entry.md5);
                Assert.AreEqual("2", entry.folder);
            }
            string validOutput = Path.Combine(outputBase, "ValidImport");
            Assert.IsTrue(Directory.EnumerateFiles(validOutput, "*.lr2folder").Any(), "正常表の必要LR2出力まで完了します。");
            CollectionAssert.AreEqual(new[] { validOutput }, Directory.GetDirectories(outputBase));
            using (playlist.AcquirePlaylistMutationLease("after-lr2-table-url-import")) { }
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            await CleanupAsync(() => operation);
            if (workspace != null)
            {
                await CleanupAsync(workspace.WaitForDetailBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistReloadCleanupIdleAsync);
                await CleanupAsync(() => { workspace.RefreshPlaylistTreeTables(null); workspace.Dispose(); return Task.CompletedTask; });
            }
            while (auxiliaryWork.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { playlist?.RequestShutdown("lr2-table-url-import-fixture"); return Task.CompletedTask; });
            await CleanupAsync(() => { library?.RequestShutdown("lr2-table-url-import-fixture"); return Task.CompletedTask; });
            await CleanupAsync(() => { Directory.Delete(root, recursive: true); return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("LR2 Table URL import cleanup", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    /// <summary>ストア未接続では準備未完了を返し、空の選択は実行対象なしとして区別します。</summary>
    [TestMethod]
    public void TryStartPlaylistImport_ReportsNotReadyAndEmptySelection()
    {
        PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);

        Assert.AreEqual(PlaylistImportStartStatus.NotReady,
            unavailableWorkspace.TryStartPlaylistImport([new Uri("https://example.test/collection.json")]).Status);
        Assert.AreEqual(PlaylistImportStartStatus.NoTargets,
            unavailableWorkspace.TryStartPlaylistImport([]).Status);
    }

    /// <summary>ルートメニューに残る新規プレイリスト作成は、ストアの接続状態に従います。</summary>
    [TestMethod]
    public void CanCreatePlaylist_UsesWorkspaceOwnedStoreState()
    {
        PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);
        Assert.IsFalse(unavailableWorkspace.CanCreatePlaylist);

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
            Assert.IsTrue(initializedWorkspace.CanCreatePlaylist);
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
            var options = new BeatorajaBmtOptionsSnapshot { KeepBeatorajaBmtFilesWhenOutputDisabled = true };
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable admitted));
            using (admitted)
            using (LibraryFileMutationCapability capability = playlist.CreatePlaylistMutationCapability(admitted))
            {
                BeatorajaBmtPreparation preparation = await settingsPort.PrepareBeatorajaBmtSettingsAsync(options, options, capability);
                await settingsPort.CompleteBeatorajaBmtSettingsAsync(preparation, capability);
            }

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
        var options = new BeatorajaBmtOptionsSnapshot();
        BeatorajaBmtPreparation preparation = await settingsPort.PrepareBeatorajaBmtSettingsAsync(options, options, null);
        await settingsPort.CompleteBeatorajaBmtSettingsAsync(preparation, null);
    }

    /// <summary>en-USで固定旧日付を通常新規取込みへ通し、不正な実採用追加日は一表保存前に止め、訂正後の成功通知・所属・fresh DBを照合します。</summary>
    /// <remarks>条件網羅と実serializer往復はBMSTableLoadTestsへ分担し、ここでは実workspaceと保存の接続を一表で確認します。共有通知の寿命を所有するため単独実行します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task TryStartPlaylistImport_NewExternalTablePreservesFourDigitAddDateInDatabase()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        var auxiliaryWork = new ConcurrentQueue<Task>();
        var summaries = new List<ExternalPlaylistImportQueueSummary>();
        EventHandler<ExternalPlaylistImportQueueSummaryReadyEventArgs> summaryHandler = (_, request) => summaries.Add(request.Summary);
        Exception? primaryFailure = null;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US", useUserOverride: false);
            string databasePath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(databasePath);
            string headerPath = Path.Combine(directory, "header.json");
            const string tableName = "Four digit year import";
            string hash = new('e', 32);
            DateTime expected = new DateTime(2024, 12, 31, 13, 24, 35).AddTicks(1);
            File.WriteAllText(headerPath, "{\"name\":\"Four digit year import\",\"symbol\":\"D\",\"data_url\":\"./data.json\"}");
            File.WriteAllText(Path.Combine(directory, "data.json"),
                "[{\"md5\":\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\"title\":\"Date song\",\"adddate\":\"invalid initial date\"}]");
            var sourceUri = new Uri(headerPath);
            playlist = new TestBmsPlaylist(databasePath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot())
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                auxiliaryWork.Enqueue(Task.Run(work));
                return true;
            };
            library = new TestBmsLibrary(databasePath);
            workspace = CreateDetailWorkspace(out _,
                playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library);
            workspace.ExternalPlaylistImportQueueSummaryReady += summaryHandler;
            Assert.AreEqual(0, workspace.CapturePlaylistImportExistingTables().Count);

            Assert.AreEqual(PlaylistImportStartStatus.Started, workspace.TryStartPlaylistImport([sourceUri]).Status);
            await playlist.WaitForPlaylistMutationIdleAsync();
            ExternalPlaylistImportQueueSummary failed = summaries.Single();
            Assert.AreEqual(0, failed.ImportedCount);
            Assert.AreEqual(1, failed.FailedCount);
            Exception? failure = failed.Outcomes.Single().Exception;
            Assert.IsInstanceOfType<PlaylistDataParseException>(failure);
            Assert.IsNotNull(failure);
            Assert.IsInstanceOfType<PlaylistJsonDateException>(failure.InnerException);
            StringAssert.Contains(failure.Message, string.Format(BeMusicSeeker.Properties.Resources.Error_PlaylistJsonDateFormat, "adddate"));
            Assert.AreEqual(0, playlist.BMSTables.Count);
            using (LR2SongDBExtended rejected = new BmsLibraryDbGateway(databasePath).OpenSongDbReadOnly())
            { Assert.AreEqual(0, rejected.Table<BMSTable>().Count()); Assert.AreEqual(0, rejected.Table<BMSTableEntry>().Count()); }
            File.WriteAllText(Path.Combine(directory, "data.json"), "[{\"md5\":\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\"title\":\"Date song\",\"adddate\":\"2024-12-31T13:24:35.0000001\"}]");
            summaries.Clear();
            Assert.AreEqual(PlaylistImportStartStatus.Started, workspace.TryStartPlaylistImport([sourceUri]).Status);
            await playlist.WaitForPlaylistMutationIdleAsync();
            ExternalPlaylistImportQueueSummary summary = summaries.Single();
            Assert.AreEqual(1, summary.ImportedCount,
                string.Join(Environment.NewLine, summary.FailedOutcomes.Select(outcome => outcome.Exception)));
            Assert.AreEqual(0, summary.FailedCount);
            ExternalPlaylistImportOutcome outcome = summary.Outcomes.Single();
            Assert.AreEqual(sourceUri, outcome.Uri);
            Assert.AreEqual(tableName, outcome.TableName);
            BMSTable table = playlist.BMSTables.Single();
            Assert.AreEqual(tableName, table.name);
            Assert.AreEqual(sourceUri, table.Header_url);
            Assert.IsTrue(table.playlist_id > 0);
            BMSTableEntry entry = table.entries.Single();
            Assert.AreEqual(table.playlist_id, entry.playlist_id);
            Assert.AreEqual(hash, entry.md5);
            Assert.AreEqual(expected, entry.adddate);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databasePath).OpenSongDbReadOnly();
            BMSTable savedTable = verify.Table<BMSTable>().Single();
            Assert.AreEqual(table.playlist_id, savedTable.playlist_id);
            Assert.AreEqual(tableName, savedTable.name);
            BMSTableEntry saved = verify.Table<BMSTableEntry>().Single();
            Assert.AreEqual(table.playlist_id, saved.playlist_id);
            Assert.AreEqual(hash, saved.md5);
            Assert.AreEqual(expected, saved.adddate);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (playlist != null) { await CleanupAsync(playlist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null)
            {
                workspace.ExternalPlaylistImportQueueSummaryReady -= summaryHandler;
                await CleanupAsync(workspace.WaitForDetailBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistReloadCleanupIdleAsync);
                await CleanupAsync(() => { workspace.RefreshPlaylistTreeTables(null); workspace.Dispose(); return Task.CompletedTask; });
            }
            while (auxiliaryWork.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { playlist?.RequestShutdown("adddate-import-test"); return Task.CompletedTask; });
            await CleanupAsync(() => { library?.RequestShutdown("adddate-import-test"); return Task.CompletedTask; });
            if (playlist != null) { await CleanupAsync(playlist.WaitForPlaylistMutationIdleAsync); }
            await CleanupAsync(() => { Directory.Delete(directory, recursive: true); return Task.CompletedTask; });
            CultureInfo.CurrentCulture = previousCulture;
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("追加日取込みテストの後片付け", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    /// <summary>出力名を持たないUnicode実名・ASCII tagの単一JSONを登録し、実終端後に次の明示要求を受けます。各一表の結果通知・URI順・実表登録を保持します。</summary>
    [TestMethod]
    public async Task TryStartPlaylistImport_AcceptsNextExplicitRequestAfterCompletion()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        BMSPlaylist? pendingPlaylist = null;
        TestBmsLibrary? pendingLibrary = null;
        PlaylistWorkspaceViewModel? workspace = null;
        var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSummary(object? sender, ExternalPlaylistImportQueueSummaryReadyEventArgs request) => summaryReady.TrySetResult(request.Summary);
        Exception? primaryFailure = null;
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
            File.WriteAllText(firstHeaderPath, "{\"name\":\"한글\",\"symbol\":\"C\",\"tag\":\"CATALOG\",\"data_url\":\"./catalog-data.json\"}");
            File.WriteAllText(firstDataPath, "[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"title\":\"Catalog song\",\"artist\":\"Artist\",\"level\":\"3\"}]");
            File.WriteAllText(secondHeaderPath, "{\"name\":\"WalkureImport\",\"symbol\":\"W\",\"output_dir\":\"WalkureImport\",\"data_url\":\"./walkure-data.json\"}");
            File.WriteAllText(secondDataPath, "[{\"md5\":\"dddddddddddddddddddddddddddddddd\",\"title\":\"Walkure song\",\"artist\":\"Artist\",\"level\":\"4\"}]");

            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot())
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            pendingPlaylist = playlist;
            var library = new TestBmsLibrary(songDbPath);
            pendingLibrary = library;
            workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            workspace.ExternalPlaylistImportQueueSummaryReady += OnSummary;

            using (playlist.AcquirePlaylistMutationLease("explicit-import-busy"))
            {
                Assert.AreEqual(PlaylistImportStartStatus.Busy,
                    workspace.TryStartPlaylistImport([new Uri(firstHeaderPath)]).Status);
            }
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsFalse(summaryReady.Task.IsCompleted);
            Assert.AreEqual(0, playlist.BMSTables.Count);

            PlaylistImportStartResult firstStart = workspace.TryStartPlaylistImport([new Uri(firstHeaderPath)]);
            Assert.AreEqual(PlaylistImportStartStatus.Started, firstStart.Status);
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsTrue(summaryReady.Task.IsCompleted, "受理済み取込みの終端までに結果通知を完了します。");
            ExternalPlaylistImportQueueSummary first = await summaryReady.Task;
            Assert.AreEqual(1, first.ImportedCount,
                string.Join(Environment.NewLine, first.FailedOutcomes.Select(outcome => outcome.Uri + ": " + outcome.Exception)));
            Assert.AreEqual("한글", playlist.BMSTables.Single().name);
            Assert.AreEqual("CATALOG", playlist.BMSTables.Single().tag);
            Assert.IsTrue(string.IsNullOrWhiteSpace(playlist.BMSTables.Single().Output_dir));
            summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.AreEqual(PlaylistImportStartStatus.Started,
                workspace.TryStartPlaylistImport([new Uri(secondHeaderPath)]).Status);
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsTrue(summaryReady.Task.IsCompleted, "次の明示要求も実終端までに結果を通知します。");
            ExternalPlaylistImportQueueSummary second = await summaryReady.Task;
            Assert.AreEqual(1, second.ImportedCount);
            CollectionAssert.AreEqual(
                new[] { new Uri(firstHeaderPath).AbsoluteUri, new Uri(secondHeaderPath).AbsoluteUri },
                first.Outcomes.Concat(second.Outcomes).Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(
                new[] { "한글", "WalkureImport" },
                playlist.BMSTables.Select(table => table.name).ToArray());

            playlist.RequestShutdown("explicit-import-test");
            Assert.AreEqual(PlaylistImportStartStatus.ShuttingDown,
                workspace.TryStartPlaylistImport([new Uri(firstHeaderPath)]).Status);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (pendingPlaylist != null) { await CleanupAsync(pendingPlaylist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null)
            {
                workspace.ExternalPlaylistImportQueueSummaryReady -= OnSummary;
                await CleanupAsync(workspace.WaitForDetailBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistReloadCleanupIdleAsync);
                await CleanupAsync(() => { workspace.RefreshPlaylistTreeTables(null); workspace.Dispose(); return Task.CompletedTask; });
            }
            await CleanupAsync(() => { pendingPlaylist?.RequestShutdown("unicode-single-import-test"); return Task.CompletedTask; });
            await CleanupAsync(() => { pendingLibrary?.RequestShutdown("unicode-single-import-test"); return Task.CompletedTask; });
            await CleanupAsync(() => { Directory.Delete(tempDirectory, recursive: true); return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("Unicode単一JSON取込みテストの後片付け", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    /// <summary>LR2取込みでは先頭のUnicode実名由来の空出力名だけを失敗にし、後続の有効表を保存・公開・実LR2出力します。Workspace側がスタンドアロンでも実storeの捕捉モードを使い、終端で受付を解放します。</summary>
    /// <remarks>通常取込み入口から対象別除外と登録への接続を確認します。モード×単表・一括の条件網羅と保存値の詳細は登録ownerの条件表へ分担します。</remarks>
    [TestMethod]
    public async Task TryStartPlaylistImport_Lr2ModeRejectsOnlyEmptyOutputNameAndImportsValidTable()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSummary(object? sender, ExternalPlaylistImportQueueSummaryReadyEventArgs request) => summaryReady.TrySetResult(request.Summary);
        Exception? primaryFailure = null;
        try
        {
            string databasePath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(databasePath);
            string rejectedHeader = Path.Combine(directory, "unicode.json");
            string validHeader = Path.Combine(directory, "valid.json");
            File.WriteAllText(rejectedHeader, "{\"name\":\"한글\",\"symbol\":\"U\",\"tag\":\"UNICODE\",\"data_url\":\"./unicode-data.json\"}");
            File.WriteAllText(Path.Combine(directory, "unicode-data.json"), "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Rejected song\",\"level\":\"1\"}]");
            File.WriteAllText(validHeader, "{\"name\":\"ValidImport\",\"symbol\":\"V\",\"tag\":\"VALID\",\"data_url\":\"./valid-data.json\"}");
            File.WriteAllText(Path.Combine(directory, "valid-data.json"), "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Valid song\",\"level\":\"2\"}]");
            string outputBase = Path.Combine(directory, "Output");
            var outputSettings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBase,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            var configOwner = new Lr2ConfigOwner(BmsPlaylistTestSupport.CreateLr2Config(Path.Combine(directory, "LR2"), outputBase));
            playlist = new TestBmsPlaylist(databasePath, configOwner, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings, BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(databasePath, CustomFolderOutputPhysicalSurface.Empty))
            { BMSTables = [] };
            library = new TestBmsLibrary(databasePath);
            // 既存factoryのWorkspace設定はOperationModeLR2DB=falseです。登録先storeだけLR2を有効にします。
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library);
            workspace.ExternalPlaylistImportQueueSummaryReady += OnSummary;
            PlaylistImportStartResult start = workspace.TryStartPlaylistImport([new Uri(rejectedHeader), new Uri(validHeader)]);
            Assert.AreEqual(PlaylistImportStartStatus.Started, start.Status);
            Assert.AreEqual(2, start.StartedCount);
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsTrue(summaryReady.Task.IsCompleted, "受理済み要求の実終端までに対象別結果を通知します。");
            ExternalPlaylistImportQueueSummary summary = await summaryReady.Task;
            string diagnostics = string.Join(Environment.NewLine, summary.FailedOutcomes.Select(outcome => outcome.Uri + ": " + outcome.Exception));
            Assert.AreEqual(1, summary.ImportedCount, diagnostics);
            Assert.AreEqual(1, summary.FailedCount, diagnostics);
            Assert.AreEqual(0, summary.SkippedDuplicateNameCount);
            Assert.AreEqual(0, summary.SkippedDuplicateUrlCount);
            ExternalPlaylistImportOutcome rejected = summary.FailedOutcomes.Single();
            Assert.AreEqual(new Uri(rejectedHeader).AbsoluteUri, rejected.Uri.AbsoluteUri);
            Assert.IsInstanceOfType<InvalidOperationException>(rejected.Exception);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Error_OutputDirNameEmpty, rejected.Exception.Message);
            ExternalPlaylistImportOutcome imported = summary.Outcomes.Single(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.Imported);
            Assert.AreEqual(new Uri(validHeader).AbsoluteUri, imported.Uri.AbsoluteUri);
            Assert.AreEqual("ValidImport", imported.TableName);
            Assert.AreEqual("ValidImport", playlist.BMSTables.Single().name);
            using (LR2SongDBExtended read = new BmsLibraryDbGateway(databasePath).OpenSongDbReadOnly())
            {
                BMSTable saved = read.Table<BMSTable>().Single();
                Assert.AreEqual("ValidImport", saved.name);
                Assert.AreEqual("VALID", saved.tag);
                BMSTableEntry entry = read.Table<BMSTableEntry>().Single();
                Assert.AreEqual(saved.playlist_id, entry.playlist_id);
                Assert.AreEqual(new string('b', 32), entry.md5);
            }
            string validOutput = Path.Combine(outputBase, "ValidImport");
            Assert.IsTrue(Directory.Exists(validOutput));
            Assert.IsTrue(Directory.EnumerateFiles(validOutput, "*.lr2folder").Any(), "有効表の必要LR2出力を実生成します。");
            CollectionAssert.AreEqual(new[] { validOutput }, Directory.GetDirectories(outputBase));
            using (playlist.AcquirePlaylistMutationLease("after-lr2-mixed-import")) { }
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (playlist != null) { await CleanupAsync(playlist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null)
            {
                workspace.ExternalPlaylistImportQueueSummaryReady -= OnSummary;
                await CleanupAsync(workspace.WaitForDetailBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync);
                await CleanupAsync(workspace.WaitForPlaylistReloadCleanupIdleAsync);
                await CleanupAsync(() => { workspace.RefreshPlaylistTreeTables(null); workspace.Dispose(); return Task.CompletedTask; });
            }
            await CleanupAsync(() => { playlist?.RequestShutdown("lr2-mixed-output-name-import-test"); return Task.CompletedTask; });
            await CleanupAsync(() => { library?.RequestShutdown("lr2-mixed-output-name-import-test"); return Task.CompletedTask; });
            await CleanupAsync(() => { Directory.Delete(directory, recursive: true); return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("LR2混合取込みテストの後片付け", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    /// <summary>選択後に登録されたページ・相対ヘッダーURLを開始時に再照合し、全除外では処理を始めず、混在要求では同URL・実ヘッダー同名・失敗を区別して一度だけ登録します。</summary>
    [TestMethod]
    public async Task TryStartPlaylistImport_RechecksCurrentUrlsAndReportsMixedOutcomes()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        SingleRequestHttpServer? existingServer = null;
        try
        {
            string databasePath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(databasePath);
            string successPath = Path.Combine(directory, "success.json");
            string duplicateNamePath = Path.Combine(directory, "same-name.json");
            string failurePath = Path.Combine(directory, "malformed.json");
            File.WriteAllText(Path.Combine(directory, "data.json"), "[]");
            File.WriteAllText(successPath, "{\"name\":\"New table\",\"symbol\":\"N\",\"data_url\":\"./data.json\"}");
            File.WriteAllText(duplicateNamePath, "{\"name\":\"Existing table\",\"symbol\":\"E\",\"data_url\":\"./data.json\"}");
            File.WriteAllText(failurePath, "{malformed JSON");
            existingServer = new SingleRequestHttpServer(BmsPlaylistTestSupport.CreateUtf8BomBytes("{}"));
            Uri pageUri = existingServer.Address;
            Uri headerUri = new(pageUri, "header.json");
            Uri successUri = new(successPath);
            Uri sameNameUri = new(duplicateNamePath);
            Uri failureUri = new(failurePath);
            // 並列取得中に未読込みのSettingsを共有せず、このケースの固定設定を渡します。
            playlist = new TestBmsPlaylist(databasePath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot())
            { BMSTables = [] };
            library = new TestBmsLibrary(databasePath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _,
                playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library);
            var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            int notificationCount = 0;
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, request) =>
            {
                notificationCount++;
                summaryReady.TrySetResult(request.Summary);
            };
            Assert.AreEqual(0, workspace.CapturePlaylistImportExistingTables().Count);
            Uri[] selectedBeforeRegistration = [pageUri, headerUri, pageUri];
            var existing = new BMSTable
            {
                playlist_id = 1,
                name = "Existing table",
                Page_url = pageUri,
                Header_url = new Uri("header.json", UriKind.Relative)
            };
            playlist.BMSTables.Add(existing);
            playlist.CommitBMSTablesWithEntriesToDB([existing]);
            IReadOnlyList<PlaylistImportExistingTable> snapshot = workspace.CapturePlaylistImportExistingTables();
            Assert.AreEqual(pageUri.AbsoluteUri, snapshot.Single().PageUrl?.AbsoluteUri);
            Assert.AreEqual(headerUri.AbsoluteUri, snapshot.Single().HeaderUrl?.AbsoluteUri);
            existing.name = "Changed after snapshot";
            Assert.AreEqual("Existing table", snapshot.Single().Name);
            existing.name = "Existing table";

            PlaylistImportStartResult allSkipped = workspace.TryStartPlaylistImport(selectedBeforeRegistration);
            Assert.AreEqual(PlaylistImportStartStatus.NoTargets, allSkipped.Status);
            Assert.AreEqual(0, allSkipped.StartedCount);
            Assert.AreEqual(2, allSkipped.SkippedDuplicateUrlCount);
            Assert.AreEqual(0, notificationCount);
            using (playlist.AcquirePlaylistMutationLease("all-url-skipped-released")) { }
            Assert.IsFalse(existingServer.HeadersSent.Task.IsCompleted);

            PlaylistImportStartResult started = workspace.TryStartPlaylistImport(
                [pageUri, successUri, sameNameUri, failureUri, headerUri, successUri]);
            Assert.AreEqual(PlaylistImportStartStatus.Started, started.Status);
            Assert.AreEqual(3, started.StartedCount);
            Assert.AreEqual(2, started.SkippedDuplicateUrlCount);
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsTrue(summaryReady.Task.IsCompleted);
            ExternalPlaylistImportQueueSummary summary = await summaryReady.Task;
            Assert.AreEqual(1, notificationCount);
            Assert.AreEqual(1, summary.ImportedCount,
                string.Join(Environment.NewLine, summary.FailedOutcomes.Select(outcome => outcome.Uri + ": " + outcome.Exception)));
            Assert.AreEqual(2, summary.SkippedDuplicateUrlCount);
            Assert.AreEqual(1, summary.SkippedDuplicateNameCount);
            Assert.AreEqual(1, summary.FailedCount);
            CollectionAssert.AreEqual(new[] { pageUri.AbsoluteUri, headerUri.AbsoluteUri },
                summary.SkippedDuplicateUrlOutcomes.Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            Assert.AreEqual(sameNameUri.AbsoluteUri, summary.SkippedDuplicateNameOutcomes.Single().Uri.AbsoluteUri);
            Assert.AreEqual("Existing table", summary.SkippedDuplicateNameOutcomes.Single().TableName);
            Assert.AreEqual(failureUri.AbsoluteUri, summary.FailedOutcomes.Single().Uri.AbsoluteUri);
            Assert.IsFalse(existingServer.HeadersSent.Task.IsCompleted, "登録済みURLはネットワークへ渡しません。");
            CollectionAssert.AreEqual(new[] { "Existing table", "New table" }, playlist.BMSTables.Select(table => table.name).ToArray());
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databasePath).OpenSongDbReadOnly();
            CollectionAssert.AreEquivalent(new[] { "Existing table", "New table" }, verify.Table<BMSTable>().Select(table => table.name).ToArray());
        }
        finally
        {
            playlist?.RequestShutdown("mixed-import-test");
            if (existingServer != null) { await existingServer.DisposeAsync(); }
            if (playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }
            library?.RequestShutdown("mixed-import-test");
            Directory.Delete(directory, recursive: true);
        }
    }


    /// <summary>catalog取得gate中は非同期Taskを保留し、取得完了後にtag分類と表名を公開して未完了の結果を先に表示しません。</summary>
    [TestMethod]
    public async Task ExternalTableListCatalog_SlowFetchDoesNotBlockCallerAndPublishesAfterCompletion()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        Assert.AreEqual(PlaylistImportCatalogState.NotStarted, workspace.ExternalTableListState);
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
            Assert.AreEqual(PlaylistImportCatalogState.Loading, workspace.ExternalTableListState);
            fetchGate.TrySetResult([table]);
            await loadTask.ConfigureAwait(false);

            Assert.AreEqual(PlaylistImportCatalogState.Loaded, workspace.ExternalTableListState);
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

    /// <summary>空の正常応答と取得失敗を別の状態にし、取得失敗を空の成功として公開しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExternalTableListCatalog_DistinguishesEmptySuccessFromFailure(bool fails)
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        await workspace.LoadExternalTableCollectionAsync(new Uri("https://example.test/catalog"),
            (_, _) => fails
                ? Task.FromException<IReadOnlyList<BMSTableSimple>>(new IOException("catalog unavailable"))
                : Task.FromResult<IReadOnlyList<BMSTableSimple>>([]));

        Assert.AreEqual(fails ? PlaylistImportCatalogState.Failed : PlaylistImportCatalogState.Loaded,
            workspace.ExternalTableListState);
        if (fails)
        {
            Assert.IsNull(workspace.BMSExternalTableListExt);
        }
        else
        {
            Assert.IsNotNull(workspace.BMSExternalTableListExt);
            Assert.AreEqual(0, workspace.BMSExternalTableListExt.Children.Count);
        }
    }

    /// <summary>終了が受付前なら後着fetchを開始せず、登録後のUI待機中ならfetch前に終端し、実fetch中ならtokenへ取消を届けます。どの順序も実Taskを回収し、終了後の後発要求と表示更新を拒否します。</summary>
    [DataTestMethod]
    [DataRow("before_registration")]
    [DataRow("before_fetch")]
    [DataRow("during_fetch")]
    public async Task ExternalTableListCatalog_ShutdownCancelsAndDoesNotPublishStaleCatalog(string shutdownPhase)
    {
        var uiEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int uiApplications = 0;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, restoreUiApplyScheduler: async action =>
        {
            if (Interlocked.Increment(ref uiApplications) == 1 && shutdownPhase == "before_fetch")
            {
                uiEntered.TrySetResult();
                await uiRelease.Task.ConfigureAwait(false);
            }
            action();
        });
        bool shutdownIssued = false;
        int fetchCount = 0;
        var statesAfterShutdown = new ConcurrentQueue<PlaylistImportCatalogState>();
        workspace.PropertyChanged += (_, args) =>
        {
            if (shutdownIssued && args.PropertyName == nameof(workspace.ExternalTableListState))
            {
                statesAfterShutdown.Enqueue(workspace.ExternalTableListState);
            }
        };
        void Shutdown()
        {
            shutdownIssued = true;
            workspace.CancelExternalTableCollectionLoadForShutdown();
        }
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

        if (shutdownPhase == "before_registration") { Shutdown(); }
        Task loadTask = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/catalog"),
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref fetchCount);
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () => cancellationObserved.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return staleCatalog;
            });

        try
        {
            Assert.AreEqual(shutdownPhase == "during_fetch" ? 1 : 0, Volatile.Read(ref fetchCount));
            if (shutdownPhase == "before_fetch")
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(uiEntered.Task, loadTask, "external catalog UI before fetch");
            }
            if (shutdownPhase != "before_registration")
            {
                Assert.IsFalse(loadTask.IsCompleted);
                Shutdown();
            }
            uiRelease.TrySetResult();
            if (shutdownPhase == "during_fetch")
            {
                // 取消の伝播を通知で確認する。workerの割当て時間は取消契約に含めない。
                await TestUiDispatcherHost.AwaitNotificationAsync(cancellationObserved.Task, loadTask, "external catalog shutdown cancellation");
            }
            await loadTask.ConfigureAwait(false);
            await workspace.LoadExternalTableCollectionAsync(new Uri("https://example.test/after-shutdown"), (_, _) =>
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult<IReadOnlyList<BMSTableSimple>>(staleCatalog);
            });

            Assert.AreEqual(shutdownPhase == "during_fetch" ? 1 : 0, Volatile.Read(ref fetchCount));
            Assert.IsTrue(loadTask.IsCompletedSuccessfully);
            Assert.AreEqual(PlaylistImportCatalogState.NotStarted, workspace.ExternalTableListState);
            Assert.IsTrue(statesAfterShutdown.All(state => state == PlaylistImportCatalogState.NotStarted));
            Assert.IsNull(workspace.BMSExternalTableListExt);
        }
        finally
        {
            Shutdown();
            uiRelease.TrySetResult();
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

    /// <summary>入力の第一分類順と第二分類の名前順を保ち、分類のない表を第一分類直下へ配置します。</summary>
    [TestMethod]
    public void ExternalTableListCatalog_PreservesCategoryAndLeafOrder()
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

    /// <summary>通常初回入口でURL転送後のheader/dataを互換読取りし、local JSONのheader/dataはHTTP参照でもStrictに保ちます。local HTMLの分類も本文で決めます。</summary>
    [DataTestMethod]
    [DataRow("url")]
    [DataRow("local-header")]
    [DataRow("local-data")]
    [DataRow("local-html")]
    public async Task TryStartPlaylistImport_DatePolicyFollowsOriginalSourceAndBodyKind(string sourceKind)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tasks = new ConcurrentQueue<Task>();
        var servers = new List<SingleRequestHttpServer>();
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Exception? primary = null;
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US", useUserOverride: false);
            string path = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(path);
            const string rows = "[{\"title\":\"Keep first\",\"adddate\":\"2026-10-19T03:04:05.0000001\"},{\"title\":\"Keep invalid\",\"artist\":\"Other\",\"adddate\":\"12/11/2025\"}]";
            var data = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(rows)); servers.Add(data);
            string headerText = new JObject { ["name"] = "Date policy", ["symbol"] = "D", ["last_update"] = sourceKind == "local-data" ? "2026-10-19" : "2026.10.19", ["data_url"] = data.Address.AbsoluteUri }.ToString();
            Uri source;
            if (sourceKind == "url")
            {
                var header = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(headerText)); servers.Add(header);
                var redirect = new SingleRequestHttpServer([], responseStatusCode: HttpStatusCode.Redirect,
                    additionalResponseHeaders: "Location: " + header.Address.AbsoluteUri + "\r\n"); servers.Add(redirect);
                source = redirect.Address;
            }
            else
            {
                string header = Path.Combine(directory, "header.json"); File.WriteAllText(header, headerText);
                source = new Uri(header);
                if (sourceKind == "local-html")
                {
                    string html = Path.Combine(directory, "source.json");
                    File.WriteAllText(html, "<html><head><meta name=\"bmstable\" content=\"header.json\"></head></html>");
                    source = new Uri(html);
                }
            }
            playlist = new TestBmsPlaylist(path, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot())
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) => { tasks.Enqueue(Task.Run(work)); return true; };
            library = new TestBmsLibrary(path);
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library);
            ExternalPlaylistImportQueueSummary? summary = null;
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, args) => summary = args.Summary;
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            DateTime before = DateTime.Now;
            Assert.AreEqual(PlaylistImportStartStatus.Started, workspace.TryStartPlaylistImport([source]).Status);
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsNotNull(summary);
            bool strict = sourceKind is "local-header" or "local-data";
            Assert.AreEqual(strict ? 0 : 1, summary.ImportedCount);
            Assert.AreEqual(strict ? 1 : 0, summary.FailedCount);
            using LR2SongDBExtended db = new BmsLibraryDbGateway(path).OpenSongDbReadOnly();
            if (strict)
            {
                Assert.AreEqual(0, playlist.BMSTables.Count);
                Assert.AreEqual(0, db.Table<BMSTable>().Count());
                Assert.AreEqual(0, db.Table<BMSTableEntry>().Count());
                Exception? error = summary.Outcomes.Single().Exception;
                Assert.IsNotNull(error);
                Assert.IsInstanceOfType<PlaylistJsonDateException>(error.InnerException);
                StringAssert.Contains(error.Message, string.Format(BeMusicSeeker.Properties.Resources.Error_PlaylistJsonDateFormat, sourceKind == "local-header" ? "last_update" : "adddate"));
                Assert.AreEqual(sourceKind == "local-header" ? 0 : 1, data.RequestTargets.Length);
            }
            else
            {
                BMSTable saved = db.Table<BMSTable>().Single();
                Assert.AreEqual(new DateTime(2026, 10, 19), saved.last_update);
                BMSTableEntry[] entries = db.Table<BMSTableEntry>().OrderBy(entry => entry.title).ToArray();
                Assert.AreEqual(2, entries.Length);
                Assert.AreEqual(new DateTime(2026, 10, 19, 3, 4, 5).AddTicks(1), entries.Single(entry => entry.title == "Keep first").adddate);
                BMSTableEntry invalid = entries.Single(entry => entry.title == "Keep invalid");
                Assert.AreEqual("Other", invalid.artist);
                Assert.IsTrue(invalid.adddate >= before && invalid.adddate <= DateTime.Now);
                Assert.IsTrue(workspace.CapturePlaylistSyncStatusSnapshot().Values.All(status => !status.HasFailureStatus));
            }

        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var failures = new List<Exception>();
            async Task Clean(Func<Task> work) { try { await work(); } catch (Exception error) { failures.Add(error); } }
            foreach (SingleRequestHttpServer server in servers) { server.ReleaseBody.TrySetResult(); server.ReleaseBodyPrefix.TrySetResult(); }
            if (playlist != null) { await Clean(playlist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null)
            {
                await Clean(workspace.WaitForDetailBuildIdleAsync); await Clean(workspace.WaitForPlaylistSummaryDataBuildIdleAsync);
                await Clean(workspace.WaitForPlaylistReloadCleanupIdleAsync);
                await Clean(() => { workspace.RefreshPlaylistTreeTables(null); workspace.Dispose(); return Task.CompletedTask; });
            }
            while (tasks.TryDequeue(out Task? task)) { await Clean(() => task); }
            playlist?.RequestShutdown("date-policy-import"); library?.RequestShutdown("date-policy-import");
            foreach (SingleRequestHttpServer server in servers) { await Clean(async () => await server.DisposeAsync()); }
            await Clean(() => { Directory.Delete(directory, recursive: true); return Task.CompletedTask; });
            CultureInfo.CurrentCulture = previousCulture;
            if (failures.Count > 0) { if (primary == null) { throw new AggregateException(failures); } Console.WriteLine(new AggregateException(failures)); }
        }
    }

    /// <summary>Table URLのheader/data不適合は新しい外部表の全行を採用し、有効cacheへ戻りません。local JSON初回はcacheがあってもStrict拒否を維持します。</summary>
    [TestMethod]
    public async Task StartBeatorajaTableUrlImport_InvalidOptionalDatesKeepExternalRowsAndStrictLocalCacheBoundary()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceExternalSourceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null; TestBmsLibrary? library = null; PlaylistWorkspaceViewModel? workspace = null;
        var tasks = new ConcurrentQueue<Task>();
        var servers = new List<SingleRequestHttpServer>();
        Task operation = Task.CompletedTask;
        Exception? primary = null;
        try
        {
            string path = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(path);
            string data = Path.Combine(directory, "data.json");
            File.WriteAllText(data, "[{\"title\":\"Keep valid\",\"adddate\":\"2026-10-19\"},{\"title\":\"Keep invalid\",\"adddate\":1700000000}]");
            var header = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(new JObject { ["name"] = "External dates", ["last_update"] = "not a date", ["data_url"] = new Uri(data).AbsoluteUri }.ToString()));
            servers.Add(header);
            string local = Path.Combine(directory, "local.json");
            File.WriteAllText(local, new JObject { ["name"] = "Strict local", ["last_update"] = "not a date", ["data_url"] = new Uri(data).AbsoluteUri }.ToString());
            string[] urls = [header.Address.AbsoluteUri, new Uri(local).AbsoluteUri];
            string beatoraja = Path.Combine(directory, "beatoraja"), output = Path.Combine(beatoraja, "table");
            Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(beatoraja, "beatoraja.jar"), []);
            File.WriteAllText(Path.Combine(beatoraja, "config_sys.json"), new JObject { ["tablepath"] = "table", ["tableURL"] = new JArray(urls) }.ToString());
            foreach (string url in urls)
            {
                WriteBmtJson(BeatorajaBmtTableImportService.GetCachedTablePath(output, url), new JObject
                { ["url"] = url, ["name"] = "Stale cache", ["folder"] = new JArray(new JObject { ["name"] = "1", ["songs"] = new JArray(new JObject { ["title"] = "stale", ["md5"] = new string('a', 32) }) }) });
            }
            var options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaRootPath = beatoraja, RegisterBeatorajaBmtUrls = true };
            playlist = new TestBmsPlaylist(path, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => options,
                () => new CustomFolderOutputSettingsSnapshot(), BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(path, CustomFolderOutputPhysicalSurface.Empty))
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) => { tasks.Enqueue(Task.Run(work)); return true; };
            library = new TestBmsLibrary(path);
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library,
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables));
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.BeatorajaTableUrlImportConfirmationRequested += (_, args) => args.Confirmed = true;
            BeatorajaTableUrlImportSummary? summary = null;
            workspace.BeatorajaTableUrlImportSummaryReady += (_, args) => summary = args.Summary;
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            operation = workspace.StartBeatorajaTableUrlImport(beatoraja); await operation;
            Assert.IsNotNull(summary);
            Assert.AreEqual(1, summary.ImportedCount); Assert.AreEqual(1, summary.FailedCount);
            Assert.AreEqual(0, summary.RestoredFromBmtCount); Assert.AreEqual(0, summary.WarningCount);
            CollectionAssert.AreEqual(new[] { BeatorajaTableUrlImportOutcomeKind.Imported, BeatorajaTableUrlImportOutcomeKind.Failed }, summary.Outcomes.Select(item => item.Kind).ToArray());
            Assert.IsInstanceOfType<PlaylistHeaderParseException>(summary.Outcomes[1].Exception);
            Assert.IsInstanceOfType<PlaylistJsonDateException>(summary.Outcomes[1].Exception?.InnerException);
            using LR2SongDBExtended db = new BmsLibraryDbGateway(path).OpenSongDbReadOnly();
            Assert.AreEqual("External dates", db.Table<BMSTable>().Single().name);
            CollectionAssert.AreEquivalent(new[] { "Keep valid", "Keep invalid" }, db.Table<BMSTableEntry>().Select(entry => entry.title).ToArray());
            Assert.IsFalse(workspace.CapturePlaylistSyncStatusSnapshot()["id:" + playlist.BMSTables.Single().playlist_id].HasFailureStatus);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var failures = new List<Exception>();
            async Task Clean(Func<Task> work) { try { await work(); } catch (Exception error) { failures.Add(error); } }
            foreach (SingleRequestHttpServer server in servers) { server.ReleaseBody.TrySetResult(); server.ReleaseBodyPrefix.TrySetResult(); }
            await Clean(() => operation);
            if (playlist != null) { await Clean(playlist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null)
            {
                await Clean(workspace.WaitForDetailBuildIdleAsync); await Clean(workspace.WaitForPlaylistSummaryDataBuildIdleAsync); await Clean(workspace.WaitForPlaylistReloadCleanupIdleAsync);
                await Clean(() => { workspace.RefreshPlaylistTreeTables(null); workspace.Dispose(); return Task.CompletedTask; });
            }
            while (tasks.TryDequeue(out Task? task)) { await Clean(() => task); }
            playlist?.RequestShutdown("date-table-url"); library?.RequestShutdown("date-table-url");
            foreach (SingleRequestHttpServer server in servers) { await Clean(async () => await server.DisposeAsync()); }
            await Clean(() => { Directory.Delete(directory, recursive: true); return Task.CompletedTask; });
            if (failures.Count > 0) { if (primary == null) { throw new AggregateException(failures); } Console.WriteLine(new AggregateException(failures)); }
        }
    }

}
