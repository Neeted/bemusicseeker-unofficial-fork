using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 更新ZIPの逐次転送を、実際の <see cref="HttpClient"/> 経路・実ファイル・手動時計で検証します。
/// 受信が続く限り累計時間で打ち切らないこと、期限と取消が各段階へ伝わること、失敗時に残骸を残さないことを分担します。
/// </summary>
[TestClass]
public sealed class UpdateDownloadServiceTransferTests
{
    private const int ChunkBytes = 100;

    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// ヘッダー待ちと各読取りが29秒ずつ進み、書込みが31秒止まっても、受信が続く限り累計300秒を超えて取得できる。
    /// 受信中に進捗とファイル出力が始まり、通知が読取り回数に比例せず、完了後に通知しないこと、
    /// 転送全体の期限や書込み時間を期限へ含める誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task LongTransferContinuesAsLongAsDataKeepsArriving()
    {
        byte[] content = CreateContent(81 * ChunkBytes);
        await using var harness = new Harness(TestContext, content);
        harness.Handler.HoldHeaders = true;
        harness.Body.HoldRead = index => index is >= 64 and < 75;
        harness.HoldWriteAt = 75;
        Task<string> download = harness.Start(harness.CreateAsset());

        await harness.Reach(harness.Handler.HeaderStarted.Task, "ヘッダー待ち");
        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.IsFalse(harness.Handler.HeaderToken.IsCancellationRequested);
        harness.Handler.ReleaseHeaders.TrySetResult();

        // 時計を止めたまま64回受信した時点で、進捗の通知は2回以下に収まる。
        PendingRead pending = await harness.Reach(harness.Body.NextPending(TestContext.CancellationToken), "64回の受信");
        Assert.IsLessThanOrEqualTo(2, harness.Progress.Count);
        for (int i = 0; i < 11; i++)
        {
            if (i > 0)
            {
                pending = await harness.Reach(harness.Body.NextPending(TestContext.CancellationToken), "保留中の読取り");
            }
            harness.Clock.Advance(TimeSpan.FromSeconds(29));
            Assert.IsFalse(pending.Token.IsCancellationRequested, "29秒進めても各読取りの期限は切れない。");
            pending.Release();
        }

        // 受信の継続中（EOF前）に、進捗の通知とファイル出力が始まっている。
        WritePending write = await harness.Reach(harness.NextWritePending(), "書込みの保留");
        Assert.IsFalse(download.IsCompleted);
        Assert.IsGreaterThanOrEqualTo(1, harness.Progress.Count);
        UpdateDownloadProgress midway = harness.Progress.Last;
        Assert.IsGreaterThan(0, midway.ReceivedBytes);
        Assert.IsLessThan(midway.TotalBytes, midway.ReceivedBytes);
        Assert.AreEqual(content.Length, midway.TotalBytes);
        Assert.IsGreaterThan(0, harness.BytesWritten);
        Assert.IsTrue(File.Exists(harness.TemporaryPath));
        Assert.IsFalse(File.Exists(harness.PackagePath));

        // 書込みの31秒は期限へ含めず、取り消されない。
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsFalse(write.Token.IsCancellationRequested);
        Assert.IsFalse(harness.Body.LastReadToken.IsCancellationRequested);
        Assert.IsFalse(download.IsCompleted);
        write.Release();

        string result = await harness.Reach(download, "完了");
        Assert.AreEqual(harness.PackagePath, result);
        CollectionAssert.AreEqual(content, File.ReadAllBytes(result));
        Assert.IsFalse(File.Exists(harness.TemporaryPath));
        Assert.IsTrue(harness.Body.IsDisposed);
        int reported = harness.Progress.Count;
        Assert.AreEqual(new UpdateDownloadProgress(content.Length, content.Length), harness.Progress.Last);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(reported, harness.Progress.Count, "完了後に進捗を通知しない。");
        Assert.IsLessThan(81, reported, "通知は読取り回数に比例しない。");
    }

