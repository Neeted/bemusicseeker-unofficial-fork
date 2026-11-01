using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// テストが所有する loopback 接続を既定で一度、または指定された有限回だけ受け付ける HTTP サーバーです。
/// 応答、要求受信、本文送信、peer 切断、cleanup の状態を観測できます。
/// </summary>
internal sealed class SingleRequestHttpServer
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping;
    private readonly CancellationToken operationToken;
    private readonly Task serverTask;
    private readonly byte[] responseBody;
    private readonly IReadOnlyList<Response>? responses;
    private readonly ConcurrentQueue<string> requestTargets = new();

    private readonly string additionalResponseHeaders;
    private readonly HttpStatusCode responseStatusCode;
    private readonly bool holdBody;
    private readonly bool holdBodyPrefix;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly ConcurrentQueue<string> events = new();
    private TcpClient? acceptedClient;
    private int cleanupStarted;
    private int stoppingDisposed;

    /// <summary>HTTP 応答ヘッダーを送信したことを通知します。</summary>
    public TaskCompletionSource HeadersSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>応答本文の先頭を送信する前に解放する signal です。</summary>
    public TaskCompletionSource ReleaseBodyPrefix { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>応答本文全体を送信する前に解放する signal です。</summary>
    public TaskCompletionSource ReleaseBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>応答本文の先頭を送信したことを通知します。</summary>
    public TaskCompletionSource BodyPrefixSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>クライアントが接続を閉じたことを通知します。</summary>
    public TaskCompletionSource ClientDisconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>サーバーが待ち受ける loopback URI です。</summary>
    public Uri Address { get; }

    /// <summary>受信した HTTP 要求メソッドです。要求前は空文字列です。</summary>
    public string RequestMethod { get; private set; } = string.Empty;

    /// <summary>受信した Content-Type です。要求前は空文字列です。</summary>
    public string RequestContentType { get; private set; } = string.Empty;

    /// <summary>受信した要求本文です。本文なし、または要求前は空配列です。</summary>
    public byte[] RequestBody { get; private set; } = [];

    /// <summary>サーバー内の到達点を診断文字列として返します。</summary>
    public string Diagnostics => string.Join(Environment.NewLine, events);

    /// <summary>大小文字を保持して受信順に記録したrequest targetのsnapshotです。</summary>
    public string[] RequestTargets => requestTargets.ToArray();

    /// <summary>開始済みaccept/read/writeと切断監視を含むサーバーの実終端です。</summary>
    public Task Completion => serverTask;

    /// <summary>有限要求用のtarget・本文・追加ヘッダーを固定する応答です。</summary>
    internal sealed record Response(string RequestTarget, byte[] Body, string AdditionalHeaders = "");

    /// <summary>
    /// 指定した本文を返す単一要求サーバーを開始します。
    /// </summary>
    /// <param name="operationToken">呼出し側が全操作段階で共有する有限期限とテスト取消のtoken。</param>
    /// <param name="responseBody">返却する本文。</param>
    /// <param name="holdBody">本文送信前に解放 signal を待つ場合は <see langword="true"/>。</param>
    /// <param name="holdBodyPrefix">本文先頭の送信前に解放 signal を待つ場合は <see langword="true"/>。</param>
    /// <param name="responseStatusCode">同じHTTP読取り経路で検証する応答コード。</param>
    /// <param name="additionalResponseHeaders">圧縮応答等の代表検証で返す追加ヘッダー。各行はCRLFで終えます。</param>
    public SingleRequestHttpServer(CancellationToken operationToken, byte[] responseBody, bool holdBody = false, bool holdBodyPrefix = false,
        HttpStatusCode responseStatusCode = HttpStatusCode.OK, string additionalResponseHeaders = "")
        : this(operationToken, responseBody, holdBody, holdBodyPrefix, responseStatusCode, additionalResponseHeaders, null)
    {
    }

    /// <summary>同じoriginで指定targetへ一回ずつ応答し、不一致・重複要求を失敗として記録します。</summary>
    /// <param name="operationToken">全要求のaccept/read/writeで共有する操作期限とテスト取消。</param>
    /// <param name="responses">同一originで一度ずつ返す有限の応答。</param>
    internal static SingleRequestHttpServer StartFiniteResponses(CancellationToken operationToken, IReadOnlyList<Response> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        return new SingleRequestHttpServer(operationToken, [], false, false, HttpStatusCode.OK, "", responses);
    }

    private SingleRequestHttpServer(CancellationToken operationToken, byte[] responseBody, bool holdBody, bool holdBodyPrefix,
        HttpStatusCode responseStatusCode, string additionalResponseHeaders, IReadOnlyList<Response>? responses)
    {
        if (responses is { Count: 0 }) { throw new ArgumentException("At least one response is required.", nameof(responses)); }
        this.operationToken = operationToken;
        stopping = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        this.responses = responses?.ToArray();
        this.responseBody = responseBody ?? [];
        this.responseStatusCode = responseStatusCode;
        this.additionalResponseHeaders = additionalResponseHeaders;
        this.holdBody = holdBody;
        this.holdBodyPrefix = holdBodyPrefix;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Address = new Uri("http://127.0.0.1:" + port + "/");
        // Accept と request read 自体を非同期にし、並列 Functional で pool thread を占有しない。
        serverTask = ServeSingleRequestAsync();
    }

    /// <summary>
    /// phaseと実producer終端を同時に監視し、先行故障・通知なしの終端を操作期限内に伝えます。
    /// 正常終了した補助producerは待機集合から除き、phaseと同時に終端した通知も保持します。
    /// </summary>
    /// <param name="phase">到達を検証する通知。</param>
    /// <param name="name">診断用の段階名。</param>
    /// <param name="clientOutcome">元のclient処理、または期待した取消を検証する処理の実終端。</param>
    /// <param name="otherProducers">同じ通知に関わる他のserver等の実終端。</param>
    public async Task WaitForPhaseAsync(Task phase, string name, Task clientOutcome, params Task[] otherProducers)
    {
        var pending = new List<Task>(otherProducers) { serverTask, clientOutcome };
        try
        {
            while (true)
            {
                // WhenAnyの勝者だけで判定せず、同時終端したproducerの故障も観測します。
                foreach (Task producer in pending.Where(task => task.IsCompleted).ToArray())
                {
                    await producer.ConfigureAwait(false);
                    // peerのEOF/resetは要求取消の確認後にも到達し得るため、serverの実観測を続けます。
                    if (producer == clientOutcome && phase != ClientDisconnected.Task)
                    {
                        Assert.IsTrue(phase.IsCompleted, "HTTP client ended before " + name + ".");
                    }
                    pending.Remove(producer);
                }
                if (phase.IsCompleted)
                {
                    await phase.ConfigureAwait(false);
                    return;
                }
                if (serverTask.IsCompleted)
                {
                    // 終端の観測前後で通知され得るため、実producerを観測してから通知を読み直します。
                    await serverTask.ConfigureAwait(false);
                    Assert.IsTrue(phase.IsCompleted, "HTTP server ended before " + name + ".");
                    await phase.ConfigureAwait(false);
                    return;
                }
                Assert.IsFalse(pending.Count == 0, "HTTP producers ended before " + name + ".");
                await Task.WhenAny(pending.Append(phase)).WaitAsync(operationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            throw new AssertFailedException("HTTP phase: " + name + Environment.NewLine + Diagnostics, failure);
        }
    }

    /// <summary>
    /// fixtureの取消を先に通知し、listener・gate・接続を解放してから実serverと切断監視を回収します。
    /// 操作の取消とは独立した、呼出し側がcleanup全体で共有する有限tokenが必要です。
    /// </summary>
    /// <param name="cleanupToken">呼出し側のfinally開始時に一度設定した独立の有限cleanup token。</param>
    public async ValueTask DisposeAsync(CancellationToken cleanupToken)
    {
        if (Interlocked.Exchange(ref cleanupStarted, 1) == 0)
        {
            Record("fixture cleanup");
            stopping.Cancel();
            listener.Stop();
            ReleaseBodyPrefix.TrySetResult();
            ReleaseBody.TrySetResult();
            acceptedClient?.Dispose();
        }
        try
        {
            await serverTask.WaitAsync(cleanupToken).ConfigureAwait(false);
        }
        finally
        {
            // 未回収時には実処理が引き続きtokenを使うため、先にCTSを破棄しません。
            if (serverTask.IsCompleted && Interlocked.Exchange(ref stoppingDisposed, 1) == 0) { stopping.Dispose(); }
        }
    }

    /// <summary>watchdogの待機取消と区別して、元のclient Taskの実終端と元結果の取消例外を検証します。</summary>
    internal static async Task AssertClientCancelledAsync(Task request)
    {
        // 同期APIを専用threadで起動したTaskはFaulted(OCE)にもなるため、状態ではなく元Taskを直接awaitします。
        try { await request.ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        Assert.Fail("未受信本文を成功として返さず、元の要求を取消終端させます。");
    }

    /// <summary>元Taskを呼出し側へ記録して操作tokenで待ち、実終端を観測したTaskだけを外します。期限で離れた実処理はcleanupへ残します。</summary>
    internal static async Task AwaitOperationAsync(Task operation, ICollection<Task> ownedOperations, CancellationToken operationToken)
    {
        ownedOperations.Add(operation);
        bool observed = false;
        try { await operation.WaitAsync(operationToken).ConfigureAwait(false); observed = true; }
        catch
        {
            if (operation.IsCompleted) { observed = true; await operation.ConfigureAwait(false); }
            throw;
        }
        finally { if (observed) { ownedOperations.Remove(operation); } }
    }

    /// <summary>結果付きの元の処理Taskを記録し、待機取消後もcleanupで実処理を回収できるようにします。</summary>
    internal static async Task<T> AwaitOperationAsync<T>(Task<T> operation, ICollection<Task> ownedOperations, CancellationToken operationToken)
    {
        ownedOperations.Add(operation);
        bool observed = false;
        try
        {
            T result = await operation.WaitAsync(operationToken).ConfigureAwait(false);
            observed = true;
            return result;
        }
        catch
        {
            if (operation.IsCompleted) { observed = true; await operation.ConfigureAwait(false); }
            throw;
        }
        finally { if (observed) { ownedOperations.Remove(operation); } }
    }

    /// <summary>
    /// 全serverの停止を先に開始し、呼出し側が所有するclientと実serverを同じcleanup期限で回収します。
    /// clientの実取消終端だけを許容し、期限による待機の取消やcleanup失敗は一次失敗とともに返します。
    /// </summary>
    /// <param name="cleanupToken">操作取消から独立し、cleanup開始時に一度設定した有限token。</param>
    /// <param name="primaryFailure">保持する操作の一次失敗。成功時はnull。</param>
    /// <param name="clientTasks">開始した元のclient処理。待ち手だけのTaskへ置き換えません。</param>
    /// <param name="servers">停止して実終端を回収する全server。</param>
    internal static async Task CompleteCleanupAsync(CancellationToken cleanupToken, Exception? primaryFailure,
        IEnumerable<Task> clientTasks, params SingleRequestHttpServer[] servers)
    {
        // fixture停止によるclient終端より先に回収対象を固定し、停止直後の故障も見逃しません。
        Task[] clients = clientTasks.Distinct().ToArray();
        Task[] cleanup = servers.Select(server => server.DisposeAsync(cleanupToken).AsTask())
            .Concat(clients.Select(JoinClientAsync)).ToArray();
        var joined = Task.WhenAll(cleanup);
        try { await joined.ConfigureAwait(false); }
        catch (Exception failure)
        {
            var failures = new List<Exception>();
            if (primaryFailure != null) { failures.Add(primaryFailure); }
            if (joined.Exception != null) { failures.AddRange(joined.Exception.InnerExceptions); }
            else { failures.Add(failure); }
            throw new AggregateException(failures);
        }

        async Task JoinClientAsync(Task task)
        {
            try { await task.WaitAsync(cleanupToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (task.IsCompleted)
            {
                // WaitAsync側のwatchdog取消を許容せず、実終端した元Taskの結果を直接確認します。
                // asyncのCanceledと同期adapterのFaulted(OCE)だけを許容し、別の故障はそのまま伝えます。
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                // 元結果が成功なら、先行した待機のwatchdog取消をcleanup失敗として保持します。
                throw;
            }
        }
    }

    private async Task ServeSingleRequestAsync()
    {
        try
        {
            var servedTargets = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < (responses?.Count ?? 1); index++)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
                acceptedClient = client;
                Record("client accepted");
                using NetworkStream stream = client.GetStream();
                using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                Task disconnectTask = Task.CompletedTask;
                try
                {
                    await ReadRequestAsync(stream, stopping.Token).ConfigureAwait(false);
                    Record("request read");
                    byte[] body = responseBody;
                    string headers = additionalResponseHeaders;
                    if (responses != null)
                    {
                        string target = requestTargets.Last();
                        Response? response = responses.SingleOrDefault(candidate => string.Equals(candidate.RequestTarget, target, StringComparison.Ordinal));
                        Assert.IsNotNull(response, "Unexpected HTTP request target: " + target);
                        Assert.IsTrue(servedTargets.Add(target), "Excess HTTP request target: " + target);
                        body = response.Body;
                        headers = response.AdditionalHeaders;
                    }
                    // 期限切れはヘッダー送信と prefix 書込みの間にも起こる。
                    // 応答を書き始める前に監視し、write failure 後も peer の終端を見届ける。
                    if (holdBody)
                    {
                        disconnectTask = ObserveClientDisconnectAsync(stream, observationCancellation.Token);
                    }
                    byte[] responseBytes = BuildHttpResponse(body, responseStatusCode, headers);
                    int headerLength = responseBytes.Length - body.Length;
                    await stream.WriteAsync(responseBytes.AsMemory(0, headerLength), stopping.Token).ConfigureAwait(false);
                    await stream.FlushAsync(stopping.Token).ConfigureAwait(false);
                    Record("headers sent");
                    HeadersSent.TrySetResult();
                    if (holdBodyPrefix)
                    {
                        await Task.WhenAny(ReleaseBodyPrefix.Task, disconnectTask).WaitAsync(stopping.Token).ConfigureAwait(false);
                        if (disconnectTask.IsCompleted)
                        {
                            await disconnectTask.ConfigureAwait(false);
                            return;
                        }
                    }
                    int prefixLength = holdBody && body.Length > 1 ? 1 : 0;
                    if (prefixLength > 0)
                    {
                        await stream.WriteAsync(body.AsMemory(0, prefixLength), stopping.Token).ConfigureAwait(false);
                        await stream.FlushAsync(stopping.Token).ConfigureAwait(false);
                    }
                    Record("body prefix sent");
                    BodyPrefixSent.TrySetResult();
                    if (holdBody)
                    {
                        await Task.WhenAny(ReleaseBody.Task, disconnectTask).WaitAsync(stopping.Token).ConfigureAwait(false);
                        if (disconnectTask.IsCompleted)
                        {
                            await disconnectTask.ConfigureAwait(false);
                            return;
                        }
                    }
                    await stream.WriteAsync(body.AsMemory(prefixLength), stopping.Token).ConfigureAwait(false);
                    await stream.FlushAsync(stopping.Token).ConfigureAwait(false);
                    Record("body sent");
                }
                catch (IOException failure) when (holdBody && !stopping.IsCancellationRequested && IsPeerDisconnect(failure))
                {
                    Record("peer closed during write: " + failure);
                    // 書込み失敗を握りつぶしたり、server 自身の Dispose を切断成功と数えたりしない。
                    await disconnectTask.ConfigureAwait(false);
                    Assert.IsTrue(ClientDisconnected.Task.IsCompletedSuccessfully, "The peer disconnect was not observed.");
                }
                finally
                {
                    // ローカル close による EOF / abort を peer の切断と誤認しない。
                    observationCancellation.Cancel();
                    await disconnectTask.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested)
        {
        }
        catch (IOException) when (stopping.IsCancellationRequested)
        {
        }
        catch (SocketException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            Record("server failure: " + failure);
            throw;
        }
        finally
        {
            if (responses != null) { listener.Stop(); }
            Record("server completed");
        }
    }

    private async Task ObserveClientDisconnectAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        Record("disconnect observer started");
        try
        {
            int count = await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count != 0)
            {
                throw new InvalidDataException("Unexpected data after the single HTTP request.");
            }
            Record("peer EOF");
            ClientDisconnected.TrySetResult();
        }
        catch (IOException failure) when (!cancellationToken.IsCancellationRequested && IsPeerDisconnect(failure))
        {
            Record("peer reset: " + failure.InnerException);
            ClientDisconnected.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsPeerDisconnect(IOException failure)
    {
        return failure.InnerException is SocketException socketFailure
            && socketFailure.SocketErrorCode is SocketError.ConnectionReset
                or SocketError.ConnectionAborted or SocketError.NetworkReset or SocketError.Shutdown;
    }

    private void Record(string message)
    {
        events.Enqueue(elapsed.Elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "ms " + message);
    }

    private async Task ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        Record("reading request");
        byte[] buffer = new byte[4096];
        using var received = new MemoryStream();
        int headerEndIndex = -1;
        while (headerEndIndex < 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException("The peer closed before the HTTP request headers were complete.");
            }
            received.Write(buffer, 0, read);
            headerEndIndex = FindHeaderTerminator(received.GetBuffer(), (int)received.Length);
        }
        byte[] receivedBytes = received.ToArray();
        if (headerEndIndex < 0)
        {
            return;
        }
        string headerText = Encoding.ASCII.GetString(receivedBytes, 0, headerEndIndex);
        string[] headerLines = headerText.Split(["\r\n"], StringSplitOptions.None);
        if (headerLines.Length > 0)
        {
            string[] requestLineParts = headerLines[0].Split(' ');
            if (requestLineParts.Length > 0)
            {
                RequestMethod = requestLineParts[0];
            }
            if (requestLineParts.Length > 1)
            {
                requestTargets.Enqueue(requestLineParts[1]);
                Record("request target " + requestLineParts[1]);
            }
        }
        int contentLength = 0;
        foreach (string line in headerLines.Skip(1))
        {
            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line.Substring(prefix.Length).Trim(), out contentLength);
            }
            const string contentTypePrefix = "Content-Type:";
            if (line.StartsWith(contentTypePrefix, StringComparison.OrdinalIgnoreCase))
            {
                RequestContentType = line.Substring(contentTypePrefix.Length).Trim();
            }
        }
        int bodyBytesRead = receivedBytes.Length - (headerEndIndex + 4);
        while (bodyBytesRead < contentLength)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, contentLength - bodyBytesRead)), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException("The peer closed before the HTTP request body was complete.");
            }
            received.Write(buffer, 0, read);
            bodyBytesRead += read;
        }
        RequestBody = received.ToArray().Skip(headerEndIndex + 4).Take(contentLength).ToArray();
    }

    private static int FindHeaderTerminator(byte[] buffer, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static byte[] BuildHttpResponse(byte[] body, HttpStatusCode statusCode, string additionalResponseHeaders)
    {
        string header = "HTTP/1.1 " + (int)statusCode + " " + statusCode
            + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" + additionalResponseHeaders + "\r\n";
        return [.. Encoding.ASCII.GetBytes(header), .. body];
    }
}
