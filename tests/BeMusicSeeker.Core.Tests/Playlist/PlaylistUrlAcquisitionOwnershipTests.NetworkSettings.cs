using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using Ribbit.Net;
using static BeMusicSeeker.TestSupport.Components.PlaylistUrlOwnershipTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>譜面・アーカイブ取得の1ファイル上限と応答待ちが、操作開始時の設定値で全入口へ適用されることを確認します。</summary>
public sealed partial class PlaylistUrlAcquisitionOwnershipTests
{
    private const long Mebibyte = 1024L * 1024L;

    private static PlaylistUrlAcquisitionOptionsSnapshot CreateFileOptions(int limitMiB, int idleSeconds = 30)
        => PlaylistUrlAcquisitionOptionsSnapshot.CreateCurrent(TestSettingsFactory.CreateIsolatedSettings(values =>
        {
            values.UrlDownloadMaxFileSizeMiB = limitMiB;
            values.UrlDownloadIdleTimeoutSeconds = idleSeconds;
        }));

    /// <summary>宣言長を持たない本文は、本文を持たない宣言長だけの応答（巨大な上限の確認用）も作れるようにします。</summary>
    private static AppHttpResponse CreateDeclaredResponse(Uri uri, long? declaredLength, byte[] body)
    {
        var message = new HttpResponseMessage { Content = new StreamContent(new NonSeekableMemoryStream([])) };
        if (declaredLength.HasValue) { message.Content.Headers.ContentLength = declaredLength.Value; }
        message.RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
        return new AppHttpResponse(uri, message, new NonSeekableMemoryStream(body));
    }

