using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class AppHttpClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Http")]
    public void GetString_FileUri_StripsUtf8Bom()
    {
        string filePath = CreateTempFileWithBytes(CreateUtf8BomBytes("header-json"));
        try
        {
            string actual = AppHttpClient.Shared.GetString(new Uri(filePath), Encoding.UTF8);

            Assert.AreEqual("header-json", actual);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    [TestCategory("Http")]
    public async Task GetStringAsync_FileUri_StripsUtf8Bom()
    {
        string filePath = CreateTempFileWithBytes(CreateUtf8BomBytes("async-json"));
        try
        {
            string actual = await AppHttpClient.Shared.GetStringAsync(new Uri(filePath), Encoding.UTF8);

            Assert.AreEqual("async-json", actual);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    [TestCategory("Http")]
    public void GetString_FileUri_PreservesPlainUtf8()
    {
        string filePath = CreateTempFileWithBytes(Encoding.UTF8.GetBytes("plain-json"));
        try
        {
            string actual = AppHttpClient.Shared.GetString(new Uri(filePath), Encoding.UTF8);

            Assert.AreEqual("plain-json", actual);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    [TestCategory("Http")]
    public async Task PostString_StripsUtf8BomFromResponse()
    {
        await using var server = new SingleRequestHttpServer(CreateUtf8BomBytes("{\"ok\":true}"));

        string response = await StartSynchronousRequest(() =>
            AppHttpClient.Shared.PostString(server.Address, "body", "application/json;charset=UTF-8", Encoding.UTF8));

        Assert.AreEqual("{\"ok\":true}", response);
        Assert.AreEqual("POST", server.RequestMethod);
    }

    [TestMethod]
    [TestCategory("Http")]
    public async Task PostFile_StripsUtf8BomFromResponse()
    {
        string uploadFilePath = CreateTempFileWithBytes(Encoding.UTF8.GetBytes("upload"));
        try
        {
            await using var server = new SingleRequestHttpServer(CreateUtf8BomBytes("{\"uploaded\":true}"));

            string response = await StartSynchronousRequest(() =>
                AppHttpClient.Shared.PostFile(server.Address, uploadFilePath, responseEncoding: Encoding.UTF8));

            Assert.AreEqual("{\"uploaded\":true}", response);
            Assert.AreEqual("POST", server.RequestMethod);
        }
        finally
        {
            File.Delete(uploadFilePath);
        }
    }

    [DataTestMethod]
    [DataRow("GetStringAsync")]
    [DataRow("PostFormAsync")]
    [DataRow("GetString")]
    [DataRow("PostForm")]
    [DataRow("PostString")]
    [TestCategory("Http")]
    public async Task BufferedRequest_HeadersAndBodyShareOneDeadline(string operation)
    {
        var clock = new ManualRequestTimeProvider();
        using var handler = new StagedResponseHandler();
        using var transport = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var client = new AppHttpClient(transport, clock);
        Task<string> request = StartBufferedRequest(client, new Uri("https://example.invalid/deadline"), operation);
        Exception? primaryFailure = null;
        try
        {
            // The request owns its deadline before headers. Advance virtual time only
            // after each client-side phase, not while waiting for a pool thread or socket.
            await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.IsFalse(handler.RequestCancellation.IsCancellationRequested);
            handler.ReleaseHeaders.TrySetResult();
            await handler.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(handler.Body.ReadCancellation.IsCancellationRequested);

            clock.Advance(TimeSpan.FromSeconds(10));

            // A fresh body deadline, a missing read token, or a task-only timeout fails
            // here without relying on how promptly an overloaded worker resumes.
            Assert.IsTrue(handler.Body.ReadCancellation.IsCancellationRequested,
                "The original request deadline must cancel the actual pending body read.");
            await AssertRequestCancelledAsync(request.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(handler.Body.IsDisposed, "Request completion must release the response stream.");
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            handler.ReleaseHeaders.TrySetResult();
            handler.Body.Release.TrySetResult(0);
            try
            {
                await request.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception cleanupFailure) when (primaryFailure != null)
            {
                TestContext.WriteLine("HTTP request cleanup: " + cleanupFailure);
            }
        }
    }

    [DataTestMethod]
    [DataRow("GetStringAsync", false)]
    [DataRow("PostFormAsync", false)]
    [DataRow("GetStringAsync", true)]
    [DataRow("PostFormAsync", true)]
    [TestCategory("Http")]
    public async Task RecommendedHttpAdapter_ExternalCancellationTerminatesPendingResponse(string operation, bool cancelBeforeBodyPrefix)
    {
        using var cancellation = new CancellationTokenSource();
        var server = new SingleRequestHttpServer([65, 66], holdBody: true, holdBodyPrefix: cancelBeforeBodyPrefix);
        using var transport = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new AppHttpClient(transport, TimeProvider.System);
        Task<string> request = StartBufferedRequest(client, server.Address, operation, cancellation.Token);
        Exception? primaryFailure = null;
        try
        {
            // ヘッダー直後と本文途中を signal で分ける。短い delay で race を推定しない。
            Task responsePhase = cancelBeforeBodyPrefix ? server.HeadersSent.Task : server.BodyPrefixSent.Task;
            await server.WaitForPhaseAsync(responsePhase, cancelBeforeBodyPrefix ? "headers sent" : "body prefix sent", TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await AssertRequestCancelledAsync(request.WaitAsync(TimeSpan.FromSeconds(5)));
            // 待ち手の Task だけを切り離す実装や、prefix 書込み後に監視を始める fixture は通さない。
            // ReleaseBodyPrefix / ReleaseBody / DisposeAsync より前に、実際の peer 切断を観測する。
            await server.WaitForPhaseAsync(server.ClientDisconnected.Task, "peer disconnected", TimeSpan.FromSeconds(5));
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            server.ReleaseBodyPrefix.TrySetResult();
            server.ReleaseBody.TrySetResult();
            try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (Exception cleanupFailure) when (primaryFailure != null)
            {
                TestContext.WriteLine("HTTP request cleanup: " + cleanupFailure);
            }
            finally
            {
                try { await server.DisposeAsync(); }
                catch (Exception cleanupFailure) when (primaryFailure != null)
                {
                    TestContext.WriteLine("HTTP server cleanup: " + cleanupFailure);
                }
                TestContext.WriteLine(operation + " beforePrefix=" + cancelBeforeBodyPrefix + Environment.NewLine + server.Diagnostics);
            }
        }
    }

    [DataTestMethod]
    [DataRow("GetStringAsync")]
    [DataRow("GetString")]
    [TestCategory("Http")]
    public async Task GetString_HttpResponsePreservesTextAndStripsBom(string operation)
    {
        await using var server = new SingleRequestHttpServer(CreateUtf8BomBytes("本文 & +"));

        string response = await StartBufferedRequest(AppHttpClient.Shared, server.Address, operation);

        Assert.AreEqual("本文 & +", response);
        Assert.AreEqual("GET", server.RequestMethod);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Http")]
    public async Task PostForm_PreservesFormValuesAndStripsResponseBom(bool asynchronous)
    {
        await using var server = new SingleRequestHttpServer(CreateUtf8BomBytes("accepted"));
        var form = new NameValueCollection
        {
            { "name", "日本語 & +" },
            { "tag", "x y" },
            { "tag", "x+y" },
            { "empty", string.Empty }
        };
        string response = asynchronous
            ? await AppHttpClient.Shared.PostFormAsync(server.Address, form)
            : await StartSynchronousRequest(() => AppHttpClient.Shared.PostForm(server.Address, form));

        Assert.AreEqual("accepted", response);
        Assert.AreEqual("POST", server.RequestMethod);
        StringAssert.StartsWith(server.RequestContentType, "application/x-www-form-urlencoded");
        NameValueCollection received = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(server.RequestBody));
        Assert.AreEqual(3, received.Count);
        Assert.AreEqual("日本語 & +", received["name"]);
        CollectionAssert.AreEqual(new[] { "x y", "x+y" }, received.GetValues("tag"));
        Assert.AreEqual(string.Empty, received["empty"]);
    }

    private static Task<string> StartBufferedRequest(AppHttpClient client, Uri address, string operation, CancellationToken cancellationToken = default)
    {
        var adapter = new AppPlaylistRecommendedTableHttpClient(client);
        var form = new NameValueCollection { { "name", "test" } };
        return operation switch
        {
            "GetStringAsync" => adapter.GetStringAsync(address, cancellationToken),
            "PostFormAsync" => adapter.PostFormAsync(address, form, cancellationToken),
            "GetString" => StartSynchronousRequest(() => client.GetString(address)),
            "PostForm" => StartSynchronousRequest(() => client.PostForm(address, form)),
            "PostString" => StartSynchronousRequest(() => client.PostString(address, "body", "text/plain")),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static Task<string> StartSynchronousRequest(Func<string> request)
    {
        // This thread is owned by, and joined through, the returned request task.
        // Blocking a legacy sync HTTP API must not consume the pool that runs the
        // asynchronous server, cancellation continuations, and unrelated fixtures.
        return Task.Factory.StartNew(request, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static async Task AssertRequestCancelledAsync(Task request)
    {
        try { await request; }
        catch (OperationCanceledException) { return; }
        Assert.Fail("未受信本文を成功として返さず、期限またはキャンセルで中止する。");
    }

    private static byte[] CreateUtf8BomBytes(string text)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    private static string CreateTempFileWithBytes(byte[] bytes)
    {
        string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllBytes(filePath, bytes);
        return filePath;
    }

    // Only the deadline test uses this local transport. The socket-based adapter
    // tests below still require a real peer disconnect before fixture cleanup.
    private sealed class StagedResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        internal StagedResponseHandler()
        {
            response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body) };
        }

        internal TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseHeaders { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PendingBodyStream Body { get; } = new();
        internal CancellationToken RequestCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCancellation = cancellationToken;
            RequestStarted.TrySetResult();
            await ReleaseHeaders.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class PendingBodyStream : Stream
    {
        private bool prefixRead;

        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken ReadCancellation { get; private set; }
        internal bool IsDisposed { get; private set; }

        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }
            if (!prefixRead)
            {
                prefixRead = true;
                buffer.Span[0] = 65;
                return ValueTask.FromResult(1);
            }
            ReadCancellation = cancellationToken;
            ReadStarted.TrySetResult();
            return new ValueTask<int>(Release.Task.WaitAsync(cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // No global clock or Timer is changed. Only one-shot CTS timers are needed by
    // this fixture; firing occurs synchronously when the test advances its clock.
    private sealed class ManualRequestTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<RequestTimer> timers = [];
        private TimeSpan elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RequestTimer(this, callback, state);
            lock (gate)
            {
                timers.Add(timer);
                timer.Change(dueTime, period);
            }
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(amount));
            }
            RequestTimer[] due;
            lock (gate)
            {
                elapsed += amount;
                due = timers.Where(timer => timer.Due <= elapsed).ToArray();
                foreach (RequestTimer timer in due)
                {
                    timer.Due = TimeSpan.MaxValue;
                }
            }
            foreach (RequestTimer timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class RequestTimer : ITimer
        {
            private readonly ManualRequestTimeProvider owner;
            private readonly TimerCallback callback;
            private readonly object? state;
            private bool disposed;

            internal RequestTimer(ManualRequestTimeProvider owner, TimerCallback callback, object? state)
            {
                this.owner = owner;
                this.callback = callback;
                this.state = state;
            }

            internal TimeSpan Due { get; set; } = TimeSpan.MaxValue;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan)
                {
                    throw new NotSupportedException("This request-clock fixture supports one-shot timers only.");
                }
                lock (owner.gate)
                {
                    if (disposed)
                    {
                        return false;
                    }
                    Due = dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : owner.elapsed + dueTime;
                    return true;
                }
            }

            internal void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner.gate)
                {
                    disposed = true;
                    owner.timers.Remove(this);
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