    /// <summary>
    /// 応答ヘッダーが29秒では取り消されず、累計30秒でその段階のトークンが取り消されて、取消以外の失敗で終わる。
    /// 呼出し側の取消と取り違える誤りと、ヘッダー待ちへ期限がない誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task HeaderDeadlineFailsWithoutCallerCancellation()
    {
        byte[] content = CreateContent(3 * ChunkBytes);
        await using var harness = new Harness(TestContext, content);
        harness.Handler.HoldHeaders = true;
        Task<string> download = harness.Start(harness.CreateAsset());

        await harness.Reach(harness.Handler.HeaderStarted.Task, "ヘッダー待ち");
        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.IsFalse(harness.Handler.HeaderToken.IsCancellationRequested);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(harness.Handler.HeaderToken.IsCancellationRequested);

        Exception failure = await harness.AwaitFailure(download);
        Assert.IsNotInstanceOfType<OperationCanceledException>(failure);
        Assert.IsFalse(harness.CallerCancellation.IsCancellationRequested);
        harness.AssertNoResidue();
    }

    /// <summary>
    /// 本文の読取りが29秒では取り消されず、累計30秒でその読取りのトークンが取り消される。
    /// 保留中の実読取りが終わるまでTaskが完了せず、取消以外の失敗で終わり、本文を閉じてから一時ファイルを消す。
    /// 待ちだけを切り離して早く返す誤りと、期限切れを取消へ変換する誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task BodyDeadlineFailsAfterPendingReadFinishes()
    {
        byte[] content = CreateContent(3 * ChunkBytes);
        await using var harness = new Harness(TestContext, content);
        harness.Body.HoldRead = index => index == 1;
        Task<string> download = harness.Start(harness.CreateAsset());

        PendingRead pending = await harness.Reach(harness.Body.NextPending(TestContext.CancellationToken), "本文の保留");
        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.IsFalse(pending.Token.IsCancellationRequested);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(pending.Token.IsCancellationRequested);
        Assert.IsFalse(download.IsCompleted, "保留中の実読取りが終わるまで完了しない。");
        pending.Release();

        Exception failure = await harness.AwaitFailure(download);
        Assert.IsNotInstanceOfType<OperationCanceledException>(failure);
        Assert.IsFalse(harness.CallerCancellation.IsCancellationRequested);
        harness.AssertNoResidue();
        Assert.IsTrue(harness.Body.IsDisposed);
        Assert.IsTrue(harness.Body.TemporaryExistedAtDispose, "本文を閉じてから一時ファイルを消す。");
    }

    /// <summary>
    /// 呼出し側の取消が応答ヘッダー待ちへ伝わり、取消済みのトークンが渡されて <see cref="OperationCanceledException"/> で終わる。
    /// 取消を通常の失敗へ変換する誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task CallerCancellationReachesHeaderWait()
    {
        byte[] content = CreateContent(3 * ChunkBytes);
        await using var harness = new Harness(TestContext, content);
        harness.Handler.HoldHeaders = true;
        Task<string> download = harness.Start(harness.CreateAsset());

        await harness.Reach(harness.Handler.HeaderStarted.Task, "ヘッダー待ち");
        harness.CallerCancellation.Cancel();
        Assert.IsTrue(harness.Handler.HeaderToken.IsCancellationRequested);

        Exception failure = await harness.AwaitFailure(download);
        Assert.IsInstanceOfType<OperationCanceledException>(failure);
        harness.AssertNoResidue();
    }

    /// <summary>
    /// 呼出し側の取消が本文の保留中の読取りへ伝わり、実読取りの終了までTaskが完了せず、取消で終わって後片付けする。
    /// 読取り中の取消を待ちだけで済ませる誤りと、取消後に一時ファイルを残す誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task CallerCancellationReachesPendingBodyRead()
    {
        byte[] content = CreateContent(3 * ChunkBytes);
        await using var harness = new Harness(TestContext, content);
        harness.Body.HoldRead = index => index == 1;
        Task<string> download = harness.Start(harness.CreateAsset());

        PendingRead pending = await harness.Reach(harness.Body.NextPending(TestContext.CancellationToken), "本文の保留");
        harness.CallerCancellation.Cancel();
        Assert.IsTrue(pending.Token.IsCancellationRequested);
        Assert.IsFalse(download.IsCompleted, "保留中の実読取りが終わるまで完了しない。");
        pending.Release();

        Exception failure = await harness.AwaitFailure(download);
        Assert.IsInstanceOfType<OperationCanceledException>(failure);
        harness.AssertNoResidue();
        Assert.IsTrue(harness.Body.IsDisposed);
        Assert.IsTrue(harness.Body.TemporaryExistedAtDispose, "本文を閉じてから一時ファイルを消す。");
    }

