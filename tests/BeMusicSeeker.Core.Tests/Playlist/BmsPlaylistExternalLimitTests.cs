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

    /// <summary>ローカル初回JSONはHTML上限を超える空白を含む100MiBちょうどまで許可し、+1は解析前に拒否します。相対data_urlも維持します。</summary>
    [TestMethod]
    public async Task ExternalJson_RealLimitIncludesLeadingWhitespaceAndRejectsOneExtraByte()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, "ExternalJsonLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string headerPath = Path.Combine(root, "header.html");
            string dataPath = Path.Combine(root, "data.json");
            File.WriteAllText(dataPath, "[]");
            byte[] header = Encoding.UTF8.GetBytes("{\"name\":\"Bounded\",\"symbol\":\"B\",\"data_url\":\"data.json\"}");
            using (FileStream file = File.Create(headerPath))
            {
                byte[] whitespace = new byte[81920]; Array.Fill(whitespace, (byte)' ');
                int remaining = ExternalPlaylistContentReader.JsonLimitBytes - header.Length;
                while (remaining > 0) { int count = Math.Min(remaining, whitespace.Length); file.Write(whitespace, 0, count); remaining -= count; }
                file.Write(header);
            }
            var playlist = new TestBmsPlaylist(databaseScope, CreateTempSongDbPath(databaseScope, root), lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, CreateTempSongDbPath(databaseScope, root)), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable result = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath));
            Assert.AreEqual("Bounded", result.name);
            Assert.AreEqual(0, result.entries.Count);
            using (var file = new FileStream(headerPath, FileMode.Append)) { file.WriteByte((byte)' '); }
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath)));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>実HTTPのgzip展開後HTMLを10MiBで制限し、小さなContent-Lengthでもmeta取得・解析へ進ませません。exactと+1は共通readerで分担します。</summary>
    [TestMethod]
    public async Task ExternalHtml_GzipIsLimitedAfterDecompression()
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
                byte[] spaces = new byte[81920]; Array.Fill(spaces, (byte)' ');
                for (int count = 0; count < ExternalPlaylistContentReader.HtmlLimitBytes; count += spaces.Length) { gzip.Write(spaces); }
            }
            compressed = output.ToArray();
        }
        string root = Path.Combine(migrationWorkspace.RootDirectory, "ExternalGzipLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Settings httpSettings = TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config"));
            server = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, compressed, additionalResponseHeaders: "Content-Encoding: gzip\r\n"));
            var playlist = new TestBmsPlaylist(databaseScope, CreateTempSongDbPath(databaseScope, root), lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, CreateTempSongDbPath(databaseScope, root)), uiScheduler: new TestUiScheduler(() => null), settings: httpSettings);
            await SingleRequestHttpServer.AwaitOperationAsync(Assert.ThrowsExactlyAsync<InvalidDataException>(() => playlist.ExternalSyncOwner.LoadExternalTableAsync(server.Address, cancellationToken: httpOperation.Token)), httpRequests, httpOperation.Token);
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
