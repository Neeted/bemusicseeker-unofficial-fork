using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class AppHttpClientTests
{
    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>有限二応答の一件だけを取得して操作取消済みでも、独立cleanupが次acceptと実serverTaskを回収します。要求不足や取消tokenの流用で回収を省く誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Http")]
    public async Task FiniteServer_DisposeReclaimsUnrequestedResponse()
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        Task<string>? request = null;

        SingleRequestHttpServer? server = null;
        Exception? operationFailure = null;
        try
        {
            server = SingleRequestHttpServer.StartFiniteResponses(httpOperation.Token, new SingleRequestHttpServer.Response[]
            {
                new("/first", Encoding.UTF8.GetBytes("first")),
                new("/second", Encoding.UTF8.GetBytes("second"))
            });

            request = AppHttpClient.Shared.GetStringAsync(new Uri(server.Address, "first"), Encoding.UTF8, cancellationToken: httpOperation.Token);
            string response = await request.WaitAsync(httpOperation.Token);
            Assert.AreEqual("first", response);
            CollectionAssert.AreEqual(new[] { "/first" }, server.RequestTargets);
            Assert.IsFalse(server.Completion.IsCompleted, "二件目のacceptをfixtureが所有しています。");
            httpOperation.Cancel();
        }
        catch (Exception failure) { operationFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpOperation.Cancel();
            await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, operationFailure,
                request == null ? [] : [request], server == null ? [] : [server]);
        }
        Assert.IsNotNull(server);
        Assert.IsTrue(server.Completion.IsCompletedSuccessfully, server.Diagnostics);
    }

    /// <summary>cleanupのwatchdog取消を未終端clientの取消成功と扱わず、元Taskを変更せず失敗させます。最後に入力を解放して実Taskも回収します。</summary>
    [TestMethod]
    [TestCategory("Http")]
    public async Task Cleanup_CancelledWatchdogRejectsUnfinishedClient()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        using var expiredCleanup = new CancellationTokenSource();
        expiredCleanup.Cancel();
        var client = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? cleanupAttempt = null;
        Task<AggregateException>? assertion = null;
        Exception? primaryFailure = null;
        try
        {
            cleanupAttempt = SingleRequestHttpServer.CompleteCleanupAsync(expiredCleanup.Token, null, [client.Task]);
            assertion = Assert.ThrowsExactlyAsync<AggregateException>(() => cleanupAttempt);
            AggregateException failure = await assertion.WaitAsync(operation.Token);
            Assert.IsTrue(failure.Flatten().InnerExceptions.Any(error => error is OperationCanceledException));
            Assert.IsFalse(client.Task.IsCompleted, "待機だけの取消で、元clientを取消済み・回収済みと扱いません。");
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            client.TrySetResult();
            operation.Cancel();
            IEnumerable<Task> pendingObservers = new Task?[] { cleanupAttempt, assertion }.OfType<Task>()
                .Where(task => !task.IsCompleted);
            await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure, pendingObservers.Prepend(client.Task));
        }
    }

    /// <summary>正常終了済みの補助producerを待機集合から除き、通知前に失敗したclientの元原因を即座に返します。</summary>
    [TestMethod]
    [TestCategory("Http")]
    public async Task PhaseWait_CompletedAuxiliaryProducersDoNotHideClientFailure()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        SingleRequestHttpServer? server = null;
        var phase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new IOException("HTTP client failure before notification");
        Exception? primaryFailure = null;
        Task? waiting = null;
        try
        {
            server = new SingleRequestHttpServer(operation.Token, []);
            waiting = server.WaitForPhaseAsync(phase.Task, "unreached phase", client.Task, Task.CompletedTask, Task.CompletedTask);
            client.TrySetException(expected);
            AssertFailedException failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => waiting);
            Assert.AreSame(expected, failure.InnerException);
            Assert.IsFalse(phase.Task.IsCompleted);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            client.TrySetException(expected);
            operation.Cancel();
            // 期待したhelper失敗は上で観測済み。未終端なら実待機を回収してからfixtureを離れます。
            await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure,
                waiting is { IsCompleted: false } ? [waiting] : [], server == null ? [] : [server]);
        }
    }

    /// <summary>client/serverの正常終端と通知が同時に見えても到達済み通知を保持し、通知なしの正常終了は成功にしません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Http")]
    public async Task PhaseWait_NormalCompletionRequiresItsNotification(bool notificationReached)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        SingleRequestHttpServer? server = null;
        Task<string>? request = null;
        Exception? primaryFailure = null;
        try
        {
            server = new SingleRequestHttpServer(operation.Token, Encoding.UTF8.GetBytes("complete"));
            request = AppHttpClient.Shared.GetStringAsync(server.Address, cancellationToken: operation.Token);
            Assert.AreEqual("complete", await request.WaitAsync(operation.Token));
            await server.Completion.WaitAsync(operation.Token);
            Task phase = notificationReached ? server.HeadersSent.Task : new TaskCompletionSource().Task;
            if (notificationReached)
            {
                await server.WaitForPhaseAsync(phase, "completed response headers", request);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<AssertFailedException>(
                    () => server.WaitForPhaseAsync(phase, "missing response phase", request));
            }
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            operation.Cancel();
            await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure, request == null ? [] : [request], server == null ? [] : [server]);
        }
    }

    /// <summary>gateで通知とproducer終端を進め、到達済み通知と同時の正常終端を保持し、元故障・真の通知欠落は成功にしません。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [TestCategory("Http")]
    public async Task PhaseWait_ReleasedProducerPreservesNotificationAndFailure(bool notify, bool failProducer)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new IOException("producer failed at notification boundary");
        SingleRequestHttpServer? server = null;
        Task? producer = null;
        Exception? primaryFailure = null;
        try
        {
            server = new SingleRequestHttpServer(operation.Token, []);
            producer = PublishAsync();
            gate.TrySetResult();
            try { await producer.WaitAsync(operation.Token); }
            catch (IOException failure) when (ReferenceEquals(failure, expected)) { }
            if (notify && !failProducer)
            {
                await server.WaitForPhaseAsync(phase.Task, "released notification", producer);
            }
            else
            {
                AssertFailedException failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
                    () => server.WaitForPhaseAsync(phase.Task, "released notification", producer));
                if (failProducer) { Assert.AreSame(expected, failure.InnerException); }
            }
            Assert.AreEqual(notify, phase.Task.IsCompletedSuccessfully);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            gate.TrySetResult();
            operation.Cancel();
            await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure,
                producer is { IsCompleted: false } ? [producer] : [], server == null ? [] : [server]);
        }

        async Task PublishAsync()
        {
            await gate.Task;
            if (notify) { phase.TrySetResult(); }
            if (failProducer) { throw expected; }
        }
    }

    /// <summary>準備の待機wrapperだけを取消した後も元Taskと遅い返却資源をcallerが所有し、実終端の成功結果または元故障をcleanupで回収します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Http")]
    public async Task Preparation_CancelledWaitRetainsLateResultAndFailure(bool failPreparation)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        using var cancelledWait = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new List<Task>();
        string directory = workspace.RootDirectory;
        var expected = new IOException("late preparation failure");
        Task<string>? preparation = null;
        Exception? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(directory);
            preparation = PrepareAsync();
            Task<string> waiting = SingleRequestHttpServer.AwaitOperationAsync(preparation, owned, cancelledWait.Token);
            owned.Add(waiting);
            cancelledWait.Cancel();
            Task cancellationCheck = Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
            owned.Add(cancellationCheck);
            await cancellationCheck.WaitAsync(operation.Token);
            Assert.IsFalse(preparation.IsCompleted);
            CollectionAssert.Contains(owned, preparation);
            Assert.IsTrue(Directory.Exists(directory));
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            release.TrySetResult();
            operation.Cancel();
            var failures = new List<Exception>();
            try
            {
                if (failPreparation && primaryFailure == null)
                {
                    AggregateException failure = await Assert.ThrowsExactlyAsync<AggregateException>(
                        () => SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, null, owned));
                    Assert.IsTrue(failure.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, expected)), failure.ToString());
                }
                else
                {
                    await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure, owned);
                    if (preparation != null)
                    {
                        Assert.AreEqual(directory, await preparation);
                        Assert.IsTrue(File.Exists(Path.Combine(directory, "prepared.txt")));
                    }
                }
            }
            catch (Exception failure) { failures.Add(failure); }
            try
            {
                Assert.IsTrue(preparation == null || preparation.IsCompleted, "準備Task未回収では返却資源を解放しません。");
                if (Directory.Exists(directory)) { workspace.Dispose(); }
            }
            catch (Exception failure) { failures.Add(failure); }
            if (failures.Count > 0)
            {
                if (primaryFailure != null) { failures.Insert(0, primaryFailure); }
                throw new AggregateException(failures);
            }
        }

        async Task<string> PrepareAsync()
        {
            await release.Task;
            File.WriteAllText(Path.Combine(directory, "prepared.txt"), "prepared");
            if (failPreparation) { throw expected; }
            return directory;
        }
    }

    /// <summary>BOM付きファイルを同期取得して本文だけを返し、BOMが解析入力へ混入する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Http")]
    public void GetString_FileUri_StripsUtf8Bom()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string filePath = CreateTempFileWithBytes(workspace, CreateUtf8BomBytes("header-json"));
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

    /// <summary>BOM付きファイルを非同期取得して本文だけを返し、BOMが解析入力へ混入する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Http")]
    public async Task GetStringAsync_FileUri_StripsUtf8Bom()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string filePath = CreateTempFileWithBytes(workspace, CreateUtf8BomBytes("async-json"));
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

    /// <summary>BOM無しUTF-8ファイルの本文を同期取得で保持し、先頭文字を欠落させません。</summary>
    [TestMethod]
    [TestCategory("Http")]
    public void GetString_FileUri_PreservesPlainUtf8()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string filePath = CreateTempFileWithBytes(workspace, Encoding.UTF8.GetBytes("plain-json"));
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

    /// <summary>実HTTPのPOST応答からUTF-8 BOMを除いた本文を取得し、要求メソッドがPOSTであることを確認します。</summary>
    [TestMethod]
    [TestCategory("Http")]
    public async Task PostString_StripsUtf8BomFromResponse()
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        Task<string>? request = null;
        SingleRequestHttpServer? server = null;

        Exception? httpFailure = null;
        try
        {
            server = new SingleRequestHttpServer(httpOperation.Token, CreateUtf8BomBytes("{\"ok\":true}"));

            request = StartSynchronousRequest(() =>
                AppHttpClient.Shared.PostString(server.Address, "body", "application/json;charset=UTF-8", Encoding.UTF8));
            string response = await request.WaitAsync(httpOperation.Token);

            Assert.AreEqual("{\"ok\":true}", response);
            Assert.AreEqual("POST", server.RequestMethod);
        }
        catch (Exception failure) { httpFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpOperation.Cancel();
            await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, httpFailure,
                request == null ? [] : [request], server == null ? [] : [server]);
        }
    }

    /// <summary>
    /// 非ASCII名は元のUTF-8バイトで、ASCII名は従来の名前で送信し、名前のMIME符号化・置換とバイナリ本文の変化を検出します。
    /// 実HTTPのPOSTと応答BOMだけを除く保証も同じ経路で維持します。
    /// </summary>
    [TestMethod]
    [DataRow("譜面-é😀.bms")]
    [DataRow("score.bms")]
    [TestCategory("Http")]
    public async Task PostFile_PreservesUtf8FileNameAndBytesAndStripsResponseBom(string fileName)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        Task<string>? request = null;
        SingleRequestHttpServer? server = null;

        string uploadDirectory = workspace.RootDirectory;
        Directory.CreateDirectory(uploadDirectory);
        Exception? httpFailure = null;
        try
        {
            byte[] uploadBytes = [0x00, 0xFF, 0x81, 0x40, 0x0D, 0x0A, 0xFE, 0x41];
            string uploadFilePath = Path.Combine(uploadDirectory, fileName);
            File.WriteAllBytes(uploadFilePath, uploadBytes);
            server = new SingleRequestHttpServer(httpOperation.Token, CreateUtf8BomBytes("{\"uploaded\":true}"));

            request = StartSynchronousRequest(() =>
                AppHttpClient.Shared.PostFile(server.Address, uploadFilePath, responseEncoding: Encoding.UTF8));
            string response = await request.WaitAsync(httpOperation.Token);

            Assert.AreEqual("{\"uploaded\":true}", response);
            Assert.AreEqual("POST", server.RequestMethod);

            var contentType = MediaTypeHeaderValue.Parse(server.RequestContentType);
            Assert.AreEqual("multipart/form-data", contentType.MediaType, ignoreCase: true);
            string boundary = contentType.Parameters.Single(parameter =>
                string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase)).Value?.Trim('"')
                ?? throw new AssertFailedException("multipartのboundaryがありません。");
            byte[] openingBoundary = Encoding.ASCII.GetBytes("--" + boundary + "\r\n");
            byte[] closingBoundary = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");
            byte[] requestBody = server.RequestBody;
            Assert.IsTrue(requestBody.AsSpan().StartsWith(openingBoundary), "先頭のmultipart境界が一致しません。");
            Assert.IsTrue(requestBody.AsSpan().EndsWith(closingBoundary), "末尾のmultipart境界が一致しません。");
            int headerLength = requestBody.AsSpan(openingBoundary.Length).IndexOf("\r\n\r\n"u8);
            Assert.IsTrue(headerLength >= 0, "ファイル部分のヘッダー終端がありません。");
            int bodyStart = openingBoundary.Length + headerLength + 4;

            // ヘッダーだけを1バイト対1文字で扱い、MIME復号せず送信された値の生バイトを比較する。
            string partHeaders = Encoding.Latin1.GetString(requestBody, openingBoundary.Length, headerLength);
            string disposition = partHeaders.Split("\r\n").Single(line =>
                line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase));
            var parameters = disposition.Split(';').Skip(1)
                .Select(parameter => parameter.Split('=', 2))
                .ToDictionary(parameter => parameter[0].Trim(), parameter => parameter[1].Trim().Trim('"'),
                    StringComparer.OrdinalIgnoreCase);
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("file"), Encoding.Latin1.GetBytes(parameters["name"]));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(fileName), Encoding.Latin1.GetBytes(parameters["filename"]),
                "filenameは元のファイル名のUTF-8バイトで送信する必要があります。");
            CollectionAssert.AreEqual(uploadBytes, requestBody[bodyStart..^closingBoundary.Length],
                "ファイル本体は文字コード変換せず送信する必要があります。");
        }
        catch (Exception failure) { httpFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpOperation.Cancel();
            await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, httpFailure,
                request == null ? [] : [request], server == null ? [] : [server]);
            try
            {
                httpCleanup.Token.ThrowIfCancellationRequested();
                workspace.Dispose();
            }
            catch (Exception cleanupFailure) when (httpFailure != null) { throw new AggregateException(httpFailure, cleanupFailure); }
        }
    }

    /// <summary>各同期・非同期取得でヘッダーから本文へ進んでも元の期限を共有し、保留本文を取消して応答streamを解放します。</summary>
    [TestMethod]
    [DataRow("GetStringAsync")]
    [DataRow("PostFormAsync")]
    [DataRow("GetString")]
    [DataRow("PostForm")]
    [DataRow("PostString")]
    [TestCategory("Http")]
    public async Task BufferedRequest_HeadersAndBodyShareOneDeadline(string operation)
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        var httpObservers = new List<Task>();
        var clock = new ManualRequestTimeProvider();
        StagedResponseHandler? handler = null;
        HttpClient? transport = null;
        Task<string>? request = null;
        Exception? primaryFailure = null;
        try
        {
            handler = new StagedResponseHandler();
            transport = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var client = new AppHttpClient(transport, clock);
            request = StartBufferedRequest(client, new Uri("https://example.invalid/deadline"), operation, httpOperation.Token);
            // 要求側の失敗を phase 待ちに隠さず、各段階の通知を直接観測する。
            await AwaitPhaseOrRequestAsync(handler.RequestStarted.Task, request, "request started", httpOperation.Token);
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.IsFalse(handler.RequestCancellation.IsCancellationRequested);
            handler.ReleaseHeaders.TrySetResult();
            await AwaitPhaseOrRequestAsync(handler.Body.ReadStarted.Task, request, "body read started", httpOperation.Token);
            Assert.IsFalse(handler.Body.ReadCancellation.IsCancellationRequested);

            clock.Advance(TimeSpan.FromSeconds(10));

            Assert.IsTrue(handler.Body.ReadCancellation.IsCancellationRequested,
                "The original request deadline must cancel the actual pending body read.");
            await SingleRequestHttpServer.AwaitOperationAsync(
                SingleRequestHttpServer.AssertClientCancelledAsync(request), httpObservers, httpOperation.Token);
            Assert.IsTrue(handler.Body.IsDisposed, "Request completion must release the response stream.");
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpOperation.Cancel();
            handler?.ReleaseHeaders.TrySetResult();
            handler?.Body.Release.TrySetResult(0);
            transport?.CancelPendingRequests();
            Task[] owned = httpObservers.Concat(new Task?[] { request }.OfType<Task>()).ToArray();
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, primaryFailure, owned); }
            finally
            {
                if (owned.All(task => task.IsCompleted)) { transport?.Dispose(); handler?.Dispose(); }
            }
        }
    }

    /// <summary>GET・form POSTのヘッダー直後と本文途中で外部取消し、fixture解放前に実peer切断まで観測して待ち手だけの取消を防ぎます。</summary>
    [TestMethod]
    [DataRow("GetStringAsync", false)]
    [DataRow("PostFormAsync", false)]
    [DataRow("GetStringAsync", true)]
    [DataRow("PostFormAsync", true)]
    [TestCategory("Http")]
    public async Task BufferedRequests_ExternalCancellationTerminatesPendingResponse(string operation, bool cancelBeforeBodyPrefix)
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        var httpObservers = new List<Task>();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(httpOperation.Token);
        SingleRequestHttpServer? server = null;
        HttpClient? transport = null;
        Task<string>? request = null;
        Exception? primaryFailure = null;
        try
        {
            server = new SingleRequestHttpServer(httpOperation.Token, [65, 66], holdBody: true, holdBodyPrefix: cancelBeforeBodyPrefix);
            transport = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var client = new AppHttpClient(transport, TimeProvider.System);
            request = StartBufferedRequest(client, server.Address, operation, cancellation.Token);
            // ヘッダー直後と本文途中を signal で分ける。短い delay で race を推定しない。
            Task responsePhase = cancelBeforeBodyPrefix ? server.HeadersSent.Task : server.BodyPrefixSent.Task;
            await server.WaitForPhaseAsync(responsePhase, cancelBeforeBodyPrefix ? "headers sent" : "body prefix sent", request);
            cancellation.Cancel();
            Task cancelled = SingleRequestHttpServer.AssertClientCancelledAsync(request);
            await SingleRequestHttpServer.AwaitOperationAsync(cancelled, httpObservers, httpOperation.Token);
            // 待ち手の Task だけを切り離す実装や、prefix 書込み後に監視を始める fixture は通さない。
            // ReleaseBodyPrefix / ReleaseBody / DisposeAsync より前に、実際の peer 切断を観測する。
            await server.WaitForPhaseAsync(server.ClientDisconnected.Task, "peer disconnected", cancelled);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            httpOperation.Cancel();
            transport?.CancelPendingRequests();
            Task[] owned = httpObservers.Concat(new Task?[] { request }.OfType<Task>()).ToArray();
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, primaryFailure, owned, server == null ? [] : [server]); }
            finally
            {
                if (owned.All(task => task.IsCompleted)) { transport?.Dispose(); }
                TestContext.WriteLine(operation + " beforePrefix=" + cancelBeforeBodyPrefix + Environment.NewLine + server?.Diagnostics);
            }
        }
    }

    /// <summary>同期・非同期GETの実応答で日本語と記号を保持してBOMだけを除き、取得経路の復号不一致を検出します。</summary>
    [TestMethod]
    [DataRow("GetStringAsync")]
    [DataRow("GetString")]
    [TestCategory("Http")]
    public async Task GetString_HttpResponsePreservesTextAndStripsBom(string operation)
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        Task<string>? request = null;
        SingleRequestHttpServer? server = null;

        Exception? httpFailure = null;
        try
        {
            server = new SingleRequestHttpServer(httpOperation.Token, CreateUtf8BomBytes("本文 & +"));

            request = StartBufferedRequest(AppHttpClient.Shared, server.Address, operation, httpOperation.Token);
            string response = await request.WaitAsync(httpOperation.Token);

            Assert.AreEqual("本文 & +", response);
            Assert.AreEqual("GET", server.RequestMethod);
        }
        catch (Exception failure) { httpFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpOperation.Cancel();
            await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, httpFailure,
                request == null ? [] : [request], server == null ? [] : [server]);
        }
    }

    /// <summary>同期・非同期form POSTで日本語・空値・同名複数値を実peerへ保持して渡し、応答BOMを除去します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Http")]
    public async Task PostForm_PreservesFormValuesAndStripsResponseBom(bool asynchronous)
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        Task<string>? request = null;
        SingleRequestHttpServer? server = null;

        Exception? httpFailure = null;
        try
        {
            server = new SingleRequestHttpServer(httpOperation.Token, CreateUtf8BomBytes("accepted"));
            var form = new NameValueCollection
            {
                { "name", "日本語 & +" },
                { "tag", "x y" },
                { "tag", "x+y" },
                { "empty", string.Empty }
            };
            request = asynchronous
                ? AppHttpClient.Shared.PostFormAsync(server.Address, form, cancellationToken: httpOperation.Token)
                : StartSynchronousRequest(() => AppHttpClient.Shared.PostForm(server.Address, form));
            string response = await request.WaitAsync(httpOperation.Token);

            Assert.AreEqual("accepted", response);
            Assert.AreEqual("POST", server.RequestMethod);
            StringAssert.StartsWith(server.RequestContentType, "application/x-www-form-urlencoded");
            NameValueCollection received = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(server.RequestBody));
            Assert.AreEqual(3, received.Count);
            Assert.AreEqual("日本語 & +", received["name"]);
            CollectionAssert.AreEqual(new[] { "x y", "x+y" }, received.GetValues("tag"));
            Assert.AreEqual(string.Empty, received["empty"]);
        }
        catch (Exception failure) { httpFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpOperation.Cancel();
            await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, httpFailure,
                request == null ? [] : [request], server == null ? [] : [server]);
        }
    }

    private static Task<string> StartBufferedRequest(AppHttpClient client, Uri address, string operation, CancellationToken cancellationToken = default)
    {
        var form = new NameValueCollection { { "name", "test" } };
        return operation switch
        {
            "GetStringAsync" => client.GetStringAsync(address, cancellationToken: cancellationToken),
            "PostFormAsync" => client.PostFormAsync(address, form, cancellationToken: cancellationToken),
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

    private static async Task AwaitPhaseOrRequestAsync(Task phase, Task request, string phaseName, CancellationToken operationToken)
    {
        await Task.WhenAny(phase, request).WaitAsync(operationToken).ConfigureAwait(false);
        if (request.IsCompleted) { await request.ConfigureAwait(false); }
        Assert.IsTrue(phase.IsCompleted, "HTTP request completed before " + phaseName + ".");
        await phase.ConfigureAwait(false);
    }

    private static byte[] CreateUtf8BomBytes(string text)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    private static string CreateTempFileWithBytes(TestWorkspace workspace, byte[] bytes)
    {
        string filePath = Path.Combine(workspace.RootDirectory, Guid.NewGuid().ToString("N") + ".txt");
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
