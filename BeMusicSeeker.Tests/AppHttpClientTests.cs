using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class AppHttpClientTests
{
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

        string response = AppHttpClient.Shared.PostString(server.Address, "body", "application/json;charset=UTF-8", Encoding.UTF8);

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

            string response = AppHttpClient.Shared.PostFile(server.Address, uploadFilePath, responseEncoding: Encoding.UTF8);

            Assert.AreEqual("{\"uploaded\":true}", response);
            Assert.AreEqual("POST", server.RequestMethod);
        }
        finally
        {
            File.Delete(uploadFilePath);
        }
    }

    [TestMethod]
    [TestCategory("Http")]
    public async Task GetStringAsync_HeadersAndBodyShareOneDeadline()
    {
        await using var server = new SingleRequestHttpServer([65], holdBody: true, headerDelayMs: 1200);
        Task<string> request = AppHttpClient.Create(2000).GetStringAsync(server.Address);
        try
        {
            await server.HeadersSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertRequestCancelledAsync(request.WaitAsync(TimeSpan.FromMilliseconds(1600)));
        }
        finally
        {
            server.ReleaseBody.TrySetResult();
            try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    [TestCategory("Http")]
    // header/body 共通経路への外部 token 接続を補助検証する。本文開始の独立証明ではない。
    public async Task GetStringAsync_ExternalCancellationTerminatesPendingResponse()
    {
        using var cancellation = new CancellationTokenSource();
        await using var server = new SingleRequestHttpServer([65], holdBody: true);
        Task<string> request = AppHttpClient.Create(30000).GetStringAsync(server.Address, cancellationToken: cancellation.Token);
        try
        {
            await server.HeadersSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await AssertRequestCancelledAsync(request.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            server.ReleaseBody.TrySetResult();
            try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
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

    private sealed class SingleRequestHttpServer : IAsyncDisposable
    {
        private readonly TcpListener listener;

        private readonly Task serverTask;

        private readonly byte[] responseBody;
        private readonly bool holdBody;
        private readonly int headerDelayMs;
        private TcpClient acceptedClient;
        public TaskCompletionSource HeadersSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri Address { get; }

        public string RequestMethod { get; private set; } = string.Empty;

        public SingleRequestHttpServer(byte[] responseBody, bool holdBody = false, int headerDelayMs = 0)
        {
            this.responseBody = responseBody ?? [];
            this.holdBody = holdBody;
            this.headerDelayMs = headerDelayMs;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Address = new Uri("http://127.0.0.1:" + port + "/");
            serverTask = Task.Run(ServeSingleRequest);
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            ReleaseBody.TrySetResult();
            acceptedClient?.Dispose();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private async Task ServeSingleRequest()
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                acceptedClient = client;
                using NetworkStream stream = client.GetStream();
                ReadRequest(stream);
                byte[] responseBytes = BuildHttpResponse(responseBody);
                // HTTP の単一期限契約を検証するためだけに、ヘッダー応答で予算を消費する。
                if (headerDelayMs > 0) await Task.Delay(headerDelayMs);
                int headerLength = responseBytes.Length - responseBody.Length;
                await stream.WriteAsync(responseBytes.AsMemory(0, headerLength));
                await stream.FlushAsync();
                HeadersSent.TrySetResult();
                if (holdBody) await ReleaseBody.Task;
                await stream.WriteAsync(responseBody);
                await stream.FlushAsync();
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException) when (holdBody)
            {
                // 本文待機を中止したクライアントの切断は、この fixture の正常な終端。
            }
        }

        private void ReadRequest(NetworkStream stream)
        {
            byte[] buffer = new byte[4096];
            using var received = new MemoryStream();
            int headerEndIndex = -1;
            while (headerEndIndex < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
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
                    break;
                }
            }
            int bodyBytesRead = receivedBytes.Length - (headerEndIndex + 4);
            while (bodyBytesRead < contentLength)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, contentLength - bodyBytesRead));
                if (read <= 0)
                {
                    break;
                }
                bodyBytesRead += read;
            }
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
}
