using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// テストが所有する loopback 接続を一度だけ受け付ける HTTP サーバーです。
/// 応答、要求受信、本文送信、peer 切断、cleanup の状態を観測できます。
/// </summary>
internal sealed class SingleRequestHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task serverTask;
    private readonly byte[] responseBody;
    private readonly bool holdBody;
    private readonly bool holdBodyPrefix;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly ConcurrentQueue<string> events = new();
    private TcpClient? acceptedClient;

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

    /// <summary>
    /// 指定した本文を返す単一要求サーバーを開始します。
    /// </summary>
    /// <param name="responseBody">返却する本文。</param>
    /// <param name="holdBody">本文送信前に解放 signal を待つ場合は <see langword="true"/>。</param>
    /// <param name="holdBodyPrefix">本文先頭の送信前に解放 signal を待つ場合は <see langword="true"/>。</param>
    public SingleRequestHttpServer(byte[] responseBody, bool holdBody = false, bool holdBodyPrefix = false)
    {
        this.responseBody = responseBody ?? [];
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
    /// 指定 phase またはサーバー完了を待ちます。
    /// </summary>
    /// <param name="phase">確認する phase signal。</param>
    /// <param name="name">失敗時に表示する phase 名。</param>
    public async Task WaitForPhaseAsync(Task phase, string name)
    {
        try
        {
            Task completed = await Task.WhenAny(phase, serverTask).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            Assert.IsTrue(phase.IsCompletedSuccessfully, "HTTP server ended before " + name + ".");
        }
        catch (Exception failure)
        {
            // phase 未通知をただの timeout にせず、server の元例外と到達点を TRX へ残す。
            throw new AssertFailedException("HTTP phase: " + name + Environment.NewLine + Diagnostics, failure);
        }
    }

    /// <summary>
    /// listener と server task を停止し、cleanup 完了を待ちます。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Record("fixture cleanup");
        stopping.Cancel();
        listener.Stop();
        ReleaseBodyPrefix.TrySetResult();
        ReleaseBody.TrySetResult();
        acceptedClient?.Dispose();
        try
        {
            // serverTaskのfinallyが切断監視も回収するため、その終結まで所有する。
            await serverTask.ConfigureAwait(false);
        }
        finally
        {
            stopping.Dispose();
        }
    }

    private async Task ServeSingleRequestAsync()
    {
        try
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
                // 期限切れはヘッダー送信と prefix 書込みの間にも起こる。
                // 応答を書き始める前に監視し、write failure 後も peer の終端を見届ける。
                if (holdBody)
                {
                    disconnectTask = ObserveClientDisconnectAsync(stream, observationCancellation.Token);
                }
                byte[] responseBytes = BuildHttpResponse(responseBody);
                int headerLength = responseBytes.Length - responseBody.Length;
                await stream.WriteAsync(responseBytes.AsMemory(0, headerLength), stopping.Token).ConfigureAwait(false);
                await stream.FlushAsync(stopping.Token).ConfigureAwait(false);
                Record("headers sent");
                HeadersSent.TrySetResult();
                if (holdBodyPrefix)
                {
                    await Task.WhenAny(ReleaseBodyPrefix.Task, disconnectTask).ConfigureAwait(false);
                    if (disconnectTask.IsCompleted)
                    {
                        await disconnectTask.ConfigureAwait(false);
                        return;
                    }
                }
                int prefixLength = holdBody && responseBody.Length > 1 ? 1 : 0;
                if (prefixLength > 0)
                {
                    await stream.WriteAsync(responseBody.AsMemory(0, prefixLength), stopping.Token).ConfigureAwait(false);
                    await stream.FlushAsync(stopping.Token).ConfigureAwait(false);
                }
                Record("body prefix sent");
                BodyPrefixSent.TrySetResult();
                if (holdBody)
                {
                    await Task.WhenAny(ReleaseBody.Task, disconnectTask).ConfigureAwait(false);
                    if (disconnectTask.IsCompleted)
                    {
                        await disconnectTask.ConfigureAwait(false);
                        return;
                    }
                }
                await stream.WriteAsync(responseBody.AsMemory(prefixLength), stopping.Token).ConfigureAwait(false);
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

    private static byte[] BuildHttpResponse(byte[] body)
    {
        string header = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
        return [.. Encoding.ASCII.GetBytes(header), .. body];
    }
}
