using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class BmsPlaylistExternalLoadTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>新規表作成へ利用者指定の出力除外maskを渡し、既定maskへ戻る設定接続の漏れを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void CreateBMSTable_UsesPlaylistDefaultIgnoreFolderOutputSetting()
    {
        testSettings.PlaylistDefaultIgnoreFolderOutput =
            (int)LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };

            BMSTable table = playlist.CreateBMSTable();

            Assert.AreEqual(LR2SongDBExtended.playlist.CustomFolderType.AllFolders, table.ignore_folder_output);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>新規外部表を実JSONから取得すると利用者指定の複合出力除外maskを採用し、空表作成との設定接続差を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_NewExternalTableUsesPlaylistDefaultIgnoreFolderOutputSetting()
    {
        LR2SongDBExtended.playlist.CustomFolderType expectedMask = LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
            | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder;
        testSettings.PlaylistDefaultIgnoreFolderOutput = (int)expectedMask;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"DefaultMaskImport\",\"symbol\":\"D\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));

            Assert.AreEqual(expectedMask, table.ignore_folder_output);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>既存表を再取得すると新規表の出力除外設定より既存表のmaskを優先し、利用者設定の上書きを防ぎます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_BaseTablePreservesIgnoreFolderOutput()
    {
        testSettings.PlaylistDefaultIgnoreFolderOutput =
            (int)LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
        LR2SongDBExtended.playlist.CustomFolderType expectedMask = LR2SongDBExtended.playlist.CustomFolderType.UserFolder
            | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"PreserveMaskImport\",\"symbol\":\"P\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);
            var baseTable = new BMSTable
            {
                ignore_folder_output = expectedMask
            };

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), baseTable);

            Assert.AreEqual(expectedMask, table.ignore_folder_output);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>BOM付きUTF-8ヘッダーと相対data_urlの実ファイルを読み、日本語の名称・記号と譜面識別子・タイトルを欠落させません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_BomHeaderAndRelativeDataUrl_LoadsSuccessfully()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"Samalite難易度表\",\r\n\"symbol\":\"夏\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1,2]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Test Song\",\"artist\":\"Test Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));

            Assert.AreEqual("Samalite難易度表", table.name);
            Assert.AreEqual("夏", table.symbol);
            Assert.AreEqual("./score.json", table.data_url);
            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
            Assert.AreEqual("Test Song", table.entries.Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>取得済みHTMLから通常の相対header/dataを保持して各一回だけ取得し、解析の再取得・別表採用・外部DTD通信を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_UsesFetchedHtmlWithoutParserNetworkRequests()
    {
        string parserFallbackHtml =
            "<html><head><meta name=\"bmstable\" content=\"../headers/alternate.json\"></head>"
            + "<body>table B</body></html>";
        await using var parserNetworkServer = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(parserFallbackHtml));

        Uri pageUri = new(parserNetworkServer.Address, "pages/start.html");
        Uri headerUri = new(parserNetworkServer.Address, "headers/main.json");
        Uri dataUri = new(parserNetworkServer.Address, "data/rows.json");
        Uri alternateHeaderUri = new(parserNetworkServer.Address, "headers/alternate.json");
        Uri alternateDataUri = new(parserNetworkServer.Address, "data/alternate.json");
        Uri externalDtdUri = new(parserNetworkServer.Address, "doctype/table.dtd");

        string pageHtml = "<!DOCTYPE html SYSTEM \"" + externalDtdUri.AbsoluteUri + "\">"
            + "<html><head><meta content=\"../headers/main.json\" name=\"bmstable\"></head>"
            + "<body>table A</body></html>";
        var responses = new Dictionary<Uri, string>
        {
            [pageUri] = pageHtml,
            [headerUri] = "{\"name\":\"Table A\",\"symbol\":\"A\",\"data_url\":\"../data/rows.json\"}",
            [dataUri] = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song A\",\"artist\":\"Artist A\",\"level\":\"1\"}]",
            [alternateHeaderUri] = "{\"name\":\"Table B\",\"symbol\":\"B\",\"data_url\":\"../data/alternate.json\"}",
            [alternateDataUri] = "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song B\",\"artist\":\"Artist B\",\"level\":\"2\"}]"
        };
        using var handler = new RecordedExternalTableHttpMessageHandler(responses);
        using var transport = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var appHttpClient = new AppHttpClient(transport, TimeProvider.System);
        var recommendedTableOwner = new PlaylistRecommendedTableOwner(
            static _ => Task.FromException<WalkureScoreInput>(new InvalidOperationException("内蔵推薦はこのケースの対象外です。")),
            new PlaylistOperationNotificationOwner(),
            static () => new CustomFolderOutputSettingsSnapshot());
        var externalOwner = new PlaylistExternalSyncOwner(
            appHttpClient,
            recommendedTableOwner,
            (_, _) => { },
            static () => false,
            static _ => { },
            customFolderOutputSettingsProvider: static () => new CustomFolderOutputSettingsSnapshot());

        BMSTable table = await externalOwner.LoadExternalTableAsync(pageUri);

        Assert.AreEqual("Table A", table.name);
        Assert.AreEqual("A", table.symbol);
        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
        Assert.AreEqual("Song A", table.entries.Single().title);
        Assert.AreEqual(pageUri, table.Page_url);
        Assert.AreEqual("../headers/main.json", table.Header_url.OriginalString);
        Assert.AreEqual("../data/rows.json", table.Data_url.OriginalString);
        Assert.AreEqual(headerUri, table.GetAbsoluteHeaderUrl());
        Assert.AreEqual(dataUri, table.GetAbsoluteDataUrl());
        Assert.AreEqual(1, handler.GetRequestCount(pageUri));
        Assert.AreEqual(1, handler.GetRequestCount(headerUri));
        Assert.AreEqual(1, handler.GetRequestCount(dataUri));
        Assert.AreEqual(3, handler.RequestUris.Count);
        Assert.AreEqual(string.Empty, parserNetworkServer.RequestMethod, parserNetworkServer.Diagnostics);
    }

    /// <summary>slash付きページへの実転送後に相対header/dataを各一回だけ辿り、元pageとentry本体・差分の意味を保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_RedirectedPageResolvesRelativeHeaderAndData()
    {
        var final = SingleRequestHttpServer.StartFiniteResponses([
            new("/tables/", Encoding.UTF8.GetBytes("<html><head><meta name=\"bmstable\" content=\"meta/header.json\"></head></html>")),
            new("/tables/meta/header.json", Encoding.UTF8.GetBytes("{\"name\":\"Page redirect\",\"symbol\":\"P\",\"data_url\":\"../data/rows.json\"}")),
            new("/tables/data/rows.json", Encoding.UTF8.GetBytes(RedirectedRelativeEntries))]);
        var source = new SingleRequestHttpServer([], responseStatusCode: HttpStatusCode.Redirect,
            additionalResponseHeaders: "Location: " + new Uri(final.Address, "tables/").AbsoluteUri + "\r\n");
        using var cancellation = new CancellationTokenSource();
        using var transport = new HttpClient();
        Task<BMSTable>? loading = null;
        Exception? failure = null;
        try
        {
            Uri originalPage = new(source.Address, "tables");
            loading = CreateRedirectExternalOwner(transport).LoadExternalTableAsync(originalPage, cancellationToken: cancellation.Token);
            BMSTable table = await loading;
            Assert.AreEqual("Page redirect", table.name);
            Assert.AreEqual(originalPage, table.Page_url);
            Assert.AreEqual(new Uri(final.Address, "tables/meta/header.json"), table.GetAbsoluteHeaderUrl());
            AssertRedirectedEntries(table, new Uri(final.Address, "tables/data/rows.json"),
                new Uri(final.Address, "tables/packages/song.zip?Token=A"), new Uri(final.Address, "tables/data/patches/fix.zip?Key=B"));
            CollectionAssert.AreEqual(new[] { "/tables" }, source.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/tables/", "/tables/meta/header.json", "/tables/data/rows.json" }, final.RequestTargets);
            await Task.WhenAll(source.Completion, final.Completion);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally { await CompleteRedirectLoadTestAsync(failure, cancellation, loading, source, final); }
    }

    /// <summary>直接headerの実転送後のdirectoryから相対dataを取得し、次回同期用の元headerを転送先へ置換しないことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_RedirectedDirectHeaderPreservesSourceAndResolvesData()
    {
        var final = SingleRequestHttpServer.StartFiniteResponses([
            new("/definitions/header.json", Encoding.UTF8.GetBytes("{\"name\":\"Header redirect\",\"symbol\":\"H\",\"data_url\":\"../rows/data.json\"}")),
            new("/rows/data.json", Encoding.UTF8.GetBytes(RedirectedRelativeEntries))]);
        var source = new SingleRequestHttpServer([], responseStatusCode: HttpStatusCode.Redirect,
            additionalResponseHeaders: "Location: " + new Uri(final.Address, "definitions/header.json").AbsoluteUri + "\r\n");
        using var cancellation = new CancellationTokenSource();
        using var transport = new HttpClient();
        Task<BMSTable>? loading = null;
        Exception? failure = null;
        try
        {
            Uri originalHeader = new(source.Address, "old/header.json");
            loading = CreateRedirectExternalOwner(transport).LoadExternalTableAsync(originalHeader, cancellationToken: cancellation.Token);
            BMSTable table = await loading;
            Assert.AreEqual("Header redirect", table.name);
            Assert.IsNull(table.Page_url);
            Assert.AreEqual(originalHeader, table.Header_url);
            AssertRedirectedEntries(table, new Uri(final.Address, "rows/data.json"),
                new Uri(final.Address, "packages/song.zip?Token=A"), new Uri(final.Address, "rows/patches/fix.zip?Key=B"));
            CollectionAssert.AreEqual(new[] { "/old/header.json" }, source.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/definitions/header.json", "/rows/data.json" }, final.RequestTargets);
            await Task.WhenAll(source.Completion, final.Completion);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally { await CompleteRedirectLoadTestAsync(failure, cancellation, loading, source, final); }
    }

    /// <summary>dataの実転送先を公開基準にして相対entryを読み、取得済み表の実登録・別readonly DB・新readerでも同じ参照と元同期元を保ちます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task RegistrateExternalTableAsync_RedirectedDataPreservesReferencesAfterDatabaseReload()
    {
        var final = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(RedirectedRelativeEntries));
        Uri finalData = new(final.Address, "published/current/rows.json");
        var redirect = new SingleRequestHttpServer([], responseStatusCode: HttpStatusCode.Redirect,
            additionalResponseHeaders: "Location: " + finalData.AbsoluteUri + "\r\n");
        var header = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(
            "{\"name\":\"Data redirect\",\"symbol\":\"D\",\"data_url\":\"" + new Uri(redirect.Address, "old/rows.json").AbsoluteUri + "\"}"));
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalLoadTests), Guid.NewGuid().ToString("N"));
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
            playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings)
            { BMSTables = new ObservableCollection<BMSTable>() };
            OwnStartupWork(playlist, nameof(playlist));
            Uri originalHeader = new(header.Address, "definitions/header.json");
            Task<BMSTable> loading = playlist.ExternalSyncOwner.LoadExternalTableAsync(originalHeader, cancellationToken: cancellation.Token);
            operation = loading;
            BMSTable loaded = await loading;
            Uri expectedUrl = new(final.Address, "published/packages/song.zip?Token=A");
            Uri expectedDiff = new(final.Address, "published/current/patches/fix.zip?Key=B");
            AssertRedirectedEntries(loaded, finalData, expectedUrl, expectedDiff);
            Task<BMSTable> registration = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(loaded, false, "redirect-data-test", cancellation.Token);
            operation = registration;
            BMSTable registered = await registration;
            Assert.AreSame(registered, playlist.BMSTables.Single());
            Assert.IsNull(registered.Page_url);
            Assert.AreEqual(originalHeader, registered.Header_url);
            AssertRedirectedEntries(registered, finalData, expectedUrl, expectedDiff);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                BMSTable persisted = db.Table<BMSTable>().Single();
                Assert.AreEqual(originalHeader, persisted.Header_url);
                Assert.AreEqual(finalData, persisted.GetAbsoluteDataUrl());
                LR2SongDBExtended.playlist_entry entry = db.Table<LR2SongDBExtended.playlist_entry>().Single();
                Assert.AreEqual(expectedUrl, new Uri(entry.url));
                Assert.AreEqual(expectedDiff, new Uri(entry.url_diff));
            }
            reader = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings);
            OwnStartupWork(reader, nameof(reader));
            operation = reader.ReloadTablesAsync(exportBeatorajaBmt: false);
            await operation;
            BMSTable hydrated = reader.BMSTables.Single();
            reader.EnsurePlaylistEntriesLoaded(hydrated, "redirect-data-test");
            Assert.IsNull(hydrated.Page_url);
            Assert.AreEqual(originalHeader, hydrated.Header_url);
            AssertRedirectedEntries(hydrated, finalData, expectedUrl, expectedDiff);
            CollectionAssert.AreEqual(new[] { "/definitions/header.json" }, header.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/old/rows.json" }, redirect.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/published/current/rows.json" }, final.RequestTargets);
            await Task.WhenAll(header.Completion, redirect.Completion, final.Completion);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try { await CompleteRedirectLoadTestAsync(failure, cancellation, operation, header, redirect, final); }
            catch (Exception ex) { cleanupFailures.Add(ex); }
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
                    store.RequestShutdown("redirect-data-test-complete");
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
                System.Diagnostics.Trace.WriteLine("転送登録のcleanup二次失敗: " + cleanupFailure);
            }
        }
    }

    /// <summary>実転送先headerの本文prefix到達後に取り消し、取得Taskの取消終端とfixture解放前のpeer切断を確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_RedirectedBodyCancellationDisposesResponse()
    {
        var final = new SingleRequestHttpServer(Encoding.UTF8.GetBytes("{\"name\":\"Incomplete\",\"data_url\":\"never.json\"}"), holdBody: true);
        var source = new SingleRequestHttpServer([], responseStatusCode: HttpStatusCode.Redirect,
            additionalResponseHeaders: "Location: " + new Uri(final.Address, "header.json").AbsoluteUri + "\r\n");
        using var cancellation = new CancellationTokenSource();
        using var transport = new HttpClient();
        Task<BMSTable>? loading = null;
        Exception? failure = null;
        try
        {
            loading = CreateRedirectExternalOwner(transport).LoadExternalTableAsync(new Uri(source.Address, "source.json"), cancellationToken: cancellation.Token);
            Task reached = await Task.WhenAny(final.BodyPrefixSent.Task, loading, final.Completion);
            if (reached != final.BodyPrefixSent.Task) { await reached; Assert.Fail("本文到達前に取得またはserverが終端しました。"); }
            await final.BodyPrefixSent.Task;
            cancellation.Cancel();
            try { await loading; Assert.Fail("部分本文を成功として返しました。"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert.IsTrue(loading.IsCanceled);
            await final.WaitForPhaseAsync(final.ClientDisconnected.Task, "peer disconnected before fixture release");
            Assert.IsFalse(final.ReleaseBody.Task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "/source.json" }, source.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/header.json" }, final.RequestTargets);
            await Task.WhenAll(source.Completion, final.Completion);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally { await CompleteRedirectLoadTestAsync(failure, cancellation, loading, source, final); }
    }

    private const string RedirectedRelativeEntries = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Redirected song\",\"level\":\"1\",\"url\":\"../packages/song.zip?Token=A\",\"url_diff\":\"patches/fix.zip?Key=B\"}]";

    /// <summary>実loopback通信を使う最小外部取得主体を作り、推薦・補完・出力を対象へ混ぜません。</summary>
    private static PlaylistExternalSyncOwner CreateRedirectExternalOwner(HttpClient transport)
    {
        var recommended = new PlaylistRecommendedTableOwner(
            static _ => Task.FromException<WalkureScoreInput>(new InvalidOperationException("内蔵推薦は対象外です。")),
            new PlaylistOperationNotificationOwner(), static () => new CustomFolderOutputSettingsSnapshot());
        return new PlaylistExternalSyncOwner(new AppHttpClient(transport, TimeProvider.System), recommended,
            (_, _) => { }, static () => false, static _ => { },
            customFolderOutputSettingsProvider: static () => new CustomFolderOutputSettingsSnapshot());
    }

    /// <summary>公開data基準とentry本体・差分を独立した既知の参照先で照合します。</summary>
    private static void AssertRedirectedEntries(BMSTable table, Uri data, Uri url, Uri diff)
    {
        Assert.AreEqual(data, table.GetAbsoluteDataUrl());
        BMSTableEntry entry = table.entries.Single();
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", entry.md5);
        Assert.AreEqual("Redirected song", entry.title);
        Assert.AreEqual(url, entry.Url);
        Assert.AreEqual(diff, entry.Url_diff);
    }

    /// <summary>失敗時もgate・取消・開始済み取得と全serverの実終端を回収し、cleanupの二次失敗で元原因を隠しません。</summary>
    private static async Task CompleteRedirectLoadTestAsync(Exception? failure, CancellationTokenSource cancellation, Task? operation, params SingleRequestHttpServer[] servers)
    {
        cancellation.Cancel();
        foreach (SingleRequestHttpServer server in servers)
        {
            server.ReleaseBodyPrefix.TrySetResult();
            server.ReleaseBody.TrySetResult();
        }
        var cleanupFailures = new List<Exception>();
        if (operation != null)
        {
            try { await operation; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { cleanupFailures.Add(ex); }
        }
        foreach (SingleRequestHttpServer server in servers)
        {
            try { await server.DisposeAsync(); }
            catch (Exception ex) { cleanupFailures.Add(ex); }
        }
        if (cleanupFailures.Count > 0)
        {
            var cleanupFailure = new AggregateException(cleanupFailures);
            if (failure == null) { throw cleanupFailure; }
            System.Diagnostics.Trace.WriteLine("転送テストのcleanup二次失敗: " + cleanupFailure);
        }
    }

    /// <summary>相対data_urlをドライブとUNCのfileヘッダー基準で解決し、上位ディレクトリとUNCホストを失う互換性退行を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTable_ExternalDataUrlResolutionPreservesDriveFileAndUncRoutes()
    {
        var driveTable = new BMSTable
        {
            Header_url = new Uri("file:///C:/tables/header.json"),
            Data_url = new Uri("../data/score.json", UriKind.Relative)
        };

        Uri driveDataUri = driveTable.GetAbsoluteDataUrl();

        Assert.AreEqual("file", driveDataUri.Scheme);
        Assert.AreEqual("C:/data/score.json", driveDataUri.AbsolutePath);

        var uncTable = new BMSTable
        {
            Header_url = new Uri("file://server/share/tables/header.json"),
            Data_url = new Uri("../data/score.json", UriKind.Relative)
        };

        Uri uncDataUri = uncTable.GetAbsoluteDataUrl();

        Assert.AreEqual("file", uncDataUri.Scheme);
        Assert.AreEqual("server", uncDataUri.Host);
        Assert.AreEqual("/share/data/score.json", uncDataUri.AbsolutePath);
    }

    /// <summary>ヘッダー参照のないHTMLは専用例外として同期入口へ返し、JSONと誤認した成功や失敗の置換を防ぎます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_HtmlWithoutHeaderMeta_ThrowsHeaderUriNotFound()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string htmlPath = Path.Combine(tempDirectory, "table.html");
            File.WriteAllText(htmlPath, "<html><head><title>No header</title></head><body>moved</body></html>", Encoding.UTF8);

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            Assert.ThrowsException<PlaylistHeaderUriNotFoundException>(() => playlist.ExternalSyncOwner.LoadExternalTable(new Uri(htmlPath)));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>閉じタグ欠損とmeta属性順の違いを受理し、HTMLから相対ヘッダーを取得して表の名称・記号・行を復元します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_UnclosedHtmlWithReorderedMetaAttributes_LoadsSuccessfully()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string htmlPath = Path.Combine(tempDirectory, "table.html");
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllText(
                htmlPath,
                "<html><head><meta content=\"header.json\" name=\"bmstable\"><title>unfinished",
                Encoding.UTF8);
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes(
                "{\"name\":\"UnclosedHtml\",\"symbol\":\"U\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes(
                "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(htmlPath));

            Assert.AreEqual("UnclosedHtml", table.name);
            Assert.AreEqual("U", table.symbol);
            Assert.AreEqual(1, table.entries.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>SHA-256だけの外部行をMD5欠損のまま保持し、SHA識別子とタイトルを同期入口から復元します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_Sha256OnlyEntry_LoadsSuccessfully()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            string sha256 = new('a', 64);
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"ShaOnly\",\"symbol\":\"S\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"sha256\":\"" + sha256 + "\",\"title\":\"Sha Song\",\"artist\":\"Sha Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual(1, table.entries.Count);
            Assert.IsNull(table.entries.Single().md5);
            Assert.AreEqual(sha256, table.entries.Single().sha256);
            Assert.AreEqual("Sha Song", table.entries.Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>両hashを持つ外部行のMD5とSHA-256を共に保持し、一方の識別子を失う取込みを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_Md5AndSha256Entry_LoadsBothHashes()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"DualHash\",\"symbol\":\"D\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Dual Song\",\"artist\":\"Dual Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
            Assert.AreEqual("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", table.entries.Single().sha256);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>不正SHA-256は除外しても有効MD5の行を残し、従来受理した譜面まで破棄する厳格化を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_InvalidSha256_IgnoresSha256AndKeepsRow()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"InvalidSha\",\"symbol\":\"I\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"invalid\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
            Assert.IsNull(table.entries.Single().sha256);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>タイトルだけの行は保持し、全項目が空の行だけを除外することで空行判定の過剰除外を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_FullyEmptyEntry_IgnoresRow()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"EmptyRow\",\"symbol\":\"E\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"title\":\"Title Only\"},{\"title\":\"\",\"artist\":\"\",\"folder\":\"\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("Title Only", table.entries.Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>外部ヘッダーに接頭辞がない再取得でも既存の空・通常・非CP932接頭辞を維持し、その接頭辞でfolderを作成します。</summary>
    [DataTestMethod]
    [TestCategory("Playlist")]
    [DataRow("", "1")]
    [DataRow("LEVEL ", "LEVEL 1")]
    [DataRow("😀", "😀1")]
    public async Task LoadExternalTableAsync_BaseTableCompatPrefixIsPreservedWhenHeaderOmitsCompatPrefix(string compatPrefix, string expectedFolder)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"data_url\":\"./score.json\",\"level_order\":[1]}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);
            var baseTable = new BMSTable
            {
                name = "Existing",
                symbol = "ExistingSymbol",
                compat_prefix = compatPrefix
            };

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), baseTable);

            Assert.AreEqual(compatPrefix, table.compat_prefix);
            Assert.AreEqual(expectedFolder, table.entries.Single().folder);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>外部ヘッダーの接頭辞より既存の空または利用者指定接頭辞を優先し、folderの利用者設定を上書きしません。</summary>
    [DataTestMethod]
    [TestCategory("Playlist")]
    [DataRow("", "1")]
    [DataRow("LOCAL ", "LOCAL 1")]
    public async Task LoadExternalTableAsync_BaseTableCompatPrefixIsPreservedWhenHeaderDefinesCompatPrefix(string compatPrefix, string expectedFolder)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"compat_prefix\":\"EXTERNAL \",\"data_url\":\"./score.json\",\"level_order\":[1]}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);
            var baseTable = new BMSTable
            {
                name = "Existing",
                symbol = "ExistingSymbol",
                compat_prefix = compatPrefix
            };

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), baseTable);

            Assert.AreEqual(compatPrefix, table.compat_prefix);
            Assert.AreEqual(expectedFolder, table.entries.Single().folder);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>既存接頭辞の維持に合わせて外部folder_orderを変換し、folder_listにも外部で指定した逆順を引き継ぎます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_HeaderFolderOrderIsRewrittenWhenCompatPrefixIsPreserved()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"compat_prefix\":\"EXTERNAL \",\"data_url\":\"./score.json\",\"folder_order\":[\"EXTERNAL 2\",\"EXTERNAL 1\"]}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song 1\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song 2\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);
            var baseTable = new BMSTable
            {
                name = "Existing",
                symbol = "ExistingSymbol",
                compat_prefix = "LOCAL "
            };

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), baseTable);

            CollectionAssert.AreEqual(new[] { "LOCAL 2", "LOCAL 1" }, table.Folder_order.ToArray());
            CollectionAssert.AreEqual(new[] { "LOCAL 2", "LOCAL 1" }, table.folder_list.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>外部接頭辞と対応folder名だけの変更はheader hashを変えず、再取得で既存接頭辞を保つ互換性を確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_HeaderHashIgnoresCompatPrefix()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"compat_prefix\":\"EXTERNAL \",\"data_url\":\"./score.json\",\"folder_order\":[\"EXTERNAL 1\"]}"));
            BMSTable first = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));

            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"compat_prefix\":\"CHANGED \",\"data_url\":\"./score.json\",\"folder_order\":[\"CHANGED 1\"]}"));
            BMSTable second = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), first);

            Assert.AreEqual(first.header_sha256, second.header_sha256);
            Assert.AreEqual(first.compat_prefix, second.compat_prefix);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>接頭辞と順序のない外部ヘッダーではsymbolからfolder接頭辞を推定し、levelゼロも失わず結合します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_InferCompatPrefixFromHeaderSymbolWhenHeaderOrderIsMissing()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"0\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings);

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));

            Assert.AreEqual("st", table.compat_prefix);
            Assert.AreEqual("st0", table.entries.Single().folder);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実JSON登録で非CP932タグを従来接頭辞へ変換し、実保存と新主体のhydration後も名称・行・所属ID・folderを保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task RegistrateExternalTableAsync_NonCp932TagPersistsLegacyPrefix()
    {
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsPlaylist? playlist = null, reader = null;
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"UnicodeImport\",\"symbol\":\"st\",\"tag\":\"😀\",\"data_url\":\"./score.json\",\"level_order\":[1]}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            playlist = new TestBmsPlaylist(songDbPath, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };

            await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(new Uri(headerJsonPath));

            BMSTable table = playlist.BMSTables.Single();
            Assert.AreEqual("LEVEL ", table.compat_prefix);
            Assert.AreEqual("LEVEL 1", table.entries.Single().folder);
            Assert.AreSame(table, table.entries.Single().parent);
            Assert.AreEqual(table.playlist_id, table.entries.Single().playlist_id);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == table.playlist_id);
                Assert.AreEqual("LEVEL ", persisted.compat_prefix);
                Assert.AreEqual("LEVEL 1", verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = ?;", table.playlist_id));
            }
            reader = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings);
            await reader.ReloadTablesAsync(exportBeatorajaBmt: false);
            BMSTable hydrated = reader.BMSTables.Single();
            reader.EnsurePlaylistEntriesLoaded(hydrated, "json-registration-test");
            Assert.AreEqual(table.playlist_id, hydrated.playlist_id);
            Assert.AreEqual("UnicodeImport", hydrated.name);
            BMSTableEntry entry = hydrated.entries.Single();
            Assert.AreSame(hydrated, entry.parent);
            Assert.AreEqual(hydrated.playlist_id, entry.playlist_id);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", entry.md5);
            Assert.AreEqual("Song", entry.title);
            Assert.AreEqual("Artist", entry.artist);
            Assert.AreEqual("LEVEL 1", entry.folder);
            Assert.AreEqual(1d, entry.level);
        }
        finally
        {
            reader?.RequestShutdown("json-registration-test-complete");
            playlist?.RequestShutdown("json-registration-test-complete");
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>登録済み名称と衝突するURL取込みは名称付き専用例外を返し、既存表を増減させない拒否を確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task RegistrateExternalTableAsync_ExistingPlaylistNameThrowsSpecificException()
    {
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"DuplicateImport\",\"symbol\":\"D\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { new BMSTable { name = "DuplicateImport" } })
            };

            PlaylistAlreadyExistsException ex = await Assert.ThrowsExceptionAsync<PlaylistAlreadyExistsException>(async delegate
            {
                await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(new Uri(headerJsonPath));
            });

            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Error_PlaylistAlreadyExists, ex.Message);
            Assert.AreEqual("DuplicateImport", ex.PlaylistName);
            Assert.AreEqual(1, playlist.BMSTables.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static byte[] CreateUtf8BomBytes(string text)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    private static string CreateTempSongDbPath(string tempDirectory)
    {
        string tempSongDbPath = Path.Combine(tempDirectory, "song.db");
        using (var _ = new LR2SongDBExtended(tempSongDbPath))
        {
        }
        PlaylistPersistenceRepository.EnsureSchema(tempSongDbPath);
        return tempSongDbPath;
    }

    private sealed class RecordedExternalTableHttpMessageHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<Uri, string> responses;
        private readonly List<Uri> requestUris = [];

        /// <summary>要求先ごとの合成応答を設定し、アプリ側の取得を記録します。</summary>
        internal RecordedExternalTableHttpMessageHandler(IReadOnlyDictionary<Uri, string> responses)
        {
            this.responses = responses ?? throw new ArgumentNullException(nameof(responses));
        }

        /// <summary>アプリのHTTP経路が取得したURIの記録です。</summary>
        internal IReadOnlyList<Uri> RequestUris => requestUris;

        /// <summary>指定URIがアプリ側で取得された回数を返します。</summary>
        internal int GetRequestCount(Uri uri)
        {
            return requestUris.Count(requestUri => requestUri == uri);
        }

        /// <summary>実通信せず合成応答を返し、未定義の取得先と取消しを失敗として伝えます。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri requestUri = request.RequestUri ?? throw new InvalidOperationException("HTTP request URI is missing.");
            requestUris.Add(requestUri);
            if (!responses.TryGetValue(requestUri, out string? responseBody) || responseBody is null)
            {
                throw new HttpRequestException("Unexpected external table request: " + requestUri);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
