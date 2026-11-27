using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 複数の外部難度表を同時に取得できるloopbackのHTTPサーバーです。
/// 保留中は、応答ヘッダーを送った時点で取得中として数え、本文を解放した時点で数えから外し、その最大値を記録します。
/// 表 <c>i</c> は <c>/t{i}/header.json</c> と相対 <c>data_url</c> の <c>/t{i}/data.json</c> を返します。
/// </summary>
internal sealed class ConcurrentTableHttpServer
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping;
    private readonly CancellationToken operationToken;
    private readonly Task acceptTask;
    private readonly ConcurrentBag<Task> connections = [];
    private readonly object gate = new();
    private readonly Queue<(TaskCompletionSource Release, bool IsData)> held = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int inFlight;
    private int maxInFlight;
    private int finishedTables;
    private int cleanupStarted;

    /// <param name="operationToken">全要求のaccept/read/writeで共有する操作期限とテスト取消。</param>
    internal ConcurrentTableHttpServer(CancellationToken operationToken)
    {
        this.operationToken = operationToken;
        stopping = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Address = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
        acceptTask = AcceptAsync();
    }

    /// <summary>サーバーの待ち受けURIです。</summary>
    internal Uri Address { get; }

    /// <summary>trueの間は、応答ヘッダーを送った後に <see cref="ReleaseOldest"/> まで本文を保留します。</summary>
    internal volatile bool Hold;

    /// <summary>表の数を返す <c>/t{index}/header.json</c> のURIです。</summary>
    internal Uri TableUri(int index) => new(Address, "t" + index + "/header.json");

    /// <summary>現在、ヘッダー送信後に本文を保留している要求の数です。</summary>
    internal int InFlight { get { lock (gate) { return inFlight; } } }

    /// <summary>記録を始めてからの <see cref="InFlight"/> の最大値です。</summary>
    internal int MaxInFlight { get { lock (gate) { return maxInFlight; } } }

    /// <summary>data本文を解放した数で、取得を終えた表の数を表します。</summary>
    internal int FinishedTables { get { lock (gate) { return finishedTables; } } }

    /// <summary>最大値と終了数の記録を、現在の保留数から取り直します。</summary>
    internal void ResetRecord()
    {
        lock (gate)
        {
            maxInFlight = inFlight;
            finishedTables = 0;
        }
    }

    /// <summary>保留中の要求が指定数に達するまで、通知で待ちます。固定時間の経過で推測しません。</summary>
    internal async Task WaitForInFlightAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (gate)
            {
                if (inFlight >= count) { return; }
                signal = changed.Task;
            }
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>最も古い保留中の要求の本文を解放し、取得中の数と終了した表の数を同じ時点で更新します。</summary>
    internal void ReleaseOldest()
    {
        TaskCompletionSource release;
        lock (gate)
        {
            (release, bool isData) = held.Dequeue();
            inFlight--;
            if (isData) { finishedTables++; }
        }
        release.TrySetResult();
    }

    /// <summary>保留をすべて解放し、待ち受けと全接続の実終端を回収します。</summary>
    internal async Task DisposeAsync(CancellationToken cleanupToken)
    {
        if (Interlocked.Exchange(ref cleanupStarted, 1) == 0)
        {
            Hold = false;
            stopping.Cancel();
            listener.Stop();
            lock (gate)
            {
                while (held.Count > 0) { held.Dequeue().Release.TrySetResult(); }
            }
        }
        await acceptTask.WaitAsync(cleanupToken).ConfigureAwait(false);
        await Task.WhenAll(connections.ToArray()).WaitAsync(cleanupToken).ConfigureAwait(false);
        stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
                connections.Add(HandleAsync(client));
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested || operationToken.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string target = await ReadTargetAsync(stream).ConfigureAwait(false);
                bool isData = target.EndsWith("/data.json", StringComparison.Ordinal);
                string body = isData
                    ? "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"song\",\"artist\":\"artist\",\"level\":\"1\"}]"
                    : "{\"name\":\"" + target.Split('/')[1] + "\",\"symbol\":\"S\",\"data_url\":\"data.json\"}";
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stopping.Token).ConfigureAwait(false);
                await stream.FlushAsync(stopping.Token).ConfigureAwait(false);
                if (Hold)
                {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource signal;
                    lock (gate)
                    {
                        held.Enqueue((release, isData));
                        inFlight++;
                        maxInFlight = Math.Max(maxInFlight, inFlight);
                        signal = changed;
                        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    signal.TrySetResult();
                    await release.Task.WaitAsync(stopping.Token).ConfigureAwait(false);
                }
                await stream.WriteAsync(bytes, stopping.Token).ConfigureAwait(false);
                await stream.FlushAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (Exception) when (stopping.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task<string> ReadTargetAsync(NetworkStream stream)
    {
        var request = new StringBuilder();
        byte[] one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one).ConfigureAwait(false) == 0) { break; }
            request.Append((char)one[0]);
        }
        // 先頭行は "GET /t1/header.json HTTP/1.1" の形です。
        return request.ToString().Split(' ')[1];
    }
}
