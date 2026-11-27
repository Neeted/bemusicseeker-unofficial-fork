using System.IO.Compression;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;


public sealed partial class BmsPlaylistExternalLoadTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    private const int Mebibyte = 1024 * 1024;

    private static Settings CreateLimitSettings(int htmlMiB, int jsonMiB) => TestSettingsFactory.CreateIsolatedSettings(values =>
    {
        values.ExternalTableHtmlLimitMiB = htmlMiB;
        values.ExternalTableJsonLimitMiB = jsonMiB;
    });

    private TestBmsPlaylist CreateLimitPlaylist(string root, Settings limitSettings)
    {
        string songDbPath = CreateTempSongDbPath(databaseScope, root);
        return new TestBmsPlaylist(databaseScope, songDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: new TestUiScheduler(() => null), settings: limitSettings);
    }

    private static void WriteWithLeadingWhitespace(string path, int whitespaceBytes, string text, int trailingSpaces = 0)
    {
        using FileStream file = File.Create(path);
        byte[] whitespace = new byte[81920]; Array.Fill(whitespace, (byte)' ');
        for (int remaining = whitespaceBytes; remaining > 0;)
        {
            int count = Math.Min(remaining, whitespace.Length);
            file.Write(whitespace, 0, count);
            remaining -= count;
        }
        file.Write(Encoding.UTF8.GetBytes(text));
        for (int index = 0; index < trailingSpaces; index++) { file.WriteByte((byte)' '); }
    }

    /// <summary>設定したJSON上限（2 MiB）のローカル入力は、HTML上限（1 MiB）を超える先頭の空白を含むちょうどまで受理し、+1は解析前に拒否します。相対data_urlも維持します。</summary>
    /// <remarks>HTML上限だけで未判定の入力を止める誤りと、JSON上限の境界の1バイト違いを検出します。</remarks>
    [TestMethod]
    public async Task ExternalJson_LocalLimitIncludesLeadingWhitespaceAndRejectsOneExtraByte()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "ExternalJsonLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string headerPath = Path.Combine(root, "header.html");
            File.WriteAllText(Path.Combine(root, "data.json"), "[]");
            string header = "{\"name\":\"Bounded\",\"symbol\":\"B\",\"data_url\":\"data.json\"}";
            WriteWithLeadingWhitespace(headerPath, 2 * Mebibyte - header.Length, header);
            TestBmsPlaylist playlist = CreateLimitPlaylist(root, CreateLimitSettings(htmlMiB: 1, jsonMiB: 2));

            BMSTable result = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath));

            Assert.AreEqual("Bounded", result.name);
            Assert.AreEqual("data.json", result.data_url);
            Assert.AreEqual(0, result.entries.Count);
            WriteWithLeadingWhitespace(headerPath, 2 * Mebibyte - header.Length, header, trailingSpaces: 1);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath)));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>HTML上限（2 MiB）がJSON上限（1 MiB）より大きい設定でも、先頭の空白の後が <c>{</c> ならJSON上限で拒否し、<c>&lt;html&gt;</c> ならHTML上限ちょうどまで受理します。</summary>
    /// <remarks>
    /// 種別が確定するまでは大きい方まで読み、確定した時点で累積量を該当する上限と比べることを、ローカル入力の全体経路で確認します。
    /// 上限の大小を固定の順序と仮定する誤りと、種別ごとの上限の取り違えを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow("json", 0, false)]
    [DataRow("html", 0, true)]
    [DataRow("html", 1, false)]
    public async Task ExternalLocal_HtmlLimitLargerThanJsonLimitAppliesDeterminedKindBound(string kind, int extraBytes, bool accepted)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "ExternalLimitInverted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string pagePath = Path.Combine(root, "page.html");
            File.WriteAllText(Path.Combine(root, "header.json"), "{\"name\":\"Inverted\",\"symbol\":\"I\",\"data_url\":\"data.json\"}");
            File.WriteAllText(Path.Combine(root, "data.json"), "[]");
            if (kind == "json")
            {
                // JSON上限（1 MiB）ちょうどの空白の後にJSONを置き、HTML上限（2 MiB）以内でもJSONとして拒否されることを見る。
                WriteWithLeadingWhitespace(pagePath, Mebibyte, "{\"name\":\"Inverted\",\"symbol\":\"I\",\"data_url\":\"data.json\"}");
            }
            else
            {
                string page = "<html><meta name=\"bmstable\" content=\"header.json\"></html>";
                WriteWithLeadingWhitespace(pagePath, 2 * Mebibyte - page.Length, page, trailingSpaces: extraBytes);
            }
            TestBmsPlaylist playlist = CreateLimitPlaylist(root, CreateLimitSettings(htmlMiB: 2, jsonMiB: 1));

            if (accepted)
            {
                BMSTable result = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(pagePath));
                Assert.AreEqual("Inverted", result.name);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(pagePath)));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>実HTTPのgzip展開後HTMLを設定したHTML上限（1 MiB）で制限し、小さなContent-Lengthでも上限ちょうどは解析へ進め、+1は解析前に拒否します。</summary>
    /// <remarks>
    /// 受理側は、サイズ検査を通過して解析へ進み、meta欠如の失敗（<see cref="PlaylistHeaderUriNotFoundException"/>）になることで観測します。
    /// 圧縮後の大きさだけで判定する誤りと、HTTPだけ別の上限を使う誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    public async Task ExternalHtml_GzipIsLimitedAfterDecompression(int extraBytes, bool rejectedBySize)
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        var httpRequests = new List<Task>();
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); return ownedServer; }

        Exception? httpPrimaryFailure = null;
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        SingleRequestHttpServer? server = null;

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes("<html>"));
                byte[] spaces = new byte[Mebibyte - 6 + extraBytes];
                Array.Fill(spaces, (byte)' ');
                gzip.Write(spaces);
            }
            compressed = output.ToArray();
        }
        string root = Path.Combine(migrationWorkspace.RootDirectory, "ExternalGzipLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            server = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, compressed, additionalResponseHeaders: "Content-Encoding: gzip\r\n"));
            TestBmsPlaylist playlist = CreateLimitPlaylist(root, CreateLimitSettings(htmlMiB: 1, jsonMiB: 2));
            Task load = rejectedBySize
                ? Assert.ThrowsExactlyAsync<InvalidDataException>(() => playlist.ExternalSyncOwner.LoadExternalTableAsync(server.Address, cancellationToken: httpOperation.Token))
                : Assert.ThrowsExactlyAsync<PlaylistHeaderUriNotFoundException>(() => playlist.ExternalSyncOwner.LoadExternalTableAsync(server.Address, cancellationToken: httpOperation.Token));
            await SingleRequestHttpServer.AwaitOperationAsync(load, httpRequests, httpOperation.Token);
        }
        catch (Exception httpPrimary) { httpPrimaryFailure = httpPrimary; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, httpPrimaryFailure, httpRequests, httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            if (httpJoinFailure != null && httpCleanup.IsCancellationRequested) { throw httpJoinFailure; }
            try
            {
                httpCleanup.Token.ThrowIfCancellationRequested(); Directory.Delete(root, true); migrationWorkspace.Dispose();
            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (httpPrimaryFailure != null) { throw new AggregateException(httpPrimaryFailure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }

}
