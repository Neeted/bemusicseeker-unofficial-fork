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

/// <summary>実外部再取得のDB確定、UI置換、取消と必要出力の終端を確認します。</summary>
/// <remarks>実JSON・SQLiteと必要なHTTP本文を使い、UI配送はWindowを作らず共有schedulerで保持します。メニューや実Windowの接続は上位が担い、失敗した正本・公開・出力を別々に観測します。</remarks>
[TestClass]
public sealed class BmsPlaylistExternalReloadTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>不適合日時のhash初期化では日時を保ち、同原文を再保存せず、既知header/data原文差だけ更新日時を進めます。旧adddate/memoは実mergeと新DBで保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_InvalidDatesKeepLocalValuesAndSkipUnchangedWrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tasks = new ConcurrentQueue<Task>();
        TestBmsPlaylist? playlist = null;
        Exception? primary = null;
        try
        {
            string path = CreateTempSongDbPath(directory); PlaylistPersistenceRepository.EnsureSchema(path);
            string headerPath = Path.Combine(directory, "header.json"), dataPath = Path.Combine(directory, "data.json");
            string header = "{\"name\":\"Hash dates\",\"last_update\":\"bad A\",\"data_url\":\"data.json\",\"unknown\":19}";
            const string data = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Keep\",\"adddate\":1700000000,\"unknown\":23}]";
            File.WriteAllText(headerPath, header); File.WriteAllText(dataPath, data);
            testSettings.EnablePlaylistUrlCompletion = false; testSettings.OperationModeLR2DB = false;
            playlist = new TestBmsPlaylist(path, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings)
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) => { tasks.Enqueue(Task.Run(work)); return true; };
            var seed = new BMSTable();
            seed.LoadHeaderJSON(header, PlaylistJsonDatePolicy.Compatible, __header_url: new Uri(headerPath));
            seed.LoadDataJSON(data, PlaylistJsonDatePolicy.Compatible);
            seed.playlist_id = 7920; seed.header_sha256 = null; seed.data_sha256 = null; seed.is_bmt_output = false;
            DateTime originalUpdate = new(2020, 2, 3, 4, 5, 6), originalAdd = new(2019, 1, 2, 3, 4, 5);
            seed.last_update = originalUpdate; seed.entries.Single().adddate = originalAdd; seed.entries.Single().memo = "Keep memo";
            playlist.BMSTables.Add(seed); playlist.CommitBMSTableWithEntriesToDB(seed);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult initialized = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([seed]);
            initialized.ThrowIfFailed();
            Assert.IsFalse(initialized.Single().Updated); Assert.IsTrue(initialized.Single().StatePersisted);
            BMSTable current = playlist.BMSTables.Single();
            Assert.AreEqual(originalUpdate, current.last_update);
            Assert.IsFalse(string.IsNullOrEmpty(current.header_sha256)); Assert.IsFalse(string.IsNullOrEmpty(current.data_sha256));
            using (var db = new LR2SongDBExtended(path))
            {
                db.Execute("CREATE TRIGGER date_no_header_write BEFORE INSERT ON playlist WHEN NEW.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged header write'); END;");
                db.Execute("CREATE TRIGGER date_no_entry_write BEFORE INSERT ON playlist_entry WHEN NEW.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged entry write'); END;");
                db.Execute("CREATE TRIGGER date_no_header_update BEFORE UPDATE ON playlist WHEN OLD.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged header update'); END;");
                db.Execute("CREATE TRIGGER date_no_entry_update BEFORE UPDATE ON playlist_entry WHEN OLD.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged entry update'); END;");
                db.Execute("CREATE TRIGGER date_no_header_delete BEFORE DELETE ON playlist WHEN OLD.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged header delete'); END;");
                db.Execute("CREATE TRIGGER date_no_entry_delete BEFORE DELETE ON playlist_entry WHEN OLD.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged entry delete'); END;");
            }
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([current]);
            unchanged.ThrowIfFailed(); Assert.IsFalse(unchanged.Single().StatePersisted); Assert.IsFalse(unchanged.Single().Updated);
            using (var db = new LR2SongDBExtended(path))
            {
                foreach (string name in new[] { "date_no_header_write", "date_no_entry_write", "date_no_header_update", "date_no_entry_update", "date_no_header_delete", "date_no_entry_delete" }) { db.Execute("DROP TRIGGER " + name); }
            }
            header = header.Replace("bad A", "bad B", StringComparison.Ordinal); File.WriteAllText(headerPath, header);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult changedHeader = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([playlist.BMSTables.Single()]);
            changedHeader.ThrowIfFailed(); Assert.IsTrue(changedHeader.Single().Updated);
            current = playlist.BMSTables.Single(); Assert.IsTrue(current.last_update > originalUpdate);
            // dataの空白も原文hashの差として残します。日時キーや未知キーを削りません。
            File.WriteAllText(dataPath, " \n" + data);
            DateTime beforeDataReload = DateTime.Now;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult changedData = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([current]);
            changedData.ThrowIfFailed(); Assert.IsTrue(changedData.Single().Updated);
            current = playlist.BMSTables.Single(); Assert.IsTrue(current.last_update >= beforeDataReload && current.last_update <= DateTime.Now);
            Assert.AreEqual(originalAdd, current.entries.Single().adddate); Assert.AreEqual("Keep memo", current.entries.Single().memo);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(path).OpenSongDbReadOnly();
            BMSTable persisted = verify.Table<BMSTable>().Single(); BMSTableEntry entry = verify.Table<BMSTableEntry>().Single();
            Assert.AreEqual(current.last_update, persisted.last_update); Assert.AreEqual(originalAdd, entry.adddate); Assert.AreEqual("Keep memo", entry.memo);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var failures = new List<Exception>();
            async Task Clean(Func<Task> work) { try { await work(); } catch (Exception error) { failures.Add(error); } }
            if (playlist != null) { await Clean(playlist.WaitForPlaylistMutationIdleAsync); }
            while (tasks.TryDequeue(out Task? task)) { await Clean(() => task); }
            playlist?.RequestShutdown("date-hash-reload");
            await Clean(() => { Directory.Delete(directory, recursive: true); return Task.CompletedTask; });
            if (failures.Count > 0) { if (primary == null) { throw new AggregateException(failures); } Console.WriteLine(new AggregateException(failures)); }
        }
    }

    /// <summary>同本文の実data転送A→Bを既存BMT/台帳を残す通常reloadへ通し、相対URL1/2をDBと実gzipへ反映します。次の同入力通常出力は投影/writeを省略します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_RedirectedRelativeUrlsReachExistingBmtAndNextOutputSkips()
    {
        const string dataJson = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Relative song\",\"level\":\"1\",\"url\":\"../packages/song.zip?Token=A\",\"url_diff\":\"patches/fix.zip?Key=B\"}]";
        var final = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(dataJson));
        Uri finalData = new(final.Address, "published/current/rows.json");
        var redirect = new SingleRequestHttpServer([], responseStatusCode: HttpStatusCode.Redirect,
            additionalResponseHeaders: "Location: " + finalData.AbsoluteUri + "\r\n");
        string headerJson = "{\"name\":\"Relative context\",\"symbol\":\"R\",\"data_url\":\"" + new Uri(redirect.Address, "data.json").AbsoluteUri + "\"}";
        var header = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(headerJson));
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        TestBmsPlaylist? playlist = null;
        Task? operation = null;
        Exception? failure = null;
        var acceptedWork = new System.Collections.Concurrent.ConcurrentQueue<(string Store, Task Work)>();
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OwnStartupWork(TestBmsPlaylist store, string storeName)
        {
            store.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                // 元のHTTP/DB/BMT観測までは保持し、受理した実delegateを一度呼ぶunwrapped Taskを所有する。
                var task = Task.Run(async () =>
                {
                    await releaseWork.Task.ConfigureAwait(false);
                    await work().ConfigureAwait(false);
                });
                acceptedWork.Enqueue((storeName, task));
                return true;
            };
        }

        try
        {
            testSettings.EnablePlaylistUrlCompletion = false;
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string bmtDirectory = Path.Combine(directory, "bmt");
            playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot
                {
                    EnableBeatorajaBmtOutput = true,
                    BeatorajaBmtTablePath = bmtDirectory,
                    BeatorajaBmtHashOutputMode = nameof(BeatorajaBmtHashOutputMode.Original)
                },
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings)
            { BMSTables = new ObservableCollection<BMSTable>() };
            OwnStartupWork(playlist, nameof(playlist));
            Uri originalHeader = new(header.Address, "original/header.json");
            var original = new BMSTable(headerJson, null, originalHeader)
            {
                Data_url = new Uri("https://old-data.test/archive/rows.json"),
                name = "Manual relative table",
                last_update = new DateTime(2024, 6, 1, 10, 20, 30)
            };
            // data基準をAへ確定してから同じ本文を解析し、事前gzipの相対URLを実際にAへ解決する。
            original.LoadDataJSON(dataJson);
            original.entries.Single().memo = "local memo";
            original.entries.Single().adddate = new DateTime(2023, 7, 15, 9, 30, 0);
            operation = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(original, false, "relative-bmt-seed", cancellation.Token);
            await operation;
            string bmtPath = Path.Combine(bmtDirectory, BMSTable.ComputeSha256Hex(originalHeader.AbsoluteUri) + ".bmt");
            string manifestPath = Path.Combine(bmtDirectory, BmtTableExportService.ManifestFileName);
            Assert.IsTrue(File.Exists(manifestPath));
            JObject oldSong = ReadRedirectedBmtSong(bmtPath);
            Assert.AreEqual("https://old-data.test/packages/song.zip?Token=A", oldSong.Value<string>("url"));
            Assert.AreEqual("https://old-data.test/archive/patches/fix.zip?Key=B", oldSong.Value<string>("appendurl"));
            DateTime lastUpdate = original.last_update;
            DateTime addDate = original.entries.Single().adddate;
            string headerHash = original.header_sha256, dataHash = original.data_sha256;
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            playlist.BmtOutput.ExportProgressReporter = progress.Enqueue;

            Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult> reload = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [original], reason: "redirect-relative-bmt", cancellationToken: cancellation.Token);
            operation = reload;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await reload;
            results.ThrowIfFailed();
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = results.Single();
            BMSTable current = playlist.BMSTables.Single();
            Uri expectedUrl = new(final.Address, "published/packages/song.zip?Token=A");
            Uri expectedDiff = new(final.Address, "published/current/patches/fix.zip?Key=B");
            Assert.AreSame(current, result.ResultTable);
            Assert.AreNotSame(original, current);
            Assert.IsFalse(result.Updated);
            Assert.IsTrue(result.StatePersisted);
            Assert.IsTrue(result.NeedsBmtExport);
            Assert.IsNotNull(result.UpdateReceipt);
            Assert.IsTrue(result.UpdateReceipt.ReferenceEntriesChanged);
            Assert.AreEqual(originalHeader, current.Header_url);
            Assert.AreEqual(finalData, current.GetAbsoluteDataUrl());
            Assert.AreEqual(headerHash, current.header_sha256);
            Assert.AreEqual(dataHash, current.data_sha256);
            Assert.AreEqual(lastUpdate, current.last_update);
            Assert.AreEqual(original.playlist_id, current.playlist_id);
            Assert.AreEqual("Manual relative table", current.name);
            Assert.AreEqual("local memo", current.entries.Single().memo);
            Assert.AreEqual(addDate, current.entries.Single().adddate);
            Assert.AreEqual(expectedUrl, current.entries.Single().Url);
            Assert.AreEqual(expectedDiff, current.entries.Single().Url_diff);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                LR2SongDBExtended.playlist_entry persisted = db.Table<LR2SongDBExtended.playlist_entry>().Single();
                Assert.AreEqual(expectedUrl, new Uri(persisted.url));
                Assert.AreEqual(expectedDiff, new Uri(persisted.url_diff));
                Assert.AreEqual(lastUpdate, db.Table<BMSTable>().Single().last_update);
            }
            JObject newSong = ReadRedirectedBmtSong(bmtPath);
            Assert.AreEqual(expectedUrl.AbsoluteUri, newSong.Value<string>("url"));
            Assert.AreEqual(expectedDiff.AbsoluteUri, newSong.Value<string>("appendurl"));
            Assert.IsTrue(progress.Any(snapshot => snapshot.IsActive), "URL解決先の差をmetadataだけで誤skipせず必要投影へ進めます。");
            byte[] updatedBytes = File.ReadAllBytes(bmtPath);
            DateTime updatedTime = File.GetLastWriteTimeUtc(bmtPath);
            progress.Clear();

            // 同じ実file状態を保った次の通常要求。mtime操作・force・追加HTTP取得で省略判定を迂回しない。
            operation = playlist.BmtOutput.ExportTablesAsync([current], "redirect-relative-bmt-repeat");
            await operation;
            Assert.IsFalse(progress.Any(snapshot => snapshot.IsActive));
            Assert.AreEqual(updatedTime, File.GetLastWriteTimeUtc(bmtPath));
            CollectionAssert.AreEqual(updatedBytes, File.ReadAllBytes(bmtPath));
            BmtTableExportService.PlaylistExportMetadata metadata = BmtTableExportService.CreatePlaylistExportMetadata(current);
            Assert.IsFalse(BmtTableExportService.CreateExportPlan(bmtDirectory, [metadata], cleanupStaleManagedFiles: false).RequiresProjection(metadata));
            CollectionAssert.AreEqual(new[] { "/original/header.json" }, header.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/data.json" }, redirect.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/published/current/rows.json" }, final.RequestTargets);
            await Task.WhenAll(header.Completion, redirect.Completion, final.Completion);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                try { cancellation.Cancel(); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
                foreach (SingleRequestHttpServer server in new[] { header, redirect, final })
                {
                    server.ReleaseBodyPrefix.TrySetResult();
                    server.ReleaseBody.TrySetResult();
                }
                if (operation != null)
                {
                    try { await operation; }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception ex) { cleanupFailures.Add(ex); }
                }
                foreach (SingleRequestHttpServer server in new[] { header, redirect, final })
                {
                    try { await server.DisposeAsync(); }
                    catch (Exception ex) { cleanupFailures.Add(ex); }
                }
            }
            finally { releaseWork.TrySetResult(); }
            // 主operationとHTTPのproducer終端後に固定し、store別の実work異常も主失敗と区別する。
            foreach (IGrouping<string, (string Store, Task Work)> storeWork in acceptedWork.ToArray().GroupBy(item => item.Store))
            {
                var workCompletion = Task.WhenAll(storeWork.Select(item => item.Work));
                try { await workCompletion; }
                catch (Exception ex)
                {
                    cleanupFailures.Add(new InvalidOperationException(
                        "転送テストの任意startup work終了失敗: " + storeWork.Key, workCompletion.Exception ?? ex));
                }
            }
            if (playlist != null)
            {
                try
                {
                    playlist.RequestShutdown("redirect-relative-bmt-complete");
                    await playlist.WaitForPlaylistMutationIdleAsync();
                    Assert.IsFalse(playlist.HasShutdownBlockingWork);
                }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) { cleanupFailures.Add(ex); }
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException(cleanupFailures);
                if (failure == null) { throw cleanupFailure; }
                System.Diagnostics.Trace.WriteLine("転送BMTのcleanup二次失敗: " + cleanupFailure);
            }
        }
    }

    /// <summary>書込み終端後に毎回新しいgzip readerを開き、実媒体に保存された単一曲を返します。</summary>
    private static JObject ReadRedirectedBmtSong(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var table = JObject.Parse(reader.ReadToEnd());
        return (JObject)table.SelectTokens("folder[*].songs[*]").Single();
    }

    /// <summary>同じ本文・全絶対entryの転送先dataだけが移動しても通常reloadで保存・新参照公開し、日時・hash・ローカル属性・元同期元を維持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_RedirectedDataContextPersistsWithoutUpdatedFlag()
    {
        const string dataJson = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Absolute song\",\"level\":\"1\",\"url\":\"https://packages.test/song.zip\",\"url_diff\":\"https://packages.test/fix.zip\"}]";
        var final = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(dataJson));
        Uri finalData = new(final.Address, "published/current/rows.json");
        var redirect = new SingleRequestHttpServer([], responseStatusCode: HttpStatusCode.Redirect,
            additionalResponseHeaders: "Location: " + finalData.AbsoluteUri + "\r\n");
        string headerJson = "{\"name\":\"Context table\",\"symbol\":\"C\",\"data_url\":\"" + new Uri(redirect.Address, "data.json").AbsoluteUri + "\"}";
        var header = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(headerJson));
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        TestBmsPlaylist? playlist = null, reader = null;
        Task? operation = null;
        Exception? failure = null;
        var acceptedWork = new System.Collections.Concurrent.ConcurrentQueue<(string Store, Task Work)>();
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OwnStartupWork(TestBmsPlaylist store, string storeName)
        {
            store.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                // 元のHTTP/DB/BMT観測までは保持し、受理した実delegateを一度呼ぶunwrapped Taskを所有する。
                var task = Task.Run(async () =>
                {
                    await releaseWork.Task.ConfigureAwait(false);
                    await work().ConfigureAwait(false);
                });
                acceptedWork.Enqueue((storeName, task));
                return true;
            };
        }

        try
        {
            testSettings.EnablePlaylistUrlCompletion = false;
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings)
            { BMSTables = new ObservableCollection<BMSTable>() };
            OwnStartupWork(playlist, nameof(playlist));
            Uri originalHeader = new(header.Address, "original/header.json");
            // 過去の成功時と同じ本文をparserへ通し、移動前の実data基準だけを保存済み状態にする。
            var original = new BMSTable(headerJson, null, originalHeader, dataJson)
            {
                Data_url = new Uri("https://old-data.test/archive/rows.json"),
                name = "Manual table name",
                symbol = "M",
                last_update = new DateTime(2024, 6, 1, 10, 20, 30),
                is_bmt_output = false
            };
            original.EnableExternalSync();
            original.entries.Single().memo = "local memo";
            original.entries.Single().adddate = new DateTime(2023, 7, 15, 9, 30, 0);
            operation = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(original, false, "redirect-context-seed", cancellation.Token);
            await operation;
            DateTime oldLastUpdate = original.last_update;
            DateTime oldAddDate = original.entries.Single().adddate;
            string oldHeaderHash = original.header_sha256;
            string oldDataHash = original.data_sha256;
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt? published = null;
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, args) => published = args.Receipt;

            Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult> reload = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [original], reason: "redirect-context-test", cancellationToken: cancellation.Token, publishReferenceReceipts: true);
            operation = reload;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await reload;
            results.ThrowIfFailed();
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = results.Single();
            BMSTable current = playlist.BMSTables.Single();
            Assert.IsTrue(result.Succeeded);
            Assert.IsFalse(result.Updated);
            Assert.IsTrue(result.StatePersisted);
            Assert.IsTrue(result.NeedsBmtExport);
            Assert.AreNotSame(original, current);
            Assert.AreSame(current, result.ResultTable);
            Assert.IsNotNull(result.UpdateReceipt);
            Assert.AreSame(current, result.UpdateReceipt.NewTable);
            Assert.AreSame(result.UpdateReceipt, published);
            Assert.IsFalse(result.UpdateReceipt.ReferenceEntriesChanged);
            Assert.IsFalse(result.UpdateReceipt.Updated);
            Assert.IsNull(current.Page_url);
            Assert.AreEqual(originalHeader, current.Header_url);
            Assert.AreEqual(finalData, current.GetAbsoluteDataUrl());
            Assert.AreEqual(original.playlist_id, current.playlist_id);
            Assert.AreEqual("Manual table name", current.name);
            Assert.AreEqual("M", current.symbol);
            Assert.AreEqual(original.Output_dir, current.Output_dir);
            Assert.AreEqual(original.ignore_folder_output, current.ignore_folder_output);
            Assert.AreEqual(original.is_bmt_output, current.is_bmt_output);
            Assert.AreEqual(original.bmt_sort, current.bmt_sort);
            Assert.AreEqual(oldLastUpdate, current.last_update);
            Assert.AreEqual(oldHeaderHash, current.header_sha256);
            Assert.AreEqual(oldDataHash, current.data_sha256);
            Assert.AreEqual("local memo", current.entries.Single().memo);
            Assert.AreEqual(oldAddDate, current.entries.Single().adddate);
            Assert.AreEqual(new Uri("https://packages.test/song.zip"), current.entries.Single().Url);
            Assert.AreEqual(new Uri("https://packages.test/fix.zip"), current.entries.Single().Url_diff);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                BMSTable persisted = db.Table<BMSTable>().Single();
                Assert.AreEqual(finalData, persisted.GetAbsoluteDataUrl());
                Assert.AreEqual(originalHeader, persisted.Header_url);
                Assert.AreEqual(oldLastUpdate, persisted.last_update);
                Assert.AreEqual(oldDataHash, persisted.data_sha256);
                LR2SongDBExtended.playlist_entry entry = db.Table<LR2SongDBExtended.playlist_entry>().Single();
                Assert.AreEqual("local memo", entry.memo);
                Assert.AreEqual(oldAddDate, entry.adddate);
            }
            reader = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings);
            OwnStartupWork(reader, nameof(reader));
            operation = reader.ReloadTablesAsync(exportBeatorajaBmt: false);
            await operation;
            BMSTable hydrated = reader.BMSTables.Single();
            reader.EnsurePlaylistEntriesLoaded(hydrated, "redirect-context-test");
            Assert.AreEqual(finalData, hydrated.GetAbsoluteDataUrl());
            Assert.AreEqual(originalHeader, hydrated.Header_url);
            Assert.AreEqual(oldLastUpdate, hydrated.last_update);
            Assert.AreEqual("local memo", hydrated.entries.Single().memo);
            Assert.AreEqual(oldAddDate, hydrated.entries.Single().adddate);
            Assert.AreEqual(current.entries.Single().Url, hydrated.entries.Single().Url);
            Assert.AreEqual(current.entries.Single().Url_diff, hydrated.entries.Single().Url_diff);
            CollectionAssert.AreEqual(new[] { "/original/header.json" }, header.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/data.json" }, redirect.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/published/current/rows.json" }, final.RequestTargets);
            await Task.WhenAll(header.Completion, redirect.Completion, final.Completion);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                try { cancellation.Cancel(); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
                foreach (SingleRequestHttpServer server in new[] { header, redirect, final })
                {
                    server.ReleaseBodyPrefix.TrySetResult();
                    server.ReleaseBody.TrySetResult();
                }
                if (operation != null)
                {
                    try { await operation; }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception ex) { cleanupFailures.Add(ex); }
                }
                foreach (SingleRequestHttpServer server in new[] { header, redirect, final })
                {
                    try { await server.DisposeAsync(); }
                    catch (Exception ex) { cleanupFailures.Add(ex); }
                }
            }
            finally { releaseWork.TrySetResult(); }
            // 主operationとHTTPのproducer終端後に固定し、store別の実work異常も主失敗と区別する。
            foreach (IGrouping<string, (string Store, Task Work)> storeWork in acceptedWork.ToArray().GroupBy(item => item.Store))
            {
                var workCompletion = Task.WhenAll(storeWork.Select(item => item.Work));
                try { await workCompletion; }
                catch (Exception ex)
                {
                    cleanupFailures.Add(new InvalidOperationException(
                        "転送テストの任意startup work終了失敗: " + storeWork.Key, workCompletion.Exception ?? ex));
                }
            }
            foreach (TestBmsPlaylist? store in new[] { reader, playlist })
            {
                if (store == null) { continue; }
                try
                {
                    store.RequestShutdown("redirect-context-test-complete");
                    await store.WaitForPlaylistMutationIdleAsync();
                    Assert.IsFalse(store.HasShutdownBlockingWork);
                }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) { cleanupFailures.Add(ex); }
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException(cleanupFailures);
                if (failure == null) { throw cleanupFailure; }
                System.Diagnostics.Trace.WriteLine("転送reloadのcleanup二次失敗: " + cleanupFailure);
            }
        }
    }

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
            var reported = new ConcurrentQueue<PlaylistExternalSyncOwner.PlaylistReloadTargetResult>();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = singleTarget
                ? await playlist.ExternalSyncOwner.ReloadAndApplySingleTableAsync(original, new Uri(header), "reload-output-failure")
                : await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original, bad], syncResultCallback: reported.Enqueue);
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult confirmed = result.Single(target => ReferenceEquals(target.SourceTable, original));
            Assert.IsNotNull(result.OperationException);
            Assert.IsNotNull(result.Exception, "派生出力の失敗を最終操作結果へ保持します。");
            if (!singleTarget)
            {
                Assert.AreEqual(result.Count, reported.Count);
                Assert.AreSame(confirmed, reported.Single(target => ReferenceEquals(target.SourceTable, original)));
            }
            Assert.IsTrue(confirmed.Succeeded, "対象確定時の成功を後続BMT失敗で書き換えません。");
            Assert.IsNotNull(confirmed.UpdateReceipt);
            Assert.IsTrue(confirmed.StatePersisted);
            Assert.IsNull(confirmed.Exception);
            Assert.AreNotSame(original, confirmed.ResultTable);
            Assert.AreSame(confirmed.ResultTable, playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id));
            Assert.AreEqual("local-memo", confirmed.ResultTable.entries.Single(entry => entry.md5 == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").memo);
            if (!singleTarget)
            {
                PlaylistExternalSyncOwner.PlaylistReloadTargetResult failed = result.Single(target => ReferenceEquals(target.SourceTable, bad));
                Assert.AreSame(failed, reported.Single(target => ReferenceEquals(target.SourceTable, bad)));
                Assert.IsTrue(failed.IsHandledExternalLoadFailure);
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
    /// 手動・任意同期でA成功とC取得失敗を確定し、Bの実HTTP本文待ち中に実Dispatcher上のsummaryへ
    /// 現参照・更新日時・STATUSを採用します。全件終了までPとTaskを保持し、重い詳細更新は終端に一度だけ要求します。
    /// summary要求の必須購読先が失敗する行でも他対象を完了させ、終端で元例外を返して次要求へ持ち越しません。
    /// A/C完了進捗からsummary buildを回収するため、終端一括採用への退行は待機継続ではなく旧表示の表明で検出します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceReload_PublishesEachSummaryBeforeOtherTargetsComplete(bool automatic, bool failSummaryRequest)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SingleRequestHttpServer? server = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Task? operation = null;
        Exception? primaryFailure = null;
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string goodHeader = Path.Combine(directory, "good-header.json");
            string goodData = Path.Combine(directory, "good-data.json");
            string blockedHeader = Path.Combine(directory, "blocked-header.json");
            string blockedData = Path.Combine(directory, "blocked-data.json");
            string badHeader = Path.Combine(directory, "bad-header.json");
            const string oldData = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]";
            File.WriteAllText(goodHeader, "{\"name\":\"A success\",\"symbol\":\"A\",\"data_url\":\"good-data.json\"}");
            File.WriteAllText(blockedHeader, "{\"name\":\"B blocked\",\"symbol\":\"B\",\"data_url\":\"blocked-data.json\"}");
            File.WriteAllText(badHeader, "{\"name\":\"C failed\",\"symbol\":\"C\",\"data_url\":\"blocked-data.json\"}");
            File.WriteAllText(goodData, oldData);
            File.WriteAllText(blockedData, oldData);
            var library = new TestBmsLibrary(songPath, settings: testSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath,
                () => new CustomFolderOutputSettingsSnapshot(), settings: testSettings);
            BMSTable good = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(goodHeader));
            BMSTable blocked = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(blockedHeader));
            BMSTable bad = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(badHeader));
            server = new SingleRequestHttpServer(CreateUtf8BomBytes(
                "{\"name\":\"B blocked\",\"symbol\":\"B\",\"data_url\":\"" + new Uri(blockedData).AbsoluteUri + "\"}"), holdBody: true);
            blocked.Page_url = server.Address;
            blocked.Header_url = server.Address;
            good.playlist_id = 7531;
            blocked.playlist_id = 7532;
            bad.playlist_id = 7533;
            DateTime oldUpdate = new(2001, 2, 3, 4, 5, 6);
            foreach (BMSTable table in new[] { good, blocked, bad })
            {
                table.last_update = oldUpdate;
                table.is_bmt_output = false;
                Assert.IsTrue(table.EnableExternalSync());
            }
            bad.entries[0].memo = "keep-failed-local";
            playlist.BMSTables = new ObservableCollection<BMSTable>([good, blocked, bad]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            var completedTargets = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summaryAdopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summaryFailure = new InvalidOperationException("incremental summary request failed");
            int summaryRequestCount = 0;
            int detailRefreshCount = 0;
            int completionCount = 0;
            string refreshReason = automatic ? "external_playlist_sync" : "manual_playlist_resync";
            TestUiDispatcherHost.Invoke(() =>
            {
                workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _,
                    playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library,
                    dispatchPresentation: TestUiDispatcherHost.Invoke);
                workspace.RefreshPlaylistTreeTables(playlist, library);
                workspace.RequestDetailSelection(good, PlaylistFolderNode.CreateFolder("1"));
                workspace.SetPlaylistSummaryMode(true);
                workspace.RebuildPlaylistSummaryView(runAsync: false);
            });
            PlaylistWorkspaceViewModel currentWorkspace = workspace
                ?? throw new AssertFailedException("Workspace was not created.");
            currentWorkspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            currentWorkspace.PlaylistDetailReloadRefreshRequested += (_, _) => Interlocked.Increment(ref detailRefreshCount);
            currentWorkspace.PlaylistExternalSyncCompleted += (_, _) => Interlocked.Increment(ref completionCount);
            currentWorkspace.PlaylistSyncProgressChanged += (_, change) =>
            {
                if (change.Snapshot.IsActive && change.Snapshot.CompletedTableCount == 2)
                {
                    completedTargets.TrySetResult();
                }
            };
            currentWorkspace.PlaylistPresentationRefreshRequested += (_, request) =>
            {
                if (request.Kind == PlaylistPresentationRefreshKind.SummaryData && request.Reason == refreshReason
                    && Interlocked.Increment(ref summaryRequestCount) == 1 && failSummaryRequest)
                {
                    throw summaryFailure;
                }
            };
            TestUiDispatcherHost.Invoke(() => currentWorkspace.PlaylistSummaryView.CollectionChanged += (_, _) =>
            {
                PlaylistSummaryRow? goodRow = currentWorkspace.PlaylistSummaryView.SingleOrDefault(row => row.PlaylistId == good.playlist_id);
                PlaylistSummaryRow? badRow = currentWorkspace.PlaylistSummaryView.SingleOrDefault(row => row.PlaylistId == bad.playlist_id);
                if (goodRow?.Status == Resources.Playlist_sync_status_updated && badRow?.HasFailureStatus == true)
                {
                    summaryAdopted.TrySetResult();
                }
            });
            File.WriteAllText(goodData, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"After\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
            File.WriteAllText(badHeader, "{ invalid json }");
            operation = Task.Run(() => automatic
                ? currentWorkspace.RunExternalPlaylistSyncAsync("incremental-summary", true, true, 1)
                : currentWorkspace.ResyncPlaylistsAsync([good, blocked, bad]));
            await TestUiDispatcherHost.AwaitNotificationAsync(server.HeadersSent.Task, operation, "incremental-summary.blocked-http");
            await TestUiDispatcherHost.AwaitNotificationAsync(completedTargets.Task, operation, "incremental-summary.completed-targets");
            await currentWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            Assert.IsTrue(summaryAdopted.Task.IsCompletedSuccessfully, "全件完了前に、成功と取得失敗を実summaryへ採用します。");
            await summaryAdopted.Task;
            BMSTable confirmed = playlist.BMSTables.Single(table => table.playlist_id == good.playlist_id);
            Assert.AreNotSame(good, confirmed);
            Assert.AreSame(confirmed, currentWorkspace.CapturePlaylistDetailSelection().Table);
            Assert.AreNotEqual(oldUpdate, confirmed.last_update);
            TestUiDispatcherHost.Invoke(() =>
            {
                PlaylistSummaryRow goodRow = currentWorkspace.PlaylistSummaryView.Single(row => row.PlaylistId == good.playlist_id);
                Assert.AreSame(confirmed, goodRow.TableRef);
                Assert.AreEqual(confirmed.last_update, goodRow.LastUpdate);
                Assert.AreEqual(Resources.Playlist_sync_status_updated, goodRow.Status);
                Assert.IsFalse(goodRow.HasFailureStatus);
                PlaylistSummaryRow badRow = currentWorkspace.PlaylistSummaryView.Single(row => row.PlaylistId == bad.playlist_id);
                Assert.AreSame(bad, badRow.TableRef);
                Assert.AreEqual(oldUpdate, badRow.LastUpdate);
                Assert.AreEqual(Resources.Playlist_sync_status_header, badRow.Status);
                Assert.IsTrue(badRow.HasFailureStatus);
            });
            PlaylistSyncRuntimeStatus failedStatus = currentWorkspace.CapturePlaylistSyncStatusSnapshot()["id:7533"];
            Assert.AreEqual(string.Join(Environment.NewLine, failedStatus.StatusText,
                failedStatus.CheckedAt.ToString("yyyy/MM/dd HH:mm:ss"), new Uri(badHeader).ToString()), failedStatus.Detail);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(confirmed.last_update, db.Table<BMSTable>().Single(table => table.playlist_id == good.playlist_id).last_update);
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == good.playlist_id));
                Assert.AreEqual(oldUpdate, db.Table<BMSTable>().Single(table => table.playlist_id == bad.playlist_id).last_update);
                Assert.AreEqual("keep-failed-local", db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == bad.playlist_id).memo);
            }
            Assert.AreSame(bad, playlist.BMSTables.Single(table => table.playlist_id == bad.playlist_id));
            Assert.AreSame(blocked, playlist.BMSTables.Single(table => table.playlist_id == blocked.playlist_id));
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(playlist.IsPlaylistUpdating);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _));
            Assert.AreEqual(0, Volatile.Read(ref completionCount));
            Assert.AreEqual(0, Volatile.Read(ref detailRefreshCount));
            Assert.AreEqual(2, Volatile.Read(ref summaryRequestCount));
            server.ReleaseBody.TrySetResult();
            if (failSummaryRequest)
            {
                Exception actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await operation);
                Assert.AreSame(summaryFailure, actual, "モデルの任意callback境界で必須採用失敗を飲み込みません。");
            }
            else { await operation; }
            Assert.AreEqual(automatic ? 1 : 0, Volatile.Read(ref completionCount));
            Assert.AreEqual(1, Volatile.Read(ref detailRefreshCount));
            Assert.AreEqual(3, Volatile.Read(ref summaryRequestCount), "終端で対象のSTATUS・参照・summary要求を二重採用しません。");
            Assert.AreEqual(failedStatus.CheckedAt, currentWorkspace.CapturePlaylistSyncStatusSnapshot()["id:7533"].CheckedAt);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable admission));
            admission.Dispose();
            await currentWorkspace.ResyncPlaylistsAsync([confirmed]);
            Assert.AreEqual(PlaylistSyncStatusKind.Ok, currentWorkspace.CapturePlaylistSyncStatusSnapshot()["id:7531"].Kind);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            server?.ReleaseBody.TrySetResult();
            if (operation != null)
            {
                try { await operation; }
                catch when (primaryFailure != null || failSummaryRequest) { }
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
    /// 実JSON再取得・SQLite確定・参照置換・BMT成功またはmanifest故障へ、推薦実力の実変化で生じるInformationの表示・診断故障を重ねます。
    /// 有効行と削除履歴の有限集合をmodel/DBで照合し、BMTは有効行だけ、manifest故障では先行生成物と旧台帳を保ちます。
    /// BMT Warningを別に観測し、終端Information配送中のP/Taskと実結果・完了事実、次の明示JSON変更と空receiptの非表示を保ちます。削除履歴の分岐はPlaylistReloadMergeTests、実modalは上位へ分担します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)] // BMT成功を実Informationの表示・診断故障で失敗へ変えない。
    [DataRow(true)] // manifest失敗のWarningと終端Informationを分け、必須出力原因を保つ。
    [TestCategory("Playlist")]
    public async Task ExternalPlaylistSync_ReceiptDisplayFailurePreservesCommittedResultAndNextRequest(bool failBmt)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var receiptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        Task? operation = null;
        FileStream? blocker = null;
        bool failPresentation = true;
        try
        {
            const string OldMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string ChangedMd5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string FreshMd5 = "cccccccccccccccccccccccccccccccc";
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string bmt = Path.Combine(directory, "bmt");
            Directory.CreateDirectory(bmt);
            string manifest = Path.Combine(bmt, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
            byte[] originalManifest = File.ReadAllBytes(manifest);
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"Receipt boundary\",\"symbol\":\"P\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            var library = new TestBmsLibrary(songPath, settings: testSettings);
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath,
                () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = true },
                playlistUrlCompletionOptionsProvider: () => new PlaylistUrlCompletionOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt },
                recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput("standard")), settings: testSettings);
            BMSTable original = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            original.playlist_id = 7513;
            original.is_bmt_output = true;
            Assert.IsTrue(original.EnableExternalSync());
            BMSTable recommendation = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri("bmseeker:table.recommended"));
            recommendation.playlist_id = 7514;
            recommendation.org_name = "旧方針 ★3.87";
            recommendation.header_sha256 = null;
            recommendation.data_sha256 = null;
            recommendation.is_bmt_output = false;
            Assert.IsTrue(recommendation.EnableExternalSync());
            Assert.IsFalse(recommendation.entries.Any(entry => entry.md5 is OldMd5 or ChangedMd5 or FreshMd5),
                "推薦入力とJSON入力のhashを分離し、JSON表の索引反映を完全一致で観測します。");
            playlist.BMSTables = new ObservableCollection<BMSTable>([original, recommendation]);
            playlist.CommitBMSTablesWithEntriesToDB([original, recommendation]);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(OldMd5, null).Symbols);
            var oldReferenceKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, OldMd5);
            var changedReferenceKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, ChangedMd5);
            PlaylistReferenceReadSnapshot initialReferences = library.CapturePlaylistReferenceReadSnapshot([oldReferenceKey, changedReferenceKey]);
            Assert.AreEqual("P", initialReferences.Values[oldReferenceKey].Symbols);
            Assert.IsFalse(initialReferences.Values.ContainsKey(changedReferenceKey));
            var diagnostics = new List<(Exception Exception, string Diagnostic)>();
            var diagnosticFailure = new InvalidOperationException("external receipt diagnostic failed");
            workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _,
                reloadFailureLog: (exception, diagnostic) =>
                {
                    diagnostics.Add((exception, diagnostic));
                    throw diagnosticFailure;
                },
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            var bmtFailures = new List<BmtTableExportService.FileOperationFailure>();
            playlist.BmtOutput.FailureReporter = failures =>
            {
                bmtFailures.AddRange(failures);
                workspace.ReportBmtOutputFailures(failures);
            };
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.RequestDetailSelection(original, PlaylistFolderNode.CreateFolder("1"));
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            var receipts = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            var displayAttempts = new List<(string Route, PlaylistOperationNotificationOwner.OperationNotification Notification)>();
            var presentationFailure = new InvalidOperationException("external receipt display failed");
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            workspace.PlaylistExternalSyncCompleted += (_, completion) => completed.Add(completion);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
            {
                receipts.Add(request);
                foreach (PlaylistOperationNotificationOwner.OperationNotification notification in request.Receipt.Notifications)
                {
                    displayAttempts.Add((request.RouteName, notification));
                    // BMT Warningは必要出力の独立通知です。終端Informationだけを保持します。
                    if (request.RouteName == "external playlist sync notification" && failPresentation)
                    {
                        receiptEntered.TrySetResult();
                        releaseReceipt.Task.GetAwaiter().GetResult();
                        throw presentationFailure;
                    }
                }
            };
            File.WriteAllText(data, "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"level\":\"2\"}]");
            if (failBmt) { blocker = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read); }
            PlaylistWorkspaceViewModel currentWorkspace = workspace;
            operation = Task.Run(() => currentWorkspace.RunExternalPlaylistSyncAsync("receipt-display-failure", false, true, 31));
            await TestUiDispatcherHost.AwaitNotificationAsync(receiptEntered.Task, operation, "external-sync.receipt-display");
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(0, completed.Count, "receipt配送の実終端前に完了事実を公開しません。");
            Assert.AreEqual(failBmt ? 2 : 1, receipts.Count);
            PlaylistOperationNotificationPresentationRequestedEventArgs terminalReceipt = receipts.Single(request =>
                request.RouteName == "external playlist sync notification");
            Assert.IsFalse(terminalReceipt.Receipt.IsEmpty);
            PlaylistOperationNotificationOwner.OperationNotification information = terminalReceipt.Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information, information.Severity);
            Assert.AreEqual(Resources.Recommend_SkillUpdatedTitle, information.Caption);
            double expectedSkill = ReadWalkureCase("standard")["rating"]?.Value<double>("playerStarRating")
                ?? throw new AssertFailedException("The independent recommendation skill expectation is missing.");
            Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage,
                expectedSkill.ToString("F2"), (expectedSkill - 3.87).ToString(" (+#0.00); (-#0.00);")), information.Message);
            Assert.AreSame(information, displayAttempts.Single(attempt => attempt.Route == "external playlist sync notification").Notification);
            PlaylistOperationNotificationPresentationRequestedEventArgs[] bmtReceipts = receipts.Where(request =>
                request.RouteName == "beatoraja BMT output failure").ToArray();
            Assert.AreEqual(failBmt ? 1 : 0, bmtReceipts.Length);
            bool acceptedWhilePresenting = playlist.TryEnterPlaylistMutation(out IDisposable admission);
            admission?.Dispose();
            Assert.IsFalse(acceptedWhilePresenting);
            Task mutationIdle = playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsFalse(mutationIdle.IsCompleted);
            Assert.AreEqual(2, playlist.BMSTables.Count);
            BMSTable confirmed = playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id);
            Assert.AreNotSame(original, confirmed);
            Assert.AreSame(confirmed, workspace.CapturePlaylistDetailSelection().Table);
            (int? PlaylistId, string Md5, bool IsRemoved, string Title, double? Level)[] expectedConfirmedEntries =
            [
                (original.playlist_id, ChangedMd5, false, "After", 2),
                (original.playlist_id, OldMd5, true, "Before", 1)
            ];
            Assert.AreEqual(2, confirmed.entries.Count);
            CollectionAssert.AreEquivalent(expectedConfirmedEntries, confirmed.entries
                .Select(entry => (entry.playlist_id, entry.md5, entry.is_removed, entry.title, entry.level)).ToArray());
            BMSTableEntry active = confirmed.entries.Single(entry => !entry.is_removed);
            Assert.AreEqual(ChangedMd5, active.md5);
            Assert.AreEqual(2, active.level);
            Assert.AreEqual("After", active.title);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(OldMd5, null).Symbols);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(ChangedMd5, null).Symbols);
            PlaylistReferenceReadSnapshot confirmedReferences = library.CapturePlaylistReferenceReadSnapshot([oldReferenceKey, changedReferenceKey]);
            Assert.IsTrue(confirmedReferences.Revision > initialReferences.Revision);
            Assert.IsFalse(confirmedReferences.Values.ContainsKey(oldReferenceKey));
            Assert.AreEqual("P", confirmedReferences.Values[changedReferenceKey].Symbols);
            Assert.AreEqual("P", initialReferences.Values[oldReferenceKey].Symbols);
            Assert.IsFalse(initialReferences.Values.ContainsKey(changedReferenceKey));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                var savedEntries = db.Table<BMSTableEntry>()
                    .Where(entry => entry.playlist_id == original.playlist_id).ToList();
                Assert.AreEqual(2, savedEntries.Count);
                CollectionAssert.AreEquivalent(expectedConfirmedEntries, savedEntries
                    .Select(entry => (entry.playlist_id, entry.md5, entry.is_removed, entry.title, entry.level)).ToArray());
                BMSTableEntry saved = savedEntries.Single(entry => !entry.is_removed);
                Assert.AreEqual(original.playlist_id, saved.playlist_id);
                Assert.AreEqual(ChangedMd5, saved.md5);
                Assert.AreEqual(2, saved.level);
                Assert.AreEqual("After", saved.title);
            }
            releaseReceipt.TrySetResult();
            Exception? observedFailure = null;
            try { await operation; }
            catch (Exception failure) { observedFailure = failure; }
            await mutationIdle;
            string outputFile = Directory.GetFiles(bmt, "*.bmt").Single();
            JObject exported = BeatorajaBmtTableImportService.ReadCachedTableData(outputFile);
            JToken song = exported["folder"]?.Single()?["songs"]?.Single()
                ?? throw new AssertFailedException("The required BMT row was not exported.");
            Assert.AreEqual(ChangedMd5, song.Value<string>("md5"));
            Assert.AreEqual("After", song.Value<string>("title"));
            if (failBmt)
            {
                Assert.IsInstanceOfType<PlaylistMutationPostCommitException>(observedFailure);
                var failure = (PlaylistMutationPostCommitException)observedFailure;
                Exception cause = failure.InnerException ?? throw new AssertFailedException("The required BMT cause was lost.");
                Assert.IsInstanceOfType<IOException>(cause.GetBaseException());
                StringAssert.Contains(cause.Message, manifest);
                Assert.IsTrue(operation.IsFaulted);
                PlaylistOperationNotificationOwner.OperationNotification warning = bmtReceipts.Single().Receipt.Notifications.Single();
                Assert.IsFalse(bmtReceipts.Single().Receipt.IsEmpty);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, warning.Severity);
                Assert.AreEqual(Resources.MessageBoxTitle_Warning, warning.Caption);
                BmtTableExportService.FileOperationFailure manifestFailure = bmtFailures.Single();
                Assert.AreEqual(bmt, manifestFailure.Path, "manifest公開の例外はBMT出力先の失敗として通知されます。");
                StringAssert.Contains(manifestFailure.Cause, manifest);
                StringAssert.Contains(manifestFailure.Cause, cause.Message);
                StringAssert.Contains(manifestFailure.Cause, cause.GetBaseException().Message);
                Assert.AreEqual(string.Format(Resources.Beatoraja_bmt_output_failure_format,
                    manifestFailure.Path, manifestFailure.Cause), warning.Message);
                Assert.AreSame(warning, displayAttempts.Single(attempt => attempt.Route == "beatoraja BMT output failure").Notification);
                Assert.IsFalse(failure.ToString().Contains(presentationFailure.Message, StringComparison.Ordinal));
                Assert.IsFalse(failure.ToString().Contains(diagnosticFailure.Message, StringComparison.Ordinal));
                CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(manifest));
            }
            else
            {
                Assert.IsNull(observedFailure);
                Assert.IsTrue(operation.IsCompletedSuccessfully);
                var managed = JObject.Parse(File.ReadAllText(manifest));
                Assert.AreEqual(Path.GetFileName(outputFile), managed["files"]?.Single()?.Value<string>());
            }
            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual(!failBmt, completed[0].Succeeded);
            Assert.IsFalse(completed[0].WasSkipped);
            Assert.AreEqual(failBmt ? 2 : 1, receipts.Count, "表示失敗で操作や通知を再実行しません。");
            Assert.AreEqual(failBmt ? 2 : 1, displayAttempts.Count);
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreSame(presentationFailure, diagnostics[0].Exception);
            StringAssert.Contains(diagnostics[0].Diagnostic, "external playlist sync notification");
            Assert.IsFalse(progress.Last().IsActive);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            Assert.IsFalse(playlist.HasShutdownBlockingWork);

            failPresentation = false;
            blocker?.Dispose();
            blocker = null;
            File.WriteAllText(data, "[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"title\":\"Fresh\",\"level\":\"3\"}]");
            operation = Task.Run(() => currentWorkspace.RunExternalPlaylistSyncAsync("fresh-json-change", false, true, 32));
            await operation;
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.AreEqual(2, playlist.BMSTables.Count);
            BMSTable fresh = playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id);
            Assert.AreNotSame(confirmed, fresh);
            Assert.AreSame(fresh, workspace.CapturePlaylistDetailSelection().Table);
            (int? PlaylistId, string Md5, bool IsRemoved, string Title, double? Level)[] expectedFreshEntries =
            [
                (original.playlist_id, FreshMd5, false, "Fresh", 3),
                (original.playlist_id, ChangedMd5, true, "After", 2),
                (original.playlist_id, OldMd5, true, "Before", 1)
            ];
            Assert.AreEqual(3, fresh.entries.Count);
            CollectionAssert.AreEquivalent(expectedFreshEntries, fresh.entries
                .Select(entry => (entry.playlist_id, entry.md5, entry.is_removed, entry.title, entry.level)).ToArray());
            BMSTableEntry freshActive = fresh.entries.Single(entry => !entry.is_removed);
            Assert.AreEqual(FreshMd5, freshActive.md5);
            Assert.AreEqual(3, freshActive.level);
            Assert.AreEqual("Fresh", freshActive.title);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(OldMd5, null).Symbols);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(ChangedMd5, null).Symbols);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(FreshMd5, null).Symbols);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                var savedEntries = db.Table<BMSTableEntry>()
                    .Where(entry => entry.playlist_id == original.playlist_id).ToList();
                Assert.AreEqual(3, savedEntries.Count);
                CollectionAssert.AreEquivalent(expectedFreshEntries, savedEntries
                    .Select(entry => (entry.playlist_id, entry.md5, entry.is_removed, entry.title, entry.level)).ToArray());
                BMSTableEntry saved = savedEntries.Single(entry => !entry.is_removed);
                Assert.AreEqual(original.playlist_id, saved.playlist_id);
                Assert.AreEqual(FreshMd5, saved.md5);
                Assert.AreEqual(3, saved.level);
                Assert.AreEqual("Fresh", saved.title);
            }
            string freshOutputFile = Directory.GetFiles(bmt, "*.bmt").Single();
            JObject freshOutput = BeatorajaBmtTableImportService.ReadCachedTableData(freshOutputFile);
            JToken freshSong = freshOutput["folder"]?.Single()?["songs"]?.Single()
                ?? throw new AssertFailedException("The next explicit change was not exported.");
            Assert.AreEqual(FreshMd5, freshSong.Value<string>("md5"));
            Assert.AreEqual("Fresh", freshSong.Value<string>("title"));
            var freshManaged = JObject.Parse(File.ReadAllText(manifest));
            Assert.AreEqual(Path.GetFileName(freshOutputFile), freshManaged["files"]?.Single()?.Value<string>());
            Assert.AreEqual(2, completed.Count);
            Assert.IsTrue(completed[1].Succeeded);
            Assert.IsFalse(completed[1].WasSkipped);
            Assert.AreEqual(failBmt ? 3 : 2, receipts.Count);
            PlaylistOperationNotificationPresentationRequestedEventArgs[] terminalReceipts = receipts.Where(request =>
                request.RouteName == "external playlist sync notification").ToArray();
            Assert.AreEqual(2, terminalReceipts.Length);
            Assert.IsTrue(terminalReceipts[1].Receipt.IsEmpty, "推薦skillが不変の次操作は表示内容を生成しません。");
            Assert.AreEqual(failBmt ? 2 : 1, displayAttempts.Count, "空receiptのevent配送では表示を試みません。");
            Assert.AreEqual(1, diagnostics.Count, "前操作の表示失敗を次の明示要求へ持ち越しません。");
            Assert.IsFalse(progress.Last().IsActive);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            Assert.IsFalse(playlist.HasShutdownBlockingWork);
        }
        finally
        {
            releaseReceipt.TrySetResult();
            if (operation != null) { try { await operation; } catch (Exception) { } }
            blocker?.Dispose();
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            if (playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Aの実JSON・DB・正本置換を確定した後、Bの実HTTP取得を操作tokenで取り消します。
    /// schedulerの完了だけを保持して全開始Taskの終端までPと進捗が続くこと、解放後にAのreceiptと全体取消原因が届きBのDBを変更しないことを確認します。
    /// workspaceの選択もAの確定表へ移します。廃止したsort再通知の故障注入は除き、通常対象失敗・出力失敗は既存ケースへ分担します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_PartialCancellationRetainsConfirmedReferenceUntilAllTasksEnd()
    {
        (string directory, TestBmsPlaylist playlist, BMSTable original, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending);
        using var cancellation = new CancellationTokenSource();
        SingleRequestHttpServer? server = null;
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? operation = null;
        Exception? primaryFailure = null;
        PlaylistWorkspaceViewModel? workspace = null;
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
            {
                var library = new TestBmsLibrary(songPath, settings: testSettings);
                workspace = CreatePlaylistWorkspace(playlist, library);
                workspace.RequestDetailSelection(original, PlaylistFolderNode.CreateFolder("1"));

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
                }, cancellationToken: cancellation.Token, reason: "partial-cancellation", publishReferenceReceipts: true);
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
            Assert.IsNull(successful.Exception);
            Assert.AreSame(confirmed, workspace?.CapturePlaylistDetailSelection().Table);
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
            var prefixEdit = new PlaylistHeaderEdit { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = "KEEP " };
            original.ApplyEntryMutation(original.PrepareHeaderMutation(prefixEdit));
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
            // 保持した利用者の追加先名を登録して出力します。未登録名の停止条件は出力担当のテストへ分担します。
            string additionalOutputBase = Path.Combine(directory, "CustomName");
            settings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = Path.Combine(directory, "Output"),
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase])
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([updated], "local-recommendations-output");
            string lr2Path = Path.Combine(additionalOutputBase, updated.Output_dir, "0000.lr2folder");
            Assert.IsTrue(File.Exists(lr2Path));
            StringAssert.Contains(ReadShiftJisText(lr2Path), "#TITLE");
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.IsTrue(verify.Table<LR2SongDB.folder>().Any(row => row.path == lr2Path));
            }
            string bmtDirectory = Path.Combine(directory, "Bmt");
            string bmtName = BmtTableExportService.ExportTableData(bmtDirectory, BmtTableExportService.BuildTableData(updated), id.ToString());
            Assert.IsTrue(File.Exists(Path.Combine(bmtDirectory, bmtName)));
            // 既存BMT契約のtag＋互換levelを固定lamp対応から期待し、保存prefixを出力名へ残さない。
            var bmtFolders = new Dictionary<string, string>
            {
                ["KEEP EASY"] = "CUSTOMEASY",
                ["KEEP NORMAL"] = "CUSTOMNORMAL",
                ["KEEP HARD"] = "CUSTOMHARD",
                ["KEEP FC"] = "CUSTOMFC"
            };
            // 同じ譜面の複数目標も有効行ごとに、実際の圧縮出力から照合します。
            string[] expectedBmtRows = updated.entries.Where(entry => !entry.is_removed && !string.IsNullOrWhiteSpace(entry.title))
                .Select(entry => entry.md5 + ":" + bmtFolders[entry.folder]).ToArray();
            JObject actualBmt = BeatorajaBmtTableImportService.ReadCachedTableData(Path.Combine(bmtDirectory, bmtName));
            Assert.AreEqual(updated.Page_url.OriginalString, actualBmt.Value<string>("url"));
            Assert.AreEqual(mixedName, actualBmt.Value<string>("name"));
            Assert.AreEqual("CUSTOM", actualBmt.Value<string>("tag"));
            CollectionAssert.AreEqual(bmtFolders.Where(pair => updated.entries.Any(entry => !entry.is_removed && !string.IsNullOrWhiteSpace(entry.title) && entry.folder == pair.Key)).Select(pair => pair.Value).ToArray(),
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

    /// <summary>保存済みの旧難度、通知設定、保存成否を変えて初回hash保存を行い、数値の難度変化だけを通知します。保存失敗で旧状態を戻さない誤りと、名前変更や同値表記だけで通知する誤りを検出します。</summary>
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

    /// <summary>同期portの元例外をそのまま返し、DBパスをディレクトリで塞いでもDBアクセスの例外へ置き換えません。port失敗後にDB保存を続ける誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2FolderSync_PropagatesSynchronizationFailureBeforeDatabaseAccess()
    {
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
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実再取得の同receipt・DBと参照bucketを確定し、旧表と捕捉値を保持します。購読者と同期callbackの二重失敗でも最初の原因を返すことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_PublishesCommittedReferencesBeforeFailingCallback()
    {
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
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => new CustomFolderOutputSettingsSnapshot(), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.playlist_id = 900011;
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            playlist.CommitBMSTableWithEntriesToDB(table);
            var oldKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, new string('a', 32));
            var newKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, new string('c', 32));
            PlaylistReferenceReadSnapshot before = library.CapturePlaylistReferenceReadSnapshot([oldKey, newKey]);
            Assert.AreEqual("S", before.Values[oldKey].Symbols);
            Assert.IsFalse(before.Values.ContainsKey(newKey));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"New Snapshot Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = null!;
            var receiptFailure = new InvalidOperationException("receipt consumer failure");
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
                throw receiptFailure;
            };

            PlaylistExternalSyncOwner.PlaylistReloadTargetResult? reportedTarget = null;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult operation = await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: target =>
                {
                    reportedTarget = target;
                    throw new InvalidOperationException("sync result callback failure");
                },
                publishReferenceReceipts: true);
            Assert.AreSame(receiptFailure, operation.Exception);
            Assert.AreSame(operation.Single(), reportedTarget);
            Assert.AreSame(receiptFailure, reportedTarget?.Exception);
            Assert.AreSame(receipt, reportedTarget?.UpdateReceipt);
            Assert.AreSame(receipt.NewTable, operation.Single().ResultTable);

            Assert.IsNotNull(receipt);
            Assert.AreSame(table, receipt!.OldTable);
            Assert.IsNotNull(receipt.NewTable);
            Assert.IsFalse(playlist.BMSTables.Contains(table));
            Assert.AreSame(receipt.NewTable, playlist.BMSTables.Single());
            Assert.AreEqual(new string('a', 32), table.entries.Single().md5);
            Assert.AreSame(table, table.entries.Single().parent);
            Assert.IsTrue(receipt.ReferenceEntriesChanged);
            PlaylistReferenceReadSnapshot after = library.CapturePlaylistReferenceReadSnapshot([oldKey, newKey]);
            Assert.IsTrue(after.Revision > before.Revision);
            Assert.IsFalse(after.Values.ContainsKey(oldKey));
            Assert.AreEqual("S", after.Values[newKey].Symbols);
            Assert.AreEqual("S", before.Values[oldKey].Symbols);
            Assert.IsFalse(before.Values.ContainsKey(newKey));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            List<LR2SongDBExtended.playlist_entry> persisted = verify.Query<LR2SongDBExtended.playlist_entry>(
                "SELECT * FROM playlist_entry WHERE playlist_id = ? AND COALESCE(is_removed, 0) = 0;", table.playlist_id);
            Assert.AreEqual(1, persisted.Count);
            Assert.AreEqual(new string('c', 32), persisted.Single().md5);
            Assert.AreEqual(new string('b', 64), persisted.Single().sha256);
        }
        finally
        {
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
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同一source hashの旧地域誤読levelも明示reloadで実JSONから訂正し、新しいDB接続と所属・ローカル属性を保ちます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_SameSourceHashRepairsOldRegionalLevel()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string header = Path.Combine(directory, "header.json");
            File.WriteAllText(header, "{\"name\":\"Regional repair\",\"compat_prefix\":\"\",\"data_url\":\"data.json\"}");
            File.WriteAllText(Path.Combine(directory, "data.json"), "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Repair level\",\"level\":\"★1.5\"}]");
            string dbPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(dbPath);
            var playlist = new TestBmsPlaylist(dbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(dbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable old = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            old.playlist_id = 9017;
            old.is_bmt_output = false;
            old.entries.Single().level = 15; // 同一原文を旧地域解釈で保存した値を明示して投入します。
            old.entries.Single().memo = "local memo";
            DateTime date = old.entries.Single().adddate;
            string hash = old.data_sha256;
            playlist.BMSTables = new ObservableCollection<BMSTable>([old]);
            playlist.CommitBMSTableWithEntriesToDB(old);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([old]);
            result.ThrowIfFailed();
            Assert.IsTrue(result.Single().Succeeded);
            Assert.IsTrue(result.Single().StatePersisted);
            BMSTable repaired = playlist.BMSTables.Single();
            Assert.AreEqual(hash, repaired.data_sha256);
            Assert.AreEqual(old.playlist_id, repaired.playlist_id);
            BMSTableEntry entry = repaired.entries.Single();
            Assert.AreEqual(1.5, entry.level);
            Assert.AreEqual("★1.5", entry.folder);
            Assert.AreSame(repaired, entry.parent);
            Assert.AreEqual("local memo", entry.memo);
            Assert.AreEqual(date, entry.adddate);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
            BMSTableEntry saved = verify.Table<BMSTableEntry>().Single();
            Assert.AreEqual(1.5, saved.level);
            Assert.AreEqual(repaired.playlist_id, saved.playlist_id);
            Assert.AreEqual("local memo", saved.memo);
        }
        finally { CultureInfo.CurrentCulture = previous; Directory.Delete(directory, recursive: true); }
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

    /// <summary>LR2同期予約中はフォルダの改名・削除・作成、行のフォルダ追加・削除をモデル、DB、出力へ反映せず、解放後の次の明示変更を一回同期します。拒否の副作用と後続要求を拒み続ける誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LocalPlaylistMutations_RejectBusyWithoutEffectsAndAcceptNextExplicitMutation()
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

    /// <summary>未commitの変更と単体モードのcommitではLR2出力を変えず、前者はDBも保持し後者はDBへ保存します。保存有無と外部出力の条件を混同する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LocalPlaylistMutations_SkipLr2OutputForUncommittedAndStandaloneChanges()
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

    /// <summary>実外部表一件の再取得で受付、対象完了、終端の進捗を届け、件数と対象名を保持します。処理済み件数の欠落と終端後も進捗を稼働表示する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReportsPlaylistSyncProgressSnapshots()
    {
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
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同期対象の外部表だけを再取得し、同期を切った不正JSONの表へは結果callbackを届けません。自動同期が利用者の対象指定を無視する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReloadsOnlyExternalSyncTargets()
    {
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
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> syncResults = [];

            await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: syncResults.Add);

            Assert.AreEqual(1, syncResults.Count);
            Assert.AreSame(externalTable, syncResults[0].SourceTable);
            Assert.IsTrue(syncResults[0].Succeeded);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同期を切った表でも明示再取得は実データの追加を採用し、同期設定は保持します。自動同期の対象条件を明示要求にも適用する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReloadsExplicitTargetRegardlessOfExternalSyncFlag()
    {
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
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>保存中のURIと別の明示URIから実表を再取得し、追加行を採用します。引数の取得元を無視して旧URIを再利用する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadAndApplySingleTable_UsesExplicitUriOverride()
    {
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
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>通常表と推薦表のUI置換を保留し、完了前は旧参照と無通知を保ち、競合する復元・削除を拒否します。UI確定前の成功通知と予約の早期解放を検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_AwaitsUiReplacementCompletionBeforeApplying(bool recommendation)
    {
        (string tempDirectory, TestBmsPlaylist playlist, BMSTable table, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending, recommendation);
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
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    // U2-R4/R2: 既存 operation scheduler で UI rejection / cancel / fault を区別し、DB 保持と予約解放を確認する。
    /// <summary>復元後のUI配送が拒否・取消・失敗しても確定DBを保持し、旧表示のまま失敗を返して次の明示復元を受理します。表示失敗でDBを巻き戻す誤りと予約漏れを検出します。</summary>
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
        testSettings.EnablePlaylistUrlCompletion = false;
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
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>通常表と推薦表のUI配送拒否を再取得適用失敗として返し、旧モデル・保存値を保持して成功通知を出しません。配送できない置換を成功扱いする誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsRejectedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Rejected,
            typeof(InvalidOperationException),
            "rejected", recommendation);

    /// <summary>通常表と推薦表のUI配送取消を元取消の分類付きで返し、旧モデル・保存値と無通知を保持します。取消を適用済み扱いする誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsAbortedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Aborted,
            typeof(OperationCanceledException),
            "aborted", recommendation);

    /// <summary>通常表と推薦表のUI配送例外を適用失敗の内部例外に保持し、旧モデル・保存値を変えません。配送失敗を隠して成功通知する誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsFaultedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Faulted,
            typeof(InvalidOperationException),
            "faulted", recommendation);

    /// <summary>実HTTP本文の待機中は削除を拒否し、取得完了だけでは拒否した削除を再実行しません。後続の明示削除でDBとモデルを除去し、通信中の予約漏れと自動再実行を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_CommunicationKeepsPAndRejectsRemovalWithoutReplay()
    {
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

    /// <summary>
    /// 実手動再取得で行・ヘッダー変更の参照置換と進捗終端を確認します。不正JSONでは現在参照とDBを保ち、
    /// STATUSに形式不正・日時・取得元を残して、Workspaceから元例外を再送出・重複記録しません。
    /// 元例外の診断はPlaylistExternalFailureBoundaryTestsの取得境界テストで確認します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceManualResync_UsesActiveTableAndPublishesLifecycle()
    {
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistWorkspaceViewModel? workspace = null;
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
            var workspaceFailureLogs = new List<(Exception Exception, string Message)>();
            workspace = new PlaylistWorkspaceViewModel(
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
                (exception, message) => workspaceFailureLogs.Add((exception, message)), (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
            workspace.ConfigureCatalogNotificationQueue(action => action());
            PlaylistWorkspaceTestPorts.AttachImmediatePlaylistPresentationRouter(workspace);
            workspace.RefreshPlaylistTreeTables(playlist);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            long summaryDataGenerationBeforeResync = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder(string.Empty));
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);

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

            BMSTable persistedBeforeFailure = new PlaylistPersistenceRepository(songDbPath)
                .LoadPlaylistHeaders().Single();
            headerRefreshedTable.header_sha256 = null;
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{"));
            Uri failureUri = headerRefreshedTable.Page_url ?? headerRefreshedTable.Header_url;
            BMSTable failureSelectionTarget = await workspace.ResyncPlaylistTableAsync(headerRefreshedTable);
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            Assert.AreEqual(0, workspaceFailureLogs.Count,
                "取得境界で診断した通常失敗をWorkspaceから再記録しません。");
            Assert.AreSame(headerRefreshedTable, workspace.CapturePlaylistDetailSelection().Table);
            Assert.AreSame(headerRefreshedTable, failureSelectionTarget);
            Assert.AreSame(headerRefreshedTable, playlist.BMSTables.Single());
            Assert.IsFalse(playlist.IsPlaylistUpdating);

            PlaylistSyncRuntimeStatus failedStatus = workspace.CapturePlaylistSyncStatusSnapshot()["id:9011"];
            Assert.AreEqual(PlaylistSyncStatusKind.HeaderParseError, failedStatus.Kind);
            Assert.AreEqual(Resources.Playlist_sync_status_header, failedStatus.StatusText);
            Assert.IsTrue(failedStatus.HasFailureStatus);
            Assert.AreNotEqual(DateTime.MinValue, failedStatus.CheckedAt);
            Assert.AreEqual(string.Join(Environment.NewLine,
                failedStatus.StatusText,
                failedStatus.CheckedAt.ToString("yyyy/MM/dd HH:mm:ss"),
                failureUri.ToString()), failedStatus.Detail,
                "STATUSには分類・日時・取得元だけを渡し、例外詳細はログへ分離します。");

            BMSTable persistedAfterFailure = new PlaylistPersistenceRepository(songDbPath)
                .LoadPlaylistHeaders().Single();
            Assert.AreEqual(persistedBeforeFailure.header_sha256, persistedAfterFailure.header_sha256);
            Assert.AreEqual(persistedBeforeFailure.data_sha256, persistedAfterFailure.data_sha256);
            Assert.AreEqual(persistedBeforeFailure.last_update, persistedAfterFailure.last_update);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(2, verify.Table<LR2SongDBExtended.playlist_entry>()
                .Count(entry => entry.playlist_id == 9011));
        }
        finally
        {
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
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