    /// <summary>
    /// 呼出し側の取消が一時ファイルの書込みへ伝わり、実書込みの終了までTaskが完了せず、取消で終わって後片付けする。
    /// 書込み中の取消を通常の失敗へ変換する誤りと、書込み完了前に一時ファイルを消そうとする誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task CallerCancellationReachesPendingFileWrite()
    {
        byte[] content = CreateContent(3 * ChunkBytes);
        await using var harness = new Harness(TestContext, content);
        harness.HoldWriteAt = 1;
        Task<string> download = harness.Start(harness.CreateAsset());

        WritePending write = await harness.Reach(harness.NextWritePending(), "書込みの保留");
        harness.CallerCancellation.Cancel();
        Assert.IsTrue(write.Token.IsCancellationRequested);
        Assert.IsFalse(download.IsCompleted, "保留中の実書込みが終わるまで完了しない。");
        write.Release();

        Exception failure = await harness.AwaitFailure(download);
        Assert.IsInstanceOfType<OperationCanceledException>(failure);
        harness.AssertNoResidue();
        Assert.IsTrue(harness.Body.IsDisposed);
        Assert.IsTrue(harness.Body.TemporaryExistedAtDispose, "本文を閉じてから一時ファイルを消す。");
    }

    /// <summary>
    /// 短い本文・サイズ超過・ハッシュ不一致・途中切断・書込み失敗・HTTP 404は、すべて取消以外の失敗で終わり、
    /// 一時ファイルも完成名のファイルも残さず本文を閉じる。超過では以降を読まず、書込み量が期待サイズを超えない。
    /// 途中切断と書込み失敗では元の例外が原因として残る。検証を省く誤りと、失敗後の残骸を検出する。
    /// </summary>
    [TestMethod]
    [DataRow("short")]
    [DataRow("oversize")]
    [DataRow("hash")]
    [DataRow("disconnect")]
    [DataRow("writefail")]
    [DataRow("http404")]
    public async Task RejectedTransferLeavesNothingBehind(string scenario)
    {
        byte[] content = CreateContent(4 * ChunkBytes);
        await using var harness = new Harness(TestContext, content);
        UpdateAssetInfo asset = harness.CreateAsset();
        IOException injected = new("injected " + scenario);
        switch (scenario)
        {
            case "short":
                asset.SizeBytes = content.Length + 50;
                break;
            case "oversize":
                asset.SizeBytes = 250;
                asset.Sha256 = Convert.ToHexString(SHA256.HashData(content.AsSpan(0, 250))).ToLowerInvariant();
                break;
            case "hash":
                asset.Sha256 = Convert.ToHexString(SHA256.HashData(CreateContent(content.Length, seed: 9))).ToLowerInvariant();
                break;
            case "disconnect":
                harness.Body.FailAtRead = 2;
                harness.Body.ReadFailure = injected;
                break;
            case "writefail":
                harness.FailWriteAt = 1;
                harness.WriteFailure = injected;
                break;
            case "http404":
                harness.Handler.Status = HttpStatusCode.NotFound;
                break;
            default:
                throw new InvalidOperationException(scenario);
        }

        Task<string> download = harness.Start(asset);
        Exception failure = await harness.AwaitFailure(download);

        Assert.IsNotInstanceOfType<OperationCanceledException>(failure);
        harness.AssertNoResidue();
        Assert.IsTrue(harness.Body.IsDisposed);
        if (scenario == "oversize")
        {
            Assert.AreEqual(3, harness.Body.ReadCalls, "超過を検出した後は読み取らない。");
            Assert.IsLessThanOrEqualTo(asset.SizeBytes, harness.BytesWritten);
        }
        if (scenario is "disconnect" or "writefail")
        {
            Assert.IsTrue(ContainsInChain(failure, injected), "元の例外が原因として残る。");
        }
        if (scenario != "http404")
        {
            Assert.IsTrue(harness.Body.TemporaryExistedAtDispose, "本文を閉じてから一時ファイルを消す。");
        }
    }

