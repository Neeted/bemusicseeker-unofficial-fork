using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeMusicSeeker.TestSupport.Components.Update;

/// <summary>
/// ケースが渡したmanifestとpackageのbytesを、loopbackのHTTP/1.1で複数の要求へ配る更新配信です。全要求を到着順に記録します。
/// </summary>
/// <remarks>
/// 配る内容は世代単位で差し替えられ、各要求はどの世代で応答したかを持ちます。未知のpathは404で応答して記録し、
/// ケースが想定外の要求として失敗にします。送信途中の故障は記録に残し、<see cref="DisposeAsync(CancellationToken)"/>で表面化させます。
/// </remarks>
internal sealed class LoopbackUpdateFeed : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping;
    private readonly Task acceptLoop;
    private readonly ConcurrentDictionary<Task, byte> connections = new();
    private readonly ConcurrentQueue<Exception> faults = new();
    private readonly object gate = new();
    private readonly List<Entry> entries = [];
    private IReadOnlyDictionary<string, byte[]> resources = new Dictionary<string, byte[]>();
    private int generation;
    private int disposed;

    /// <summary>全要求の待受と読書きが共有する、呼出元の操作期限とテスト取消のtokenで開始します。</summary>
    /// <param name="operationToken">accept・read・writeで共有する有限期限のtoken。</param>
    internal LoopbackUpdateFeed(CancellationToken operationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        BaseAddress = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
        acceptLoop = AcceptAsync();
    }

    /// <summary>OSが割り当てたportを含むorigin（末尾スラッシュ付き）です。</summary>
    internal Uri BaseAddress { get; }

    /// <summary>現在の世代番号です。<see cref="Publish"/>が呼ばれるたびに1から増えます。0は未公開です。</summary>
    internal int Generation { get { lock (gate) { return generation; } } }

    /// <summary>以後の要求へ配る内容を、新しい世代として置き換えます。</summary>
    /// <param name="content">先頭スラッシュなしのpath（queryを除く）から本文bytesへの対応です。</param>
    /// <returns>公開した世代番号です。</returns>
    internal int Publish(IReadOnlyDictionary<string, byte[]> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var copy = content.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        lock (gate)
        {
            resources = copy;
            return ++generation;
        }
    }

    /// <summary>到着順に記録した全要求の、その時点の変更不能な複製です。</summary>
    internal IReadOnlyList<FeedRequest> Requests
    {
        get { lock (gate) { return [.. entries.Select(entry => entry.Snapshot())]; } }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
                Task connection = ServeAsync(client);
                connections.TryAdd(connection, 0);
                _ = connection.ContinueWith(done => connections.TryRemove(done, out _), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
        catch (SocketException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        Entry? entry = null;
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                string head = await ReadHeadAsync(stream).ConfigureAwait(false);
                string[] line = head.Split("\r\n", 2)[0].Split(' ');
                if (line.Length != 3) { throw new InvalidDataException("要求行が不正です: " + head); }
                string target = line[1];
                int query = target.IndexOf('?', StringComparison.Ordinal);
                string path = Uri.UnescapeDataString((query < 0 ? target : target[..query]).TrimStart('/'));
                byte[]? body;
                lock (gate)
                {
                    resources.TryGetValue(path, out body);
                    entry = new Entry(entries.Count + 1, generation, line[0], target, path, body is null ? 404 : 200);
                    entries.Add(entry);
                }
                bool headOnly = string.Equals(line[0], "HEAD", StringComparison.Ordinal);
                byte[] payload = body ?? Encoding.UTF8.GetBytes("not found");
                byte[] header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {entry.Status} {(entry.Status == 200 ? "OK" : "Not Found")}\r\nContent-Type: application/octet-stream\r\n"
                    + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stopping.Token).ConfigureAwait(false);
                if (!headOnly)
                {
                    // 一部ずつ書き、送信済みbytes数を実際に書けた分だけ数えます。
                    for (int offset = 0; offset < payload.Length; offset += 65536)
                    {
                        int count = Math.Min(65536, payload.Length - offset);
                        await stream.WriteAsync(payload.AsMemory(offset, count), stopping.Token).ConfigureAwait(false);
                        entry.AddSent(count);
                    }
                }
                await stream.FlushAsync(stopping.Token).ConfigureAwait(false);
                entry.MarkCompleted();
            }
        }
        catch (Exception failure)
        {
            entry?.MarkFailed(failure);
            faults.Enqueue(failure);
        }
    }

    private async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[8192];
        int length = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), stopping.Token).ConfigureAwait(false);
            if (read == 0) { throw new EndOfStreamException("要求の途中で接続が閉じました。"); }
            length += read;
            string text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) { return text; }
            if (length == buffer.Length) { throw new InvalidDataException("要求ヘッダーが大きすぎます。"); }
        }
    }

    /// <summary>cleanup期限を課さずに待受を止め、全接続処理の実終端を回収します。</summary>
    public async ValueTask DisposeAsync() => await DisposeAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>待受を止め、全接続処理の実終端を回収します。送信途中の故障があれば表面化させます。</summary>
    /// <param name="cleanupToken">呼出元がcleanup全体で共有する、独立した有限token。</param>
    internal async ValueTask DisposeAsync(CancellationToken cleanupToken)
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            stopping.Cancel();
            listener.Stop();
        }
        await acceptLoop.WaitAsync(cleanupToken).ConfigureAwait(false);
        await Task.WhenAll(connections.Keys).WaitAsync(cleanupToken).ConfigureAwait(false);
        stopping.Dispose();
        // 回収のために止めた接続の取消は故障に数えません。それ以外の送信途中の故障を成功に変えません。
        Exception[] unexpected = [.. faults.Where(failure => failure is not OperationCanceledException)];
        if (unexpected.Length > 0) { throw new AggregateException("更新配信の接続処理が失敗しました。", unexpected); }
    }

    /// <summary>記録した一件の要求です。</summary>
    /// <param name="Sequence">到着順の1始まりの番号です。</param>
    /// <param name="Generation">応答に使った内容の世代です。</param>
    /// <param name="Method">HTTPメソッドです。</param>
    /// <param name="Target">queryを含む要求targetです。</param>
    /// <param name="Path">queryを除き先頭スラッシュを除いた、解決したpathです。</param>
    /// <param name="Status">返したHTTP状態です。</param>
    /// <param name="SentBytes">実際に書けた本文のbytes数です。</param>
    /// <param name="Completed">応答を最後まで送信できたかどうかです。</param>
    /// <param name="Failure">送信途中の故障です。なければnullです。</param>
    internal sealed record FeedRequest(int Sequence, int Generation, string Method, string Target, string Path, int Status,
        long SentBytes, bool Completed, Exception? Failure);

    private sealed class Entry(int sequence, int generation, string method, string target, string path, int status)
    {
        private long sent;
        private bool completed;
        private Exception? failure;

        internal int Status { get; } = status;

        internal void AddSent(int count) => Interlocked.Add(ref sent, count);

        internal void MarkCompleted() => Volatile.Write(ref completed, true);

        internal void MarkFailed(Exception exception) => Interlocked.CompareExchange(ref failure, exception, null);

        internal FeedRequest Snapshot()
            => new(sequence, generation, method, target, path, Status, Interlocked.Read(ref sent), Volatile.Read(ref completed), Volatile.Read(ref failure));
    }
}