    private sealed class NonSeekableMemoryStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override bool CanSeek => false;
    }

    /// <summary>1ファイル上限は宣言長と実受信量をそれぞれ独立に、上限ちょうどは受理して+1は拒否し、拒否した場合は保存物を残さず、宣言長での拒否は書込みも始めません。</summary>
    /// <remarks>
    /// 上限は設定の1 MiBと16384 MiB（long の17179869184バイト）で確認します。16384 MiBは巨大な実受信を作れないため宣言長だけを確認します。
    /// 既定の512 MiBへの固定、int計算の桁あふれ、宣言長だけ・実受信量だけの判定を検出します。
    /// </remarks>
    [TestMethod]
    [DataRow(1, 0L, 0L, "Downloaded", true)]
    [DataRow(1, long.MinValue, 0L, "Downloaded", true)]
    [DataRow(1, 1L, long.MinValue, "BlockedBySizeLimit", false)]
    [DataRow(1, long.MinValue, 1L, "BlockedBySizeLimit", true)]
    [DataRow(1, 0L, 1L, "BlockedBySizeLimit", true)]
    [DataRow(16384, 0L, long.MinValue, "Downloaded", true)]
    [DataRow(16384, 1L, long.MinValue, "BlockedBySizeLimit", false)]
    public async Task DownloadCandidate_FileSizeLimitAppliesDeclaredAndActualBytesIndependently(
        int limitMiB, long declaredOffset, long actualOffset, string expectedKind, bool expectWrite)
    {
        PlaylistUrlDownloadResultKind expected = Enum.Parse<PlaylistUrlDownloadResultKind>(expectedKind);
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            long limit = limitMiB * Mebibyte;
            Uri uri = new("https://example.invalid/package.zip");
            byte[] body = actualOffset == long.MinValue ? [1, 2, 3] : new byte[limit + actualOffset];
            long? declared = declaredOffset == long.MinValue ? null : limit + declaredOffset;
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(uri, () => CreateDeclaredResponse(uri, declared, body));

            PlaylistUrlDownloadResult result = await new PlaylistUrlAcquisitionWorkflow(gateway, _ => { })
                .DownloadCandidateAsync(uri, options: CreateFileOptions(limitMiB));

            Assert.AreEqual(expected, result.Kind);
            Assert.AreEqual(expectWrite ? 1 : 0, gateway.OpenWriteCount, "宣言長で拒否する場合は書込みを始めません。");
            if (expected == PlaylistUrlDownloadResultKind.Downloaded)
            {
                Assert.AreEqual(body.Length, new FileInfo(result.FilePath).Length);
            }
            else
            {
                Assert.AreEqual(0, Directory.GetFiles(root).Length, "拒否した取得物を残しません。");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>共有ページ解析用のHTMLは既存の2 MiBで読み、1ファイル上限（1 MiB）より大きい1.5 MiBの宣言長でも解決して本体を取得します。</summary>
    /// <remarks>共有ページの解決を1ファイル上限で先に拒否する誤りを検出します。</remarks>
    [TestMethod]
    public async Task DownloadCandidate_SharedPageHtmlIsIndependentOfFileSizeLimit()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Uri pageUri = new("https://www.mediafire.com/file/test-page/playlist");
            Uri directUri = new("https://download.mediafire.com/test-download/package.zip");
            string html = "<html><body><a id=\"downloadButton\" href=\"" + directUri + "\">Download</a>"
                + new string(' ', (int)(Mebibyte * 3 / 2)) + "</body></html>";
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(pageUri, () => CreateHttpResponse(pageUri, Encoding.UTF8.GetBytes(html), "text/html"));
            gateway.AddResponse(directUri, () => CreateHttpResponse(directUri, [1, 2, 3], "application/octet-stream"));

            PlaylistUrlDownloadResult result = await new PlaylistUrlAcquisitionWorkflow(gateway, _ => { })
                .DownloadCandidateAsync(pageUri, options: CreateFileOptions(limitMiB: 1));

            Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
            CollectionAssert.AreEqual(new[] { pageUri.AbsoluteUri, directUri.AbsoluteUri }, gateway.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(result.FilePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>単発・一括・外部APIの各入口が、操作開始時の1ファイル上限と応答待ちを使い、設定を変えた次の操作は新しい値を使います。</summary>
    /// <remarks>
    /// 宣言長が1 MiB+1の応答を、上限1 MiBの設定では拒否し、2 MiBへ変えた次の操作では取得します。同時に、ヘッダー待ちとして渡した秒数も
    /// 操作の開始時の値であることを確認します。入口ごとに既定値や古い値を使う誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow("single")]
    [DataRow("bulk")]
    [DataRow("external")]
    public async Task WorkspaceEntries_ApplyOperationStartLimitsAndNextOperationUsesNewValues(string entry)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            Uri uri = new("https://example.invalid/package.zip");
            const string md5 = "11111111111111111111111111111111";
            var gateway = new RecordingPlaylistUrlDownloadGateway(root);
            gateway.AddResponse(uri, () => CreateDeclaredResponse(uri, Mebibyte + 1, [1, 2, 3]));
            PlaylistUrlAcquisitionOptionsSnapshot current = CreateFileOptions(limitMiB: 1, idleSeconds: 7);
            int installs = 0, browsers = 0;
            workspace = CreateWorkspace(databaseScope, action => action(),
                () => current,
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                installSink: paths => { installs += paths.Count; return true; }, browserSink: _ => browsers++,
                dialogService: new RecordingPlaylistUrlDialogService(),
                externalLookupService: new PlaylistExternalPackageLookupService([new BatchHandoffLookupProvider(new Dictionary<string, Uri> { [md5] = uri })]));
            current = new PlaylistUrlAcquisitionOptionsSnapshot
            {
                AutoInstall = true,
                ScanBmsFilesOnStartup = true,
                MaxFileSizeBytes = current.MaxFileSizeBytes,
                IdleTimeout = current.IdleTimeout
            };

            Task Run() => entry switch
            {
                "single" => workspace.RunSinglePlaylistUrlAsync(uri),
                "bulk" => workspace.RunPlaylistUrlBatchAsync([uri], isDiffUrl: false),
                _ => workspace.RunPlaylistExternalPackageLookupAsync([CreatePlaylistExternalPackageRow(md5)])
            };

            await Run();
            Assert.AreEqual(0, gateway.OpenWriteCount, "上限1 MiBの設定では宣言長1 MiB+1を書込み前に拒否します。");
            Assert.AreEqual(0, installs);
            Assert.AreEqual(0, browsers, "サイズ超過をブラウザー代替へ流しません。");
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(7) }, gateway.HeaderTimeouts);

            current = new PlaylistUrlAcquisitionOptionsSnapshot
            {
                AutoInstall = true,
                ScanBmsFilesOnStartup = true,
                MaxFileSizeBytes = 2 * Mebibyte,
                IdleTimeout = TimeSpan.FromSeconds(9)
            };
            await Run();
            Assert.AreEqual(1, gateway.OpenWriteCount, "設定を変えた次の操作は新しい上限を使います。");
            Assert.AreEqual(1, installs);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9) }, gateway.HeaderTimeouts);
        }
        finally
        {
            try
            {
                workspace?.RequestPlaylistUrlShutdown();
                if (workspace != null) { await workspace.WaitForPlaylistUrlIdleAsync(); }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>一括・外部APIの進行中に設定を変えても、同じ操作の2件目は開始時の1ファイル上限と応答待ちを使い、次の操作から新しい値を使います。</summary>
    /// <remarks>
    /// 1件目の応答を保留している間に上限を1 MiBから4 MiBへ、応答待ちを7秒から9秒へ変えます。2件目（宣言長2 MiB）は開始時の1 MiBで拒否され、
    /// 次の操作で取得されます。要求ごとに設定を読み直す誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow("bulk")]
    [DataRow("external")]
    public async Task WorkspaceBatch_PendingFirstItemKeepsStartValuesForSecondItem(string entry)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistUrlAcquisitionOwnershipTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PlaylistWorkspaceViewModel? workspace = null;
        Task? operation = null;
        HoldFirstGateway? gateway = null;
        try
        {
            Uri first = new("https://example.invalid/first.zip");
            Uri second = new("https://example.invalid/second.zip");
            string[] md5s = ["11111111111111111111111111111111", "22222222222222222222222222222222"];
            var recording = new RecordingPlaylistUrlDownloadGateway(root);
            recording.AddResponse(first, () => CreateDeclaredResponse(first, 1024, [1, 2, 3]));
            recording.AddResponse(second, () => CreateDeclaredResponse(second, 2 * Mebibyte, [4, 5, 6]));
            gateway = new HoldFirstGateway(recording);
            PlaylistUrlAcquisitionOptionsSnapshot current = CreateFileOptions(limitMiB: 1, idleSeconds: 7);
            var installed = new List<string>();
            workspace = CreateWorkspace(databaseScope, action => action(),
                () => current,
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                installSink: paths => { installed.AddRange(paths.Select(path => Path.GetFileName(path)!)); return true; }, browserSink: _ => { },
                dialogService: new RecordingPlaylistUrlDialogService(),
                externalLookupService: new PlaylistExternalPackageLookupService([new BatchHandoffLookupProvider(
                    new Dictionary<string, Uri> { [md5s[0]] = first, [md5s[1]] = second })]));

            operation = entry == "bulk"
                ? workspace.RunPlaylistUrlBatchAsync([first, second], isDiffUrl: false)
                : workspace.RunPlaylistExternalPackageLookupAsync(md5s.Select(CreatePlaylistExternalPackageRow));
            await gateway.FirstStarted.Task.WaitAsync(TestContext.CancellationToken);
            current = CreateFileOptions(limitMiB: 4, idleSeconds: 9);
            gateway.ReleaseFirst.TrySetResult();
            await operation.WaitAsync(TestContext.CancellationToken);

            CollectionAssert.AreEqual(new[] { "first.zip" }, installed, "2件目は開始時の1 MiB上限で拒否します。");
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) }, recording.HeaderTimeouts);

            installed.Clear();
            operation = entry == "bulk"
                ? workspace.RunPlaylistUrlBatchAsync([second], isDiffUrl: false)
                : workspace.RunPlaylistExternalPackageLookupAsync([CreatePlaylistExternalPackageRow(md5s[1])]);
            await operation.WaitAsync(TestContext.CancellationToken);
            CollectionAssert.AreEqual(new[] { "second.zip" }, installed, "次の操作は新しい4 MiB上限を使います。");
            Assert.AreEqual(TimeSpan.FromSeconds(9), recording.HeaderTimeouts[^1]);
        }
        finally
        {
            gateway?.ReleaseFirst.TrySetResult();
            try
            {
                workspace?.RequestPlaylistUrlShutdown();
                if (workspace != null) { await workspace.WaitForPlaylistUrlIdleAsync(); }
                if (operation != null) { try { await operation; } catch (OperationCanceledException) { } }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>最初の要求だけを合図まで保留し、以後は記録用gatewayへ委ねます。</summary>
    private sealed class HoldFirstGateway(RecordingPlaylistUrlDownloadGateway inner) : IPlaylistUrlDownloadGateway
    {
        private int calls;
        internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AppHttpResponse> OpenReadAsync(Uri uri, TimeSpan headerTimeout, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            return await inner.OpenReadAsync(uri, headerTimeout, cancellationToken);
        }

        public string GetTemporaryDirectory() => inner.GetTemporaryDirectory();
        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share) => inner.OpenWrite(path, mode, access, share);
        public bool FileExists(string path) => inner.FileExists(path);
        public void DeleteFile(string path) => inner.DeleteFile(path);
    }
}