    /// <summary>
    /// 不正なファイル名は、HTTP要求とupdate_work配下の作成のどちらよりも前に <see cref="UpdateManifestValidationException"/> で拒否する。
    /// 検査をディレクトリ作成の後へ置く誤りと、検査前に通信する誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("..\\app.zip")]
    [DataRow("sub/app.zip")]
    public async Task InvalidFileNameIsRejectedBeforeAnyRequestOrDirectory(string fileName)
    {
        byte[] content = CreateContent(ChunkBytes);
        await using var harness = new Harness(TestContext, content);
        UpdateAssetInfo asset = harness.CreateAsset();
        asset.FileName = fileName;

        await Assert.ThrowsExactlyAsync<UpdateManifestValidationException>(
            () => harness.Service.DownloadAndVerifyAsync(asset, harness.CallerCancellation.Token));

        Assert.AreEqual(0, harness.Handler.RequestCount);
        Assert.IsFalse(Directory.Exists(Path.Combine(harness.Workspace.RootDirectory, "update_work")));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(harness.Workspace.RootDirectory, "*", SearchOption.AllDirectories)
            .Count(path => !path.EndsWith("BeMusicSeeker.exe", StringComparison.Ordinal)));
    }

    private static bool ContainsInChain(Exception failure, Exception expected)
    {
        for (Exception? current = failure; current != null; current = current.InnerException)
        {
            if (ReferenceEquals(current, expected))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] CreateContent(int length, int seed = 1)
    {
        byte[] content = new byte[length];
        for (int i = 0; i < length; i++)
        {
            content[i] = (byte)((i * 31 + seed) & 0xFF);
        }
        return content;
    }

    private sealed class PendingRead(CancellationToken token)
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken Token { get; } = token;

        internal Task Gate => gate.Task;

        internal void Release() => gate.TrySetResult();
    }

    private sealed class WritePending(CancellationToken token)
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken Token { get; } = token;

        internal Task Gate => gate.Task;

        internal void Release() => gate.TrySetResult();
    }

    /// <summary>ケースごとの作業領域・時計・HTTP・ファイル出力を所有し、終端時に保留をすべて解放して回収します。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestContext testContext;
        private readonly byte[] content;
        private readonly Channel<WritePending> writes = Channel.CreateUnbounded<WritePending>();
        private readonly List<WritePending> allWrites = [];
        private readonly object gate = new();
        private int writeCalls;
        private long bytesWritten;
        private Task<string>? download;

        internal Harness(TestContext testContext, byte[] content)
        {
            this.testContext = testContext;
            this.content = content;
            Workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string applicationPath = Path.Combine(Workspace.RootDirectory, "BeMusicSeeker.exe");
            File.WriteAllText(applicationPath, string.Empty);
            Clock = new ManualClock();
            Body = new BodyStream(content, ChunkBytes, this);
            Handler = new StagedHandler(Body);
            Service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway(),
                [],
                Handler,
                Clock,
                path => new GateFileStream(path, this));
        }

        internal TestWorkspace Workspace { get; }

        internal ManualClock Clock { get; }

        internal BodyStream Body { get; }

        internal StagedHandler Handler { get; }

        internal UpdateDownloadService Service { get; }

        internal CancellationTokenSource CallerCancellation { get; } = new();

        internal RecordingProgress Progress { get; } = new();

        internal int HoldWriteAt { get; set; } = -1;

        internal int FailWriteAt { get; set; } = -1;

        internal Exception? WriteFailure { get; set; }

        internal long BytesWritten => Interlocked.Read(ref bytesWritten);

        internal string DownloadsDirectory => Path.Combine(Workspace.RootDirectory, "update_work", "downloads");

        internal string PackagePath => Path.Combine(DownloadsDirectory, "app.zip");

        internal string TemporaryPath => PackagePath + ".download";

        internal UpdateAssetInfo CreateAsset() => new()
        {
            FileName = "app.zip",
            Url = "https://update.example.test/app.zip",
            SizeBytes = content.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()
        };

        internal Task<string> Start(UpdateAssetInfo asset)
        {
            download = Service.DownloadAndVerifyAsync(asset, CallerCancellation.Token, Progress);
            return download;
        }

        internal Task<WritePending> NextWritePending() => writes.Reader.ReadAsync(testContext.CancellationToken).AsTask();

        internal async Task<T> Reach<T>(Task<T> signal, string phase)
        {
            await Reach((Task)signal, phase);
            return await signal;
        }

        /// <summary>段階の合図とダウンロードTaskの終了のうち早い方を待ち、合図より先にTaskが終われば失敗にします。</summary>
        internal async Task Reach(Task signal, string phase)
        {
            Task first = await Task.WhenAny(signal, download ?? new TaskCompletionSource().Task).WaitAsync(testContext.CancellationToken);
            if (first != signal)
            {
                throw new AssertFailedException(
                    "ダウンロードが " + phase + " より先に終了しました: " + (download!.Exception?.GetBaseException().ToString() ?? download.Status.ToString()));
            }
            await signal;
        }

        internal async Task<Exception> AwaitFailure(Task<string> task)
        {
            try
            {
                await task.WaitAsync(testContext.CancellationToken);
            }
            catch (Exception failure) when (!testContext.CancellationToken.IsCancellationRequested)
            {
                return failure;
            }
            throw new AssertFailedException("取得が成功しました。");
        }

        /// <summary>一時ファイルも完成名のファイルも残っていないことを確かめます。</summary>
        internal void AssertNoResidue()
        {
            Assert.IsFalse(File.Exists(TemporaryPath), ".download が残っている。");
            Assert.IsFalse(File.Exists(PackagePath), "完成名のファイルが残っている。");
        }

        public async ValueTask DisposeAsync()
        {
            CallerCancellation.Cancel();
            Body.ReleaseAll();
            Handler.ReleaseHeaders.TrySetResult();
            lock (gate)
            {
                HoldWriteAt = -1;
                foreach (WritePending write in allWrites)
                {
                    write.Release();
                }
            }
            if (download != null)
            {
                try
                {
                    await download.WaitAsync(testContext.CancellationToken);
                }
                catch (Exception)
                {
                }
            }
            Handler.Dispose();
            CallerCancellation.Dispose();
            Workspace.Dispose();
        }

        private sealed class GateFileStream(string path, Harness owner)
            : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, false)
        {
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int index = Interlocked.Increment(ref owner.writeCalls) - 1;
                if (owner.FailWriteAt == index)
                {
                    throw owner.WriteFailure!;
                }
                if (owner.HoldWriteAt == index)
                {
                    var pending = new WritePending(cancellationToken);
                    lock (owner.gate)
                    {
                        owner.allWrites.Add(pending);
                    }
                    owner.writes.Writer.TryWrite(pending);
                    // 実書込みは取消を無視して終了まで続く。呼出し側は終了を待つ。
                    await pending.Gate.ConfigureAwait(false);
                }
                await base.WriteAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Add(ref owner.bytesWritten, buffer.Length);
            }
        }
    }

    private sealed class RecordingProgress : IProgress<UpdateDownloadProgress>
    {
        private readonly List<UpdateDownloadProgress> values = [];

        internal int Count
        {
            get
            {
                lock (values)
                {
                    return values.Count;
                }
            }
        }

        internal UpdateDownloadProgress Last
        {
            get
            {
                lock (values)
                {
                    return values[^1];
                }
            }
        }

        public void Report(UpdateDownloadProgress value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }

    /// <summary>ヘッダーの解放を保留でき、渡されたトークンと要求数を記録するハンドラーです。</summary>
    private sealed class StagedHandler(Stream body) : HttpMessageHandler
    {
        private int requestCount;

        internal bool HoldHeaders { get; set; }

        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        internal TaskCompletionSource HeaderStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseHeaders { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken HeaderToken { get; private set; }

        internal int RequestCount => Volatile.Read(ref requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            HeaderToken = cancellationToken;
            HeaderStarted.TrySetResult();
            if (HoldHeaders)
            {
                await ReleaseHeaders.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            // 長さなしの非シーク本文にして、全文を溜めずに読む経路を通す。
            return new HttpResponseMessage(Status) { Content = new StreamContent(body) };
        }
    }

    /// <summary>読取りを1回ずつ保留・解放でき、トークンと破棄を記録し、例外を注入できる長さなしの本文です。</summary>
    private sealed class BodyStream(byte[] data, int chunkBytes, Harness owner) : Stream
    {
        private readonly Channel<PendingRead> pending = Channel.CreateUnbounded<PendingRead>();
        private readonly List<PendingRead> allPending = [];
        private int position;
        private int readCalls;
        private volatile bool forceEof;

        internal Func<int, bool> HoldRead { get; set; } = _ => false;

        internal int FailAtRead { get; set; } = -1;

        internal Exception? ReadFailure { get; set; }

        internal CancellationToken LastReadToken { get; private set; }

        internal int ReadCalls => Volatile.Read(ref readCalls);

        internal bool IsDisposed { get; private set; }

        internal bool TemporaryExistedAtDispose { get; private set; }

        internal Task<PendingRead> NextPending(CancellationToken cancellationToken) => pending.Reader.ReadAsync(cancellationToken).AsTask();

        internal void ReleaseAll()
        {
            forceEof = true;
            lock (allPending)
            {
                foreach (PendingRead read in allPending)
                {
                    read.Release();
                }
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int index = Interlocked.Increment(ref readCalls) - 1;
            LastReadToken = cancellationToken;
            if (forceEof)
            {
                return 0;
            }
            if (FailAtRead == index)
            {
                throw ReadFailure!;
            }
            if (HoldRead(index))
            {
                var read = new PendingRead(cancellationToken);
                lock (allPending)
                {
                    allPending.Add(read);
                }
                pending.Writer.TryWrite(read);
                // 実読取りは取消を無視して終了まで続く。呼出し側は終了を待つ。
                await read.Gate.ConfigureAwait(false);
                if (forceEof)
                {
                    return 0;
                }
            }
            int count = Math.Min(Math.Min(chunkBytes, buffer.Length), data.Length - position);
            if (count <= 0)
            {
                return 0;
            }
            data.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                TemporaryExistedAtDispose = File.Exists(owner.TemporaryPath);
                IsDisposed = true;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// <see cref="TimeProvider.CreateTimer"/> で作られた期限を、<see cref="Advance"/> の中で同期的に発火する手動時計です。
    /// <see cref="TimeProvider.GetTimestamp"/> と <see cref="TimeProvider.GetUtcNow"/> も同じ時刻で進み、周期タイマーにも対応します。
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private const long Never = long.MaxValue;

        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (gate)
            {
                return ticks;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (gate)
            {
                timers.Add(timer);
            }
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            long target;
            lock (gate)
            {
                target = ticks + amount.Ticks;
            }
            while (true)
            {
                ManualTimer? next;
                lock (gate)
                {
                    next = timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                    if (next == null)
                    {
                        ticks = target;
                        return;
                    }
                    ticks = Math.Max(ticks, next.Due);
                    next.Reschedule();
                }
                next.Fire();
            }
        }

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long period;

            internal long Due { get; private set; } = Never;

            public bool Change(TimeSpan dueTime, TimeSpan periodTime)
            {
                lock (owner.gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? Never : owner.ticks + dueTime.Ticks;
                    period = periodTime == Timeout.InfiniteTimeSpan ? 0 : periodTime.Ticks;
                }
                return true;
            }

            internal void Reschedule() => Due = period > 0 ? Due + period : Never;

            internal void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner.gate)
                {
                    owner.timers.Remove(this);
                    Due = Never;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
